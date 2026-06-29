using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Server.Hosting.Support;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// Radio-side glue to the Cloudflare broker (Phase 3). When remote access is
/// enabled (a session password is set) and the operator is QRZ-signed-in, this
/// keeps a "host" WebSocket open to the broker for the operator's callsign and
/// turns each relayed WebRTC <c>offer</c> into an <c>answer</c> via
/// <see cref="RemoteWebRtcService"/> (gated by the SPAKE2+ password, ADR-0008).
///
/// It ALSO carries the maintainer-support control plane (remote-diag P3): an
/// inbound <c>support-request</c> is parked on <see cref="SupportRequestCoordinator"/>
/// (which prompts the operator); when the operator approves, the resulting grant is
/// pushed back to the broker as <c>support-grant</c> so the waiting admin sends its
/// offer; a support <c>offer</c> is answered via <see cref="SupportWebRtcService"/>
/// (read-only, grant-gated) instead of the operator tunnel.
///
/// Inert until the operator opts in (no password → no connection). The broker
/// only relays signaling; media is peer-to-peer and the password/grant are proven
/// end-to-end at the radio, so the broker is never trusted.
///
/// NOTE: vanilla-ICE — candidates are embedded in the offer/answer SDP. Live
/// verification needs the deployed broker; the offer→answer + support dispatch
/// (<see cref="HandleSignalAsync"/>) is unit-tested headlessly.
/// </summary>
public sealed class RemoteBrokerClient : BackgroundService
{
    private static readonly TimeSpan ReconnectMin = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReconnectMax = TimeSpan.FromSeconds(30);

    private readonly RemotePasswordStore _passwords;
    private readonly RemoteWebRtcService _rtc;
    private readonly SupportWebRtcService _support;
    private readonly SupportRequestCoordinator _coord;
    private readonly QrzService _qrz;
    private readonly ILogger<RemoteBrokerClient> _log;
    private readonly string _brokerUrl;

