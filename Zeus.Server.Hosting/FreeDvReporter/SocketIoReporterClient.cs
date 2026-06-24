// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// SocketIoReporterClient — a minimal, clean-room Engine.IO v4 / Socket.IO
// client over a single WebSocket, just enough to consume the FreeDV Reporter
// stations feed at qso.freedv.org as a read-only ("view") observer.
//
// This is a from-scratch implementation of the public Engine.IO 4 / Socket.IO
// framing (no third-party Socket.IO library, no NuGet dependency, no GPL source
// copied). It uses only System.Net.WebSockets.ClientWebSocket and
// System.Text.Json so it builds cleanly on every platform Zeus targets
// (macOS / Windows / Linux x64+arm). It does NOT touch the radio / DSP / TX
// path; it only opens an outbound TLS WebSocket and dispatches parsed events.
//
// Framing handled (text frames only):
//   Engine.IO OPEN   "0{...sid,pingInterval,pingTimeout...}"  (server -> client)
//   Engine.IO PING   "2"                                       (server -> client)
//   Engine.IO PONG   "3"                                       (client -> server)
//   Socket.IO CONNECT     "40{...}"  (both directions; ack carries the sid)
//   Socket.IO DISCONNECT  "41"       (namespace disconnect; server -> client)
//   Socket.IO EVENT       "42[name,payload]"                   (server -> client)
//   Socket.IO CONNECT_ERR "44{...}"  (rejected; we throw to force a reconnect)

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.FreeDvReporter;

/// <summary>
/// Single-WebSocket Engine.IO v4 / Socket.IO client for the FreeDV Reporter
/// feed. One instance is reused across reconnects: each call to
/// <see cref="RunLoopAsync"/> opens a fresh socket, performs the handshake, and
/// pumps the receive loop until the socket closes, the token cancels, or an
/// error is thrown — at which point the caller waits and calls again.
/// </summary>
public sealed class SocketIoReporterClient
{
    private const string ReporterUrl = "wss://qso.freedv.org/socket.io/?EIO=4&transport=websocket";

    private readonly Action<string, JsonElement> _onEvent;
    private readonly ILogger _log;

    // Published as a plain string so the snapshot DTO can surface it verbatim.
    private volatile string _connectionState = "Disconnected";

    public SocketIoReporterClient(Action<string, JsonElement> onEvent, ILogger log)
    {
        _onEvent = onEvent;
        _log = log;
    }

    /// <summary>Live link state: Disconnected | Connecting | Connected | Reconnecting.</summary>
    public string ConnectionState => _connectionState;

    /// <summary>
    /// Connects, performs the Engine.IO/Socket.IO handshake, then pumps the
    /// receive loop — answering server pings and dispatching events — until the
    /// socket closes, an error occurs, or <paramref name="ct"/> cancels. Returns
    /// (or throws) so the caller can wait and reconnect. Set <paramref name="reconnect"/>
    /// so the reported state is "Reconnecting" rather than "Connecting".
    /// </summary>
    public async Task RunLoopAsync(CancellationToken ct, bool reconnect = false)
    {
        _connectionState = reconnect ? "Reconnecting" : "Connecting";
        using var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri(ReporterUrl), ct).ConfigureAwait(false);

            // 1) Engine.IO OPEN — a "0{...}" text frame with the session params.
            var open = await ReceiveMessageAsync(ws, ct).ConfigureAwait(false);
            if (open is null || open.Length == 0 || open[0] != '0')
                throw new InvalidOperationException("FreeDV Reporter: expected Engine.IO OPEN frame");
            // pingInterval/pingTimeout are informational here — in EIO4 the
            // server drives the heartbeat (sends "2", we answer "3"), so we just
            // react to pings rather than scheduling our own.

            // 2) Socket.IO CONNECT — join the default namespace as a read-only viewer.
            await SendTextAsync(ws, "40{\"protocol_version\":2,\"role\":\"view\"}", ct).ConfigureAwait(false);

            // 3) Pump. The CONNECT ack ("40{...}") or reject ("44{...}") arrives
            //    inline in the receive loop; a stray ping may precede it.
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var msg = await ReceiveMessageAsync(ws, ct).ConfigureAwait(false);
                if (msg is null) break; // close received
                if (msg.Length == 0) continue;
                DispatchPacket(ws, msg, ct);
            }
        }
        finally
        {
            _connectionState = "Disconnected";
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { /* best-effort close */ }
            }
        }
    }

    // Routes one decoded Engine.IO / Socket.IO text message by packet prefix.
    private void DispatchPacket(ClientWebSocket ws, string msg, CancellationToken ct)
    {
        char eio = msg[0];

        // Engine.IO PING — answer PONG immediately, at any time in the loop
        // (including before the Socket.IO connect-ack).
        if (eio == '2')
        {
            // Fire-and-forget the pong; failures surface on the next receive.
            _ = SendTextAsync(ws, "3", ct);
            return;
        }

        // Engine.IO MESSAGE (Socket.IO packet) — prefix '4', then a Socket.IO
        // packet-type digit.
        if (eio != '4' || msg.Length < 2) return;

        char sio = msg[1];
        switch (sio)
        {
            case '0': // Socket.IO CONNECT ack — namespace joined.
                _connectionState = "Connected";
                _log.LogInformation("FreeDV Reporter: connected");
                break;

            case '1': // Socket.IO DISCONNECT (namespace) — treat as link down.
                _log.LogInformation("FreeDV Reporter: server sent namespace disconnect");
                _connectionState = "Disconnected";
                break;

            case '2': // Socket.IO EVENT — "42[name, payload]".
                DispatchEvent(msg.AsSpan(2));
                break;

            case '4': // Socket.IO CONNECT_ERROR — rejected; force a reconnect.
                throw new InvalidOperationException(
                    "FreeDV Reporter: connect rejected by server (" + msg + ")");

            default:
                // 3 = ACK, 5 = BINARY_EVENT, 6 = BINARY_ACK — not used by the feed.
                break;
        }
    }

    // Parses "[eventName, payload]" (the body after the "42" prefix) and invokes
    // the handler. Tolerant of malformed bodies and a missing payload element.
    private void DispatchEvent(ReadOnlySpan<char> body)
    {
        string json = body.ToString();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                return;

            string? name = root[0].ValueKind == JsonValueKind.String ? root[0].GetString() : null;
            if (string.IsNullOrEmpty(name))
                return;

            // Some events (rare) carry no payload object; pass an undefined element.
            JsonElement payload = root.GetArrayLength() > 1 ? root[1] : default;

            // Clone so the JsonElement outlives the using-disposed document.
            _onEvent(name!, payload.ValueKind == JsonValueKind.Undefined ? default : payload.Clone());
        }
        catch (JsonException ex)
        {
            _log.LogDebug(ex, "FreeDV Reporter: dropped malformed event frame");
        }
    }

    private static async Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    // Accumulates frames until EndOfMessage, then UTF-8 decodes the whole
    // message. Returns null when the server initiates a close.
    private static async Task<string?> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }
}
