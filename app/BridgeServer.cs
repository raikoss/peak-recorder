using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PeakRecorder;

/// <summary>
/// Tiny local HTTP server the browser extension talks to.
///   POST /event  {type, matchId?, opponent?, players?}
///   GET  /status -> {recording, opponent, matchId}
/// </summary>
public sealed class BridgeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly RecorderService _recorder;
    private readonly CancellationTokenSource _cts = new();

    public BridgeServer(RecorderService recorder, int port)
    {
        _recorder = recorder;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(LoopAsync);
        Log.Write($"Bridge server listening on {_listener.Prefixes.First()}");
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch when (_cts.IsCancellationRequested) { return; }
            catch (Exception ex) { Log.Write($"Bridge accept error: {ex.Message}"); continue; }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        // The extension has host permission so CORS isn't strictly needed,
        // but sendBeacon and future callers appreciate it.
        res.Headers["Access-Control-Allow-Origin"] = "*";
        res.Headers["Access-Control-Allow-Headers"] = "Content-Type";

        try
        {
            var path = ctx.Request.Url?.AbsolutePath.Trim('/').ToLowerInvariant();
            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
            }
            else if (path == "status")
            {
                await WriteJsonAsync(res, new
                {
                    recording = _recorder.IsRecording,
                    opponent = _recorder.Opponent,
                    matchId = _recorder.MatchId,
                });
            }
            else if (path == "event" && ctx.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = JsonNode.Parse(await reader.ReadToEndAsync())?.AsObject();
                var type = body?["type"]?.GetValue<string>();
                if (type == null)
                {
                    res.StatusCode = 400;
                    await WriteJsonAsync(res, new { error = "missing type" });
                }
                else
                {
                    var players = body!["players"]?.AsArray()
                        .Select(n => n?.GetValue<string>())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!)
                        .ToArray();
                    var resultStr = body["result"]?.GetValue<string>();
                    var matchResult = resultStr is "W" or "L"
                        ? new MatchResultInfo(resultStr,
                            body["gamesWon"]?.GetValue<int>() ?? 0,
                            body["gamesLost"]?.GetValue<int>() ?? 0)
                        : null;
                    // gamesWon/gamesLost double as the final tally on a result
                    // event and the live score mid-set; only the latter feeds
                    // GameInfo so the two never fight over the same fields.
                    var gameInfo = new GameInfo(
                        body["stage"]?.GetValue<string>(),
                        body["myCharacter"]?.GetValue<string>(),
                        body["opponentCharacter"]?.GetValue<string>(),
                        matchResult == null ? body["gamesWon"]?.GetValue<int>() : null,
                        matchResult == null ? body["gamesLost"]?.GetValue<int>() : null,
                        body["gameInProgress"]?.GetValue<bool>());
                    // Fire-and-forget so slow OBS startup doesn't stall the extension.
                    _ = _recorder.HandleEventAsync(
                        type,
                        body["matchId"]?.GetValue<string>(),
                        body["opponent"]?.GetValue<string>(),
                        players,
                        matchResult,
                        gameInfo.IsEmpty ? null : gameInfo);
                    await WriteJsonAsync(res, new { accepted = true });
                }
            }
            else if (path == "register" && ctx.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = JsonNode.Parse(await reader.ReadToEndAsync())?.AsObject();
                var extensionId = body?["extensionId"]?.GetValue<string>();
                if (extensionId != null)
                {
                    try { NativeHost.Register(extensionId); }
                    catch (Exception ex) { Log.Write($"Native host registration failed: {ex.Message}"); }
                }
                await WriteJsonAsync(res, new { accepted = extensionId != null });
            }
            else if (path == "dump" && ctx.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                var dumpDir = Path.Combine(Config.Dir, "dumps");
                Directory.CreateDirectory(dumpDir);
                var file = Path.Combine(dumpDir, $"dump-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                await File.WriteAllTextAsync(file, body);
                Log.Write($"Page dump saved: {file}");
                await WriteJsonAsync(res, new { accepted = true, path = file });
            }
            else
            {
                res.StatusCode = 404;
                await WriteJsonAsync(res, new { error = "not found" });
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Bridge request error: {ex.Message}");
            try { res.StatusCode = 500; } catch { }
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res, object payload)
    {
        res.ContentType = "application/json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await res.OutputStream.WriteAsync(bytes);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _listener.Close();
    }
}
