using System.Net.WebSockets;
using System.Text.Json;
using AcBridge.Models;

namespace AcBridge.Telemetry;

/// <summary>
/// Ein Client = eine Schleife. Nachrichten (JSON, Text-Frames):
///   → { "type": "status",    "data": BridgeStatus }   beim Verbinden und wenn sich Connected ändert
///   → { "type": "session",   "data": SessionInfo|null } beim Verbinden und bei Session-Wechsel
///   → { "type": "telemetry", "data": TelemetryFrame } mit der gewählten Rate, nur bei neuen Daten
///   ← { "hz": 10 }  Client kann die Rate zur Laufzeit ändern
/// </summary>
public sealed class TelemetryWebSocket(TelemetryStore store, TelemetryOptions options, ILogger<TelemetryWebSocket> log)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("WebSocket-Request erwartet");
            return;
        }

        var hz = ParseHz(ctx.Request.Query["hz"]);
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        var remote = ctx.Connection.RemoteIpAddress;
        log.LogInformation("Client {Remote} verbunden ({Hz} Hz), aktiv: {Count}", remote, hz, store.ClientConnected());

        var receive = ReceiveLoopAsync(ws, newHz => hz = newHz, cts);
        try
        {
            long lastSeq = -1;
            var lastSessionVersion = -1;
            bool? lastConnected = null;

            while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                if (lastConnected != store.Connected)
                {
                    lastConnected = store.Connected;
                    await SendAsync(ws, "status", store.GetStatus(), cts.Token);
                }

                var sv = store.SessionVersion;
                if (sv != lastSessionVersion)
                {
                    lastSessionVersion = sv;
                    await SendAsync(ws, "session", store.Session, cts.Token);
                }

                if (store.Latest is { } frame && frame.Seq != lastSeq)
                {
                    lastSeq = frame.Seq;
                    await SendAsync(ws, "telemetry", frame, cts.Token);
                }

                await Task.Delay(TimeSpan.FromSeconds(1.0 / hz), cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            cts.Cancel();
            try { await receive; } catch { /* egal */ }

            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                catch { /* Verbindung schon weg */ }
            }
            log.LogInformation("Client {Remote} getrennt, aktiv: {Count}", remote, store.ClientDisconnected());
        }
    }

    private async Task ReceiveLoopAsync(WebSocket ws, Action<int> setHz, CancellationTokenSource cts)
    {
        var buffer = new byte[4096];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage) continue;

                try
                {
                    using var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
                    if (doc.RootElement.TryGetProperty("hz", out var h) && h.TryGetInt32(out var v))
                        setHz(Math.Clamp(v, 1, options.MaxStreamHz));
                }
                catch (JsonException) { /* unbekannte Nachricht ignorieren */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally { cts.Cancel(); }
    }

    private int ParseHz(string? raw) =>
        int.TryParse(raw, out var v) ? Math.Clamp(v, 1, options.MaxStreamHz) : options.DefaultStreamHz;

    private static Task SendAsync<T>(WebSocket ws, string type, T data, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new WsMessage<T>(type, data), Json);
        return ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }
}
