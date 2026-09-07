using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PeakRecorder;

/// <summary>
/// Main library / player / settings window (design turns 2a/2b/2c). One
/// WebView2 hosting a small hand-rolled SPA in Assets/main.html; state is
/// pushed down as JSON on every change, actions come back as JSON messages.
/// With <c>overlay: true</c> the same window becomes the floating live-match
/// companion: always-on-top, pinned to the live page, shown beside the
/// browser for the duration of a match so notes can be jotted without
/// alt-tabbing (lifecycle managed by TrayAppContext).
/// </summary>
internal sealed class MainWindow : Form
{
    private readonly Config _config;
    private readonly Store _store;
    private readonly RecorderService _recorder;
    private readonly CharacterIcons _icons;
    private readonly bool _overlay;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    private string _page = "library";

    /// <summary>Raised when the settings page asks to preview the pre-match briefing.</summary>
    public event Action? PreviewBriefingRequested;

    /// <summary>Switch the page shown in this window (no-op for the overlay).</summary>
    public void Navigate(string page)
    {
        if (_overlay) return;
        _page = page;
        _playerName = null;
        PushState();
    }
    private string? _playerName;
    private string? _lastPhase;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public MainWindow(Config config, Store store, RecorderService recorder, CharacterIcons icons, bool overlay = false)
    {
        _config = config;
        _store = store;
        _recorder = recorder;
        _icons = icons;
        _overlay = overlay;

        BackColor = Color.FromArgb(0x10, 0x14, 0x26);
        Icon = AppIcons.App;
        if (overlay)
        {
            Text = "PeakRecorder — Live match";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            TopMost = true;
            ShowInTaskbar = false;
            MinimumSize = new Size(360, 460);
            ClientSize = new Size(470, 690);
            StartPosition = FormStartPosition.Manual;
            var wa = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(wa.Right - Width - 24, wa.Top + 24);
            _page = "live";
        }
        else
        {
            Text = "PeakRecorder";
            ClientSize = new Size(1080, 680);
            MinimumSize = new Size(760, 480);
            StartPosition = FormStartPosition.CenterScreen;
        }
        Controls.Add(_web);

        _store.Changed += PushStateOnUiThread;
        _recorder.StateChanged += PushStateOnUiThread;
        _icons.Changed += PushStateOnUiThread;

        FormClosed += (_, _) =>
        {
            _store.Changed -= PushStateOnUiThread;
            _recorder.StateChanged -= PushStateOnUiThread;
            _icons.Changed -= PushStateOnUiThread;
        };

        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await BriefingRuntime.GetEnvironmentAsync();
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.WebMessageReceived += (_, e) => HandleMessage(e.TryGetWebMessageAsString());

            // Serve the cached character icons to the page (which itself is
            // loaded via NavigateToString, so it has no folder of its own).
            Directory.CreateDirectory(CharacterIcons.Dir);
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                CharacterIcons.VirtualHost, CharacterIcons.Dir, CoreWebView2HostResourceAccessKind.Allow);

