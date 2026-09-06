using System.Diagnostics;

namespace PeakRecorder;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Chrome invokes us as a native messaging host with the extension
        // origin as an argument; handle that mode without any UI.
        if (args.Any(a => a.StartsWith("chrome-extension://")))
        {
            NativeHost.Run();
            return;
        }

        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(true, "PeakRecorder-single-instance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("PeakRecorder is already running (check the system tray).",
                "PeakRecorder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayAppContext());
    }
}

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly RecorderService _recorder;
    private readonly BridgeServer _bridge;
    private readonly Config _config;
    private readonly Store _store;
    private readonly CharacterIcons _icons;
    private readonly ToolStripMenuItem _statusItem;
    private Form? _briefing;
    private MainWindow? _mainWindow;
    private bool _wasRecording;

    // Recorder events arrive on thread-pool threads, but everything UI
    // (NotifyIcon, briefing windows, WebView2) must run on the main STA
    // thread. The menu/tray can't marshal reliably before their handles
    // exist (InvokeRequired is false without a handle), so use a dedicated
    // control whose handle is created eagerly in the constructor.
    private readonly Control _uiThread = new();

    public TrayAppContext()
    {
        _ = _uiThread.Handle; // force handle creation on the UI thread
        _config = Config.Load();
        _store = Store.Load();
        _icons = new CharacterIcons(_store);
        _recorder = new RecorderService(_config, _store);
        _bridge = new BridgeServer(_recorder, _icons, _config.BridgePort);
        MainWindow.StartupBridgePort = _config.BridgePort;

        _statusItem = new ToolStripMenuItem("Idle") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open PeakRecorder", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start recording now", null, async (_, _) =>
            await _recorder.HandleEventAsync("manual_start", null, null, null));
        menu.Items.Add("Stop recording now", null, async (_, _) =>
            await _recorder.HandleEventAsync("manual_stop", null, null, null));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings", null, (_, _) => ShowMainWindow("settings"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = AppIcons.Tray,
            Text = "PeakRecorder — idle",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();

        _recorder.StateChanged += () => RunOnUiThread(() =>
        {
            var text = _recorder.IsRecording
                ? $"Recording vs {_recorder.Opponent ?? "unknown"}"
                : "Idle";
            _statusItem.Text = text;
            _tray.Icon = _recorder.IsRecording ? AppIcons.TrayRecording : AppIcons.Tray;
            _tray.Text = ("PeakRecorder — " + text) is { Length: > 63 } t ? t[..63] : "PeakRecorder — " + text;
            if (_recorder.IsRecording)
            {
                // StateChanged fires on every mid-set event too (stage/char
                // picks, score updates), not just start/stop — only toast on
                // the false->true edge so later games don't re-notify.
                if (!_wasRecording) _tray.ShowBalloonTip(2000, "PeakRecorder", text, ToolTipIcon.Info);
                // The full-window briefing hides at game start; the live
                // overlay stays up so notes can be jotted during the set.
                if (_briefing is BriefingWindow) CloseBriefing();
            }
            _wasRecording = _recorder.IsRecording;
            if (_recorder.Phase == null) CloseBriefing(); // match over
        });

        _recorder.MatchFound += opponent => RunOnUiThread(() => ShowBriefing(opponent));

        // Settings can also be edited from the main window; keep the tray in step.
        _config.Saved += () => RunOnUiThread(() =>
        {
            if (_config.BriefingStyle == "off") CloseBriefing();
        });

        _recorder.MatchDismissed += () => RunOnUiThread(CloseBriefing);

        try
        {
            _bridge.Start();
        }
        catch (Exception ex)
        {
            Log.Write($"FATAL: bridge server failed to start: {ex.Message}");
            MessageBox.Show($"Could not start the local server on port {_config.BridgePort}:\n{ex.Message}",
                "PeakRecorder", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        Log.Write("PeakRecorder started.");

        if (Environment.GetEnvironmentVariable("PEAKRECORDER_AUTOOPEN") == "1") ShowMainWindow();
    }

    private void RunOnUiThread(Action action)
    {
        if (_uiThread.InvokeRequired) _uiThread.BeginInvoke(action);
        else action();
    }

    private void ShowBriefing(string opponent)
    {
        CloseBriefing();
        _briefing = _config.BriefingStyle switch
        {
            "full" => new BriefingWindow(opponent, _store.Snapshot()),
            "card" => new MainWindow(_config, _store, _recorder, _icons, overlay: true),
            _ => null,
        };
        _briefing?.Show();
    }

    private void CloseBriefing()
    {
        _briefing?.Close();
        _briefing?.Dispose();
        _briefing = null;
    }

    private void ShowMainWindow(string? page = null)
    {
        if (_mainWindow is not { IsDisposed: false })
        {
            _mainWindow = new MainWindow(_config, _store, _recorder, _icons);
            // "Preview" on the settings page shows the briefing for a fake opponent.
            _mainWindow.PreviewBriefingRequested += () => ShowBriefing("Zetsubing");
            _mainWindow.Show();
        }
        else
        {
            _mainWindow.Show();
            _mainWindow.WindowState = FormWindowState.Normal;
            _mainWindow.Activate();
        }
        if (page != null) _mainWindow.Navigate(page);
    }

    internal static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* best effort */ }
    }

    protected override void ExitThreadCore()
    {
        CloseBriefing();
        if (_mainWindow is { IsDisposed: false }) _mainWindow.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _uiThread.Dispose();
        _bridge.Dispose();
        _recorder.Dispose();
        Log.Write("PeakRecorder exited.");
        base.ExitThreadCore();
    }
}
