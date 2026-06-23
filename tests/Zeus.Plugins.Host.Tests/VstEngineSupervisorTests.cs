using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// Deterministic tests for the <see cref="VstEngineController"/> self-heal
/// supervisor — auto-relaunch, exponential backoff, crash-loop cap, hang
/// watchdog, handshake validation, and reconnect replay. These run on ANY
/// platform: the controller's test/seam constructor injects fake process and
/// bridge implementations, so no real engine or Windows shared memory is needed.
///
/// In the LoadSensitive collection (non-parallel): these assert on timing
/// (watchdog intervals, relaunch backoff), so running them without sibling
/// tests competing for the runner's cores removes the contention that made
/// them flaky once the assembly grew.
/// </summary>
[Collection("LoadSensitive")]
public class VstEngineSupervisorTests
{
    static VstEngineSupervisorTests()
    {
        // These supervisor tests are timing-sensitive (watchdog timers, relaunch
        // backoff, polling waits) and run in parallel with the rest of the
        // assembly. On a 2-core Windows CI runner the default thread-pool floor
        // can starve a watchdog timer callback or a Task.Delay continuation long
        // enough to blow a test's wait budget — the source of the intermittent
        // HungEngine / crash-loop flakes. Raise the floor once (process-wide,
        // idempotent) so timers and continuations always get a thread.
        ThreadPool.GetMinThreads(out var worker, out var io);
        ThreadPool.SetMinThreads(Math.Max(worker, 16), Math.Max(io, 16));
    }

    private static AudioBlockContext Ctx(int frames, int channels) =>
        new(sampleRate: 48000, channels: channels, frames: frames, sampleTime: 0, mox: true);

    private static JsonElement ReadyPayload(int frames = 512, int rate = 48000, int channels = 1, int protocol = 1) =>
        JsonDocument.Parse(
            $"{{\"event\":\"ready\",\"protocol\":{protocol},\"frames\":{frames},\"rate\":{rate},\"channels\":{channels}}}")
            .RootElement.Clone();

