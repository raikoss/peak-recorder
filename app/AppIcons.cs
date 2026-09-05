namespace PeakRecorder;

/// <summary>
/// Branded icons shipped in Assets/icons (copied next to the exe). Falls
/// back to the stock system icon if a file is missing so a broken build
/// never hides the tray entry.
/// </summary>
internal static class AppIcons
{
    public static readonly Icon App = Load("app.ico");
    public static readonly Icon Tray = Load("tray.ico");
    public static readonly Icon TrayRecording = Load("tray-recording.ico");

    private static Icon Load(string file)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "icons", file);
            if (File.Exists(path)) return new Icon(path);
            Log.Write($"Icon missing: {path}");
        }
        catch (Exception ex)
        {
            Log.Write($"Icon load failed ({file}): {ex.Message}");
        }
        return SystemIcons.Application;
    }
}
