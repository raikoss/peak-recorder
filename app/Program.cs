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
    private readonly ToolStripMenuItem _statusItem;

    public TrayAppContext()
    {
        var config = Config.Load();
        _recorder = new RecorderService(config);
        _bridge = new BridgeServer(_recorder, config.BridgePort);

        _statusItem = new ToolStripMenuItem("Idle") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start recording now", null, async (_, _) =>
            await _recorder.HandleEventAsync("manual_start", null, null, null));
        menu.Items.Add("Stop recording now", null, async (_, _) =>
            await _recorder.HandleEventAsync("manual_stop", null, null, null));
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
                    _tray.ShowBalloonTip(2000, "PeakRecorder", text, ToolTipIcon.Info);
            }
        };

        try
        {
            _bridge.Start();
        }
        catch (Exception ex)
        {
            Log.Write($"FATAL: bridge server failed to start: {ex.Message}");
            MessageBox.Show($"Could not start the local server on port {config.BridgePort}:\n{ex.Message}",
                "PeakRecorder", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        Log.Write("PeakRecorder started.");
    }

    private static void OpenFile(string path)
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
        _tray.Visible = false;
        _tray.Dispose();
        _bridge.Dispose();
        _recorder.Dispose();
        Log.Write("PeakRecorder exited.");
        base.ExitThreadCore();
    }
}
