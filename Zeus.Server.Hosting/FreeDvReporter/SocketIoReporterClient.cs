// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// SocketIoReporterClient — a minimal, clean-room Engine.IO v4 / Socket.IO
// client over a single WebSocket, just enough to consume the FreeDV Reporter
// stations feed at qso.freedv.org and (opt-in) report the operator's own
// station back onto the public map.
//
// This is a from-scratch implementation of the public Engine.IO 4 / Socket.IO
// framing (no third-party Socket.IO library, no NuGet dependency, no GPL source
// copied). It uses only System.Net.WebSockets.ClientWebSocket and
// System.Text.Json so it builds cleanly on every platform Zeus targets
// (macOS / Windows / Linux x64+arm). It does NOT touch the radio / DSP / TX
// path; it only opens an outbound TLS WebSocket and dispatches parsed events
// (and, in report role, emits the operator's own freq/TX/message events).
//
// Roles:
//   "view"   — read-only observer (default). CONNECT payload identifies no
//              operator and Zeus only RECEIVES the roster. This is the
//              historical behaviour, preserved byte-for-byte.
//   "report" — opt-in. CONNECT payload carries the operator's callsign + grid +
//              version + os; Zeus still receives the full roster but ALSO emits
//              freq_change / tx_report / message_update / qsy_request events.
//
// Framing handled (text frames only):
//   Engine.IO OPEN   "0{...sid,pingInterval,pingTimeout...}"  (server -> client)
//   Engine.IO PING   "2"                                       (server -> client)
//   Engine.IO PONG   "3"                                       (client -> server)
//   Socket.IO CONNECT     "40{...}"  (both directions; ack carries the sid)
//   Socket.IO DISCONNECT  "41"       (namespace disconnect; server -> client)
//   Socket.IO EVENT       "42[name,payload]"          (both directions)
//   Socket.IO CONNECT_ERR "44{...}"  (rejected; we throw to force a reconnect)

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.FreeDvReporter;

