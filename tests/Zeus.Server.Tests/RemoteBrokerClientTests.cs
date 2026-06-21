using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Net;
using Zeus.Server.Hosting.Remote;

namespace Zeus.Server.Tests;

/// <summary>
/// Tests the radio-side broker bridge: a relayed WebRTC <c>offer</c> becomes an
/// <c>answer</c> (carrying the broker's clientId) via the password-gated
/// transport; non-offer signaling is ignored. The WebSocket-to-broker plumbing
/// needs the deployed broker; this covers the offer→answer logic headlessly.
/// </summary>
public sealed class RemoteBrokerClientTests
{
    [Fact]
    public async Task BridgeSignal_OfferBecomesAnswer_WithClientId()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zeus-rbc-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new RemotePasswordStore(NullLogger<RemotePasswordStore>.Instance, path);
            store.Set("broker-test-password");
            var rtc = new RemoteWebRtcService(store, NullLogger<RemoteWebRtcService>.Instance);

            var offerSdp = await MakeOfferAsync();
            var json = JsonSerializer.Serialize(new { t = "offer", sdp = offerSdp, clientId = "abc-123" });

            var reply = await RemoteBrokerClient.BridgeSignalAsync(rtc, json, NullLogger.Instance, default);

            Assert.NotNull(reply);
            using var doc = JsonDocument.Parse(reply!);
            Assert.Equal("answer", doc.RootElement.GetProperty("t").GetString());
            Assert.Equal("abc-123", doc.RootElement.GetProperty("clientId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("sdp").GetString()));
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task BridgeSignal_NonOffer_Ignored()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zeus-rbc-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new RemotePasswordStore(NullLogger<RemotePasswordStore>.Instance, path);
            store.Set("broker-test-password");
            var rtc = new RemoteWebRtcService(store, NullLogger<RemoteWebRtcService>.Instance);

            var reply = await RemoteBrokerClient.BridgeSignalAsync(
                rtc, "{\"t\":\"candidate\",\"candidate\":\"x\"}", NullLogger.Instance, default);

            Assert.Null(reply);
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    private static async Task<string> MakeOfferAsync()
    {
        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = new List<RTCIceServer>() });
        await pc.createDataChannel("control");
        await pc.createDataChannel("frames");
        var offer = pc.createOffer(null);
        await pc.setLocalDescription(offer);

        if (pc.iceGatheringState != RTCIceGatheringState.complete)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChange(RTCIceGatheringState s) { if (s == RTCIceGatheringState.complete) tcs.TrySetResult(); }
            pc.onicegatheringstatechange += OnChange;
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
            await using (cts.Token.Register(() => tcs.TrySetResult()))
                await tcs.Task.ConfigureAwait(false);
            pc.onicegatheringstatechange -= OnChange;
        }

        var sdp = pc.localDescription.sdp.ToString();
        pc.close();
        return sdp;
    }
}
