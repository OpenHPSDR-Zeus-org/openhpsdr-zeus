using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.Net;
using Zeus.Server.Hosting.Remote;

namespace Zeus.Server.Tests;

/// <summary>
/// End-to-end Phase-1 transport test: a real WebRTC peer (standing in for the
/// browser) connects to <see cref="RemoteWebRtcSession"/>, runs the SPAKE2+
/// password handshake over the control DataChannel, and only then receives a
/// frame on the data channel. Proves the ADR-0008 gate holds across the actual
/// WebRTC wire — correct password flows, wrong password never does.
/// </summary>
public sealed class RemoteWebRtcSessionTests
{
    private const string Password = "remote-bench-password";
    private const int Iter = 1, MemKib = 8, Par = 1;

    private static RemoteVerifierMaterial RegisterVerifier(string password)
    {
        var v = Spake2PlusRegistration.Register(password, Iter, MemKib, Par);
        return new RemoteVerifierMaterial(
            Spake2Plus.ScalarFromBytes(v.W0), Spake2Plus.DecodeL(v.L),
            v.Salt, v.Iterations, v.MemoryKib, v.Parallelism);
    }

    [Fact]
    public async Task CorrectPassword_Unlocks_AndFrameFlows()
    {
        var server = new RemoteWebRtcSession(RegisterVerifier(Password), NullLogger.Instance);
        await using var client = new ProverClient(Password);

        var unlockedOnServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Unlocked += () => unlockedOnServer.TrySetResult();

        var answer = await server.CreateAnswerAsync(await client.CreateOfferAsync());
        await client.AcceptAnswerAsync(answer);

        // Handshake completes on both ends.
        await client.Unlocked.WaitAsync(TimeSpan.FromSeconds(20));
        await unlockedOnServer.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(server.IsUnlocked);

        // A frame egressed after unlock reaches the client intact.
        var frame = Encoding.UTF8.GetBytes("display-frame-payload");
        Assert.True(server.TrySendFrame(frame));
        var received = await client.NextFrame().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(frame, received);

        server.Close();
    }

    [Fact]
    public async Task WrongPassword_NeverUnlocks_AndNoFrameFlows()
    {
        var server = new RemoteWebRtcSession(RegisterVerifier(Password), NullLogger.Instance);
        await using var client = new ProverClient("the-wrong-password");

        var answer = await server.CreateAnswerAsync(await client.CreateOfferAsync());
        await client.AcceptAnswerAsync(answer);

        // Client should be rejected, not unlocked.
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await client.Unlocked.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.False(server.IsUnlocked);
        Assert.False(server.TrySendFrame(Encoding.UTF8.GetBytes("should-not-send")));

        server.Close();
    }

    [Fact]
    public async Task UnlockedSession_ReceivesHubBroadcastFrame()
    {
        var hub = new Zeus.Server.StreamingHub(NullLogger<Zeus.Server.StreamingHub>.Instance);
        var server = new RemoteWebRtcSession(RegisterVerifier(Password), NullLogger.Instance, hub: hub);
        await using var client = new ProverClient(Password);

        var answer = await server.CreateAnswerAsync(await client.CreateOfferAsync());
        await client.AcceptAnswerAsync(answer);
        await client.Unlocked.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(server.IsUnlocked);

        // A normal hub broadcast (always-on RX meter, 5 bytes) reaches the remote
        // client through the StreamingHub fan-out → RemoteFrameSink → frames channel.
        // Re-broadcast on a loop (mirroring the real 5–60 Hz frame flow) so the
        // RemoteFrameSink attach-race + async hop can't time the single send out
        // on a slow/loaded CI runner — TrySetResult is a no-op once resolved.
        var received = await BroadcastUntilReceived(
            client, () => hub.Broadcast(new Zeus.Contracts.RxMeterFrame(-73.0f)));

        Assert.Equal(5, received.Length);
        server.Close();
    }

