using System.Diagnostics;
using System.Text.Json;
using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Host.Audio;

namespace Zeus.Plugins.Host.Tests;

/// <summary>
/// Deterministic tests for the <see cref="VstEngineController"/> self-heal
/// supervisor — auto-relaunch, exponential backoff, crash-loop cap, hang
/// watchdog, handshake validation, and reconnect replay. These run on ANY
/// platform: the controller's test/seam constructor injects fake process and
/// bridge implementations, so no real engine or Windows shared memory is needed.
/// </summary>
public class VstEngineSupervisorTests
{
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

        // After faulting it cools down — the attempt count must plateau.
        int countAtFault = created;
        await Task.Delay(300);
        Assert.True(created <= countAtFault + 1,
            $"should stop hot-looping after fault (was {countAtFault}, now {created})");
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

        var recycled = await WaitUntilAsync(() => c.RestartCount >= 1, 4000);

        Assert.True(recycled, "watchdog should force-recycle a hung engine");
        Assert.True(created.Count >= 2);
        Assert.Contains(created, p => p.Killed); // the watchdog killed it, not a clean exit
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
