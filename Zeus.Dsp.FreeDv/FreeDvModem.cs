// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// FreeDV modem as a streaming, frame-synchronous insert in the Zeus audio bus.
// The radio runs USB underneath; this class turns received SSB audio into
// decoded speech (RX) and mic speech into a transmitted modem signal (TX).
// Both directions accept fixed 48 kHz mono blocks and return the same sample
// count — the rate change to FreeDV's 8 kHz modem rate and FreeDV's variable
// frame sizes are absorbed internally with resamplers + fixed lock-free SPSC
// rings, silence-padded while starved (e.g. before RX sync) and latency-bounded.
//
// REALTIME DISCIPLINE — matches the rest of the Zeus audio bus (AudioChain,
// FloatSpscRing, VstEngineController): the hot path (ProcessRxInPlace /
// ProcessTxInPlace) takes NO lock and allocates NOTHING. All scratch and ring
// storage is sized once when the modem opens (off the hot path). Reconfiguring
// (open/close/submode change) runs on a control thread, serialised by
// _reconfigGate, and hands the native handle off to the hot path through a
// Dekker-fenced seqlock: the control thread publishes IntPtr.Zero, full-fences,
// then spins until the per-direction busy flag clears before closing the old
// handle. The realtime thread never spins or blocks — it only sets a busy flag
// and reads the published handle. Squelch changes are applied lazily by the hot
// path via a dirty flag, so they need no handle swap.

using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Dsp.FreeDv;

public sealed class FreeDvModem : IDisposable
{
    private readonly ILogger<FreeDvModem>? _log;
    private readonly object _reconfigGate = new();   // serialises control-thread reconfig only
    private readonly bool _nativeAvailable;

    // Output latency ceiling per direction (48 kHz samples). ~250 ms — past this
    // the modem stalled or drifted; drop oldest to stay real-time.
    private const int MaxOutFifo = FreeDvResampler.FsHigh / 4;
    private const int MaxBlock = 8192;               // generous cap on a 48 kHz audio block

    // Native handles — published to the hot path via Volatile; IntPtr.Zero means
    // "do not process" (closed / reconfiguring / native missing).
    private IntPtr _rxFreedv = IntPtr.Zero;
    private IntPtr _txFreedv = IntPtr.Zero;
    private int _rxBusy;                              // 0/1 seqlock busy flag (hot path owns)
    private int _txBusy;

    private FreeDvSubmode _submode = FreeDvSubmode.Mode700D;
    private volatile bool _active;

    // RX chain (touched only by the hot path, or during reconfig while gated out)
    private readonly FreeDvResampler.Decimator _rxDown = FreeDvResampler.NewDecimator();
    private readonly FreeDvResampler.Interpolator _rxUp = FreeDvResampler.NewInterpolator();
    private readonly FreeDvSampleRing _rx8In = new(8192);    // 8 kHz modem samples awaiting freedv_rx
    private readonly FreeDvSampleRing _rxOut48 = new(16384);  // decoded speech, 48 kHz, awaiting playout
    private readonly float[] _rxDecScratch = new float[FreeDvResampler.MaxDecimatedLength(MaxBlock)];
    private short[] _rxModemShort = new short[256];
    private float[] _rxModemFloat = new float[256];
    private short[] _rxSpeechShort = new short[256];
    private float[] _rxSpeechFloat = new float[256];
    private float[] _rxInterp = new float[256];

    // TX chain
    private readonly FreeDvResampler.Decimator _txDown = FreeDvResampler.NewDecimator();
    private readonly FreeDvResampler.Interpolator _txUp = FreeDvResampler.NewInterpolator();
    private readonly FreeDvSampleRing _tx8In = new(8192);
    private readonly FreeDvSampleRing _txOut48 = new(16384);
    private readonly float[] _txDecScratch = new float[FreeDvResampler.MaxDecimatedLength(MaxBlock)];
    private short[] _txSpeechShort = new short[256];
    private float[] _txSpeechFloat = new float[256];
    private short[] _txModemShort = new short[256];
    private float[] _txModemFloat = new float[256];
    private float[] _txInterp = new float[256];
    private int _txNSpeech;                            // cached at open
    private int _txNNomModem;

