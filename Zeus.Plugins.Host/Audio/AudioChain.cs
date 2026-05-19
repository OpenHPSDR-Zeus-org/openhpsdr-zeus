using Zeus.Plugins.Contracts.Audio;
using Zeus.Plugins.Contracts.Extensions;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// Serial chain of <see cref="IAudioPlugin"/> instances with master
/// enable + per-slot bypass. The realtime <see cref="Process"/> method
/// allocates nothing, takes no locks, and short-circuits to a single
/// memcpy when master enable is false — matching the bit-identical
/// pass-through requirement from the v1 ADR (§5.7).
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
    private volatile bool _masterEnabled = true;

    public AudioChain(int maxFrames = 4096, int maxChannels = 2)
    {
        _scratch = new float[maxFrames * maxChannels];
        for (int i = 0; i < MaxSlots; i++) _slots[i] = new ChainSlot();
    }

    public int SlotCount => MaxSlots;

    public bool MasterEnabled
    {
        get => _masterEnabled;
        set => _masterEnabled = value;
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
    /// When master is disabled, this is a single <c>input.CopyTo(output)</c>
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

        if (!_masterEnabled)
        {
            input.CopyTo(output);
            return;
        }

        var needed = ctx.Frames * ctx.Channels;
        if (scratch.Length < needed)
        {
            // Caller-supplied scratch too small — pass through rather
            // than corrupt unrelated memory.
            input.CopyTo(output);
            return;
        }

        // Seed the chain by copying input into output; subsequent stages
        // ping-pong between `output` and `scratch`.
        input.CopyTo(output);

        // Track which buffer (output vs scratch) is the most-recently-written
        // one without tuple-swapping Span<float> (which is a ref struct
        // and disallowed in tuples).
        bool currentIsOutput = true;

        for (int i = 0; i < MaxSlots; i++)
        {
            var slot = _slots[i];
            var plugin = slot.Plugin;
            if (plugin is null || slot.Bypassed) continue;

            if (currentIsOutput)
                plugin.Process(output[..needed], scratch[..needed], ctx);
            else
                plugin.Process(scratch[..needed], output[..needed], ctx);

            currentIsOutput = !currentIsOutput;
        }

        if (!currentIsOutput)
        {
            scratch[..needed].CopyTo(output);
        }
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
