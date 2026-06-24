// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// RADE V1 (Radio Autoencoder) modem as a streaming, frame-synchronous insert in
// the Zeus audio bus — the RADE twin of FreeDvModem. RX ONLY this pass: it turns
// received SSB audio into decoded 16 kHz speech via the native zeus_rade shim
// (radae_c + opus_dnn/FARGAN), absorbing the 48 kHz <-> 8 kHz modem-rate and
// 16 kHz -> 48 kHz speech-rate changes internally with resamplers + fixed
// lock-free SPSC rings, silence-padded while starved (before sync). RADE TX is a
// follow-up; ProcessTxInPlace silences the block when active (does NOT transmit
// raw mic).
//
// REALTIME DISCIPLINE — identical to FreeDvModem: the hot path (ProcessRxInPlace)
// takes NO lock and allocates NOTHING; all scratch and ring storage is sized once
// when the modem opens (off the hot path). Reconfiguring (open/close) runs on a
// control thread serialised by _reconfigGate, handing the native handle off to
// the hot path through a Dekker-fenced seqlock: the control thread publishes
// IntPtr.Zero, full-fences, then spins until the busy flag clears before closing
// the old handle. The realtime thread never spins or blocks — it sets a busy flag
// and reads the published handle. When the handle is Zero (native missing /
// reconfiguring), RX passes audio through UNCHANGED.

using System.Threading;
using Microsoft.Extensions.Logging;

namespace Zeus.Dsp.FreeDv;

public sealed class RadeModem : IDisposable
{
    private readonly ILogger<RadeModem>? _log;
    private readonly object _reconfigGate = new();   // serialises control-thread reconfig only
    private readonly bool _nativeAvailable;

    // Output latency ceiling (48 kHz samples). ~250 ms — past this the modem
    // stalled or drifted; drop oldest to stay real-time.
    private const int MaxOutFifo = RadeResampler.FsHigh / 4;
    private const int MaxBlock = 8192;               // generous cap on a 48 kHz audio block

    // Native handle — published to the hot path via Volatile; IntPtr.Zero means
    // "do not process" (closed / reconfiguring / native missing).
    private IntPtr _rade = IntPtr.Zero;
    private int _rxBusy;                              // 0/1 seqlock busy flag (hot path owns)

    private volatile bool _active;

    // RX chain (touched only by the hot path, or during reconfig while gated out).
    // 48k -> 8k decimation reuses the classic FreeDV decimator (6:1, voice-band
    // low-pass is fine for the narrowband OFDM modem input). 16k -> 48k uses the
    // dedicated wideband 3:1 interpolator so FARGAN's full bandwidth survives.
    private readonly FreeDvResampler.Decimator _rxDown = FreeDvResampler.NewDecimator();
    private readonly RadeResampler.Interpolator _rxUp = RadeResampler.NewInterpolator();
    private readonly FreeDvSampleRing _rx8In = new(8192);     // 8 kHz modem samples awaiting zeus_rade_rx
    private readonly FreeDvSampleRing _rxOut48 = new(16384);  // decoded speech, 48 kHz, awaiting playout
    private readonly float[] _rxDecScratch = new float[FreeDvResampler.MaxDecimatedLength(MaxBlock)];
    private float[] _rx8Block = new float[256];       // nin 8k real samples pulled from the ring
    private float[] _rxIn = new float[512];            // interleaved complex IQ (2 * ninMax), imag stays 0
    private short[] _rxPcm = new short[256];           // int16 PCM @16k from the decoder
    private float[] _rxSpeechFloat = new float[256];   // PCM as float
    private float[] _rxInterp = new float[256];        // 48k after wideband interpolation
    private int _ninMax;                               // cached at open

    // Telemetry (lock-free reads)
    private volatile bool _synced;
    private double _snrDb;

    public RadeModem(ILogger<RadeModem>? log = null)
    {
        _log = log;
        _nativeAvailable = FreeDvNativeLoader.TryProbeRade();
        if (_nativeAvailable)
        {
            RadeNativeMethods.zeus_rade_global_init();
        }
        else
        {
            _log?.LogWarning("RADE: zeus_rade native library not found; RADE V1 will pass audio through unchanged.");
        }
    }

