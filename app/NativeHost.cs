using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PeakRecorder;

/// <summary>
/// Chrome native-messaging support. Chrome launches PeakRecorder.exe with a
/// "chrome-extension://..." argument when the popup's "Start companion app"
/// button is clicked; we make sure the tray app is running and reply.
/// </summary>
public static class NativeHost
{
    public const string HostName = "eu.smashthepeak.peakrecorder";

    public static void Run()
    {
        try
        {
            // Read the request (length-prefixed JSON); content doesn't matter.
            using var stdin = Console.OpenStandardInput();
            var lenBuf = new byte[4];
            stdin.ReadExactly(lenBuf);
            var payload = new byte[BitConverter.ToInt32(lenBuf)];
            stdin.ReadExactly(payload);
        }
        catch { /* Chrome may close the pipe early; proceed anyway */ }

        var alreadyRunning = Mutex.TryOpenExisting("PeakRecorder-single-instance", out var m);
        m?.Dispose();
        if (!alreadyRunning)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath)!,
            });
        }

        try
        {
            var json = $"{{\"ok\":true,\"alreadyRunning\":{(alreadyRunning ? "true" : "false")}}}";
            var bytes = Encoding.UTF8.GetBytes(json);
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(BitConverter.GetBytes(bytes.Length));
            stdout.Write(bytes);
            stdout.Flush();
        }
        catch { /* reply is best-effort */ }
    }

    /// <summary>
    /// Registers this exe as the native messaging host for the given
    /// extension. Idempotent; called when the extension reports its ID.
    /// </summary>
    public static void Register(string extensionId)
    {
        if (!Regex.IsMatch(extensionId, "^[a-p]{32}$"))
        {
            Log.Write($"Ignoring invalid extension id '{extensionId}'.");
            return;
        }

        var manifestPath = Path.Combine(Config.Dir, "nativehost.json");
        var manifest =
            "{\n" +
            $"  \"name\": \"{HostName}\",\n" +
            "  \"description\": \"Launches the PeakRecorder companion app\",\n" +
            $"  \"path\": {System.Text.Json.JsonSerializer.Serialize(Environment.ProcessPath)},\n" +
            "  \"type\": \"stdio\",\n" +
            $"  \"allowed_origins\": [\"chrome-extension://{extensionId}/\"]\n" +
            "}\n";

        if (File.Exists(manifestPath) && File.ReadAllText(manifestPath) == manifest) return; // already registered

        Directory.CreateDirectory(Config.Dir);
        File.WriteAllText(manifestPath, manifest);
        using var key = Registry.CurrentUser.CreateSubKey(
            $@"Software\Google\Chrome\NativeMessagingHosts\{HostName}");
        key.SetValue("", manifestPath);
        Log.Write($"Registered native messaging host for extension {extensionId}.");
    }
}
