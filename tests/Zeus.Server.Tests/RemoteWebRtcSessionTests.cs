using System.Net;
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

    // -- Read-only API tunnel ------------------------------------------------

    private const string LoopbackBase = "http://127.0.0.1:6060";

    private static RemoteWebRtcSession ApiSession(
        StubHttpClientFactory factory, out RemoteWebRtcSession session)
    {
        session = new RemoteWebRtcSession(
            RegisterVerifier(Password), NullLogger.Instance,
            iceServers: null, hub: null, httpFactory: factory, loopbackBaseUrl: LoopbackBase);
        return session;
    }

    private static async Task UnlockAsync(RemoteWebRtcSession server, ProverClient client)
    {
        var answer = await server.CreateAnswerAsync(await client.CreateOfferAsync());
        await client.AcceptAnswerAsync(answer);
        await client.Unlocked.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(server.IsUnlocked);
    }

    [Fact]
    public async Task PostUnlock_TunneledGet_ReachesLoopback_AndResponseReturns()
    {
        var factory = new StubHttpClientFactory((req) =>
        {
            // Proves the request hit loopback at the right URL with GET.
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Equal($"{LoopbackBase}/api/state", req.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"vfoA\":14200000}", Encoding.UTF8, "application/json"),
            };
        });
        ApiSession(factory, out var server);
        await using var client = new ProverClient(Password);
        await UnlockAsync(server, client);

        var replyJson = await SendApiUntilReply(client, 7, "GET", "/api/state");
        using var doc = JsonDocument.Parse(replyJson);
        Assert.Equal(7, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(200, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("{\"vfoA\":14200000}", doc.RootElement.GetProperty("body").GetString());
        Assert.Equal(1, factory.CallCount);

        server.Close();
    }

    [Fact]
    public async Task PostUnlock_NonGet_Refused405_WithoutTouchingLoopback()
    {
        var factory = new StubHttpClientFactory(_ =>
            throw new InvalidOperationException("loopback must NOT be called for a non-GET"));
        ApiSession(factory, out var server);
        await using var client = new ProverClient(Password);
        await UnlockAsync(server, client);

        var replyJson = await SendApiUntilReply(client, 3, "POST", "/api/state");
        using var doc = JsonDocument.Parse(replyJson);
        Assert.Equal(405, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(0, factory.CallCount); // read-only: never reached the radio

        server.Close();
    }

    [Fact]
    public async Task PostUnlock_DenylistedPath_Refused403_WithoutTouchingLoopback()
    {
        var factory = new StubHttpClientFactory(_ =>
            throw new InvalidOperationException("loopback must NOT be called for a denied path"));
        ApiSession(factory, out var server);
        await using var client = new ProverClient(Password);
        await UnlockAsync(server, client);

        // Prefs DB export is a denylisted secret-exfiltration path.
        var replyJson = await SendApiUntilReply(
            client, 9, "GET", "/api/prefs/databases/export?relativePath=zeus-prefs.db");
        using var doc = JsonDocument.Parse(replyJson);
        Assert.Equal(403, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(0, factory.CallCount);

        server.Close();
    }

    [Fact]
    public async Task PostUnlock_TraversalToDenylistedPath_Refused_WithoutTouchingLoopback()
    {
        var factory = new StubHttpClientFactory(_ =>
            throw new InvalidOperationException("loopback must NOT be reached via a traversal path"));
        ApiSession(factory, out var server);
        await using var client = new ProverClient(Password);
        await UnlockAsync(server, client);

        // "/api/state/../prefs/databases/export" prefix-matches no denylist entry
        // as raw text, but Uri canonicalisation collapses the "../" onto the
        // denied prefs-DB export. The server must refuse it (traversal guard /
        // canonical denylist) and never reach loopback — otherwise the QRZ
        // password + remote verifier leak.
        var replyJson = await SendApiUntilReply(
            client, 11, "GET", "/api/state/../prefs/databases/export?relativePath=zeus-prefs.db");
        using var doc = JsonDocument.Parse(replyJson);
        Assert.Equal(403, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(0, factory.CallCount);

        server.Close();
    }

    [Fact]
    public async Task PreUnlock_ApiInput_DoesNothing()
    {
        var factory = new StubHttpClientFactory(_ =>
            throw new InvalidOperationException("loopback must NOT be called before unlock"));
        ApiSession(factory, out var server);
        // LockedClient never runs the SPAKE2+ handshake, so the session stays
        // LOCKED — exactly the deny-by-default condition under test.
        await using var client = new LockedApiClient();

        var answer = await server.CreateAnswerAsync(await client.CreateOfferAsync());
        await client.AcceptAnswerAsync(answer);
        await WaitForAsync(() => client.ApiChannelOpen, TimeSpan.FromSeconds(10));
        Assert.False(server.IsUnlocked);

        // Fire an API request while still LOCKED — it must be ignored entirely:
        // no loopback call, and no reply ever arrives.
        var reply = client.NextApiReply();
        client.SendApiRaw(JsonSerializer.Serialize(new { id = 1, method = "GET", path = "/api/state" }));

        var completed = await Task.WhenAny(reply, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.NotSame(reply, completed); // timed out → no reply (fail-closed)
        Assert.False(server.IsUnlocked);
        Assert.Equal(0, factory.CallCount);

        server.Close();
    }

    /// <summary>
    /// Send a tunnelled API request, re-firing until a reply lands (≤15 s),
    /// mirroring BroadcastUntilReceived's hardening against the data-channel
    /// attach-race + async hop on slow CI runners. The server replies per id, so
    /// a duplicate send is harmless (the prover keeps the latest reply slot).
    /// </summary>
    private static async Task<string> SendApiUntilReply(
        ProverClient client, int id, string method, string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            var reply = client.SendApiRequest(id, method, path);
            var done = await Task.WhenAny(reply, Task.Delay(250));
            if (done == reply) return await reply;
            if (DateTime.UtcNow > deadline) return await reply.WaitAsync(TimeSpan.FromSeconds(1));
        }
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
    /// Await the client's next-frame task while re-driving <paramref name="broadcast"/>
    /// at ≈10 Hz, bounded only by a generous CI backstop. Re-firing is required, not
    /// merely defensive: a frame broadcast before the server's RemoteFrameSink attaches
    /// (or before SCTP/DTLS is carrying data) is dropped, never queued — so we keep
    /// firing until the awaited frame TCS resolves. This mirrors the real radio's
    /// continuous 5–60 Hz frame flow and removes the attach-race together with the
    /// single-shot latency-timeout fragility that flaked on the slowest loaded runner
    /// (macOS arm64), where a fixed ~15 s window plus a tight final 1 s await could race
    /// out. The frame TCS uses TrySetResult, so re-firing past resolution is a no-op,
    /// and the 30 s backstop turns a genuine never-arrives into a deterministic failure
    /// (OperationCanceledException) rather than a tight-timeout flake.
    /// </summary>
    private static async Task<byte[]> BroadcastUntilReceived(ProverClient client, Action broadcast)
    {
        using var backstop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var next = client.NextFrame();
        while (true)
        {
            broadcast();
            try
            {
                return await next.WaitAsync(TimeSpan.FromMilliseconds(100), backstop.Token);
            }
            catch (TimeoutException)
            {
                // 100 ms tick elapsed with no frame — re-broadcast and keep awaiting.
            }
        }
    }

    /// <summary>Minimal in-process SPAKE2+ prover over WebRTC — what the browser will do.</summary>
    private sealed class ProverClient : IAsyncDisposable
    {
        private readonly string _password;
        private readonly RTCPeerConnection _pc;
        private readonly RTCDataChannel _control;
        private readonly RTCDataChannel _frames;
        private readonly RTCDataChannel _api;
        private readonly Spake2Plus _prover = new(
            Spake2Role.Prover, RemoteAuthConstants.Context,
            RemoteAuthConstants.IdProver, RemoteAuthConstants.IdVerifier);
        private Spake2PlusOutcome? _outcome;

        private readonly TaskCompletionSource _unlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<byte[]> _frame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<string> _apiReply = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProverClient(string password)
        {
            _password = password;
            _pc = new RTCPeerConnection(new RTCConfiguration { iceServers = new List<RTCIceServer>() });
            _control = _pc.createDataChannel("control").Result;
            _frames = _pc.createDataChannel("frames").Result; // reliable for test determinism
            _api = _pc.createDataChannel("api").Result;        // read-only REST tunnel
            _control.onopen += () => _control.send("{\"t\":\"hello\"}");
            _control.onmessage += (_, _, data) => _ = HandleControlAsync(data);
            _frames.onmessage += (_, _, data) => _frame.TrySetResult(data);
            _api.onmessage += (_, _, data) => _apiReply.TrySetResult(Encoding.UTF8.GetString(data));
        }

        public Task Unlocked => _unlocked.Task;
        public Task<byte[]> NextFrame() => _frame.Task;
        public bool ApiChannelOpen => _api.readyState == RTCDataChannelState.open;

        /// <summary>Send a raw binary control frame (post-unlock stream-request, e.g. 0x22 01).</summary>
        public void SendControlBinary(byte[] frame) => _control.send(frame);

        /// <summary>Send a read-only API tunnel request {id, method, path} and await the next reply JSON.</summary>
        public Task<string> SendApiRequest(int id, string method, string path)
        {
            _apiReply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _api.send(JsonSerializer.Serialize(new { id, method, path }));
            return _apiReply.Task;
        }

        /// <summary>Send a raw API message without resetting the reply slot (for pre-unlock no-op checks).</summary>
        public void SendApiRaw(string json) => _api.send(json);

        /// <summary>The next API reply that arrives (resets the slot), without sending anything.</summary>
        public Task<string> NextApiReply()
        {
            _apiReply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _apiReply.Task;
        }

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

    /// <summary>
    /// In-test <see cref="IHttpClientFactory"/> whose clients run a stub handler —
    /// the loopback seam required by the task (no real radio / Kestrel). The
    /// handler delegate decides each response and CallCount lets a test assert
    /// loopback was (or, for 405/403, was NOT) reached.
    /// </summary>
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public int CallCount { get; private set; }

        public StubHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> respond)
            => _respond = respond;

        public HttpClient CreateClient(string name) => new(new StubHandler(this));

        private sealed class StubHandler(StubHttpClientFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.CallCount++;
                return Task.FromResult(owner._respond(request));
            }
        }
    }

    /// <summary>
    /// A peer that opens the control + api channels but deliberately NEVER runs
    /// the SPAKE2+ handshake, so the server session stays LOCKED. Used to prove
    /// pre-unlock API input is ignored (deny-by-default).
    /// </summary>
    private sealed class LockedApiClient : IAsyncDisposable
    {
        private readonly RTCPeerConnection _pc;
        private readonly RTCDataChannel _control;
        private readonly RTCDataChannel _api;
        private TaskCompletionSource<string> _apiReply = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LockedApiClient()
        {
            _pc = new RTCPeerConnection(new RTCConfiguration { iceServers = new List<RTCIceServer>() });
            _control = _pc.createDataChannel("control").Result; // opened, but no hello/auth ever sent
            _ = _pc.createDataChannel("frames").Result;
            _api = _pc.createDataChannel("api").Result;
            _api.onmessage += (_, _, data) => _apiReply.TrySetResult(Encoding.UTF8.GetString(data));
        }

        public bool ApiChannelOpen => _api.readyState == RTCDataChannelState.open;
        public void SendApiRaw(string json) => _api.send(json);

        public Task<string> NextApiReply()
        {
            _apiReply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _apiReply.Task;
        }

        public async Task<string> CreateOfferAsync()
        {
            var offer = _pc.createOffer(null);
            await _pc.setLocalDescription(offer);
            if (_pc.iceGatheringState != RTCIceGatheringState.complete)
            {
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnChange(RTCIceGatheringState s) { if (s == RTCIceGatheringState.complete) tcs.TrySetResult(); }
                _pc.onicegatheringstatechange += OnChange;
                try
                {
                    if (_pc.iceGatheringState != RTCIceGatheringState.complete)
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
                        await using (cts.Token.Register(() => tcs.TrySetResult()))
                            await tcs.Task.ConfigureAwait(false);
                    }
                }
                finally { _pc.onicegatheringstatechange -= OnChange; }
            }
            return _pc.localDescription.sdp.ToString();
        }

        public Task AcceptAnswerAsync(string answerSdp)
        {
            var r = _pc.setRemoteDescription(
                new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp });
            Assert.Equal(SetDescriptionResultEnum.OK, r);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            try { _pc.close(); } catch { /* ignore */ }
            return ValueTask.CompletedTask;
        }
    }
}