    // Telemetry (lock-free reads)
    private volatile bool _synced;
    private float _snrDb;
    private int _speechRate = FreeDvResampler.FsLow;
    private int _modemRate = FreeDvResampler.FsLow;

    // Config (control thread writes; hot path applies squelch lazily)
    private bool _squelchEnabled = true;
    private float _snrSquelchThresh = DefaultSquelchThresh(FreeDvSubmode.Mode700D);
    private int _squelchDirty;

    // ---- Text sidechannel (FreeDV low-bit-rate "txt" stream: callsign etc.) ----
    // The codec2 modem carries a slow varicode text stream alongside voice. The
    // RX/TX callbacks fire INSIDE freedv_rx / freedv_tx on the hot path, so they
    // are strictly lock-free and allocation-free. The delegate instances are held
    // in fields for the modem's lifetime so the GC can never collect them while
    // native code holds their function pointers.
    private readonly FreeDvNativeMethods.FreeDvRxTextCallback _rxTextCb;
    private readonly FreeDvNativeMethods.FreeDvTxTextCallback _txTextCb;
    private readonly IntPtr _rxTextCbPtr;
    private readonly IntPtr _txTextCbPtr;
    // TX text to transmit, ASCII + trailing CR delimiter. Control thread swaps the
    // reference; the hot-path TX callback Volatile.Reads it and cycles forever.
    private byte[] _txTextBytes = Array.Empty<byte>();
    private int _txTextPos;                              // hot-path-owned cursor
    // RX decoded chars: hot path enqueues, the status thread drains + assembles.
    private readonly FreeDvByteRing _rxTextRing = new(256);
    private readonly object _rxTextLock = new();         // status-thread reassembly only
    private readonly StringBuilder _rxTextWork = new(80);
    private string? _rxTextLine;

    public FreeDvModem(ILogger<FreeDvModem>? log = null)
    {
        _log = log;
        _nativeAvailable = FreeDvNativeLoader.TryProbe();
        if (!_nativeAvailable)
            _log?.LogWarning("FreeDV: codec2 native library not found; FreeDV mode will pass audio through unchanged.");
        _rxTextCb = OnRxTextChar;
        _txTextCb = OnTxTextChar;
        _rxTextCbPtr = Marshal.GetFunctionPointerForDelegate(_rxTextCb);
        _txTextCbPtr = Marshal.GetFunctionPointerForDelegate(_txTextCb);
    }

    // Hot path (inside freedv_rx). Single-byte lock-free enqueue; drop on overflow.
    private void OnRxTextChar(IntPtr state, byte c) => _rxTextRing.WriteByte(c);

    // Hot path (inside freedv_tx). Returns the next char of the repeating TX text,
    // or 0 when no text is configured (codec2 sends an idle txt stream).
    private byte OnTxTextChar(IntPtr state)
    {
        var bytes = Volatile.Read(ref _txTextBytes);
        if (bytes.Length == 0) return 0;
        int pos = _txTextPos;
        byte c = bytes[pos % bytes.Length];
        _txTextPos = pos + 1;
        return c;
    }

    /// <summary>
    /// Set the low-bit-rate TX text (callsign / short message). Control-thread
    /// only. Encoded as 7-bit ASCII with a trailing CR so the receiver gets a
    /// line delimiter; published atomically to the hot-path TX callback.
    /// </summary>
    public void SetTxText(string? text)
    {
        byte[] bytes;
        if (string.IsNullOrEmpty(text))
        {
            bytes = Array.Empty<byte>();
        }
        else
        {
            var t = text.Length > 63 ? text[..63] : text;
            bytes = new byte[t.Length + 1];
            for (int i = 0; i < t.Length; i++) bytes[i] = (byte)(t[i] & 0x7f);
            bytes[t.Length] = (byte)'\r';
        }
        Volatile.Write(ref _txTextBytes, bytes);
    }

