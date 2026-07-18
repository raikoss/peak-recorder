using Microsoft.Web.WebView2.Core;

namespace PeakRecorder;

// The compact briefing card that used to live here was replaced 2026-07-18 by
// the live-match overlay (MainWindow with overlay: true), which stays open
// for the whole set instead of hiding at game start.

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
