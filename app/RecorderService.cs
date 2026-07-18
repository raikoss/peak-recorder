using System.Diagnostics;
using System.Text.Json.Nodes;

namespace PeakRecorder;

/// <summary>
/// Orchestrates the whole flow: ensure OBS is running, connect to
/// obs-websocket, start/stop recording, rename the output file.
/// </summary>
public sealed class RecorderService : IDisposable
{
    private readonly Config _config;
    private readonly Store _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ObsWebSocketClient? _obs;

    public bool IsRecording { get; private set; }
    public string? Opponent { get; private set; }
    public string? MatchId { get; private set; }

    /// <summary>Id of the Store MatchRecord for the current/last recording,
    /// so the main window can attach notes to it.</summary>
    public string? CurrentMatchRecordId { get; private set; }

    private DateTime _recordingStartedAt;

    public event Action? StateChanged;

    /// <summary>Fired when the extension resolves an opponent during stage
    /// striking, before the match is actually "ready" to record.</summary>
    public event Action<string>? MatchFound;

    /// <summary>Fired when a found match goes away before it started (page
    /// left, match cancelled) — the UI should dismiss any briefing shown.</summary>
    public event Action? MatchDismissed;

    public RecorderService(Config config, Store store)
    {
        _config = config;
        _store = store;
    }

    public async Task HandleEventAsync(string type, string? matchId, string? opponent, string[]? players)
    {
        await _gate.WaitAsync();
        try
        {
            Log.Write($"Event: {type} matchId={matchId} opponent={opponent ?? "?"} players=[{string.Join(", ", players ?? [])}]");
            switch (type)
            {
                case "match_started":
                case "manual_start":
                    if (IsRecording)
                    {
                        // Already rolling (e.g. duplicate event). Just refresh metadata.
                        UpdateOpponent(matchId, opponent, players);
                        break;
                    }
                    MatchId = matchId;
                    Opponent = null; // don't carry a stale opponent into a new recording
                    UpdateOpponent(matchId, opponent, players);
                    await StartRecordingAsync();
                    break;

                case "match_found":
                    UpdateOpponent(matchId, opponent, players);
                    if (!string.IsNullOrWhiteSpace(Opponent)) MatchFound?.Invoke(Opponent);
                    break;

                case "match_dismissed":
                    MatchId = null;
                    Opponent = null;
                    MatchDismissed?.Invoke();
                    break;

                case "match_update":
                    UpdateOpponent(matchId, opponent, players);
                    break;

                case "match_ended":
                case "manual_stop":
                    if (!IsRecording) break;
                    // Ignore stray end-events from a different match page.
                    if (type == "match_ended" && MatchId != null && matchId != null && matchId != MatchId) break;
                    UpdateOpponent(matchId, opponent, players);
                    await StopRecordingAndRenameAsync();
                    break;

                default:
                    Log.Write($"Unknown event type '{type}' ignored.");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"ERROR handling {type}: {ex.Message}");
        }
        finally
        {
            _gate.Release();
            StateChanged?.Invoke();
        }
    }

    private void UpdateOpponent(string? matchId, string? opponent, string[]? players)
    {
        if (matchId != null && MatchId == null) MatchId = matchId;
        if (!string.IsNullOrWhiteSpace(opponent)) Opponent = opponent.Trim();
        // No resolved opponent but we know both players: keep them as a fallback name.
        else if (Opponent == null && players is { Length: >= 2 })
            Opponent = string.Join("_", players.Take(2));
    }

    // ---- OBS lifecycle -------------------------------------------------------

    private async Task StartRecordingAsync()
    {
        await EnsureObsConnectedAsync();
        try
        {
            await _obs!.RequestAsync("StartRecord", null, Timeout(10));
        }
        catch (ObsRequestException ex) when (ex.Code == 500) // OUTPUT_RUNNING: already recording
        {
            Log.Write("OBS was already recording; adopting that recording.");
        }
        IsRecording = true;
        _recordingStartedAt = DateTime.Now;
        CurrentMatchRecordId = _store.StartMatch(MatchId ?? "", Opponent ?? "unknown").Id;
        Log.Write($"Recording started (opponent: {Opponent ?? "unknown"}).");
    }

