using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Net;
using Zeus.Server.Hosting.Remote;

namespace Zeus.Server.Tests;

/// <summary>
/// Phase-0 WebRTC spike (docs/designs/remote-access-webrtc.md): proves the
/// SIPSorcery DataChannel path works end-to-end on our net10 stack by standing up
/// a second in-process peer as the "browser" client, connecting it to
/// <see cref="WebRtcSpikeService"/>, and round-tripping a binary message through a
/// real ICE→DTLS→SCTP DataChannel. De-risks the data plane before Phase 1.
/// </summary>
public sealed class WebRtcSpikeTests
{
    [Fact]
    public async Task EchoDataChannel_RoundTripsBinaryThroughRealPeerConnection()
    {
        var service = new WebRtcSpikeService(NullLogger<WebRtcSpikeService>.Instance);

        // Host-candidate ICE only — no external STUN dependency for the in-process
        // loopback path, so the test is fast and runs offline.
        var client = new RTCPeerConnection(new RTCConfiguration { iceServers = new List<RTCIceServer>() });

        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var echoed = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        var dc = await client.createDataChannel("spike");
        dc.onopen += () => opened.TrySetResult();
        dc.onmessage += (_, _, data) => echoed.TrySetResult(data);

        // Client offers (mirrors the browser); service answers + wires the echo.
        var offer = client.createOffer(null);
        await client.setLocalDescription(offer);
        await WaitForIceGatheringAsync(client, TimeSpan.FromMilliseconds(750));

        var answerSdp = await service.CreateEchoAnswerAsync(client.localDescription.sdp.ToString());

        var setResult = client.setRemoteDescription(
            new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp });
        Assert.Equal(SetDescriptionResultEnum.OK, setResult);

        await opened.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var payload = Encoding.UTF8.GetBytes("zeus-webrtc-spike");
        var sw = Stopwatch.StartNew();
        dc.send(payload);
        var received = await echoed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sw.Stop();

        Assert.Equal(payload, received);
        Assert.Equal(1, service.ActivePeers);

        // Informational: in-process DataChannel round-trip latency.
        Console.WriteLine($"[spike] DataChannel echo RTT: {sw.Elapsed.TotalMilliseconds:F2} ms");

        client.close();
        service.CloseAll();
    }

    private static async Task WaitForIceGatheringAsync(RTCPeerConnection pc, TimeSpan timeout)
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
            using var cts = new CancellationTokenSource(timeout);
            await using (cts.Token.Register(() => tcs.TrySetResult()))
                await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            pc.onicegatheringstatechange -= OnChange;
        }
    }
}
