// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// RADE V1 (Radio Autoencoder) modem as a streaming, frame-synchronous insert in
// the Zeus audio bus — the RADE twin of FreeDvModem, now full-duplex:
//   RX: received SSB audio -> decoded 16 kHz speech (radae_c + opus_dnn/FARGAN).
//   TX: mic speech -> RADE modem waveform (opus_dnn LPCNet analyzer + rade_tx),
//       transmitted as the REAL part of the modem IQ (inverse of the RX feed).
// Both directions absorb the 48 kHz <-> modem-rate and 48 kHz <-> 16 kHz speech
// rate changes internally with resamplers + fixed lock-free SPSC rings,
// silence-padded while starved. The End-of-Over callsign is carried by the
// FreeDV reliable-text LDPC (rade_text), so it interoperates with FreeDV-GUI.
//
// REALTIME DISCIPLINE — identical to FreeDvModem: the hot paths (ProcessRxInPlace
// / ProcessTxInPlace) take NO lock and allocate NOTHING; all scratch and ring
// storage is sized once when the modem opens (off the hot path). Reconfiguring
// (open/close) runs on a control thread serialised by _reconfigGate, handing each
// native handle off to its hot path through a Dekker-fenced seqlock: the control
// thread publishes IntPtr.Zero, full-fences, then spins until the busy flag
// clears before closing the old handle. When a handle is Zero, RX passes audio
// through UNCHANGED; TX SILENCES the block (never transmits raw mic on a RADE
// frequency).

using System.Threading;
using Microsoft.Extensions.Logging;

namespace Zeus.Dsp.FreeDv;

public sealed class RadeModem : IDisposable
{
    private readonly ILogger<RadeModem>? _log;
    private readonly object _reconfigGate = new();   // serialises control-thread reconfig only
    private readonly bool _nativeAvailable;

    private const int CallsignMax = 8;               // RADE_EOO_CALLSIGN_MAX

    // Output latency ceiling (48 kHz samples). ~250 ms — past this the modem
    // stalled or drifted; drop oldest to stay real-time.
    private const int MaxOutFifo = RadeResampler.FsHigh / 4;
    private const int MaxBlock = 8192;               // generous cap on a 48 kHz audio block

    // Native handles — published to the hot paths via Volatile; IntPtr.Zero means
    // "do not process" (closed / reconfiguring / native missing). RX and TX use
    // independent rade contexts so the two hot paths never contend on one handle.
    private IntPtr _rade = IntPtr.Zero;              // RX context
    private IntPtr _txRade = IntPtr.Zero;            // TX context
    private int _rxBusy;                             // 0/1 seqlock busy flag (hot path owns)
    private int _txBusy;

    private volatile bool _active;

    // RX chain (touched only by the hot path, or during reconfig while gated out).
    // 48k -> 8k decimation reuses the classic FreeDV decimator (6:1) for the
    // narrowband OFDM modem input. 16k -> 48k uses the wideband 3:1 interpolator
    // so FARGAN's full bandwidth survives.
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
    private readonly byte[] _eooScratch = new byte[CallsignMax + 1]; // hot-path EOO fetch buffer

    // TX chain. Mic 48k -> 16k speech (wideband 3:1 decimate) -> zeus_rade_tx ->
    // modem IQ @8k (real lane) -> 8k -> 48k (6:1 interpolate) -> playout.
    private readonly RadeResampler.Decimator _txDown = RadeResampler.NewDecimator();
    private readonly FreeDvResampler.Interpolator _txUp = FreeDvResampler.NewInterpolator();
    private readonly FreeDvSampleRing _tx16In = new(16384);   // 16 kHz speech awaiting zeus_rade_tx
    private readonly FreeDvSampleRing _txOut48 = new(16384);  // modem audio, 48 kHz, awaiting playout
    private readonly float[] _txDecScratch = new float[RadeResampler.MaxDecimatedLength(MaxBlock)];
    private float[] _tx16Float = new float[640];        // n_speech 16k samples pulled from the ring
    private short[] _tx16Short = new short[640];
    private float[] _txIqOut = new float[2 * 1024];     // interleaved complex modem IQ from the encoder
    private float[] _txModemReal = new float[1024];     // the real lane (transmitted SSB audio @8k)
    private float[] _txInterp = new float[2048];        // 48k after interpolation
    private int _txNSpeech;                             // cached at open: int16 @16k per tx call
    private int _txNTxOut;                              // cached at open: modem samples per tx call
    private int _txNEooOut;                             // cached at open: modem samples in the EOO frame

