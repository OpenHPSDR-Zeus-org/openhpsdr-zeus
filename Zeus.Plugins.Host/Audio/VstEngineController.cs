using System.Text.Json;
using Zeus.Plugins.Contracts.Audio;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// One plugin enumerated by the engine's scanner. A "shell" VST3 yields many of
/// these from a single <see cref="File"/>, disambiguated by <see cref="Uid"/>.
/// </summary>
public sealed record EngineScannedPlugin(
    string Uid,
    string Name,
    string Manufacturer,
    string File,
    string Format,
    string Category,
    bool IsInstrument);

/// <summary>Outcome of a <see cref="VstEngineController.ActivateAsync"/> call.</summary>
public enum VstEngineStartResult
{
    /// <summary>The engine launched, handshook <c>ready</c>, and the realtime tap is live.</summary>
    Started,
    /// <summary>No <c>VSTHostEngine.exe</c> was found (caller surfaces "Get VSTHost").</summary>
    EngineNotFound,
    /// <summary>The engine process failed to start.</summary>
    LaunchFailed,
    /// <summary>The engine started but did not handshake <c>ready</c> within the budget.</summary>
    NotReady,
    /// <summary>Named shared memory + events are Windows-only.</summary>
    PlatformUnsupported,
}

/// <summary>
/// Public lifecycle + realtime facade over the out-of-process VST engine
/// (the internal <see cref="VstEngineBridge"/> audio plane and
/// <see cref="VstEngineProcess"/> supervisor). This is the seam
/// <c>Zeus.Server.Hosting</c> drives: it can't see the internal bridge
/// types, so everything an opt-in "VST" processing mode needs is exposed here.
///
/// <para><b>Robust-path lifetime model (load-bearing).</b> The shared-memory
/// bridge is created once, on first activation, and held for the controller's
/// whole lifetime — it is NEVER disposed on a mode toggle. Deactivation only
/// flips a single <c>volatile</c> gate and tears down the external <em>process</em>;
/// the realtime <see cref="Process"/> tap therefore never races a half-disposed
/// memory map. In Native mode (<see cref="IsActive"/> = false) the realtime path
/// is pure passthrough and never touches the engine at all — byte-identical to
/// having no VST mode. Windows-only (named SHM + events); on other platforms
/// activation returns <see cref="VstEngineStartResult.PlatformUnsupported"/> and
/// the tap stays passthrough.</para>
/// </summary>
public sealed class VstEngineController : IAsyncDisposable
{
    private readonly int _maxFrames;
    private readonly int _rate;
    private readonly int _channels;
    private readonly string _shmName;
    private readonly object _lifecycleLock = new();

    // Created lazily on first ActivateAsync (Windows-gated), then held for the
    // controller's lifetime. Read on the realtime thread; only ever assigned
    // once (non-null thereafter) under _lifecycleLock.
    private VstEngineBridge? _bridge;
    private VstEngineProcess? _proc;
    private volatile bool _active;
    private bool _disposed;

    /// <summary>Forwarded engine stderr lines (diagnostics) — control thread.</summary>
    public event Action<string>? StdErr;

    /// <summary>Forwarded parsed engine events (e.g. <c>chain</c>, <c>error</c>) — control thread.</summary>
    public event Action<JsonElement>? EngineEvent;

