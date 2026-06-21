using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// Radio-side glue to the Cloudflare broker (Phase 3). When remote access is
/// enabled (a session password is set) and the operator is QRZ-signed-in, this
/// keeps a "host" WebSocket open to the broker for the operator's callsign and
/// turns each relayed WebRTC <c>offer</c> into an <c>answer</c> via
/// <see cref="RemoteWebRtcService"/> (gated by the SPAKE2+ password, ADR-0008).
///
/// Inert until the operator opts in (no password → no connection). The broker
/// only relays signaling; media is peer-to-peer and the password is proven
/// end-to-end at the radio, so the broker is never trusted.
///
/// NOTE: vanilla-ICE — candidates are embedded in the offer/answer SDP, so the
/// only relayed messages are offer→answer. Live verification needs the deployed
/// broker; <see cref="HandleSignalAsync"/> (the offer→answer bridge) is unit-tested.
/// </summary>
public sealed class RemoteBrokerClient : BackgroundService
{
    private static readonly TimeSpan ReconnectMin = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReconnectMax = TimeSpan.FromSeconds(30);

    private readonly RemotePasswordStore _passwords;
    private readonly RemoteWebRtcService _rtc;
    private readonly QrzService _qrz;
    private readonly ILogger<RemoteBrokerClient> _log;
    private readonly string _brokerUrl;

    public RemoteBrokerClient(
        RemotePasswordStore passwords, RemoteWebRtcService rtc, QrzService qrz,
        ILogger<RemoteBrokerClient> log)
    {
        _passwords = passwords;
        _rtc = rtc;
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

        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var json = await ReceiveTextAsync(socket, buffer, ct);
            if (json is null) break; // close frame

            var reply = await HandleSignalAsync(json, ct);
            if (reply is not null)
            {
                var bytes = Encoding.UTF8.GetBytes(reply);
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
    }

    /// <summary>
    /// Bridge one signaling message: an <c>offer</c> becomes an <c>answer</c>
    /// (tagged with the broker's <c>clientId</c>); everything else is ignored.
    /// Returns the JSON to send back, or null. Never throws — a bad offer just
    /// yields no answer (fails closed).
    /// </summary>
    internal Task<string?> HandleSignalAsync(string json, CancellationToken ct)
        => BridgeSignalAsync(_rtc, json, _log, ct);

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
