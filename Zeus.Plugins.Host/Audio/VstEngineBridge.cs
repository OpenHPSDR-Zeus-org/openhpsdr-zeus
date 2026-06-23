using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Zeus.Plugins.Contracts.Audio;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// Zeus side of the VST-engine audio plane: owns the shared-memory
/// double-buffer + the two auto-reset events, and exposes the realtime
/// <see cref="Process"/> tap. Zeus CREATES the shared memory + events; the
/// engine OPENS them. See <c>docs/designs/vst-engine-bridge-protocol.md</c>.
///
/// <para><b>Robust-path contract (load-bearing):</b> the realtime TX thread
/// NEVER blocks unbounded and NEVER allocates here. Each block is a memcpy-in →
/// signal → <em>bounded</em> wait → memcpy-out; on timeout / engine-down /
/// stale-seq it falls through to clean passthrough and keeps feeding the radio.
/// The view pointer + event handles are acquired once and held for the bridge's
/// lifetime. Windows-only (named shared memory + events).</para>
/// </summary>
internal sealed unsafe class VstEngineBridge : IVstEngineBridge
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly EventWaitHandle _inEvent;
    private readonly EventWaitHandle _outEvent;

    private byte* _base;
    private readonly int _maxFrames;
    private readonly int _channels;
    private readonly int _regionBytes;

    private ulong _inSeq;
    private long _degraded;
    private int _disposed;
    private int _engineReady;

    /// <summary>
    /// Bounded wait ceiling per block (ms). A wedged plugin costs at most one
    /// block of passthrough, never a dropped radio. Kept well under the block
    /// period (512f @ 48 kHz ≈ 10.6 ms).
    /// </summary>
    public int WaitBudgetMs { get; set; } = 4;

    /// <summary>
    /// Set true by the controller once the engine's <c>ready</c> handshake is
    /// seen (the engine's audio thread is then blocked on <c>.in</c>). While
    /// false, <see cref="Process"/> is pure passthrough — the realtime path
    /// never touches the engine.
    /// </summary>
    public bool EngineReady
    {
        get => Volatile.Read(ref _engineReady) != 0;
        set => Volatile.Write(ref _engineReady, value ? 1 : 0);
    }

    /// <summary>Count of blocks that fell through to passthrough (timeout / stale).</summary>
    public long DegradedBlocks => Interlocked.Read(ref _degraded);

    /// <summary>
    /// Engine-written lifecycle state (OffEngineState: 0=init 1=running 2=draining).
    /// Diagnostics-only reader — capture a stable base pointer and null-check it so a
    /// race with <see cref="Dispose"/> degrades to 0 instead of faulting on a torn map.
    /// </summary>
    public uint EngineState
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return 0;
            var b = _base;
            return b == null ? 0u : *(uint*)(b + VstEngineProtocol.OffEngineState);
        }
    }

    /// <summary>Engine-written flags (OffFlags: bit0 = bypassed / empty chain). Diagnostics-only.</summary>
    public uint EngineFlags
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return 0;
            var b = _base;
            return b == null ? 0u : *(uint*)(b + VstEngineProtocol.OffFlags);
        }
    }

    public string ShmName { get; }

    private VstEngineBridge(string shm, MemoryMappedFile mmf, MemoryMappedViewAccessor view,
                            EventWaitHandle inEv, EventWaitHandle outEv,
                            int maxFrames, int channels)
    {
        ShmName = shm;
        _mmf = mmf;
        _view = view;
        _inEvent = inEv;
        _outEvent = outEv;
        _maxFrames = maxFrames;
        _channels = channels;
        _regionBytes = VstEngineProtocol.RegionBytes(maxFrames, channels);

        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _view.PointerOffset;
    }

    /// <summary>Create the shared memory + events and initialise the header.</summary>
    public static VstEngineBridge Create(string shm, int maxFrames, int rate, int channels)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("VST engine bridge is Windows-only.");
        if (maxFrames <= 0 || channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFrames));

        long total = VstEngineProtocol.TotalBytes(maxFrames, channels);
        var mmf = MemoryMappedFile.CreateNew(shm, total);
        MemoryMappedViewAccessor? view = null;
        EventWaitHandle? inEv = null, outEv = null;
        try
        {
            view = mmf.CreateViewAccessor(0, total, MemoryMappedFileAccess.ReadWrite);
            inEv = new EventWaitHandle(false, EventResetMode.AutoReset, VstEngineProtocol.InEventName(shm));
            outEv = new EventWaitHandle(false, EventResetMode.AutoReset, VstEngineProtocol.OutEventName(shm));

            var bridge = new VstEngineBridge(shm, mmf, view, inEv, outEv, maxFrames, channels);
            bridge.InitHeader(rate);
            return bridge;
        }
        catch
        {
            outEv?.Dispose();
            inEv?.Dispose();
            view?.Dispose();
            mmf.Dispose();
            throw;
        }
    }

    private void InitHeader(int rate)
    {
        WriteU32(VstEngineProtocol.OffMagic, VstEngineProtocol.Magic);
        WriteU32(VstEngineProtocol.OffProtocol, VstEngineProtocol.Version);
        WriteU32(VstEngineProtocol.OffMaxFrames, (uint)_maxFrames);
        WriteU32(VstEngineProtocol.OffChannels, (uint)_channels);
        WriteU32(VstEngineProtocol.OffRate, (uint)rate);
        WriteU32(VstEngineProtocol.OffFramesThisBlock, 0);
        WriteU64(VstEngineProtocol.OffInSeq, 0);
        WriteU64(VstEngineProtocol.OffOutSeq, 0);
        WriteU32(VstEngineProtocol.OffEngineState, 0);
        WriteU32(VstEngineProtocol.OffFlags, 0);
    }

    /// <summary>
    /// Realtime tap. Sends one block to the engine and reads the processed
    /// result back, or passes through unchanged on any failure. Returns true
    /// only when the output came from the engine. No allocation, no managed
    /// lock. See the robust-path contract on the class.
    /// </summary>
    public bool Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)
    {
        int n = ctx.Frames, ch = ctx.Channels;
        int count = n * ch;

        if (Volatile.Read(ref _disposed) != 0 || !EngineReady
            || n <= 0 || n > _maxFrames || ch != _channels
            || input.Length < count || output.Length < count)
        {
            input.CopyTo(output);
            return false;
        }

        // Drain any stale .out left signaled by a LATE response to an earlier
        // block. Without this, a single timeout permanently desyncs the bridge:
        // the engine ends up one block behind, every .out we then wait on carries
        // the previous block's seq, so every block fails the seq check and we wedge
        // into 100% passthrough that never recovers even after the load drops.
        // Draining first means the next signal we wait on is for THIS block.
        while (_outEvent.WaitOne(0)) { /* discard stale signal */ }

        // input → shared input region
        WriteU32(VstEngineProtocol.OffFramesThisBlock, (uint)n);
        var inRegion = new Span<float>(_base + VstEngineProtocol.HeaderBytes, count);
        input.Slice(0, count).CopyTo(inRegion);

        // publish: stamp seq, then wake the engine
        _inSeq++;
        WriteU64(VstEngineProtocol.OffInSeq, _inSeq);
        _inEvent.Set();

        // Bounded wait for OUR response (matching seq). A late response for an
        // earlier block is skipped, but we keep waiting WITHIN the same budget so
        // one slow block costs at most one passthrough — not a permanent cascade.
        // Never block the radio longer than WaitBudgetMs total.
        long deadline = Stopwatch.GetTimestamp() + (long)(WaitBudgetMs * 1e-3 * Stopwatch.Frequency);
        while (true)
        {
            long remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                input.CopyTo(output);
                Interlocked.Increment(ref _degraded);
                return false;
            }
            int remainingMs = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
            if (!_outEvent.WaitOne(remainingMs <= 0 ? 1 : remainingMs))
            {
                input.CopyTo(output);
                Interlocked.Increment(ref _degraded);
                return false;
            }
            // only trust output whose seq matches the block we just sent
            if (ReadU64(VstEngineProtocol.OffOutSeq) == _inSeq) break;
            // stale late response — loop and keep waiting within the budget
        }

        var outRegion = new Span<float>(_base + VstEngineProtocol.HeaderBytes + _regionBytes, count);
        outRegion.CopyTo(output.Slice(0, count));
        return true;
    }

    /// <summary>
    /// Disarm the gate and drain any stale <c>.in</c>/<c>.out</c> signals left by
    /// a crashed engine so the relaunched engine starts from a clean sequence.
    /// The realtime tap must already be disarmed (engine down) when this runs —
    /// the controller only calls it between process exit and re-arm.
    /// </summary>
    public void ResetForRelaunch()
    {
        EngineReady = false;
        if (Volatile.Read(ref _disposed) != 0) return;
        // Discard any signals the dead engine (or our last unanswered Set) left
        // pending, so the first block after relaunch waits on ITS own response.
        while (_outEvent.WaitOne(0)) { }
        while (_inEvent.WaitOne(0)) { }
    }

    private void WriteU32(int off, uint v) => *(uint*)(_base + off) = v;
    private uint ReadU32(int off) => *(uint*)(_base + off);
    private void WriteU64(int off, ulong v) => *(ulong*)(_base + off) = v;
    private ulong ReadU64(int off) => *(ulong*)(_base + off);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        EngineReady = false;

        if (_base != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
        }
        _outEvent.Dispose();
        _inEvent.Dispose();
        _view.Dispose();
        _mmf.Dispose();
    }
}