    public RemoteBrokerClient(
        RemotePasswordStore passwords, RemoteWebRtcService rtc,
        SupportWebRtcService support, SupportRequestCoordinator coord,
        QrzService qrz, ILogger<RemoteBrokerClient> log)
    {
        _passwords = passwords;
        _rtc = rtc;
        _support = support;
        _coord = coord;
        _qrz = qrz;
        _log = log;
        _brokerUrl = Environment.GetEnvironmentVariable("ZEUS_REMOTE_BROKER_URL")
            ?? "wss://remote.openhpsdrzeus.com/signal?role=host";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = ReconnectMin;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Remote access off (no password) → stay disconnected.
            if (!_passwords.HasPassword())
            {
                await Task.Delay(ReconnectMax, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
                continue;
            }

            var identity = await TryGetIdentityAsync(stoppingToken);
            if (identity is null)
            {
                await DelayQuietly(backoff, stoppingToken);
                continue;
            }

            try
            {
                await RunConnectionAsync(identity.Value.Callsign, identity.Value.SessionKey, stoppingToken);
                backoff = ReconnectMin;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "remote broker connection ended; retrying in {Delay}", backoff);
            }

            await DelayQuietly(backoff, stoppingToken);
            backoff = NextBackoff(backoff);
        }
    }

    private async Task<(string Callsign, string SessionKey)?> TryGetIdentityAsync(CancellationToken ct)
    {
        try { return await _qrz.GetChatIdentityAsync(ct); }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "remote broker: no QRZ identity yet");
            return null;
        }
    }

    private async Task RunConnectionAsync(string callsign, string sessionKey, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-QRZ-Session", sessionKey);
        socket.Options.SetRequestHeader("X-QRZ-Callsign", callsign);

        _log.LogInformation("remote broker: connecting to {Url} as {Callsign}", _brokerUrl, callsign);
        await socket.ConnectAsync(new Uri(_brokerUrl), ct);
        _log.LogInformation("remote broker: host online for {Callsign}", callsign);

        // All outbound frames go through one queue + one send loop so the receive
        // loop's answers and the async support-grant pushes never call SendAsync
        // concurrently (ClientWebSocket forbids overlapping sends).
        var outbound = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true });

        // Operator approved a support request → push the grant to the waiting admin
        // so it sends its WebRTC offer. Subscribed only while this connection is up.
        void OnApproved(SupportGrant g)
            => outbound.Writer.TryWrite(
                JsonSerializer.Serialize(new { t = "support-grant", requestId = g.RequestId }));
        _coord.Approved += OnApproved;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var recv = ReceiveLoopAsync(socket, outbound.Writer, linked.Token);
            var send = SendLoopAsync(socket, outbound.Reader, linked.Token);
            await Task.WhenAny(recv, send).ConfigureAwait(false);
            linked.Cancel(); // whichever ended, stop the other
            await Task.WhenAll(
                recv.ContinueWith(_ => { }, CancellationToken.None),
                send.ContinueWith(_ => { }, CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            _coord.Approved -= OnApproved;
            outbound.Writer.TryComplete();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, ChannelWriter<string> outbound, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[64 * 1024];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var json = await ReceiveTextAsync(socket, buffer, ct);
                if (json is null) break; // close frame

                var reply = await HandleSignalAsync(json, ct);
                if (reply is not null) outbound.TryWrite(reply);
            }
        }
        finally
        {
            outbound.TryComplete();
        }
    }

    private static async Task SendLoopAsync(ClientWebSocket socket, ChannelReader<string> outbound, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in outbound.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var bytes = Encoding.UTF8.GetBytes(msg);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>
    /// Dispatch one inbound signaling message. Maintainer-support traffic
    /// (<c>support-request</c> and a support <c>offer</c>) routes to the read-only,
    /// grant-gated support path; everything else is the operator tunnel's
    /// password-gated offer→answer bridge. Returns the JSON to send back, or null.
    /// Never throws — malformed or unauthorised input simply yields no reply
    /// (fails closed).
    /// </summary>
    internal Task<string?> HandleSignalAsync(string json, CancellationToken ct)
    {
        bool isSupport;
        try
        {
            using var doc = JsonDocument.Parse(json);
            isSupport = IsSupportMessage(doc.RootElement);
        }
        catch
        {
            return Task.FromResult<string?>(null); // unparseable — ignore
        }

        return isSupport
            ? HandleSupportSignalAsync(_support, _coord, json, _log, ct)
            : BridgeSignalAsync(_rtc, json, _log, ct);
    }

    /// <summary>True for the maintainer-support message types (a <c>support-request</c>,
    /// or an <c>offer</c> explicitly tagged <c>support:true</c>).</summary>
    internal static bool IsSupportMessage(JsonElement root)
    {
        var t = root.TryGetProperty("t", out var tEl) ? tEl.GetString() : null;
        if (t == "support-request") return true;
        return t == "offer"
            && root.TryGetProperty("support", out var sEl)
            && sEl.ValueKind == JsonValueKind.True;
    }

    internal static async Task<string?> BridgeSignalAsync(
        RemoteWebRtcService rtc, string json, ILogger log, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("t", out var tEl) && tEl.GetString() == "offer"
                && root.TryGetProperty("sdp", out var sdpEl))
            {
                var clientId = root.TryGetProperty("clientId", out var cid) ? cid.GetString() : null;
                var answer = await rtc.ConnectAsync(sdpEl.GetString() ?? "", ct);
                return JsonSerializer.Serialize(new Dictionary<string, string?>
                {
                    ["t"] = "answer",
                    ["sdp"] = answer,
                    ["clientId"] = clientId,
                });
            }
        }
        catch (RemoteAccessDisabledException)
        {
            log.LogWarning("remote broker: offer arrived but no password set — ignoring");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "remote broker: failed to answer offer");
        }
        return null;
    }

    /// <summary>
    /// Handle a maintainer-support signaling message:
    ///   • <c>support-request</c> → park it on the coordinator (prompts the operator);
    ///     no reply (the grant, if approved, is pushed later as <c>support-grant</c>).
    ///   • support <c>offer</c> → answer via the grant-gated, read-only support
    ///     service, echoing <c>clientId</c> and tagging the answer <c>support:true</c>.
    /// Never throws; an unauthorised offer (no operator grant) yields no answer.
    /// </summary>
    internal static async Task<string?> HandleSupportSignalAsync(
        SupportWebRtcService support, SupportRequestCoordinator coord,
        string json, ILogger log, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var t = root.TryGetProperty("t", out var tEl) ? tEl.GetString() : null;

            if (t == "support-request")
            {
                var requestId = root.TryGetProperty("requestId", out var r) ? r.GetString() : null;
                var admin = root.TryGetProperty("admin", out var a) ? a.GetString() : null;
                if (!string.IsNullOrEmpty(requestId))
                    coord.RegisterRequest(requestId!, admin ?? "");
                return null; // operator must Allow before anything flows
            }

            if (t == "offer")
            {
                var requestId = root.TryGetProperty("requestId", out var r) ? r.GetString() : null;
                var clientId = root.TryGetProperty("clientId", out var c) ? c.GetString() : null;
                var sdp = root.TryGetProperty("sdp", out var s) ? s.GetString() : null;
                if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(sdp))
                    return null;

                var answer = await support.ConnectSupportAsync(requestId!, sdp!, ct);
                return JsonSerializer.Serialize(new { t = "answer", sdp = answer, clientId, support = true });
            }
        }
        catch (SupportNotAuthorizedException)
        {
            log.LogWarning("remote broker: support offer without an operator grant — ignoring");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "remote broker: failed to answer support offer");
        }
        return null;
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, byte[] buffer, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                return null;
            }
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static async Task DelayQuietly(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private static TimeSpan NextBackoff(TimeSpan current)
    {
        var next = TimeSpan.FromMilliseconds(current.TotalMilliseconds * 2);
        return next > ReconnectMax ? ReconnectMax : next;
    }
}
