using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// Serial chain of <see cref="IAudioPlugin"/> instances with master
/// bypass + per-slot bypass. The realtime <see cref="Process"/> method
/// allocates nothing, takes no locks, and short-circuits to a single
/// memcpy when master bypass is engaged — matching the bit-identical
/// pass-through requirement from the v1 ADR (§5.7).
///
/// <para><b>Master bypass</b> is the operator's "disengage the whole
/// Audio Suite" lever, surfaced via the Audio Suite UI and persisted
/// by <c>AudioChainMasterBypassService</c>. Default in this realtime
/// type is <c>false</c> (chain hot); the service writes the operator's
/// persisted preference through on startup. Toggling does NOT touch
/// per-slot bypass — when master goes from engaged back to off, the
/// individual plugins resume in whatever bypass state the operator
/// last left them in. CFC lives downstream in WDSP and is unaffected.</para>
///
/// Slot mutation methods (<see cref="SetSlot"/>, <see cref="ClearSlot"/>,
/// <see cref="SetSlotBypass"/>) are NOT realtime-safe — call from the
/// control thread before / after a block, never from inside it.
/// </summary>
public sealed class AudioChain : IAsyncDisposable
{
    public const int MaxSlots = 8;

    private readonly ChainSlot[] _slots = new ChainSlot[MaxSlots];
    private readonly float[] _scratch;
    private volatile bool _masterBypassed;
    // Chain-level signal meters — peak |sample| of the block entering
    // slot 0 (_inPeak) and leaving the last active slot (_outPeak).
    // Written on the realtime thread in Process, read (lock-free) by
    // the control thread for the /api/audio-suite/chain/meters poll.
    // Linear peak. Finite overshoot is preserved for plugin-chain headroom;
    // non-finite samples are ignored/repaired before they can poison meters or
    // downstream slots.
    private volatile float _inPeak;
    private volatile float _outPeak;

    // Realtime perf + quality telemetry for the diagnostics surface. Written
    // only on the audio thread (single writer) and published via Volatile so a
    // 64-bit read on the control thread can't tear; no locks, no Interlocked
    // contention on the hot path. All read 0 until the chain runs a block.
    private long _procTicksLast;    // last processed block's chain duration (Stopwatch ticks)
    private long _procTicksMax;     // peak block duration since start
    private long _blocksProcessed;  // blocks that actually ran the chain (not bypassed)
    private long _nonFiniteRepairs; // total NaN/Inf samples a plugin emitted and the chain zeroed

    public AudioChain(int maxFrames = 4096, int maxChannels = 2)
    {
        _scratch = new float[maxFrames * maxChannels];
        for (int i = 0; i < MaxSlots; i++) _slots[i] = new ChainSlot();
    }

    public int SlotCount => MaxSlots;

    /// <summary>
    /// Operator master bypass. <c>true</c> = chain inert, mic passes
    /// through bit-identical to WDSP; <c>false</c> = chain hot, plugins
    /// run in slot order. Single <c>volatile bool</c> — realtime read
    /// in <see cref="Process"/>, control-thread write from
    /// <c>AudioChainMasterBypassService</c>. No locks, no re-init.
    /// </summary>
    public bool MasterBypassed
    {
        get => _masterBypassed;
        set => _masterBypassed = value;
    }

    /// <summary>
    /// Most-recent chain-level signal meters: peak |sample| entering
    /// the chain (In) and leaving it (Out), linear. Lock-free snapshot
    /// for the control thread; the UI converts to dBFS. Both read 0
    /// until the chain has processed at least one block (idle / no MOX).
    /// </summary>
    public (float In, float Out) Meters => (_inPeak, _outPeak);

    /// <summary>
    /// Lock-free realtime telemetry for the diagnostics surface: how long the
    /// chain took to process the most-recent and peak block (microseconds), how
    /// many blocks have run, and how many non-finite (NaN/Inf) samples plugins
    /// emitted and the chain repaired. All zero until the first processed block.
    /// </summary>
    public readonly record struct ChainTelemetry(
        double LastProcMicros,
        double MaxProcMicros,
        long BlocksProcessed,
        long NonFiniteRepairs);

    public ChainTelemetry Telemetry => new(
        TicksToMicros(Volatile.Read(ref _procTicksLast)),
        TicksToMicros(Volatile.Read(ref _procTicksMax)),
        Volatile.Read(ref _blocksProcessed),
        Volatile.Read(ref _nonFiniteRepairs));

    private static double TicksToMicros(long ticks) =>
        ticks * (1_000_000.0 / System.Diagnostics.Stopwatch.Frequency);

    private static float BlockPeak(ReadOnlySpan<float> block)
    {
        float peak = 0f;
        for (int i = 0; i < block.Length; i++)
        {
            float a = block[i];
            if (!float.IsFinite(a)) continue;
            if (a < 0f) a = -a;
            if (a > peak) peak = a;
        }
        return peak;
    }

    private static int RepairNonFiniteSamples(Span<float> block)
    {
        int repaired = 0;
        for (int i = 0; i < block.Length; i++)
        {
            if (!float.IsFinite(block[i]))
            {
                block[i] = 0f;
                repaired++;
            }
        }
        return repaired;
    }

    public IAudioPlugin? GetSlot(int index)
    {
        ValidateIndex(index);
        return _slots[index].Plugin;
    }

    public void SetSlot(int index, IAudioPlugin plugin)
    {
        ValidateIndex(index);
        var slot = _slots[index];
        slot.Plugin = plugin;
        slot.Bypassed = false;
    }

    public void ClearSlot(int index)
    {
        ValidateIndex(index);
        _slots[index].Plugin = null;
        _slots[index].Bypassed = false;
    }

    public bool IsSlotBypassed(int index)
    {
        ValidateIndex(index);
        return _slots[index].Bypassed;
    }