    private static async Task<bool> WaitUntilAsync(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            await Task.Delay(10);
        }
        return cond();
    }

    // Polls <read> until its value stops changing for <settleMs> consecutive ms
    // (i.e. it has plateaued), returning true; or false at <timeoutMs> if it
    // never settles (kept growing). Used to assert "no more work is scheduled"
    // deterministically: a loaded CI runner can lag a single in-flight operation
    // past a fixed wall-clock window, but a plateau is observable at any runner
    // speed, so a stop-condition expressed this way is not timing-racy.
    private static async Task<bool> WaitUntilStableAsync(Func<int> read, int settleMs, int timeoutMs)
    {
        var total = Stopwatch.StartNew();
        var last = read();
        var stableSince = Stopwatch.StartNew();
        while (total.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(10);
            var now = read();
            if (now != last)
            {
                last = now;
                stableSince.Restart();
            }
            else if (stableSince.ElapsedMilliseconds >= settleMs)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A scriptable in-process stand-in for the engine process.</summary>
    private sealed class FakeProcess : IVstEngineProcess
    {
        private readonly TaskCompletionSource<JsonElement> _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<JsonElement>? EngineEvent;
        public event Action<string>? StdErrLine;
        public event Action<IVstEngineProcess>? Exited;

        public Task<JsonElement> Ready => _ready.Task;
        public bool HasExited { get; private set; }
        public bool Killed { get; private set; }
        public List<object> Sent { get; } = new();

        public void SignalReady(JsonElement payload) => _ready.TrySetResult(payload);

        public void SimulateExit()
        {
            if (HasExited) return;
            HasExited = true;
            _ready.TrySetException(new InvalidOperationException("engine exited before ready"));
            Exited?.Invoke(this);
        }

        public void Kill() { Killed = true; SimulateExit(); }
        public void Send(object command) => Sent.Add(command);
        public void Dispose() { HasExited = true; }

        public void RaiseStdErr(string s) => StdErrLine?.Invoke(s);
        public void RaiseEvent(JsonElement e) => EngineEvent?.Invoke(e);
    }

    /// <summary>A no-op realtime bridge with a settable degraded counter.</summary>
    private sealed class FakeBridge : IVstEngineBridge
    {
        private long _degraded;
        public bool EngineReady { get; set; }
        public int WaitBudgetMs { get; set; }
        public int ResetCount { get; private set; }
        public uint EngineState { get; set; }
        public uint EngineFlags { get; set; }

        /// <summary>
        /// When > 0, every read of <see cref="DegradedBlocks"/> advances the counter
        /// by this much — modelling an engine that keeps dropping blocks. This makes
        /// the hang-watchdog deterministic: it sees a positive delta on every sampling
        /// interval, so its consecutive-interval streak can't be reset by CI thread-pool
        /// starvation (the source of a Windows-only flake when a background pump drove
        /// the counter instead).
        /// </summary>
        public long DegradePerRead { get; set; }

        public long DegradedBlocks
        {
            get
            {
                long step = DegradePerRead;
                return step > 0
                    ? Interlocked.Add(ref _degraded, step)
                    : Interlocked.Read(ref _degraded);
            }
        }

        public void AddDegraded(long n) => Interlocked.Add(ref _degraded, n);

        public bool Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
        {
            input.CopyTo(output);
            return EngineReady;
        }

        public void ResetForRelaunch() { ResetCount++; EngineReady = false; }
        public void Dispose() { }
    }

    private static VstEngineController NewController(
        Func<string, IVstEngineProcess> launch,
        out Func<FakeBridge?> getBridge,
        string? exe = "C:/fake/VSTHostEngine.exe")
    {
        // The bridge is created lazily on first activation; the out delegate lets a
        // test read it afterwards without a static side-table.
        FakeBridge? captured = null;
        getBridge = () => captured;
        return new VstEngineController(
            maxFrames: 512, rate: 48000, channels: 1,
            launch: launch,
            resolveExe: () => exe,
            createBridge: () => captured = new FakeBridge());
    }

    // ── Auto-relaunch on crash + reconnect replay ─────────────────────────────
    [Fact]
    public async Task EngineCrash_AutoRelaunches_AndFiresReconnected()
    {
        var created = new List<FakeProcess>();
        Func<string, IVstEngineProcess> launch = _ =>
        {
            var p = new FakeProcess();
            p.SignalReady(ReadyPayload());
            created.Add(p);
            return p;
        };

        await using var c = NewController(launch, out _);
        c.RelaunchBaseDelayMs = 5;
        c.RelaunchMaxDelayMs = 20;

        var reconnected = 0;
        c.Reconnected += () => Interlocked.Increment(ref reconnected);

        var start = await c.ActivateAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VstEngineStartResult.Started, start);
        Assert.True(c.IsActive);
        Assert.Single(created);
        Assert.Equal(0, reconnected); // first activation is NOT a reconnect

        // The engine process dies unexpectedly.
        created[0].SimulateExit();

        Assert.True(await WaitUntilAsync(() => created.Count >= 2 && c.IsActive, 3000),
            "supervisor should relaunch and re-arm after a crash");
        Assert.True(c.RestartCount >= 1);
        Assert.True(reconnected >= 1, "Reconnected should fire on relaunch so owners replay the chain");
        Assert.Equal(VstEngineState.Active, c.State);
    }

    // ── Crash-loop cap → Faulted, stops hot-looping ───────────────────────────
    [Fact]
    public async Task PersistentLaunchFailure_TripsCrashLoopCap_AndFaults()
    {
        var created = 0;
        // Processes that never handshake ⇒ every attempt times out as NotReady.
        Func<string, IVstEngineProcess> launch = _ => { Interlocked.Increment(ref created); return new FakeProcess(); };

        await using var c = NewController(launch, out _);
        c.RelaunchBaseDelayMs = 5;
        c.RelaunchMaxDelayMs = 10;
        c.RelaunchReadyTimeout = TimeSpan.FromMilliseconds(30);
        c.MaxRelaunchesPerWindow = 3;
        c.RelaunchWindowSeconds = 60;
        c.FaultCooldownMs = 5000; // long, so we can observe the hot-loop stop

        string? fault = null;
        c.Faulted += r => fault = r;

        var start = await c.ActivateAsync(TimeSpan.FromMilliseconds(30));
        Assert.Equal(VstEngineStartResult.NotReady, start);

        Assert.True(await WaitUntilAsync(() => fault is not null, 4000),
            "crash-loop cap should fault rather than relaunch forever");
        Assert.Equal(VstEngineState.Faulted, c.State);
        Assert.False(c.IsActive);

        // After faulting it cools down (FaultCooldownMs = 5 s, far longer than a
        // relaunch cycle). At most one relaunch may be in flight at the instant
        // the cap trips; once that settles the spawn count must STOP GROWING.
        // Assert that plateau directly — a stable count is observable at any
        // runner speed, whereas the old fixed 300 ms / +1 window let a loaded
        // Windows CI runner lag an in-flight launch past it and flake.
        Assert.True(await WaitUntilStableAsync(() => created, settleMs: 150, timeoutMs: 3000),
            $"spawn count should plateau (stop hot-looping) after fault; now {created}");
        Assert.Equal(VstEngineState.Faulted, c.State); // stayed faulted, not relaunching
    }

    // ── Hang watchdog recycles an alive-but-unresponsive engine ───────────────
    [Fact]
    public async Task HungEngine_IsRecycledByWatchdog()
    {
        var created = new List<FakeProcess>();
        Func<string, IVstEngineProcess> launch = _ =>
        {
            var p = new FakeProcess();
            p.SignalReady(ReadyPayload());
            created.Add(p);
            return p;
        };

        await using var c = NewController(launch, out var getBridge);
        c.RelaunchBaseDelayMs = 5;
        c.RelaunchMaxDelayMs = 10;
        c.HangWatchdogIntervalMs = 15;
        c.HangDegradedThreshold = 5;
        c.HangConsecutiveIntervals = 3;
        c.MaxRelaunchesPerWindow = 100; // don't let the cap interfere with this test

        var start = await c.ActivateAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VstEngineStartResult.Started, start);
        var bridge = getBridge()!;

        // Simulate the engine no longer servicing blocks: every watchdog sample of
        // DegradedBlocks sees the count climb past the threshold. Driving it from the
        // read (instead of a background pump) keeps the watchdog's consecutive-interval
        // streak from being reset by CI thread-pool starvation — previously flaky on
        // Windows, where the pump task could be starved long enough to zero the delta.
        bridge.DegradePerRead = c.HangDegradedThreshold * 2;

        // Generous budget: this asserts a CORRECTNESS invariant (the watchdog
        // does recycle a hung engine), not a latency bound. A real failure to
        // recycle still fails the test; a loaded runner just takes longer to
        // observe it. ~50 ms when healthy.
        var recycled = await WaitUntilAsync(() => c.RestartCount >= 1, 15000);

        Assert.True(recycled, "watchdog should force-recycle a hung engine");
        Assert.True(created.Count >= 2);
        Assert.Contains(created, p => p.Killed); // the watchdog killed it, not a clean exit
    }

    // ── Tier-1 recovery: in-place chain reload, only while live ───────────────
    [Fact]
    public async Task RequestChainReload_RaisesEvent_OnlyWhenActive()
    {
        var created = new List<FakeProcess>();
        Func<string, IVstEngineProcess> launch = _ =>
        {
            var p = new FakeProcess();
            p.SignalReady(ReadyPayload());
            created.Add(p);
            return p;
        };

        await using var c = NewController(launch, out _);

        var reloads = 0;
        c.ChainReloadRequested += () => Interlocked.Increment(ref reloads);

        // Before activation the engine is not the live route — must be a no-op so we
        // never push a chain at a dead engine.
        c.RequestChainReload("test (inactive)");
        Assert.Equal(0, reloads);

        await c.ActivateAsync(TimeSpan.FromSeconds(5));
        Assert.True(c.IsActive);

        // While live it asks owners to replay their chain (the manual-profile-change
        // fix) without recycling the process.
        c.RequestChainReload("test (active)");
        Assert.Equal(1, reloads);
        Assert.Single(created); // no process recycle
        Assert.Equal(0, c.RestartCount);

        c.Deactivate();
        c.RequestChainReload("test (after deactivate)");
        Assert.Equal(1, reloads); // no further reloads once down
    }

    // ── Tier-2 recovery: force-recycle kills the process and relaunches ───────
    [Fact]
    public async Task ForceRecycle_KillsProcess_AndRelaunches_ReplayingChain()
    {
        var created = new List<FakeProcess>();
        Func<string, IVstEngineProcess> launch = _ =>
        {
            var p = new FakeProcess();
            p.SignalReady(ReadyPayload());
            created.Add(p);
            return p;
        };

        await using var c = NewController(launch, out _);
        c.RelaunchBaseDelayMs = 5;
        c.RelaunchMaxDelayMs = 20;
        c.MaxRelaunchesPerWindow = 100;

        var reconnected = 0;
        c.Reconnected += () => Interlocked.Increment(ref reconnected);

        await c.ActivateAsync(TimeSpan.FromSeconds(5));
        Assert.True(c.IsActive);
        Assert.Single(created);

        // Escalation: the TX-liveness watchdog couldn't clear the wedge with a
        // reload, so it recycles. Routes through the normal exit→relaunch path.
        c.ForceRecycle("test recycle");

        Assert.True(await WaitUntilAsync(() => created.Count >= 2 && c.IsActive, 3000),
            "ForceRecycle should kill the engine and relaunch it");
        Assert.True(created[0].Killed); // killed, not a clean exit
        Assert.True(c.RestartCount >= 1);
        Assert.True(reconnected >= 1, "relaunch should fire Reconnected so owners replay the chain");
        Assert.Equal("test recycle", c.LastFault);
    }

    // ── ForceRecycle on an inactive engine is a safe no-op ────────────────────
    [Fact]
    public async Task ForceRecycle_WhenInactive_IsNoOp()
    {
        await using var c = NewController(_ => new FakeProcess(), out _);
        c.ForceRecycle("test (inactive)"); // must not throw or spawn anything
        Assert.False(c.IsActive);
        Assert.Equal(0, c.RestartCount);
    }

    // ── Deactivate cancels the supervisor (no relaunch after intentional stop) ─
    [Fact]
    public async Task Deactivate_StopsSupervisor_NoRelaunch()
    {
        var created = new List<FakeProcess>();
        Func<string, IVstEngineProcess> launch = _ =>
        {
            var p = new FakeProcess();
            p.SignalReady(ReadyPayload());
            created.Add(p);
            return p;
        };

        await using var c = NewController(launch, out _);
        c.RelaunchBaseDelayMs = 5;

        await c.ActivateAsync(TimeSpan.FromSeconds(5));
        Assert.True(c.IsActive);
        Assert.Single(created);

        c.Deactivate();
        Assert.False(c.IsActive);
        Assert.Equal(VstEngineState.Inactive, c.State);

        // A late exit of the now-orphaned process must NOT trigger a relaunch.
        created[0].SimulateExit();
        await Task.Delay(200);
        Assert.Single(created);
        Assert.False(c.IsActive);
    }

    // ── Handshake validation rejects a protocol/contract mismatch ─────────────
    [Fact]
    public async Task HandshakeMismatch_IsRejected()
    {
        Func<string, IVstEngineProcess> launch = _ =>
        {
            var p = new FakeProcess();
            p.SignalReady(ReadyPayload(protocol: 999)); // wrong protocol
            return p;
        };

        await using var c = NewController(launch, out _);
        c.RelaunchReadyTimeout = TimeSpan.FromMilliseconds(50);

        var start = await c.ActivateAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(VstEngineStartResult.NotReady, start);
        Assert.False(c.IsActive);

        c.Deactivate(); // stop the supervisor retry loop
    }

    // ── Inactive tap is still a clean passthrough (regression guard) ──────────
    [Fact]
    public async Task InactiveTap_PassesThroughBitIdentical()
    {
        await using var c = NewController(_ => new FakeProcess(), out _);
        var input = new float[256];
        for (int i = 0; i < input.Length; i++) input[i] = MathF.Sin(i * 0.05f);
        var output = new float[256];

        c.Process(input, output, Ctx(256, 1));
        Assert.Equal(input, output);
        Assert.Equal(0, c.DegradedBlocks);
    }
}