    /// <summary>Seconds elapsed since the current recording started — used to
    /// timestamp notes taken while a match is live.</summary>
    public int RecordingElapsedSeconds =>
        IsRecording ? (int)(DateTime.Now - _recordingStartedAt).TotalSeconds : 0;

    private async Task StopRecordingAndRenameAsync()
    {
        string? outputPath = null;
        try
        {
            var response = await _obs!.RequestAsync("StopRecord", null, Timeout(15));
            outputPath = response?["outputPath"]?.GetValue<string>();
        }
        finally
        {
            IsRecording = false;
        }
        Log.Write($"Recording stopped. File: {outputPath ?? "unknown"}");

        var recordId = CurrentMatchRecordId;
        string? finalPath = outputPath;
        string? renamed = null;
        if (outputPath != null)
        {
            renamed = await RenameRecordingAsync(outputPath);
            Log.Write(renamed != null ? $"Renamed to: {renamed}" : "Rename failed; file kept with original name.");
            if (renamed != null) finalPath = renamed;
        }
        if (recordId != null) _store.EndMatch(recordId, finalPath);
        // Watch for OBS's auto-remuxed copy only after the record has its
        // path, so the remuxed file's EndMatch is always the last write —
        // otherwise the record can end up pointing at the .mkv the remux
        // cleanup deletes.
        if (outputPath != null && renamed != null)
            _ = RenameRemuxedSiblingAsync(outputPath, Path.Combine(
                Path.GetDirectoryName(renamed)!, Path.GetFileNameWithoutExtension(renamed)), renamed, recordId);
        CurrentMatchRecordId = null;
        Opponent = null;
        MatchId = null;
    }

