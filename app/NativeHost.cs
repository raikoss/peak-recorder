using System.Diagnostics;
using System.Runtime.InteropServices;
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
        if (!alreadyRunning) StartTrayApp();

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
    /// Starts the tray app detached from the browser. Firefox runs native
    /// hosts inside a Windows job object that kills every process in it
    /// (children included) once the host exits, so a plain Process.Start
    /// gets the tray app killed seconds after launch. CREATE_BREAKAWAY_FROM_JOB
    /// takes the child out of that job; Firefox's job permits it. Falls back to
    /// a normal start (Chrome, or a job that forbids breakaway).
    /// </summary>
    private static void StartTrayApp()
    {
        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;

        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        if (CreateProcess(exe, $"\"{exe}\"", IntPtr.Zero, IntPtr.Zero, false,
                CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_PROCESS_GROUP | DETACHED_PROCESS,
                IntPtr.Zero, dir, ref si, out var pi))
        {
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
            return;
        }

        Log.Write($"Breakaway launch failed (error {Marshal.GetLastWin32Error()}); starting normally.");
        Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true, WorkingDirectory = dir });
    }

    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    private const uint DETACHED_PROCESS = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

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

    /// <summary>
    /// Firefox add-on ID, fixed in the extension manifest's
    /// browser_specific_settings.gecko.id.
    /// </summary>
    public const string FirefoxExtensionId = "peakrecorder@smashthepeak.eu";

    /// <summary>
    /// Registers this exe as the Firefox native messaging host. The add-on ID
    /// is fixed, so this runs at startup. Idempotent.
    /// </summary>
    public static void RegisterFirefox()
    {
        var manifestPath = Path.Combine(Config.Dir, "nativehost-firefox.json");
        var manifest =
            "{\n" +
            $"  \"name\": \"{HostName}\",\n" +
            "  \"description\": \"Launches the PeakRecorder companion app\",\n" +
            $"  \"path\": {System.Text.Json.JsonSerializer.Serialize(Environment.ProcessPath)},\n" +
            "  \"type\": \"stdio\",\n" +
            $"  \"allowed_extensions\": [\"{FirefoxExtensionId}\"]\n" +
            "}\n";

        var keyPath = $@"Software\Mozilla\NativeMessagingHosts\{HostName}";
        using (var existing = Registry.CurrentUser.OpenSubKey(keyPath))
        {
            if (File.Exists(manifestPath) && File.ReadAllText(manifestPath) == manifest
                && existing?.GetValue("") as string == manifestPath) return; // already registered
        }

        Directory.CreateDirectory(Config.Dir);
        File.WriteAllText(manifestPath, manifest);
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue("", manifestPath);
        Log.Write("Registered Firefox native messaging host.");
    }
}