    public void SetSlotBypass(int index, bool bypassed)
    {
        ValidateIndex(index);
        _slots[index].Bypassed = bypassed;
    }

    /// <summary>
    /// Run the chain over one block. Input is read once into output;
    /// the chain then ping-pongs between <paramref name="output"/> and
    /// the internal scratch buffer to chain plugins without allocating
    /// per call.
    ///
    /// When master bypass is engaged, this is a single <c>input.CopyTo(output)</c>
    /// and exits. Slots whose Plugin is null or Bypassed = true are
    /// skipped without a copy (handled by the ping-pong logic).
    /// </summary>
    public void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
    {
        // Field-backed scratch — appropriate for the single-tap WDSP TX
        // path that has historically been the only caller. A second tap
        // (e.g. NativeMicCapture's pre-MOX preview) MUST use the caller-
        // supplied-scratch overload below with its own private scratch
        // span so the two paths never collide on _scratch if they ever
        // race at a MOX edge.
        var needed = ctx.Frames * ctx.Channels;
        if (needed > _scratch.Length)
        {
            // Block bigger than we sized for — fall back to safe pass-through
            // before we touch the scratch overload (which would refuse a
            // smaller scratch the same way).
            if (output.Length < input.Length)
                throw new ArgumentException("output too small", nameof(output));
            input.CopyTo(output);
            return;
        }
        Process(input, output, _scratch.AsSpan(0, needed), ctx);
    }

    /// <summary>
    /// Caller-supplied-scratch overload. Behaviour matches
    /// <see cref="Process(ReadOnlySpan{float}, Span{float}, AudioBlockContext)"/>
    /// bit-for-bit when invoked with the field-backed scratch — used by
    /// the field-backed overload as its implementation. Exists so a
    /// second tap point (the desktop-mode pre-MOX preview path in
    /// <c>AudioPluginBridge.ProcessLivePreview</c>) can run the chain
    /// without touching the WDSP TX path's <c>_scratch</c>. The two
    /// callers are gated mutually-exclusive in time by their MOX /
    /// monitor checks; the separate scratch protects against the
    /// microsecond-scale overlap window at a MOX edge.
    ///
    /// <paramref name="scratch"/> must be at least <c>ctx.Frames *
    /// ctx.Channels</c> samples long.
    /// </summary>
    public void Process(
        ReadOnlySpan<float> input,
        Span<float> output,
        Span<float> scratch,
        AudioBlockContext ctx)
    {
        if (output.Length < input.Length)
            throw new ArgumentException("output too small", nameof(output));

        // Input meter tap (before slot 0). Cheap O(n) max-|sample|,
        // same shape as the WDSP TX stage meters.
        float inPk = BlockPeak(input);

        if (_masterBypassed)
        {
            // Inert chain: output IS input, so both meters read the same.
            input.CopyTo(output);
            _inPeak = inPk;
            _outPeak = inPk;
            return;
        }

        var needed = ctx.Frames * ctx.Channels;
        if (scratch.Length < needed)
        {
            // Caller-supplied scratch too small — pass through rather
            // than corrupt unrelated memory.
            input.CopyTo(output);
            _inPeak = inPk;
            _outPeak = inPk;
            return;
        }

        // Time the actual chain processing for the realtime DSP-load metric.
        long startTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        // Seed the chain by copying input into output; subsequent stages
        // ping-pong between `output` and `scratch`.
        input.CopyTo(output);

        // Track which buffer (output vs scratch) is the most-recently-written
        // one without tuple-swapping Span<float> (which is a ref struct
        // and disallowed in tuples).
        bool currentIsOutput = true;
        int nonFinite = 0;

        for (int i = 0; i < MaxSlots; i++)
        {
            var slot = _slots[i];
            var plugin = slot.Plugin;
            if (plugin is null || slot.Bypassed) continue;

            if (currentIsOutput)
            {
                var next = scratch[..needed];
                plugin.Process(output[..needed], next, ctx);
                nonFinite += RepairNonFiniteSamples(next);
            }
            else
            {
                var next = output[..needed];
                plugin.Process(scratch[..needed], next, ctx);
                nonFinite += RepairNonFiniteSamples(next);
            }

            currentIsOutput = !currentIsOutput;
        }

        if (!currentIsOutput)
        {
            scratch[..needed].CopyTo(output);
        }

        // Output meter tap (after the last active slot).
        _inPeak = inPk;
        _outPeak = BlockPeak(output[..needed]);

        // Publish realtime telemetry (single writer; lock-free, no alloc).
        long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - startTicks;
        Volatile.Write(ref _procTicksLast, elapsed);
        if (elapsed > Volatile.Read(ref _procTicksMax)) Volatile.Write(ref _procTicksMax, elapsed);
        Volatile.Write(ref _blocksProcessed, Volatile.Read(ref _blocksProcessed) + 1);
        if (nonFinite > 0)
            Volatile.Write(ref _nonFiniteRepairs, Volatile.Read(ref _nonFiniteRepairs) + nonFinite);
    }

    private static void ValidateIndex(int index)
    {
        if ((uint)index >= MaxSlots)
            throw new ArgumentOutOfRangeException(nameof(index), $"slot index must be in 0..{MaxSlots - 1}");
    }

    public async ValueTask DisposeAsync()
    {
        for (int i = 0; i < MaxSlots; i++)
        {
            var p = _slots[i].Plugin;
            _slots[i].Plugin = null;
            if (p is IAsyncDisposable ad) await ad.DisposeAsync().ConfigureAwait(false);
            else if (p is IDisposable d) d.Dispose();
        }
    }

    private sealed class ChainSlot
    {
        public IAudioPlugin? Plugin;
        public bool Bypassed;
    }
}
