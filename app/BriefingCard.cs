using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PeakRecorder;

/// <summary>
/// Turn 3b: compact always-on-top card that floats beside the browser during
/// stage striking. Dismisses itself when the game starts (see TrayAppContext).
/// </summary>
internal sealed class BriefingCard : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    public BriefingCard(string opponent, AppData data)
    {
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(380, 560);
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(0x0c, 0x10, 0x20);
        Controls.Add(_web);

        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Right - Width - 24, wa.Top + 24);

        _ = InitAsync(opponent, data);
    }

    private async Task InitAsync(string opponent, AppData data)
    {
        try
        {
            var env = await BriefingRuntime.GetEnvironmentAsync();
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                switch (e.TryGetWebMessageAsString())
                {
                    case "close": Close(); break;
                    case "drag": StartDrag(); break;
                }
            };
            _web.CoreWebView2.NavigateToString(BriefingHtml.Card(opponent, data));
        }
        catch (Exception ex)
        {
            Log.Write($"BriefingCard WebView2 init failed: {ex.Message}");
            Close();
        }
    }

    // WebView2 owns its own HWND, so the form never sees the mouse-down that
    // starts a drag; the page posts a "drag" message from the header instead.
    private void StartDrag()
    {
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
    }

    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);
}

/// <summary>
/// One shared CoreWebView2Environment for the whole app. WebView2 controls
/// can share an environment fine, and creating a separate environment per
/// window against the same user-data folder is a common source of silent
/// init failures (the window is left blank with nothing logged unless the
/// caller wraps EnsureCoreWebView2Async in a try/catch, which is easy to
/// forget) — so every window goes through this instead of calling
/// CoreWebView2Environment.CreateAsync itself.
/// </summary>
internal static class BriefingRuntime
{
    public static string UserDataFolder => Path.Combine(Config.Dir, "webview2");

    private static Task<CoreWebView2Environment>? _env;

    public static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        // Never cache a failed creation attempt — a single early failure
        // (e.g. a call from the wrong thread) would otherwise leave every
        // later window permanently blank until the app restarts.
        if (_env == null || _env.IsFaulted || _env.IsCanceled)
            _env = CoreWebView2Environment.CreateAsync(userDataFolder: UserDataFolder);
        return _env;
    }
}