    // EOO callsign to embed in the TX frame (control thread writes; applied at
    // open and on change).
    private volatile string? _txCallsign;

    // Telemetry (lock-free reads)
    private volatile bool _synced;
    private double _snrDb;

    // Decoded RX callsign publish (hot path writes bytes + bumps _eooSeq; the
    // status thread rebuilds the string when the sequence changes — a seqlock, so
    // the hot path never allocates and the reader never tears).
    private readonly byte[] _rxEooBytes = new byte[CallsignMax + 1];
    private int _rxEooLen;
    private long _rxEooSeq;
    private long _rxEooSeenSeq;
    private string? _rxCallsign;
    private readonly object _rxCallsignLock = new();

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
    /// Last decoded End-of-Over callsign, or null if none decoded since the modem
    /// opened. Decoded by the native shim via the FreeDV reliable-text LDPC
    /// (CRC-checked, FreeDV-GUI compatible). Drained on the status thread.
    /// </summary>
    public string? RxText
    {
        get
        {
            long seq = Volatile.Read(ref _rxEooSeq);
            if (seq != _rxEooSeenSeq)
            {
                lock (_rxCallsignLock)
                {
                    // Re-read under the seqlock: copy the bytes, then confirm the
                    // sequence didn't move mid-copy. If it did, leave the cached
                    // value and let the next poll pick up the newer callsign.
                    Span<byte> buf = stackalloc byte[CallsignMax + 1];
                    int len = _rxEooLen;
                    if (len > CallsignMax) len = CallsignMax;
                    for (int i = 0; i < len; i++) buf[i] = _rxEooBytes[i];
                    long after = Volatile.Read(ref _rxEooSeq);
                    if (after == seq)
                    {
                        _rxCallsign = len > 0 ? System.Text.Encoding.ASCII.GetString(buf[..len]) : _rxCallsign;
                        _rxEooSeenSeq = seq;
                    }
                }
            }
            return _rxCallsign;
        }
    }

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
            RetireTx(IntPtr.Zero);
        }
    }

    /// <summary>
    /// No-op. RADE has no codec2-style SNR squelch; kept for API symmetry with
    /// FreeDvModem so callers can drive both modems uniformly. Inert.
    /// </summary>
    public void SetSquelch(bool? enabled, double? threshDb) { /* RADE has no squelch — inert */ }

    /// <summary>
    /// Set the End-of-Over callsign (&lt;= 8 chars) transmitted in the RADE EOO
    /// frame. Control thread only. Applied to the live TX context immediately if
    /// open, and re-applied at the next open. Empty/null clears it.
    /// </summary>
    public void SetTxText(string? text)
    {
        string? cs = NormalizeCallsign(text);
        _txCallsign = cs;
        lock (_reconfigGate)
        {
            IntPtr tx = Volatile.Read(ref _txRade);
            if (tx != IntPtr.Zero)
            {
                // Gate TX out while we mutate the EOO bits on the native side.
                Volatile.Write(ref _txRade, IntPtr.Zero);
                Thread.MemoryBarrier();
                SpinUntilIdle(ref _txBusy);
                RadeNativeMethods.zeus_rade_set_tx_callsign(tx, cs ?? string.Empty);
                Volatile.Write(ref _txRade, tx);
            }
        }
    }

    private static string? NormalizeCallsign(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().ToUpperInvariant();
        // The first line only (the FreeDV TX text often has a trailing CR/message);
        // RADE's EOO carries a single short callsign.
        int br = t.IndexOfAny(['\r', '\n', ' ']);
        if (br >= 0) t = t[..br];
        return t.Length > CallsignMax ? t[..CallsignMax] : t;
    }

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

                // Pull any freshly-decoded EOO callsign (cheap — just returns the
                // already-decoded string). Publish via the seqlock; no alloc.
                int csn = RadeNativeMethods.zeus_rade_get_eoo_callsign(h, _eooScratch);
                if (csn > 0)
                {
                    int n = csn > CallsignMax ? CallsignMax : csn;
                    for (int i = 0; i < n; i++) _rxEooBytes[i] = _eooScratch[i];
                    _rxEooLen = n;
                    Volatile.Write(ref _rxEooSeq, _rxEooSeq + 1);
                }

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
        Volatile.Write(ref _txBusy, 1);
        Thread.MemoryBarrier();
        try
        {
            IntPtr h = Volatile.Read(ref _txRade);
            // RADE active but TX not open (native missing / reconfiguring): SILENCE
            // rather than transmit raw mic — raw SSB on a RADE frequency is worse
            // than dead air.
            if (h == IntPtr.Zero) { block48k.Clear(); return; }

            int nspeech = _txNSpeech, ntx = _txNTxOut;
            if (nspeech <= 0 || ntx <= 0) { block48k.Clear(); return; }

            // 1. decimate mic 48k -> 16k speech into the speech ring.
            int n16 = _txDown.Process(block48k, _txDecScratch);
            _tx16In.Write(_txDecScratch.AsSpan(0, n16));

            // 2. encode whole frame-groups: 16k speech -> modem IQ; keep the real
            //    lane (the SSB audio) and upsample 8k -> 48k.
            while (_tx16In.Count >= nspeech)
            {
                _tx16In.Read(_tx16Float.AsSpan(0, nspeech));
                FloatToShort(_tx16Float, _tx16Short, nspeech);
                int n = RadeNativeMethods.zeus_rade_tx(h, _tx16Short, _txIqOut);
                if (n > ntx) n = ntx;
                for (int i = 0; i < n; i++) _txModemReal[i] = _txIqOut[2 * i];
                int up = _txUp.Process(_txModemReal.AsSpan(0, n), _txInterp);
                WriteBounded(_txOut48, _txInterp.AsSpan(0, up));
            }

            // 3. drain modem audio into the block; silence-pad the remainder.
            int filled = _txOut48.Read(block48k);
            if (filled < block48k.Length) block48k.Slice(filled).Clear();
        }
        finally
        {
            Volatile.Write(ref _txBusy, 0);
        }
    }

    /// <summary>
    /// Emit the End-of-Over frame (carrying the configured callsign) into the TX
    /// output so it transmits as the final modem audio of the over. Best-effort:
    /// the samples only reach the air if the TX tail keeps draining ProcessTx
    /// after the operator un-keys. Control thread; safe against the TX hot path.
    /// </summary>
    public void EmitEoo()
    {
        if (!_nativeAvailable) return;
        lock (_reconfigGate)
        {
            IntPtr tx = Volatile.Read(ref _txRade);
            if (tx == IntPtr.Zero || _txNEooOut <= 0) return;
            int n = RadeNativeMethods.zeus_rade_tx_eoo(tx, _txIqOut);
            if (n > _txNEooOut) n = _txNEooOut;
            for (int i = 0; i < n; i++) _txModemReal[i] = _txIqOut[2 * i];
            int up = _txUp.Process(_txModemReal.AsSpan(0, n), _txInterp);
            WriteBounded(_txOut48, _txInterp.AsSpan(0, up));
        }
    }

    /// <summary>
    /// Drop buffered TX audio on a MOX falling edge so the next over starts clean
    /// (mirrors FreeDvModem.FlushTx). Does NOT emit EOO — call EmitEoo first if a
    /// closing callsign is wanted.
    /// </summary>
    public void FlushTx()
    {
        if (!_nativeAvailable) return;
        lock (_reconfigGate)
        {
            IntPtr tx = Volatile.Read(ref _txRade);
            if (tx == IntPtr.Zero) return;
            Volatile.Write(ref _txRade, IntPtr.Zero);
            Thread.MemoryBarrier();
            SpinUntilIdle(ref _txBusy);
            _tx16In.Clear(); _txOut48.Clear();
            _txDown.Reset(); _txUp.Reset();
            Volatile.Write(ref _txRade, tx);
        }
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

    private static void FloatToShort(float[] src, short[] dst, int n)
    {
        for (int i = 0; i < n; i++)
            dst[i] = (short)Math.Clamp((int)MathF.Round(src[i] * 32767f), short.MinValue, short.MaxValue);
    }

    // -------- reconfig helpers (control thread, under _reconfigGate) --------

    private void OpenLocked()
    {
        if (!_nativeAvailable)
        {
            // Leave the handles Zero — RX passes through, TX silences.
            RetireRx(IntPtr.Zero);
            RetireTx(IntPtr.Zero);
            return;
        }

        IntPtr rx = RadeNativeMethods.zeus_rade_open();
        IntPtr tx = RadeNativeMethods.zeus_rade_open();
        if (rx == IntPtr.Zero || tx == IntPtr.Zero)
        {
            _log?.LogError("RADE: zeus_rade_open failed — passthrough (no decode/encode).");
            if (rx != IntPtr.Zero) RadeNativeMethods.zeus_rade_close(rx);
            if (tx != IntPtr.Zero) RadeNativeMethods.zeus_rade_close(tx);
            RetireRx(IntPtr.Zero);
            RetireTx(IntPtr.Zero);
            return;
        }

        // RX scratch from the new context (off the hot path).
        _ninMax = RadeNativeMethods.zeus_rade_nin_max(rx);
        int maxPcm = RadeNativeMethods.zeus_rade_max_pcm_per_rx(rx);
        EnsureFloat(ref _rx8Block, _ninMax);
        EnsureFloatZeroed(ref _rxIn, 2 * _ninMax);
        EnsureShort(ref _rxPcm, maxPcm);
        EnsureFloat(ref _rxSpeechFloat, maxPcm);
        EnsureFloat(ref _rxInterp, RadeResampler.InterpolatedLength(maxPcm));

        // TX scratch + sizes.
        _txNSpeech = RadeNativeMethods.zeus_rade_n_speech_samples(tx);
        _txNTxOut = RadeNativeMethods.zeus_rade_n_tx_out(tx);
        _txNEooOut = RadeNativeMethods.zeus_rade_n_tx_eoo_out(tx);
        int txMax = Math.Max(_txNTxOut, _txNEooOut);
        EnsureFloat(ref _tx16Float, _txNSpeech);
        EnsureShort(ref _tx16Short, _txNSpeech);
        EnsureFloat(ref _txIqOut, 2 * txMax);
        EnsureFloat(ref _txModemReal, txMax);
        EnsureFloat(ref _txInterp, FreeDvResampler.InterpolatedLength(txMax));

        // Apply the operator's EOO callsign to the fresh TX context before it goes
        // live.
        RadeNativeMethods.zeus_rade_set_tx_callsign(tx, _txCallsign ?? string.Empty);

        _synced = false;
        Volatile.Write(ref _snrDb, 0d);

        RetireRx(rx);
        RetireTx(tx);

        _log?.LogInformation(
            "RADE: opened ninMax={NinMax} maxPcm={MaxPcm} txSpeech={TxSpeech} txOut={TxOut} eooOut={EooOut}",
            _ninMax, maxPcm, _txNSpeech, _txNTxOut, _txNEooOut);
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

    private void RetireTx(IntPtr replacement)
    {
        IntPtr old = Volatile.Read(ref _txRade);
        Volatile.Write(ref _txRade, IntPtr.Zero);
        Thread.MemoryBarrier();
        SpinUntilIdle(ref _txBusy);
        if (old != IntPtr.Zero) RadeNativeMethods.zeus_rade_close(old);
        _tx16In.Clear(); _txOut48.Clear();
        _txDown.Reset(); _txUp.Reset();
        Volatile.Write(ref _txRade, replacement);
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
            RetireTx(IntPtr.Zero);
        }
    }
}