    /// <summary>True when the native zeus_rade shim is loadable and RADE can decode.</summary>
    public bool RadeAvailable => _nativeAvailable;
    public bool Active => _active;
    public bool Synced => _synced;
    public double SnrDb => Volatile.Read(ref _snrDb);
    public int SpeechSampleRateHz => 16000;
    public int ModemSampleRateHz => 8000;
    public string? LibraryVersion => _nativeAvailable ? "RADE V1 (radae_c)" : null;

    /// <summary>
    /// RX text sidechannel. RADE carries an End-of-Over callsign, but the
    /// zeus_rade EOO decode is KNOWN-GARBLED until freedv_text is wired, so it is
    /// not surfaced yet — always null.
    /// </summary>
    public string? RxText => null;

    // -------- control thread (rare; serialised by _reconfigGate) --------

    public void Activate()
    {
        if (!_nativeAvailable) { _active = true; return; }
        lock (_reconfigGate)
        {
            OpenLocked();
            _active = true;
        }
    }

    public void Deactivate()
    {
        _active = false;
        lock (_reconfigGate)
        {
            RetireRx(IntPtr.Zero);
        }
    }

    /// <summary>
    /// No-op. RADE has no codec2-style SNR squelch; kept for API symmetry with
    /// FreeDvModem so callers can drive both modems uniformly. Inert.
    /// </summary>
    public void SetSquelch(bool? enabled, double? threshDb) { /* RADE has no squelch — inert */ }

    // -------- RX hot path (DSP tick thread; no lock, no alloc) --------

    public void ProcessRxInPlace(Span<float> block48k)
    {
        if (!_active) return;
        Volatile.Write(ref _rxBusy, 1);
        Thread.MemoryBarrier(); // StoreLoad fence vs. the reconfig handoff
        try
        {
            IntPtr h = Volatile.Read(ref _rade);
            if (h == IntPtr.Zero) return; // passthrough (native missing / reconfiguring) — leave block unchanged

            // 1. decimate 48k -> 8k real into the 8k input ring.
            int n8 = _rxDown.Process(block48k, _rxDecScratch);
            _rx8In.Write(_rxDecScratch.AsSpan(0, n8));

            // 2. drain the 8k ring through the decoder, nin samples at a time.
            int nin = RadeNativeMethods.zeus_rade_nin(h);
            while (nin > 0 && nin <= _ninMax && _rx8In.Count >= nin)
            {
                _rx8In.Read(_rx8Block.AsSpan(0, nin));
                // Pack as interleaved complex: real = sample, imag = 0. Odd indices
                // were pre-zeroed at open and the decoder never writes rx_in, so
                // they stay 0 across calls — only the real lanes need refreshing.
                for (int i = 0; i < nin; i++) _rxIn[2 * i] = _rx8Block[i];

                int npcm = RadeNativeMethods.zeus_rade_rx(h, _rxIn, _rxPcm);
                _synced = RadeNativeMethods.zeus_rade_sync(h) != 0;
                Volatile.Write(ref _snrDb, RadeNativeMethods.zeus_rade_snr_db(h));

                if (npcm > 0)
                {
                    ShortToFloat(_rxPcm, _rxSpeechFloat, npcm);
                    int up = _rxUp.Process(_rxSpeechFloat.AsSpan(0, npcm), _rxInterp);
                    WriteBounded(_rxOut48, _rxInterp.AsSpan(0, up));
                }
                nin = RadeNativeMethods.zeus_rade_nin(h);
            }

            // 3. read decoded speech into the block; silence-pad the remainder
            // (decoded speech replaces the demod audio, exactly like FreeDvModem).
            int filled = _rxOut48.Read(block48k);
            if (filled < block48k.Length) block48k.Slice(filled).Clear();
        }
        finally
        {
            Volatile.Write(ref _rxBusy, 0);
        }
    }

    // -------- TX hot path (mic-ingest thread; no lock, no alloc) --------

