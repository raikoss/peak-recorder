using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeakRecorder;

public class Config
{
    public int BridgePort { get; set; } = 8123;
    public string ObsExePath { get; set; } = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";

    /// <summary>Extra arguments when launching OBS.</summary>
    public string ObsLaunchArgs { get; set; } = "--disable-shutdown-check --minimize-to-tray";

    /// <summary>Override obs-websocket port. null = read from OBS's own config.</summary>
    public int? ObsWsPort { get; set; }

    /// <summary>Override obs-websocket password. null = read from OBS's own config.</summary>
    public string? ObsWsPassword { get; set; }

    /// <summary>Filename template. Placeholders: {date}, {time}, {opponent}, {matchId}.</summary>
    public string FilenameTemplate { get; set; } = "{date}_{time}_vs_{opponent}";

    /// <summary>
    /// When OBS auto-remuxes the recording (e.g. mkv -> mp4), delete the
    /// original file once the remuxed copy is renamed and looks complete.
    /// </summary>
    public bool DeleteOriginalAfterRemux { get; set; } = true;

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PeakRecorder");

    public static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to read config, using defaults: {ex.Message}");
        }
        var cfg = new Config();
        cfg.Save();
        return cfg;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }
}

public static class Log
{
    private static readonly object Lock = new();
    public static string FilePath => Path.Combine(Config.Dir, "log.txt");

    public static event Action<string>? OnMessage;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* logging must never crash the app */ }
        }
        OnMessage?.Invoke(line);
    }
}