    [Fact]
    public async Task DisplayRequest_OpensGate_AndDisplayFrameReachesRemotePeer()
    {
        var hub = new Zeus.Server.StreamingHub(NullLogger<Zeus.Server.StreamingHub>.Instance);
        var server = new RemoteWebRtcSession(RegisterVerifier(Password), NullLogger.Instance, hub: hub);
        await using var client = new ProverClient(Password);

        var answer = await server.CreateAnswerAsync(await client.CreateOfferAsync());
        await client.AcceptAnswerAsync(answer);
        await client.Unlocked.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(server.IsUnlocked);

        // The display gate starts closed, so a DisplayFrame broadcast is dropped.
        Assert.False(hub.DisplayStreamRequested);

        // Client asks for the RX display stream over the control channel (0x22 01).
        client.SendControlBinary(new byte[] { 0x22, 0x01 });

        // The hub's global display gate must open (the remote session bumped it).
        await WaitForAsync(() => hub.DisplayStreamRequested, TimeSpan.FromSeconds(5));
        Assert.True(hub.DisplayStreamRequested);

        // A DisplayFrame broadcast now fans out through RemoteFrameSink → frames
        // channel and reaches the remote peer. Re-broadcast on a loop so the
        // sink attach-race + async hop can't time out on a slow CI runner.
        const ushort width = 4;
        var pan = new float[width] { -100f, -90f, -80f, -70f };
        var wf = new float[width] { -100f, -90f, -80f, -70f };
        var frame = new Zeus.Contracts.DisplayFrame(
            Seq: 1,
            TsUnixMs: 0,
            RxId: 0,
            BodyFlags: Zeus.Contracts.DisplayBodyFlags.PanValid | Zeus.Contracts.DisplayBodyFlags.WfValid,
            Width: width,
            CenterHz: 14_200_000,
            HzPerPixel: 1f,
            PanDb: pan,
            WfDb: wf);
        var received = await BroadcastUntilReceived(client, () => hub.Broadcast(frame));
        Assert.NotEmpty(received);

        // Closing the session unwinds the gate it opened — no pinned display.
        server.Close();
        await WaitForAsync(() => !hub.DisplayStreamRequested, TimeSpan.FromSeconds(5));
        Assert.False(hub.DisplayStreamRequested);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Drive <paramref name="broadcast"/> repeatedly (≈10 Hz, ≤15 s) until the
    /// client's next-frame task completes, then return the received bytes. This
    /// mirrors the real radio's continuous frame flow and removes both the
    /// RemoteFrameSink attach-race and the single-shot latency-timeout fragility
    /// that flakes on slow/loaded CI runners. Re-firing is safe — the prover's
    /// frame TCS uses TrySetResult, so a late frame still resolves the await.
    /// </summary>
    private static async Task<byte[]> BroadcastUntilReceived(ProverClient client, Action broadcast)
    {
        var next = client.NextFrame();
        for (int i = 0; i < 150 && !next.IsCompleted; i++)
        {
            broadcast();
            await Task.WhenAny(next, Task.Delay(100));
        }
        return await next.WaitAsync(TimeSpan.FromSeconds(1));
    }

    /// <summary>Minimal in-process SPAKE2+ prover over WebRTC — what the browser will do.</summary>
    private sealed class ProverClient : IAsyncDisposable
    {
        private readonly string _password;
        private readonly RTCPeerConnection _pc;
        private readonly RTCDataChannel _control;
        private readonly RTCDataChannel _frames;
        private readonly Spake2Plus _prover = new(
            Spake2Role.Prover, RemoteAuthConstants.Context,
            RemoteAuthConstants.IdProver, RemoteAuthConstants.IdVerifier);
        private Spake2PlusOutcome? _outcome;

        private readonly TaskCompletionSource _unlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<byte[]> _frame = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProverClient(string password)
        {
            _password = password;
            _pc = new RTCPeerConnection(new RTCConfiguration { iceServers = new List<RTCIceServer>() });
            _control = _pc.createDataChannel("control").Result;
            _frames = _pc.createDataChannel("frames").Result; // reliable for test determinism
            _control.onopen += () => _control.send("{\"t\":\"hello\"}");
            _control.onmessage += (_, _, data) => _ = HandleControlAsync(data);
            _frames.onmessage += (_, _, data) => _frame.TrySetResult(data);
        }

        public Task Unlocked => _unlocked.Task;
        public Task<byte[]> NextFrame() => _frame.Task;

        /// <summary>Send a raw binary control frame (post-unlock stream-request, e.g. 0x22 01).</summary>
        public void SendControlBinary(byte[] frame) => _control.send(frame);

        public async Task<string> CreateOfferAsync()
        {
            var offer = _pc.createOffer(null);
            await _pc.setLocalDescription(offer);
            await WaitGather();
            return _pc.localDescription.sdp.ToString();
        }

        public Task AcceptAnswerAsync(string answerSdp)
        {
            var r = _pc.setRemoteDescription(
                new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp });
            Assert.Equal(SetDescriptionResultEnum.OK, r);
            return Task.CompletedTask;
        }

        private async Task HandleControlAsync(byte[] data)
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                switch (doc.RootElement.GetProperty("t").GetString())
                {
                    case "auth-params":
                    {
                        var salt = Convert.FromBase64String(doc.RootElement.GetProperty("salt").GetString()!);
                        var (w0, w1) = Spake2PlusRegistration.DeriveScalars(
                            _password, salt,
                            doc.RootElement.GetProperty("iterations").GetInt32(),
                            doc.RootElement.GetProperty("memoryKib").GetInt32(),
                            doc.RootElement.GetProperty("parallelism").GetInt32());
                        var shareP = _prover.StartProver(w0, w1);
                        Send("auth-share", "share", shareP);
                        break;
                    }
                    case "auth-share":
                    {
                        var shareV = Convert.FromBase64String(doc.RootElement.GetProperty("share").GetString()!);
                        _outcome = _prover.Process(shareV);
                        Send("auth-confirm", "confirm", _outcome.LocalConfirm);
                        break;
                    }
                    case "auth-ok":
                    {
                        var confirmV = Convert.FromBase64String(doc.RootElement.GetProperty("confirm").GetString()!);
                        if (_outcome is not null && Spake2Plus.VerifyPeerConfirm(_outcome, confirmV))
                            _unlocked.TrySetResult();
                        else
                            _unlocked.TrySetException(new InvalidOperationException("server confirm invalid"));
                        break;
                    }
                    case "auth-fail":
                        _unlocked.TrySetException(new InvalidOperationException("auth rejected"));
                        break;
                }
            }
            catch (Exception ex)
            {
                _unlocked.TrySetException(ex);
            }
            await Task.CompletedTask;
        }

        private void Send(string t, string field, byte[] value)
            => _control.send(JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["t"] = t,
                [field] = Convert.ToBase64String(value),
            }));

        private async Task WaitGather()
        {
            if (_pc.iceGatheringState == RTCIceGatheringState.complete) return;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChange(RTCIceGatheringState s) { if (s == RTCIceGatheringState.complete) tcs.TrySetResult(); }
            _pc.onicegatheringstatechange += OnChange;
            try
            {
                if (_pc.iceGatheringState == RTCIceGatheringState.complete) return;
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
                await using (cts.Token.Register(() => tcs.TrySetResult()))
                    await tcs.Task.ConfigureAwait(false);
            }
            finally { _pc.onicegatheringstatechange -= OnChange; }
        }

        public ValueTask DisposeAsync()
        {
            try { _pc.close(); } catch { /* ignore */ }
            return ValueTask.CompletedTask;
        }
    }
}