            var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "main.html");
            var found = File.Exists(htmlPath);
            var html = found
                ? File.ReadAllText(htmlPath)
                : "<body style='background:#101426;color:#fff;font-family:sans-serif;padding:20px'>Assets/main.html not found next to the executable.</body>";
            Log.Write($"MainWindow loading html from {htmlPath} (found={found}, length={html.Length})");

            _web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                Log.Write($"MainWindow NavigationCompleted success={e.IsSuccess} status={e.WebErrorStatus}");
                PushState();
            };
            _web.CoreWebView2.NavigateToString(html);
        }
        catch (Exception ex)
        {
            Log.Write($"MainWindow WebView2 init failed: {ex}");
            MessageBox.Show($"Could not open the PeakRecorder window:\n{ex.Message}",
                "PeakRecorder", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void PushStateOnUiThread()
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(PushState);
        else PushState();
    }

    private void PushState()
    {
        if (_web.CoreWebView2 == null) return;

        // Auto-switch on phase transitions only, so the user can still browse
        // other pages mid-match; the window itself is never opened or focused.
        // The overlay window stays pinned to the live page instead.
        var phase = _recorder.Phase;
        if (!_overlay)
        {
            if (phase != null && _lastPhase == null) _page = "live";
            else if (phase == null && _lastPhase != null && _page == "live") _page = "library";
            _lastPhase = phase;
        }

        var snapshot = _store.Snapshot();
        var payload = new
        {
            page = _page,
            overlay = _overlay,
            playerName = _playerName,
            focusGoal = snapshot.FocusGoal,
            matches = snapshot.Matches,
            gamePlans = snapshot.GamePlans,
            characterIcons = _icons.UrlMap(
                snapshot.Matches.SelectMany(m => new[] { m.MyCharacter, m.OpponentCharacter })),
            recording = new
            {
                isRecording = _recorder.IsRecording,
                phase,
                opponent = _recorder.Opponent,
                matchId = _recorder.MatchId,
                currentMatchRecordId = _recorder.CurrentMatchRecordId,
                gamesWon = _recorder.GamesWon,
                gamesLost = _recorder.GamesLost,
                currentGame = _recorder.CurrentGameNumber,
            },
            config = BuildConfigPayload(),
        };
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        _ = PushStateScriptAsync(json);
    }

    // Everything the settings page shows: the raw config values, their
    // defaults (for "reset" links), and a few derived facts so the page can
    // explain what "auto" / "blank" currently resolve to.
    private object BuildConfigPayload()
    {
        var defaults = new Config();
        var obsOwn = ObsSettings.ReadObsOwn();
        string? recordingsFolderResolved = null;
        if (!string.IsNullOrWhiteSpace(_config.RecordingsFolder))
        {
            try { recordingsFolderResolved = Path.GetFullPath(Environment.ExpandEnvironmentVariables(_config.RecordingsFolder)); }
            catch { recordingsFolderResolved = null; }
        }
        return new
        {
            // recording
            filenameTemplate = _config.FilenameTemplate,
            recordingsFolder = _config.RecordingsFolder,
            recordingsFolderResolved,
            recordingsFolderExists = recordingsFolderResolved != null && Directory.Exists(recordingsFolderResolved),
            deleteOriginalAfterRemux = _config.DeleteOriginalAfterRemux,
            // obs
            obsExePath = _config.ObsExePath,
            obsExeFound = File.Exists(_config.ObsExePath),
            obsLaunchArgs = _config.ObsLaunchArgs,
            obsWsPort = _config.ObsWsPort,
            obsWsPassword = _config.ObsWsPassword,
            obsOwnConfigFound = obsOwn.port != null || obsOwn.authRequired != null,
            obsOwnPort = obsOwn.port,
            obsOwnAuthRequired = obsOwn.authRequired,
            obsOwnServerEnabled = obsOwn.enabled,
            obsOwnConfigPath = ObsSettings.ConfigPath,
            // briefing
            briefingStyle = _config.BriefingStyle,
            // app
            bridgePort = _config.BridgePort,
            bridgePortInUse = StartupBridgePort,
            configPath = Config.FilePath,
            logPath = Log.FilePath,
            dataDir = Config.Dir,
            defaults = new
            {
                filenameTemplate = defaults.FilenameTemplate,
                obsExePath = defaults.ObsExePath,
                obsLaunchArgs = defaults.ObsLaunchArgs,
                bridgePort = defaults.BridgePort,
                briefingStyle = defaults.BriefingStyle,
                deleteOriginalAfterRemux = defaults.DeleteOriginalAfterRemux,
            },
        };
    }

    /// <summary>The port the bridge server was actually started on; the
    /// config value can differ until the app is restarted.</summary>
    internal static int StartupBridgePort { get; set; }

    private async Task PushStateScriptAsync(string json)
    {
        try
        {
            var result = await _web.CoreWebView2.ExecuteScriptAsync("window.__applyState(" + json + ")");
            Log.Write($"PushState executed, result={result}");
        }
        catch (Exception ex)
        {
            Log.Write($"PushState script failed: {ex}");
        }
    }

    private void HandleMessage(string? raw)
    {
        if (raw == null) return;
        JsonObject? msg;
        try { msg = JsonNode.Parse(raw)?.AsObject(); }
        catch { return; }
        var action = msg?["action"]?.GetValue<string>();
        if (action == null) return;

        try
        {
            switch (action)
            {
                case "jsError":
                    Log.Write($"MainWindow JS error: {msg!["message"]?.GetValue<string>()} " +
                              $"(line {msg["lineno"]}, col {msg["colno"]})\n{msg["stack"]?.GetValue<string>()}");
                    break;

                case "navigate":
                    if (_overlay) break; // overlay is pinned to the live page
                    _page = msg!["page"]?.GetValue<string>() ?? "library";
                    _playerName = msg["opponent"]?.GetValue<string>();
                    PushState();
                    break;

                case "addNote":
                    var noteMatchId = msg!["matchId"]!.GetValue<string>();
                    // Notes on the live match are stamped with the recording
                    // clock (0:00 during striking); notes on past matches are
                    // general VOD-review notes, stored with -1 = no timestamp.
                    var timestamp = noteMatchId == _recorder.CurrentMatchRecordId
                        ? _recorder.RecordingElapsedSeconds
                        : -1;
                    _store.AddNote(
                        noteMatchId,
                        timestamp,
                        msg["gameNumber"]?.GetValue<int>(),
                        msg["text"]!.GetValue<string>(),
                        msg["tags"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? []);
                    break;

                case "updateNote":
                    _store.UpdateNote(
                        msg!["noteId"]!.GetValue<long>(),
                        msg["text"]!.GetValue<string>(),
                        msg["tags"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [],
                        msg["gameNumber"]?.GetValue<int>());
                    break;

                case "deleteNote":
                    _store.DeleteNote(msg!["noteId"]!.GetValue<long>());
                    break;

                case "setFocusGoal":
                    _store.SetFocusGoal(msg!["text"]?.GetValue<string>() ?? "");
                    break;

                case "setGamePlan":
                    var opponent = msg!["opponent"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(opponent)) _store.SetGamePlan(opponent, msg["text"]?.GetValue<string>() ?? "");
                    break;

                case "stopRecording":
                    _ = _recorder.HandleEventAsync("manual_stop", null, null, null);
                    break;

                case "openVideo":
                    OpenVideo(msg!["matchId"]!.GetValue<string>());
                    break;

                case "deleteMatch":
                    _store.DeleteMatch(msg!["matchId"]!.GetValue<string>());
                    break;

                case "openConfig":
                    TrayAppContext.OpenFile(Config.FilePath);
                    break;

                case "openLog":
                    TrayAppContext.OpenFile(Log.FilePath);
                    break;

                case "saveSettings":
                    ApplySettings(msg!);
                    _config.Save();
                    PushState();
                    break;

                case "previewBriefing":
                    PreviewBriefingRequested?.Invoke();
                    break;

                case "browseFolder":
                    BrowseFolder();
                    break;

                case "browseObsExe":
                    BrowseObsExe();
                    break;

                case "openFolder":
                    if (msg!["path"]?.GetValue<string>() is { } folder && Directory.Exists(folder))
                        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"MainWindow message error ({action}): {ex.Message}");
        }
    }

    // Only keys present in the message are applied, so the page can save a
    // single field (or a single toggle) without resending everything.
    private void ApplySettings(JsonObject msg)
    {
        var defaults = new Config();
        if (msg["toggleDeleteOriginal"]?.GetValue<bool>() == true)
            _config.DeleteOriginalAfterRemux = !_config.DeleteOriginalAfterRemux;

        if (msg["filenameTemplate"]?.GetValue<string>() is { } ft)
            _config.FilenameTemplate = string.IsNullOrWhiteSpace(ft) ? defaults.FilenameTemplate : ft.Trim();
        if (msg["recordingsFolder"]?.GetValue<string>() is { } rf)
            _config.RecordingsFolder = string.IsNullOrWhiteSpace(rf) ? null : rf.Trim();

        if (msg["obsExePath"]?.GetValue<string>() is { } oe)
            _config.ObsExePath = string.IsNullOrWhiteSpace(oe) ? defaults.ObsExePath : oe.Trim().Trim('"');
        if (msg["obsLaunchArgs"]?.GetValue<string>() is { } la)
            _config.ObsLaunchArgs = la.Trim();
        if (msg.ContainsKey("obsWsPort"))
            _config.ObsWsPort = ParsePort(msg["obsWsPort"]?.GetValue<string>());
        if (msg["obsWsPassword"]?.GetValue<string>() is { } pw)
            _config.ObsWsPassword = pw.Length == 0 ? null : pw;

        if (msg["briefingStyle"]?.GetValue<string>() is { } bs && bs is "card" or "full" or "off")
            _config.BriefingStyle = bs;

        if (msg.ContainsKey("bridgePort"))
            _config.BridgePort = ParsePort(msg["bridgePort"]?.GetValue<string>()) ?? defaults.BridgePort;
    }

    private static int? ParsePort(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return int.TryParse(raw.Trim(), out var p) && p is >= 1 and <= 65535 ? p : null;
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Folder to move finished recordings into",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        var current = _config.RecordingsFolder;
        if (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                var expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables(current));
                if (Directory.Exists(expanded)) dlg.SelectedPath = expanded;
            }
            catch { /* ignore */ }
        }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _config.RecordingsFolder = dlg.SelectedPath;
        _config.Save();
        PushState();
    }

    private void BrowseObsExe()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Locate obs64.exe",
            Filter = "OBS Studio (obs64.exe)|obs64.exe|Executables (*.exe)|*.exe",
            CheckFileExists = true,
        };
        var dir = Path.GetDirectoryName(_config.ObsExePath);
        if (dir != null && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _config.ObsExePath = dlg.FileName;
        _config.Save();
        PushState();
    }

    private void OpenVideo(string matchId)
    {
        var m = _store.Snapshot().Matches.FirstOrDefault(x => x.Id == matchId);
        var path = m?.VideoPath;
        if (path != null && !File.Exists(path))
        {
            // Records written before the remux race was fixed can point at a
            // .mkv that was deleted after remuxing; fall back to the sibling
            // with the same name and another extension (e.g. the .mp4).
            var dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir))
                path = Directory.EnumerateFiles(dir, Path.GetFileNameWithoutExtension(path) + ".*").FirstOrDefault();
        }
        if (path == null)
        {
            MessageBox.Show("No video file for this match yet.", "PeakRecorder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }
}
