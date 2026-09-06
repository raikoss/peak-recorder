using System.Text.Json.Nodes;

namespace PeakRecorder;

/// <summary>Reads (and minimally edits) OBS's own obs-websocket configuration.</summary>
public static class ObsSettings
{
    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "obs-studio", "plugin_config", "obs-websocket", "config.json");

    /// <summary>Resolve port + password: explicit config overrides win, otherwise OBS's config file.</summary>
    public static (int port, string? password) Resolve(Config config)
    {
        var port = config.ObsWsPort ?? 4455;
        var password = config.ObsWsPassword;
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = JsonNode.Parse(File.ReadAllText(ConfigPath));
                if (config.ObsWsPort == null && json?["server_port"] != null)
                    port = json["server_port"]!.GetValue<int>();
                if (password == null && json?["auth_required"]?.GetValue<bool>() == true)
                    password = json["server_password"]?.GetValue<string>();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read OBS websocket config: {ex.Message}");
        }
        return (port, password);
    }

    /// <summary>
    /// What OBS's own websocket config says, for showing the user what the
    /// "auto" setting resolves to. Nulls when the file is missing/unreadable.
    /// </summary>
    public static (int? port, bool? authRequired, bool enabled) ReadObsOwn()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return (null, null, false);
            var json = JsonNode.Parse(File.ReadAllText(ConfigPath));
            return (
                json?["server_port"]?.GetValue<int>(),
                json?["auth_required"]?.GetValue<bool>(),
                json?["server_enabled"]?.GetValue<bool>() ?? false);
        }
        catch
        {
            return (null, null, false);
        }
    }

    /// <summary>
    /// Flips server_enabled to true in OBS's websocket config. Only safe while
    /// OBS is closed (OBS overwrites the file on exit).
    /// </summary>
    public static void EnableWebSocketServer()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var json = JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();
            if (json["server_enabled"]?.GetValue<bool>() == true) return;
            json["server_enabled"] = true;
            File.WriteAllText(ConfigPath, json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Log.Write("Enabled obs-websocket server in OBS config.");
        }
        catch (Exception ex)
        {
            Log.Write($"Could not enable obs-websocket server: {ex.Message}");
        }
    }
}