    public VstEngineController(int maxFrames = 4096, int rate = 48000, int channels = 1)
    {
        if (maxFrames <= 0) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        _maxFrames = maxFrames;
        _rate = rate;
        _channels = channels;
        _shmName = "zeus-vst-" + Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Resolve the engine exe without launching anything (override → default
    /// install → PATH). Null = not installed; the caller surfaces "Get VSTHost".
    /// </summary>
    public static string? FindEngineExe() => VstEngineProcess.FindEngineExe();

    /// <summary>True while the realtime tap routes audio through the engine.</summary>
    public bool IsActive => _active;

    /// <summary>Blocks that fell through to passthrough (timeout / stale-seq) while active.</summary>
    public long DegradedBlocks => _bridge?.DegradedBlocks ?? 0;

    /// <summary>The engine exe resolved on the last activation attempt, if any.</summary>
    public string? ResolvedEnginePath { get; private set; }

    /// <summary>
    /// Bounded per-block wait ceiling handed to the bridge (ms). Kept under the
    /// TX block period (960 frames @ 48 kHz ≈ 20 ms) so a wedged plugin costs at
    /// most one passthrough block. 4 ms was too tight: the cross-process event
    /// round-trip plus Windows timer granularity exceeded it on EVERY block, so
    /// 100 % of blocks degraded to passthrough and the engine never processed.
    /// 12 ms leaves ~8 ms of headroom under the 20 ms period while reliably
    /// completing the round-trip.
    /// </summary>
    public int WaitBudgetMs { get; set; } = 12;

    /// <summary>
    /// Launch the engine and bring the realtime tap online. Idempotent-ish:
    /// calling while already active is a no-op returning <see cref="VstEngineStartResult.Started"/>.
    /// Runs on the control thread (never the realtime thread).
    /// </summary>
    public async Task<VstEngineStartResult> ActivateAsync(TimeSpan readyTimeout, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return VstEngineStartResult.PlatformUnsupported;

        VstEngineProcess proc;
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active) return VstEngineStartResult.Started;

            var path = VstEngineProcess.FindEngineExe();
            if (path is null) return VstEngineStartResult.EngineNotFound;
            ResolvedEnginePath = path;

            // Create the shared-memory plane once and keep it for our lifetime.
            _bridge ??= VstEngineBridge.Create(_shmName, _maxFrames, _rate, _channels);
            _bridge.WaitBudgetMs = WaitBudgetMs;

            try
            {
                proc = VstEngineProcess.Launch(path, _shmName, _maxFrames, _rate, _channels);
            }
            catch
            {
                return VstEngineStartResult.LaunchFailed;
            }
            proc.StdErrLine += OnStdErr;
            proc.EngineEvent += OnEngineEvent;
            _proc = proc;
        }

        try
        {
            await proc.Ready.WaitAsync(readyTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Engine never handshook — tear the process back down, stay passthrough.
            Deactivate();
            return VstEngineStartResult.NotReady;
        }

        // Engine's audio thread is now blocked on .in; arm the realtime tap.
        lock (_lifecycleLock)
        {
            if (_disposed || _proc != proc)
            {
                // Disposed or superseded while we waited — don't arm a dead engine.
                return VstEngineStartResult.NotReady;
            }
            if (_bridge is { } b) b.EngineReady = true;
            _active = true;
        }
        return VstEngineStartResult.Started;
    }

    /// <summary>
    /// Take the realtime tap offline and stop the external process. The bridge
    /// (shared memory) is intentionally left mapped so an in-flight realtime
    /// block can't fault on a torn-down map. Safe to call repeatedly.
    /// </summary>
    public void Deactivate()
    {
        VstEngineProcess? proc;
        lock (_lifecycleLock)
        {
            // Order matters: stop the realtime path FIRST so no block enters the
            // engine round-trip after this point, THEN drop the process.
            _active = false;
            if (_bridge is { } b) b.EngineReady = false;
            proc = _proc;
            _proc = null;
        }
        if (proc is not null)
        {
            proc.StdErrLine -= OnStdErr;
            proc.EngineEvent -= OnEngineEvent;
            proc.Dispose();
        }
    }

    /// <summary>
    /// Send a control-plane command to the engine (e.g. <c>add_plugin</c>,
    /// <c>set_slot_gain</c>). No-op when not running. Control thread only.
    /// </summary>
    public void SendCommand(object command)
    {
        VstEngineProcess? proc;
        lock (_lifecycleLock) proc = _proc;
        proc?.Send(command);
    }

    /// <summary>
    /// Drive the engine's <c>scan_plugins</c> and await its <c>plugins_scanned</c>
    /// reply, returning every plugin the engine's scanner enumerated. For a
    /// "shell" VST3 (e.g. Waves WaveShell) this yields one entry PER hosted
    /// sub-plugin, each with its own <see cref="EngineScannedPlugin.Uid"/> — the
    /// identifier Zeus must pass back in <c>load_chain</c> to load that exact one.
    /// The engine also scans its formats' default locations (where WaveShell
    /// lives), so the chosen path is additive, not exclusive. Returns empty when
    /// the engine isn't active. Control thread only (never the realtime path).
    /// </summary>
    public async Task<IReadOnlyList<EngineScannedPlugin>> ScanPluginsAsync(
        IReadOnlyList<string> paths, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!_active) return Array.Empty<EngineScannedPlugin>();

        var tcs = new TaskCompletionSource<IReadOnlyList<EngineScannedPlugin>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(JsonElement e)
        {
            try
            {
                if (!e.TryGetProperty("event", out var ev)
                    || ev.ValueKind != JsonValueKind.String
                    || ev.GetString() != "plugins_scanned") return;

                var list = new List<EngineScannedPlugin>();
                if (e.TryGetProperty("plugins", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in arr.EnumerateArray())
                    {
                        static string Str(JsonElement o, string k) =>
                            o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                                ? v.GetString() ?? "" : "";
                        static bool Bool(JsonElement o, string k) =>
                            o.TryGetProperty(k, out var v)
                            && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

                        list.Add(new EngineScannedPlugin(
                            Uid: Str(p, "uid"),
                            Name: Str(p, "name"),
                            Manufacturer: Str(p, "manufacturer"),
                            File: Str(p, "file"),
                            Format: Str(p, "format"),
                            Category: Str(p, "category"),
                            IsInstrument: Bool(p, "isInstrument")));
                    }
                }
                tcs.TrySetResult(list);
            }
            catch { tcs.TrySetResult(Array.Empty<EngineScannedPlugin>()); }
        }

        EngineEvent += Handler;
        try
        {
            SendCommand(new { cmd = "scan_plugins", paths });
            return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Array.Empty<EngineScannedPlugin>();
        }
        finally
        {
            EngineEvent -= Handler;
        }
    }

    /// <summary>
    /// Realtime tap. In Native mode (not active) this is a bit-identical copy and
    /// never touches the engine. In VST mode it hands the block to the bridge,
    /// which round-trips it through the engine or passes through on any failure.
    /// No allocation, no managed lock.
    /// </summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
    {
        // Single volatile read; _bridge is assigned once and never nulled before
        // disposal, so reading it here without a lock is safe.
        var bridge = _bridge;
        if (!_active || bridge is null)
        {
            input.CopyTo(output);
            return;
        }
        bridge.Process(input, output, ctx);
    }

    private void OnStdErr(string line) => StdErr?.Invoke(line);
    private void OnEngineEvent(JsonElement e) => EngineEvent?.Invoke(e);

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
        }
        Deactivate();
        VstEngineBridge? bridge;
        lock (_lifecycleLock)
        {
            bridge = _bridge;
            _bridge = null;
        }
        bridge?.Dispose();
        return ValueTask.CompletedTask;
    }
}