/// <summary>
/// Operator identity used to build the Socket.IO CONNECT auth payload. When
/// <see cref="Role"/> is "report" the callsign / grid are broadcast publicly; in
/// "view" role they are ignored and only the role is sent.
/// </summary>
public sealed record ReporterIdentity(
    string Role,        // "view" | "report"
    string Callsign,
    string GridSquare,
    string Version,
    string Os);

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
    private readonly Func<ReporterIdentity> _identityProvider;
    private readonly ILogger _log;

    // Published as a plain string so the snapshot DTO can surface it verbatim.
    private volatile string _connectionState = "Disconnected";

    // The role we actually connected with this session (resolved at connect time
    // from the identity provider). Drives whether emits are live. Volatile so a
    // reader on another thread sees the latest handshake outcome.
    private volatile string _role = "view";

    // Our own per-connection session id, captured from the CONNECT ack
    // ("40{"sid":"..."}"). Null until acked / after disconnect. Exposed as MySid
    // so the frontend can highlight the operator's own roster row.
    private volatile string? _mySid;

    // Live socket reference so emits can run between handshakes. Guarded together
    // with the send semaphore below; null when no socket is open.
    private ClientWebSocket? _ws;

    // ClientWebSocket forbids concurrent SendAsync calls; this serialises every
    // send (handshake frames, pongs and report-role emits) onto one writer.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Cached last-published report state so a reconnect can re-publish the
    // operator's current freq / mode / TX / message without waiting for the next
    // radio event. Mutated under _stateLock; read when (re)connecting.
    private readonly object _stateLock = new();
    private long _lastFreqHz;
    private string _lastMode = "";
    private bool _lastTransmitting;
    private string _lastMessage = "";

    public SocketIoReporterClient(
        Action<string, JsonElement> onEvent,
        Func<ReporterIdentity> identityProvider,
        ILogger log)
    {
        _onEvent = onEvent;
        _identityProvider = identityProvider;
        _log = log;
    }

    /// <summary>Live link state: Disconnected | Connecting | Connected | Reconnecting.</summary>
    public string ConnectionState => _connectionState;

    /// <summary>The role of the current connection: "view" (read-only) or "report".</summary>
    public string Role => _role;

    /// <summary>True when connected in report role (the operator is on the public map).</summary>
    public bool Reporting => _role == "report" && _connectionState == "Connected";

    /// <summary>Our own session id while connected, else null.</summary>
    public string? MySid => _mySid;

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
        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(new Uri(ReporterUrl), ct).ConfigureAwait(false);

            // Publish the live socket so emits can find it between handshakes.
            _ws = ws;

            // 1) Engine.IO OPEN — a "0{...}" text frame with the session params.
            var open = await ReceiveMessageAsync(ws, ct).ConfigureAwait(false);
            if (open is null || open.Length == 0 || open[0] != '0')
                throw new InvalidOperationException("FreeDV Reporter: expected Engine.IO OPEN frame");
            // pingInterval/pingTimeout are informational here — in EIO4 the
            // server drives the heartbeat (sends "2", we answer "3"), so we just
            // react to pings rather than scheduling our own.

            // 2) Socket.IO CONNECT — join the default namespace with the
            //    identity-driven auth payload. View role keeps the historical
            //    {"protocol_version":2,"role":"view"} byte-for-byte; report role
            //    adds the operator's credentials.
            var identity = _identityProvider();
            _role = identity.Role == "report" ? "report" : "view";
            await SendTextAsync(ws, BuildConnectPacket(identity), ct).ConfigureAwait(false);

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
            _mySid = null;
            _ws = null;
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { /* best-effort close */ }
            }
            ws.Dispose();
        }
    }

    /// <summary>
    /// Force the current connection to drop so the service's reconnect loop
    /// re-handshakes with a fresh identity (e.g. after the operator toggles
    /// report mode). No-op if nothing is connected. The receive loop returns when
    /// the socket aborts, the caller waits its backoff, then reconnects.
    /// </summary>
    public void ForceReconnect()
    {
        var ws = _ws;
        if (ws is null) return;
        try { ws.Abort(); }
        catch { /* best-effort — the loop will surface the close */ }
    }

    // ----- report-role emits (no-ops unless connected in report role) -----

    /// <summary>Emit the operator's current VFO frequency (Hz). Cached for re-publish.</summary>
    public void EmitFreqChange(long hz)
    {
        lock (_stateLock) _lastFreqHz = hz;
        if (!Reporting) return;
        var payload = new Dictionary<string, object?> { ["freq"] = hz };
        EmitEvent("freq_change", payload);
    }

    /// <summary>Emit the operator's current mode + TX state. Cached for re-publish.</summary>
    public void EmitTxReport(string mode, bool transmitting)
    {
        lock (_stateLock)
        {
            _lastMode = mode ?? "";
            _lastTransmitting = transmitting;
        }
        if (!Reporting) return;
        var payload = new Dictionary<string, object?>
        {
            ["mode"] = mode ?? "",
            ["transmitting"] = transmitting,
        };
        EmitEvent("tx_report", payload);
    }

    /// <summary>Emit the operator's optional status message. Cached for re-publish.</summary>
    public void EmitMessageUpdate(string msg)
    {
        lock (_stateLock) _lastMessage = msg ?? "";
        if (!Reporting) return;
        var payload = new Dictionary<string, object?> { ["message"] = msg ?? "" };
        EmitEvent("message_update", payload);
    }

    /// <summary>
    /// Emit an rx_report — "I'm hearing <paramref name="rxCallsign"/> at
    /// <paramref name="snrDb"/> in <paramref name="mode"/>". The reporter server
    /// tags on our own sid and re-broadcasts, so other clients (and the public
    /// map) can populate the SNR column on our roster row. No-op unless connected
    /// in report role. Not cached — this is per-decode telemetry, not
    /// operator-configured state; a reconnect just resumes emitting when sync
    /// re-acquires.
    /// </summary>
    public void EmitRxReport(double snrDb, string? rxCallsign, string mode)
    {
        if (!Reporting) return;
        var payload = new Dictionary<string, object?>
        {
            // Integer, not a rounded double: the reference client (freedv-gui
            // FreeDVReporter::addReceiveRecord) sends yyjson add_int((int)snr),
            // and the server's handling of a float here is unverified — match
            // the reference wire format exactly.
            ["snr"] = (int)Math.Round(snrDb),
            ["callsign"] = rxCallsign ?? "",
            ["mode"] = mode ?? "",
        };
        EmitEvent("rx_report", payload);
    }

    /// <summary>Ask another station (by sid) to QSY to the given frequency.</summary>
    public void EmitQsy(string destSid, long freqHz, string message)
    {
        if (!Reporting || string.IsNullOrEmpty(destSid)) return;
        var payload = new Dictionary<string, object?>
        {
            ["dest_sid"] = destSid,
            ["message"] = message ?? "",
            ["frequency"] = freqHz,
        };
        EmitEvent("qsy_request", payload);
    }

    /// <summary>
    /// Re-publish the operator's cached freq / mode / TX / message after a
    /// (re)connect in report role. Called by the service once the link is up.
    /// </summary>
    public void RepublishState()
    {
        if (!Reporting) return;
        long freq;
        string mode, message;
        bool tx;
        lock (_stateLock)
        {
            freq = _lastFreqHz;
            mode = _lastMode;
            tx = _lastTransmitting;
            message = _lastMessage;
        }
        if (freq > 0)
            EmitEvent("freq_change", new Dictionary<string, object?> { ["freq"] = freq });
        EmitEvent("tx_report", new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["transmitting"] = tx,
        });
        if (!string.IsNullOrEmpty(message))
            EmitEvent("message_update", new Dictionary<string, object?> { ["message"] = message });
    }

    // Builds the Socket.IO CONNECT auth packet via System.Text.Json so the
    // callsign / grid are JSON-escaped (never hand-concatenated). View role emits
    // exactly the historical payload; report role adds operator fields.
    private static string BuildConnectPacket(ReporterIdentity id)
    {
        Dictionary<string, object?> auth;
        if (id.Role == "report")
        {
            auth = new Dictionary<string, object?>
            {
                ["protocol_version"] = 2,
                ["role"] = "report",
                ["callsign"] = id.Callsign,
                ["grid_square"] = id.GridSquare,
                ["version"] = string.IsNullOrWhiteSpace(id.Version) ? "Zeus" : id.Version,
                ["rx_only"] = false,
                ["os"] = id.Os,
            };
        }
        else
        {
            auth = new Dictionary<string, object?>
            {
                ["protocol_version"] = 2,
                ["role"] = "view",
            };
        }
        return "40" + JsonSerializer.Serialize(auth);
    }

    // Serialises one Socket.IO EVENT ("42[name,payload]") and ships it on the
    // live socket. Best-effort: a closed/absent socket or a serialisation hiccup
    // is swallowed — emits must never disturb the radio path.
    private void EmitEvent(string name, object payload)
    {
        var ws = _ws;
        if (ws is null) return;
        try
        {
            var frame = "42" + JsonSerializer.Serialize(new object[] { name, payload });
            _ = SendTextAsync(ws, frame, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "FreeDV Reporter: emit '{Event}' dropped", name);
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
            case '0': // Socket.IO CONNECT ack — namespace joined; ack carries our sid.
                _connectionState = "Connected";
                _mySid = ExtractSid(msg.AsSpan(2));
                _log.LogInformation("FreeDV Reporter: connected (role={Role}, sid={Sid})",
                    _role, _mySid ?? "?");
                break;

            case '1': // Socket.IO DISCONNECT (namespace) — treat as link down.
                _log.LogInformation("FreeDV Reporter: server sent namespace disconnect");
                _connectionState = "Disconnected";
                _mySid = null;
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

    // Parses the CONNECT-ack body ("{"sid":"..."}") for our session id. Tolerant
    // of a missing/odd body — returns null rather than throwing.
    private static string? ExtractSid(ReadOnlySpan<char> body)
    {
        if (body.IsEmpty || body[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(body.ToString());
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("sid", out var sid)
                && sid.ValueKind == JsonValueKind.String)
                return sid.GetString();
        }
        catch (JsonException) { /* no sid available */ }
        return null;
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

    // Serialises sends onto a single writer (ClientWebSocket forbids concurrent
    // SendAsync). Best-effort: a closed socket throws, which the caller logs/ignores.
    private async Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
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
