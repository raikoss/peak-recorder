using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PeakRecorder;

/// <summary>
/// Minimal obs-websocket v5 client: connect + identify (with auth), then
/// sequential requests. Events from OBS are ignored.
/// </summary>
public sealed class ObsWebSocketClient : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private int _requestCounter;

    public bool IsConnected => _ws.State == WebSocketState.Open;

    public async Task ConnectAsync(int port, string? password, CancellationToken ct)
    {
        await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), ct);

        var hello = await ReceiveMessageAsync(ct);
        if (hello?["op"]?.GetValue<int>() != 0)
            throw new InvalidOperationException("Expected Hello (op 0) from obs-websocket.");

        var identifyData = new JsonObject { ["rpcVersion"] = 1 };
        var authInfo = hello["d"]?["authentication"];
        if (authInfo != null)
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException("obs-websocket requires a password but none is configured.");
            var challenge = authInfo["challenge"]!.GetValue<string>();
            var salt = authInfo["salt"]!.GetValue<string>();
            identifyData["authentication"] = ComputeAuth(password, salt, challenge);
        }

        await SendAsync(new JsonObject { ["op"] = 1, ["d"] = identifyData }, ct);

        while (true)
        {
            var msg = await ReceiveMessageAsync(ct);
            var op = msg?["op"]?.GetValue<int>();
            if (op == 2) break; // Identified
            if (op is null) throw new InvalidOperationException("Connection closed during identify (wrong password?).");
        }
    }

    private static string ComputeAuth(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    /// <summary>Sends a request and waits for its response, skipping event messages.</summary>
    public async Task<JsonNode?> RequestAsync(string requestType, JsonObject? requestData, CancellationToken ct)
    {
        var requestId = $"req-{Interlocked.Increment(ref _requestCounter)}";
        var payload = new JsonObject
        {
            ["op"] = 6,
            ["d"] = new JsonObject
            {
                ["requestType"] = requestType,
                ["requestId"] = requestId,
                ["requestData"] = requestData ?? new JsonObject(),
            },
        };
        await SendAsync(payload, ct);

        while (true)
        {
            var msg = await ReceiveMessageAsync(ct) ?? throw new InvalidOperationException("Connection closed waiting for response.");
            if (msg["op"]?.GetValue<int>() != 7) continue; // skip events (op 5) etc.
            var d = msg["d"]!;
            if (d["requestId"]?.GetValue<string>() != requestId) continue;

            var status = d["requestStatus"]!;
            if (!status["result"]!.GetValue<bool>())
            {
                var code = status["code"]?.GetValue<int>();
                var comment = status["comment"]?.GetValue<string>();
                throw new ObsRequestException(requestType, code ?? 0, comment);
            }
            return d["responseData"];
        }
    }

    private async Task SendAsync(JsonObject payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private async Task<JsonNode?> ReceiveMessageAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await _ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray()));
    }

    public void Dispose() => _ws.Dispose();
}

public class ObsRequestException(string requestType, int code, string? comment)
    : Exception($"OBS request {requestType} failed (code {code}): {comment ?? "no details"}")
{
    public int Code { get; } = code;
}