    /// <summary>
    /// Last fully-received line of RX text (callsign etc.), or null if none has
    /// been decoded yet. Drains the lock-free RX-text ring and reassembles on the
    /// caller's (status) thread — never touched by the hot path beyond the ring
    /// enqueue. Guarded so concurrent status polls don't race the reassembly.
    /// </summary>
    public string? RxText
    {
        get
        {
            lock (_rxTextLock)
            {
                Span<byte> buf = stackalloc byte[64];
                int n;
                while ((n = _rxTextRing.Read(buf)) > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        char c = (char)buf[i];
                        if (c is '\r' or '\n')
                        {
                            if (_rxTextWork.Length > 0)
                            {
                                _rxTextLine = _rxTextWork.ToString();
                                _rxTextWork.Clear();
                            }
                        }
                        else if (c is >= ' ' and < (char)127)
                        {
                            if (_rxTextWork.Length >= 64) _rxTextWork.Clear();
                            _rxTextWork.Append(c);
                        }
                    }
                }
                return _rxTextLine;
            }
        }
    }

    public bool NativeAvailable => _nativeAvailable;
    public bool Active => _active;
    public FreeDvSubmode Submode => _submode;
    public bool Synced => _synced;
    public double SnrDb => Volatile.Read(ref _snrDb);
    public bool SquelchEnabled => _squelchEnabled;
    public double SnrSquelchThreshDb => _snrSquelchThresh;
    public int SpeechSampleRateHz => _speechRate;
    public int ModemSampleRateHz => _modemRate;
    public string? LibraryVersion => _nativeAvailable ? "codec2 1.2.0 (FreeDV)" : null;

    private static int ToNativeMode(FreeDvSubmode m) => m switch
    {
        FreeDvSubmode.Mode700D => FreeDvNativeMethods.FREEDV_MODE_700D,
        FreeDvSubmode.Mode700E => FreeDvNativeMethods.FREEDV_MODE_700E,
        FreeDvSubmode.Mode700C => FreeDvNativeMethods.FREEDV_MODE_700C,
        FreeDvSubmode.Mode1600 => FreeDvNativeMethods.FREEDV_MODE_1600,
        FreeDvSubmode.Mode800XA => FreeDvNativeMethods.FREEDV_MODE_800XA,
        _ => FreeDvNativeMethods.FREEDV_MODE_700D,
    };

    private static float DefaultSquelchThresh(FreeDvSubmode m) => m switch
    {
        FreeDvSubmode.Mode700D => -2.0f,
        FreeDvSubmode.Mode700E => 1.0f,
        FreeDvSubmode.Mode700C => 2.0f,
        FreeDvSubmode.Mode1600 => 4.0f,
        FreeDvSubmode.Mode800XA => 2.0f,
        _ => 0.0f,
    };

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

    public void SetSubmode(FreeDvSubmode submode)
    {
        lock (_reconfigGate)
        {
            if (_submode == submode) return;
            _submode = submode;
            _snrSquelchThresh = DefaultSquelchThresh(submode);
            if (_active && _nativeAvailable)
                OpenLocked();
        }
    }

    public void SetSquelch(bool? enabled, double? threshDb)
    {
        lock (_reconfigGate)
        {
            if (enabled.HasValue) _squelchEnabled = enabled.Value;
            if (threshDb.HasValue) _snrSquelchThresh = (float)threshDb.Value;
            Volatile.Write(ref _squelchDirty, 1); // applied by the RX hot path on its handle
        }
    }

    public void FlushTx()
    {
        if (!_nativeAvailable) return;
        lock (_reconfigGate)
        {
            // Gate the TX hot path out, clear its buffers, restore the handle.
            IntPtr fd = Volatile.Read(ref _txFreedv);
            Volatile.Write(ref _txFreedv, IntPtr.Zero);
            Thread.MemoryBarrier();
            SpinUntilIdle(ref _txBusy);
            _tx8In.Clear(); _txOut48.Clear();
            _txDown.Reset(); _txUp.Reset();
            Volatile.Write(ref _txFreedv, fd);
        }
    }

    // -------- RX hot path (DSP tick thread; no lock, no alloc) --------

    public void ProcessRxInPlace(Span<float> block48k)
    {
        if (!_active) return;
        Volatile.Write(ref _rxBusy, 1);
        Thread.MemoryBarrier(); // StoreLoad fence vs. the reconfig handoff
        try
        {
            IntPtr fd = Volatile.Read(ref _rxFreedv);
            if (fd == IntPtr.Zero) return; // passthrough (native missing / reconfiguring)

            if (Volatile.Read(ref _squelchDirty) != 0 && Interlocked.Exchange(ref _squelchDirty, 0) != 0)
            {
                FreeDvNativeMethods.freedv_set_squelch_en(fd, _squelchEnabled);
                FreeDvNativeMethods.freedv_set_snr_squelch_thresh(fd, _snrSquelchThresh);
            }

            int n8 = _rxDown.Process(block48k, _rxDecScratch);
            _rx8In.Write(_rxDecScratch.AsSpan(0, n8));

            int nin = FreeDvNativeMethods.freedv_nin(fd);
            while (nin > 0 && _rx8In.Count >= nin)
            {
                _rx8In.Read(_rxModemFloat.AsSpan(0, nin));
                FloatToShort(_rxModemFloat, _rxModemShort, nin);

                int nout = FreeDvNativeMethods.freedv_rx(fd, _rxSpeechShort, _rxModemShort);
                FreeDvNativeMethods.freedv_get_modem_stats(fd, out int sync, out float snr);
                _synced = sync != 0;
                Volatile.Write(ref _snrDb, snr);

                if (nout > 0)
                {
                    ShortToFloat(_rxSpeechShort, _rxSpeechFloat, nout);
                    int up = _rxUp.Process(_rxSpeechFloat.AsSpan(0, nout), _rxInterp);
                    WriteBounded(_rxOut48, _rxInterp.AsSpan(0, up));
                }
                nin = FreeDvNativeMethods.freedv_nin(fd);
            }

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
            IntPtr fd = Volatile.Read(ref _txFreedv);
            if (fd == IntPtr.Zero) return;

            int nspeech = _txNSpeech, nmodem = _txNNomModem;
            if (nspeech <= 0 || nmodem <= 0) { block48k.Clear(); return; }

            int n8 = _txDown.Process(block48k, _txDecScratch);
            _tx8In.Write(_txDecScratch.AsSpan(0, n8));

            while (_tx8In.Count >= nspeech)
            {
                _tx8In.Read(_txSpeechFloat.AsSpan(0, nspeech));
                FloatToShort(_txSpeechFloat, _txSpeechShort, nspeech);
                FreeDvNativeMethods.freedv_tx(fd, _txModemShort, _txSpeechShort);
                ShortToFloat(_txModemShort, _txModemFloat, nmodem);
                int up = _txUp.Process(_txModemFloat.AsSpan(0, nmodem), _txInterp);
                WriteBounded(_txOut48, _txInterp.AsSpan(0, up));
            }

            int filled = _txOut48.Read(block48k);
            if (filled < block48k.Length) block48k.Slice(filled).Clear();
        }
        finally
        {
            Volatile.Write(ref _txBusy, 0);
        }
    }

    private static void WriteBounded(FreeDvSampleRing ring, ReadOnlySpan<float> src)
    {
        if (ring.Count > MaxOutFifo) ring.Drop(ring.Count - MaxOutFifo);
        ring.Write(src); // short write (ring full) acts as a final safety drop
    }

    private static void FloatToShort(float[] src, short[] dst, int n)
    {
        for (int i = 0; i < n; i++)
            dst[i] = (short)Math.Clamp((int)MathF.Round(src[i] * 32767f), short.MinValue, short.MaxValue);
    }

    private static void ShortToFloat(short[] src, float[] dst, int n)
    {
        for (int i = 0; i < n; i++) dst[i] = src[i] * (1f / 32768f);
    }

    // -------- reconfig helpers (control thread, under _reconfigGate) --------

    private void OpenLocked()
    {
        int mode = ToNativeMode(_submode);
        IntPtr rx = FreeDvNativeMethods.freedv_open(mode);
        IntPtr tx = FreeDvNativeMethods.freedv_open(mode);
        if (rx == IntPtr.Zero || tx == IntPtr.Zero)
        {
            _log?.LogError("FreeDV: freedv_open failed for submode {Submode}", _submode);
            if (rx != IntPtr.Zero) FreeDvNativeMethods.freedv_close(rx);
            if (tx != IntPtr.Zero) FreeDvNativeMethods.freedv_close(tx);
            RetireRx(IntPtr.Zero);
            RetireTx(IntPtr.Zero);
            return;
        }

        // Size scratch from the new modem (off the hot path).
        int rxMaxModem = FreeDvNativeMethods.freedv_get_n_max_modem_samples(rx);
        int rxMaxSpeech = FreeDvNativeMethods.freedv_get_n_max_speech_samples(rx);
        EnsureShort(ref _rxModemShort, rxMaxModem);
        EnsureFloat(ref _rxModemFloat, rxMaxModem);
        EnsureShort(ref _rxSpeechShort, rxMaxSpeech);
        EnsureFloat(ref _rxSpeechFloat, rxMaxSpeech);
        EnsureFloat(ref _rxInterp, FreeDvResampler.InterpolatedLength(rxMaxSpeech));

        _txNSpeech = FreeDvNativeMethods.freedv_get_n_speech_samples(tx);
        _txNNomModem = FreeDvNativeMethods.freedv_get_n_nom_modem_samples(tx);
        EnsureShort(ref _txSpeechShort, _txNSpeech);
        EnsureFloat(ref _txSpeechFloat, _txNSpeech);
        EnsureShort(ref _txModemShort, _txNNomModem);
        EnsureFloat(ref _txModemFloat, _txNNomModem);
        EnsureFloat(ref _txInterp, FreeDvResampler.InterpolatedLength(_txNNomModem));

        _speechRate = FreeDvNativeMethods.freedv_get_speech_sample_rate(rx);
        _modemRate = FreeDvNativeMethods.freedv_get_modem_sample_rate(rx);
        FreeDvNativeMethods.freedv_set_squelch_en(rx, _squelchEnabled);
        FreeDvNativeMethods.freedv_set_snr_squelch_thresh(rx, _snrSquelchThresh);
        Volatile.Write(ref _squelchDirty, 0);

        _synced = false;
        Volatile.Write(ref _snrDb, 0f);

        // Wire the text sidechannel on the fresh handles BEFORE publishing them to
        // the hot path: RX handle decodes received chars, TX handle pulls chars to
        // send. Restart the TX cursor so a reopen (submode change) re-sends from
        // the start of the message. Both handles are still gated out here.
        FreeDvNativeMethods.freedv_set_callback_txt(rx, _rxTextCbPtr, IntPtr.Zero, IntPtr.Zero);
        FreeDvNativeMethods.freedv_set_callback_txt(tx, IntPtr.Zero, _txTextCbPtr, IntPtr.Zero);
        _txTextPos = 0;

        RetireRx(rx);
        RetireTx(tx);

        _log?.LogInformation(
            "FreeDV: opened submode={Submode} mode={Mode} speechFs={SpeechFs} modemFs={ModemFs}",
            _submode, mode, _speechRate, _modemRate);
    }

    // Gate the RX hot path out, close the old handle, reset RX buffers, publish
    // the replacement (IntPtr.Zero to leave RX disengaged).
    private void RetireRx(IntPtr replacement)
    {
        IntPtr old = Volatile.Read(ref _rxFreedv);
        Volatile.Write(ref _rxFreedv, IntPtr.Zero);
        Thread.MemoryBarrier();
        SpinUntilIdle(ref _rxBusy);
        if (old != IntPtr.Zero) FreeDvNativeMethods.freedv_close(old);
        _rx8In.Clear(); _rxOut48.Clear();
        _rxDown.Reset(); _rxUp.Reset();
        _rxTextRing.Clear(); // drop stale decoded chars across a resync/close
        Volatile.Write(ref _rxFreedv, replacement);
    }

    private void RetireTx(IntPtr replacement)
    {
        IntPtr old = Volatile.Read(ref _txFreedv);
        Volatile.Write(ref _txFreedv, IntPtr.Zero);
        Thread.MemoryBarrier();
        SpinUntilIdle(ref _txBusy);
        if (old != IntPtr.Zero) FreeDvNativeMethods.freedv_close(old);
        _tx8In.Clear(); _txOut48.Clear();
        _txDown.Reset(); _txUp.Reset();
        Volatile.Write(ref _txFreedv, replacement);
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
