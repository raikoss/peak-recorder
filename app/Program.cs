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
    private readonly ToolStripMenuItem _statusItem;
    private Form? _briefing;
    private MainWindow? _mainWindow;

    public TrayAppContext()
    {
        _config = Config.Load();
        _store = Store.Load();
        _recorder = new RecorderService(_config, _store);
        _bridge = new BridgeServer(_recorder, _config.BridgePort);

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
        menu.Items.Add(BuildBriefingStyleMenu());
        menu.Items.Add("Preview briefing (test opponent)", null, (_, _) => ShowBriefing("Zetsubing"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open config", null, (_, _) => OpenFile(Config.FilePath));
        menu.Items.Add("Open log", null, (_, _) => OpenFile(Log.FilePath));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "PeakRecorder — idle",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();

        _recorder.StateChanged += () =>
        {
            var text = _recorder.IsRecording
                ? $"Recording vs {_recorder.Opponent ?? "unknown"}"
                : "Idle";
            // NotifyIcon must be touched on the UI thread.
            var syncMenu = menu;
            if (syncMenu.InvokeRequired) syncMenu.BeginInvoke(Apply);
            else Apply();

            void Apply()
            {
                _statusItem.Text = text;
                _tray.Text = ("PeakRecorder — " + text) is { Length: > 63 } t ? t[..63] : "PeakRecorder — " + text;
                if (_recorder.IsRecording)
                {
                    _tray.ShowBalloonTip(2000, "PeakRecorder", text, ToolTipIcon.Info);
                    CloseBriefing(); // recording started -> game is live, dismiss the briefing
                }
            }
        };

        _recorder.MatchFound += opponent =>
        {
            var syncMenu = menu;
            if (syncMenu.InvokeRequired) syncMenu.BeginInvoke(() => ShowBriefing(opponent));
            else ShowBriefing(opponent);
        };

        _recorder.MatchDismissed += () =>
        {
            var syncMenu = menu;
            if (syncMenu.InvokeRequired) syncMenu.BeginInvoke(CloseBriefing);
            else CloseBriefing();
        };

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

    private ToolStripMenuItem BuildBriefingStyleMenu()
    {
        var root = new ToolStripMenuItem("Pre-match briefing");
        var options = new (string Value, string Label)[]
        {
            ("card", "Floating card"),
            ("full", "Full window"),
            ("off", "Off"),
        };
        ToolStripMenuItem[] items = options.Select(o =>
        {
            var item = new ToolStripMenuItem(o.Label) { CheckOnClick = false, Checked = _config.BriefingStyle == o.Value };
            item.Click += (_, _) =>
            {
                _config.BriefingStyle = o.Value;
                _config.Save();
                foreach (ToolStripMenuItem other in root.DropDownItems) other.Checked = false;
                item.Checked = true;
                if (o.Value == "off") CloseBriefing();
            };
            return item;
        }).ToArray();
        root.DropDownItems.AddRange(items);
        return root;
    }

    private void ShowBriefing(string opponent)
    {
        CloseBriefing();
        _briefing = _config.BriefingStyle switch
        {
            "full" => new BriefingWindow(opponent),
            "card" => new BriefingCard(opponent),
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

    private void ShowMainWindow()
    {
        if (_mainWindow is { IsDisposed: false })
        {
            _mainWindow.Show();
            _mainWindow.WindowState = FormWindowState.Normal;
            _mainWindow.Activate();
            return;
        }
        _mainWindow = new MainWindow(_config, _store, _recorder);
        _mainWindow.Show();
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
        _bridge.Dispose();
        _recorder.Dispose();
        Log.Write("PeakRecorder exited.");
        base.ExitThreadCore();
    }
}