    private async Task EnsureObsConnectedAsync()
    {
        if (_obs is { IsConnected: true })
        {
            // Verify the connection is actually alive.
            try
            {
                await _obs.RequestAsync("GetVersion", null, Timeout(5));
                return;
            }
            catch
            {
                _obs.Dispose();
                _obs = null;
            }
        }

        var obsRunning = Process.GetProcessesByName("obs64").Length > 0;
        if (!obsRunning)
        {
            ObsSettings.EnableWebSocketServer(); // only editable while OBS is closed
            LaunchObs();
        }

        var (port, password) = ObsSettings.Resolve(_config);

        // OBS takes a while to boot; retry the connection.
        var deadline = DateTime.UtcNow.AddSeconds(45);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var client = new ObsWebSocketClient();
            try
            {
                await client.ConnectAsync(port, password, Timeout(5));
                _obs = client;
                Log.Write($"Connected to obs-websocket on port {port}.");
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                client.Dispose();
                await Task.Delay(1500);
            }
        }
        throw new InvalidOperationException($"Could not connect to obs-websocket: {last?.Message}");
    }

    private void LaunchObs()
    {
        if (!File.Exists(_config.ObsExePath))
            throw new FileNotFoundException($"OBS not found at {_config.ObsExePath} (set ObsExePath in config.json).");

        Log.Write("Launching OBS...");
        Process.Start(new ProcessStartInfo
        {
            FileName = _config.ObsExePath,
            Arguments = _config.ObsLaunchArgs,
            // OBS refuses to start with the wrong working directory.
            WorkingDirectory = Path.GetDirectoryName(_config.ObsExePath)!,
            UseShellExecute = true,
        });
    }

    // ---- renaming --------------------------------------------------------------

    // Where finished recordings end up: the configured RecordingsFolder, or
    // the file's own (= OBS's output) folder when unset or unusable.
    private string ResolveTargetDir(string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath)!;
        if (string.IsNullOrWhiteSpace(_config.RecordingsFolder)) return dir;
        try
        {
            var target = Path.GetFullPath(Environment.ExpandEnvironmentVariables(_config.RecordingsFolder));
            Directory.CreateDirectory(target);
            return target;
        }
        catch (Exception ex)
        {
            Log.Write($"Cannot use RecordingsFolder '{_config.RecordingsFolder}': {ex.Message}; keeping the file in OBS's output folder.");
            return dir;
        }
    }

    private async Task<string?> RenameRecordingAsync(string outputPath)
    {
        var ext = Path.GetExtension(outputPath);
        if (Path.GetDirectoryName(outputPath) == null) return null;
        var dir = ResolveTargetDir(outputPath);

        var name = _config.FilenameTemplate
            .Replace("{date}", _recordingStartedAt.ToString("yyyy-MM-dd"))
            .Replace("{time}", _recordingStartedAt.ToString("HH-mm"))
            .Replace("{opponent}", Sanitize(Opponent ?? "unknown"))
            .Replace("{matchId}", MatchId ?? "0");

        // OBS may still be finalizing the file; wait until it is unlocked.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(outputPath))
                {
                    using (File.Open(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    var target = Path.Combine(dir, name + ext);
                    for (var i = 2; File.Exists(target); i++)
                        target = Path.Combine(dir, $"{name}_{i}{ext}");
                    File.Move(outputPath, target);
                    return target;
                }
            }
            catch (IOException) { /* still locked */ }
            await Task.Delay(1000);
        }
        return null;
    }

    // OBS's "automatically remux to mp4" writes a sibling file with the
    // original name after the recording stops.
    private async Task RenameRemuxedSiblingAsync(string originalPath, string targetWithoutExt, string renamedOriginal, string? recordId)
    {
        var dir = Path.GetDirectoryName(originalPath)!;
        var origBase = Path.GetFileNameWithoutExtension(originalPath);
        var origExt = Path.GetExtension(originalPath);

        // Remux time scales with file size; poll generously.
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            var sibling = Directory.EnumerateFiles(dir, origBase + ".*")
                .FirstOrDefault(f => !string.Equals(Path.GetExtension(f), origExt, StringComparison.OrdinalIgnoreCase));
            if (sibling != null)
            {
                try
                {
                    using (File.Open(sibling, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    var ext = Path.GetExtension(sibling);
                    var target = targetWithoutExt + ext;
                    for (var i = 2; File.Exists(target); i++)
                        target = $"{targetWithoutExt}_{i}{ext}";
                    File.Move(sibling, target);
                    Log.Write($"Renamed remuxed file to: {target}");
                    DeleteOriginalIfSafe(renamedOriginal, target);
                    if (recordId != null) _store.EndMatch(recordId, target);
                    return;
                }
                catch (IOException) { /* remux still in progress */ }
            }
            await Task.Delay(2000);
        }
        Log.Write("No remuxed sibling appeared within 5 minutes (expected if auto-remux is off).");
    }

    // A remux is a lossless container swap, so the copy should be roughly the
    // same size as the original. Only delete when that holds — a truncated or
    // failed remux must never cost the only good recording.
    private void DeleteOriginalIfSafe(string original, string remuxed)
    {
        if (!_config.DeleteOriginalAfterRemux) return;
        try
        {
            var origSize = new FileInfo(original).Length;
            var remuxSize = new FileInfo(remuxed).Length;
            if (remuxSize >= origSize * 0.9)
            {
                File.Delete(original);
                Log.Write($"Deleted original recording: {original}");
            }
            else
            {
                Log.Write($"Kept original: remuxed file looks incomplete ({remuxSize} vs {origSize} bytes).");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not delete original recording: {ex.Message}");
        }
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Trim().Replace(' ', '_');
        return s.Length > 60 ? s[..60] : s;
    }

    private static CancellationToken Timeout(int seconds) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    public void Dispose()
    {
        _obs?.Dispose();
        _gate.Dispose();
    }
}
