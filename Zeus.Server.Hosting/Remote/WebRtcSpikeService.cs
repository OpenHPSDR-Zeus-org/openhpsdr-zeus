using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;

namespace Zeus.Server.Hosting.Remote;

/// <summary>Request body for the dev-only <c>POST /api/rtc/spike/offer</c> endpoint.</summary>
public sealed record RtcSpikeOffer(string Sdp);

/// <summary>
/// Phase-0 spike for the WebRTC remote-access data plane
/// (see <c>docs/designs/remote-access-webrtc.md</c>). This is NOT the production
/// transport — it answers a single SDP offer and echoes binary DataChannel
/// messages straight back, so we can prove the .NET↔browser WebRTC path works on
/// our exact stack (SIPSorcery on net10, all target arches) and measure the
/// added round-trip latency before building the real transport abstraction.
///
/// The echo handler does the absolute minimum (send the received bytes back on
/// the same channel) so the measured RTT reflects the transport, not our code.
/// </summary>
public sealed class WebRtcSpikeService
{
    private readonly ILogger<WebRtcSpikeService> _log;
    private readonly ConcurrentDictionary<Guid, RTCPeerConnection> _peers = new();

    public WebRtcSpikeService(ILogger<WebRtcSpikeService> log) => _log = log;

    public int ActivePeers => _peers.Count;

    /// <summary>
    /// Accept a remote SDP offer, wire an echo DataChannel, and return a
    /// self-contained ("vanilla ICE") answer SDP with host candidates embedded.
    /// </summary>
    public async Task<string> CreateEchoAnswerAsync(string offerSdp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(offerSdp))
            throw new ArgumentException("offer SDP is empty", nameof(offerSdp));

        var id = Guid.NewGuid();
        var config = new RTCConfiguration
        {
            // STUN only for the spike; the production path adds Cloudflare TURN.
            iceServers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.cloudflare.com:3478" },
            },
        };

        var pc = new RTCPeerConnection(config);
        _peers[id] = pc;

        pc.ondatachannel += dc =>
        {
            _log.LogInformation("rtc.spike[{Id}] datachannel opened label={Label}", id, dc.label);
            dc.onmessage += (chan, _, data) => chan.send(data); // echo — minimal added latency
        };

        pc.onconnectionstatechange += state =>
        {
            _log.LogInformation("rtc.spike[{Id}] state={State}", id, state);
            if (state is RTCPeerConnectionState.closed
                or RTCPeerConnectionState.failed
                or RTCPeerConnectionState.disconnected)
            {
                if (_peers.TryRemove(id, out var dead))
                    dead.close();
            }
        };

        var setResult = pc.setRemoteDescription(
            new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp });
        if (setResult != SetDescriptionResultEnum.OK)
        {
            _peers.TryRemove(id, out _);
            pc.close();
            throw new InvalidOperationException($"setRemoteDescription failed: {setResult}");
        }

        var answer = pc.createAnswer(null);
        await pc.setLocalDescription(answer);
        await WaitForIceGatheringAsync(pc, TimeSpan.FromMilliseconds(750), ct);

        return pc.localDescription.sdp.ToString();
    }

    /// <summary>Close all active spike peers (test teardown / shutdown).</summary>
    public void CloseAll()
    {
        foreach (var kv in _peers)
            if (_peers.TryRemove(kv.Key, out var pc))
                pc.close();
    }

    private static async Task WaitForIceGatheringAsync(
        RTCPeerConnection pc, TimeSpan timeout, CancellationToken ct)
    {
        if (pc.iceGatheringState == RTCIceGatheringState.complete)
            return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChange(RTCIceGatheringState s)
        {
            if (s == RTCIceGatheringState.complete)
                tcs.TrySetResult();
        }

        pc.onicegatheringstatechange += OnChange;
        try
        {
            if (pc.iceGatheringState == RTCIceGatheringState.complete)
                return;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            await using (timeoutCts.Token.Register(() => tcs.TrySetResult()))
                await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            pc.onicegatheringstatechange -= OnChange;
        }
    }
}