    public void ProcessTxInPlace(Span<float> block48k)
    {
        if (!_active) return;
        // TODO RADE TX (encode + EOO): RADE TX is not implemented this pass. When
        // active, silence the block rather than transmitting raw mic audio (which
        // would put plain SSB on a RADE frequency). Passthrough when inactive.
        block48k.Clear();
    }

    private static void WriteBounded(FreeDvSampleRing ring, ReadOnlySpan<float> src)
    {
        if (ring.Count > MaxOutFifo) ring.Drop(ring.Count - MaxOutFifo);
        ring.Write(src); // short write (ring full) acts as a final safety drop
    }

    private static void ShortToFloat(short[] src, float[] dst, int n)
    {
        for (int i = 0; i < n; i++) dst[i] = src[i] * (1f / 32768f);
    }

    // -------- reconfig helpers (control thread, under _reconfigGate) --------

    private void OpenLocked()
    {
        if (!_nativeAvailable)
        {
            // Leave the handle Zero — the hot path runs as a clean passthrough.
            RetireRx(IntPtr.Zero);
            return;
        }

        IntPtr h = RadeNativeMethods.zeus_rade_open();
        if (h == IntPtr.Zero)
        {
            _log?.LogError("RADE: zeus_rade_open failed — passthrough (no decode).");
            RetireRx(IntPtr.Zero);
            return;
        }

        // Size scratch from the new context (off the hot path).
        _ninMax = RadeNativeMethods.zeus_rade_nin_max(h);
        int maxPcm = RadeNativeMethods.zeus_rade_max_pcm_per_rx(h);

        EnsureFloat(ref _rx8Block, _ninMax);
        // 2 * ninMax interleaved complex floats; pre-zeroed so imag lanes stay 0.
        EnsureFloatZeroed(ref _rxIn, 2 * _ninMax);
        EnsureShort(ref _rxPcm, maxPcm);
        EnsureFloat(ref _rxSpeechFloat, maxPcm);
        EnsureFloat(ref _rxInterp, RadeResampler.InterpolatedLength(maxPcm));

        _synced = false;
        Volatile.Write(ref _snrDb, 0d);

        RetireRx(h);

        _log?.LogInformation(
            "RADE: opened ninMax={NinMax} maxPcm={MaxPcm} modemFs={ModemFs} speechFs={SpeechFs}",
            _ninMax, maxPcm, ModemSampleRateHz, SpeechSampleRateHz);
    }

    // Gate the RX hot path out, close the old handle, reset RX buffers, publish
    // the replacement (IntPtr.Zero to leave RX disengaged / passthrough).
    private void RetireRx(IntPtr replacement)
    {
        IntPtr old = Volatile.Read(ref _rade);
        Volatile.Write(ref _rade, IntPtr.Zero);
        Thread.MemoryBarrier();
        SpinUntilIdle(ref _rxBusy);
        if (old != IntPtr.Zero) RadeNativeMethods.zeus_rade_close(old);
        _rx8In.Clear(); _rxOut48.Clear();
        _rxDown.Reset(); _rxUp.Reset();
        Volatile.Write(ref _rade, replacement);
    }

    private static void SpinUntilIdle(ref int busyFlag)
    {
        var sw = new SpinWait();
        while (Volatile.Read(ref busyFlag) == 1) sw.SpinOnce();
    }

    private static void EnsureFloat(ref float[] buf, int n)
    {
        if (buf.Length < n) buf = new float[Math.Max(n, buf.Length * 2)];
    }

    // For the interleaved IQ buffer: a fresh allocation is zeroed by the runtime,
    // so a grow leaves all lanes (including imag) at 0. When the existing buffer
    // is large enough, re-zero it so a reopen can't leave a stale imag lane.
    private static void EnsureFloatZeroed(ref float[] buf, int n)
    {
        if (buf.Length < n) buf = new float[Math.Max(n, buf.Length * 2)];
        else Array.Clear(buf, 0, buf.Length);
    }

    private static void EnsureShort(ref short[] buf, int n)
    {
        if (buf.Length < n) buf = new short[Math.Max(n, buf.Length * 2)];
    }

    public void Dispose()
    {
        _active = false;
        lock (_reconfigGate)
        {
            RetireRx(IntPtr.Zero);
        }
    }
}
