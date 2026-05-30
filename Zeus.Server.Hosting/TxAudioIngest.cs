// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Zeus.Dsp;
using Zeus.Protocol1;

namespace Zeus.Server;

/// <summary>
/// Bridges browser-side mic audio to WDSP TXA and onward to the EP2 IQ
/// payload. Inputs are 960-sample f32le blocks from the /ws MicPcm frame
/// (20 ms @ 48 kHz mono); the service accumulates into WDSP's native block
/// size, calls <see cref="IDspEngine.ProcessTxBlock"/>, and pushes the
/// resulting modulated IQ into <see cref="TxIqRing"/> for
/// <see cref="Protocol1Client"/> to pull at EP2 packet rate.
///
/// Threading: <see cref="OnMicPcmBytes"/> runs on the StreamingHub receive
/// loop thread. We hold <see cref="_sync"/> for the duration of the flush
/// so back-to-back mic frames don't interleave into the same WDSP block
/// half-written.
///
/// Lifecycle: constructed via DI (singleton), subscribes to
/// <see cref="StreamingHub.MicPcmReceived"/> immediately. Drops input
/// silently when the engine is Synthetic (no TXA available) or MOX is off —
/// the ring stays empty in those cases so the EP2 packer emits silence.
/// </summary>
public sealed class TxAudioIngest : IDisposable
{
    private const int MicBlockSamples = 960;   // 20 ms @ 48 kHz (matches front-end worklet)
    private const int MicBlockBytes = MicBlockSamples * 4;

    private readonly TxIqRing _ring;
    private readonly Func<IDspEngine?> _engineProvider;
    private readonly Func<bool> _isMoxOn;
    private readonly ILogger<TxAudioIngest> _log;
    private readonly StreamingHub _hub;
    private readonly Action<ReadOnlyMemory<byte>> _handler;
    private Action<int>? _onWdspConsumed;

    private readonly object _sync = new();
    // Accumulator scratch — sized to at least one WDSP block plus one frontend
    // block (1024 + 960 = 1984) so we can always append a new arrival before
    // draining. The excess gets shifted back after each flush.
    private readonly float[] _accumulator = new float[2048];
    private int _accumulatorFill;
    // Sized for the larger of the P1 (1024 in / 2048 iq) and P2
    // (512 in / 4096 iq) profiles so we don't reallocate at protocol switch.
    private readonly float[] _scratchMic = new float[1024];
    private readonly float[] _scratchIq = new float[4096];

    private long _totalMicSamples;
    private long _totalTxBlocks;
    private long _droppedFrames;
    // Tracks the last-seen MOX state so Clear() fires exactly once per MOX
    // falling edge instead of on every mic frame that happens to arrive while
    // MOX is off. The hot-loop Clear caused a race with the MOX rising edge:
    // client-optimistic mic frames can reach the hub before /api/tx/mox has
    // flipped the server's IsMoxOn, and the pre-flip frames were wiping the
    // ring of IQ Protocol1Client had just produced.
    private bool _lastSeenMox;
    // TCI-source recency: set on every OnMicPcmBytesFromTci call. If a frame
    // from the mic source arrives within TciHysteresisMs of the last TCI feed,
    // it is silently dropped — only the TCI source is authoritative for that
    // window. This prevents NativeMicCapture's always-on capture stream from
    // injecting mic-silence blocks into the accumulator while a TCI client
    // (MSHV, TCI Remote, …) is the actual audio source. 500 ms covers the
    // 42.67 ms TX_CHRONO cadence with >10× margin while remaining short
    // enough that a genuine fallback to mic happens instantly after TCI stops.
    private const int TciHysteresisMs = 500;
    private long _lastTciTickMs;
    // Diagnostic: log peak of mic-in and IQ-out once per second of TX. If
    // mic-peak is high but iq-peak is ~0, WDSP TXA is producing silence
    // despite good input. If mic-peak itself is ~0, the uplink is broken.
    private DateTime _lastPeakLogUtc;
    private float _peakMicAccum;
    private float _peakIqAccum;
    private int _peakBlocksAccum;

    public TxAudioIngest(
        TxIqRing ring,
        DspPipelineService pipeline,
        TxService tx,
        StreamingHub hub,
        ILogger<TxAudioIngest> log)
        : this(ring, () => pipeline.CurrentEngine, () => tx.IsMoxOn, hub, log,
               forwardP2: iq => pipeline.ForwardTxIqToP2(iq.Span),
               txOwnedByTuneDriver: () => tx.IsTunOn || tx.IsTwoToneOn)
    {
    }

    /// <summary>Test-only constructor that wires the engine + MOX lookups
    /// through plain delegates so unit tests don't need a live pipeline.
    /// <paramref name="forwardP2"/> is called with the same IQ block that's
    /// handed to the P1 ring so mic MOX on a Protocol 2 radio (G2 MkII) has
    /// a TX path. Null in tests that don't exercise the P2 forward.</summary>
    internal TxAudioIngest(
        TxIqRing ring,
        Func<IDspEngine?> engineProvider,
        Func<bool> isMoxOn,
        StreamingHub hub,
        ILogger<TxAudioIngest> log,
        Action<ReadOnlyMemory<float>>? forwardP2 = null,
        Action<int>? onWdspConsumed = null,
        Func<bool>? txOwnedByTuneDriver = null)
    {
        _ring = ring;
        _engineProvider = engineProvider;
        _isMoxOn = isMoxOn;
        _forwardP2 = forwardP2;
        _onWdspConsumed = onWdspConsumed;
        _txOwnedByTuneDriver = txOwnedByTuneDriver ?? (static () => false);
        _hub = hub;
        _log = log;
        _handler = OnMicPcmBytesFromMic;
        _hub.MicPcmReceived += _handler;
    }

    private readonly Action<ReadOnlyMemory<float>>? _forwardP2;
    // True while TUN or the two-tone test is active. TxTuneDriver is the sole TX
    // driver in those states; this mic-ingest path must NOT also run ProcessTxBlock
    // or push IQ, or two threads drive the same TXA (fexchange2) and BOTH feed the
    // radio → double-fed / corrupted signal. Desktop-only symptom (#559): native
    // mic capture keeps feeding here during a two-tone; web has no native mic so
    // only TxTuneDriver drove it and it stayed clean.
    private readonly Func<bool> _txOwnedByTuneDriver;

    // Cross-thread handoff: written from the TCI timer thread (Start/Stop of
    // the TX_CHRONO service), read every audio block from the WDSP worker.
    // x86/TSO hides the missing fence, but Apple-Silicon / Pi-class ARM does
    // not. Mirror the Interlocked.Exchange pattern used for _txChronoTimer.
    internal void SetWdspConsumedCallback(Action<int>? cb)
        => Interlocked.Exchange(ref _onWdspConsumed, cb);
    public long TotalMicSamples { get { lock (_sync) return _totalMicSamples; } }
    public long TotalTxBlocks { get { lock (_sync) return _totalTxBlocks; } }
    public long DroppedFrames { get { lock (_sync) return _droppedFrames; } }

    public void Dispose()
    {
        _hub.MicPcmReceived -= _handler;
    }

    /// <summary>
    /// Source-tagged entry point for TCI TX audio (from
    /// <see cref="Zeus.Server.Tci.TciTxAudioReceiver"/>). Updates the TCI
    /// recency timestamp so a concurrent <see cref="OnMicPcmBytesFromMic"/>
    /// call within <see cref="TciHysteresisMs"/> is silently suppressed.
    /// </summary>
    internal void OnMicPcmBytesFromTci(ReadOnlyMemory<byte> f32lePayload)
    {
        Volatile.Write(ref _lastTciTickMs, Environment.TickCount64);
        OnMicPcmBytes(f32lePayload);
    }

    /// <summary>
    /// Source-tagged entry point for the local mic path (browser
    /// <c>getUserMedia</c> via <see cref="StreamingHub.MicPcmReceived"/>, or
    /// <see cref="NativeMicCapture"/> in desktop mode). Drops the block
    /// silently if TCI fed within the last <see cref="TciHysteresisMs"/>
    /// milliseconds so a TCI source is never mixed with mic silence.
    /// </summary>
    internal void OnMicPcmBytesFromMic(ReadOnlyMemory<byte> f32lePayload)
    {
        long lastTci = Volatile.Read(ref _lastTciTickMs);
        if (lastTci != 0 && Environment.TickCount64 - lastTci < TciHysteresisMs)
            return;
        OnMicPcmBytes(f32lePayload);
    }

    // Internal so tests can drive the ingest directly without standing up a WS.
    internal void OnMicPcmBytes(ReadOnlyMemory<byte> f32lePayload)
    {
        if (f32lePayload.Length != MicBlockBytes)
        {
            lock (_sync) _droppedFrames++;
            return;
        }

        // Gate: process mic samples when MOX is on (normal TX) OR when the TX
        // monitor is on (audition without keying so the operator can hear
        // their VST chain / EQ / leveler before going on the air). When both
        // are off the chain doesn't run — pre-monitor behaviour, plus the
        // mic-leak protection that motivated the original gate.
        //
        // The ring-clear / accumulator-clear on the MOX falling edge stays
        // tied to MOX, not monitor: dropping accumulator state on a monitor
        // toggle would chop mid-syllable for no benefit, and the IQ ring is
        // only consumed during MOX anyway.
        var engine = _engineProvider();
        bool monitorOn = engine?.IsTxMonitorOn ?? false;
        bool moxNow = _isMoxOn();
        if (!moxNow && !monitorOn)
        {
            lock (_sync)
            {
                if (_accumulatorFill > 0) _accumulatorFill = 0;
                if (_lastSeenMox)
                {
                    // MOX fell since our last frame — drain the IQ ring so the
                    // next keyed TX starts clean, without the tail of this one.
                    _ring.Clear();
                    _lastSeenMox = false;
                }
            }
            return;
        }
        if (!moxNow && _lastSeenMox)
        {
            // MOX fell while monitor is on. Drain the IQ ring so the next
            // key-down isn't tailed by stale RF samples, but keep the
            // accumulator + chain feed running for the audition.
            lock (_sync)
            {
                _ring.Clear();
                _lastSeenMox = false;
            }
        }
        // Latch the MOX rising edge so the next falling edge will drain the
        // ring. Monitor-only operation never sets _lastSeenMox so it's a true
        // edge tracker for keyed TX.
        if (moxNow && !_lastSeenMox) { lock (_sync) _lastSeenMox = true; }

        int blockSize = engine?.TxBlockSamples ?? 0;
        int iqOut = engine?.TxOutputSamples ?? 0;
        if (engine is null || blockSize <= 0 || iqOut <= 0
            || blockSize > _scratchMic.Length || 2 * iqOut > _scratchIq.Length)
        {
            // Synthetic engine, no TXA open, or a protocol whose block size
            // exceeds our scratch buffers. Swallow samples quietly.
            return;
        }

        // TUN / two-tone active → TxTuneDriver is the sole TX driver. Running
        // ProcessTxBlock here too would put two threads on one TXA's fexchange2
        // AND push a second IQ stream to the radio (double-feed → corrupted /
        // dirty signal; PS then calibrates on garbage). The mic isn't
        // transmitted during a tune/two-tone anyway, so drop the batch and reset
        // the accumulator so it can't back up. Fixes the desktop-only dirty
        // two-tone in #559 (native mic capture kept feeding here; web had no
        // native mic so only TxTuneDriver drove it → clean).
        if (_txOwnedByTuneDriver())
        {
            lock (_sync) _accumulatorFill = 0;
            return;
        }

        lock (_sync)
        {
            // Decode f32le into accumulator. WDSP wants -1..+1 range; browser
            // ships the same convention.
            var src = f32lePayload.Span;
            int need = MicBlockSamples;
            if (_accumulatorFill + need > _accumulator.Length)
            {
                // Should only happen if BlockSamples grew unexpectedly. Treat
                // as a protocol mismatch — drop the accumulator to avoid
                // writing past the array bound.
                _accumulatorFill = 0;
                _droppedFrames++;
                return;
            }
            for (int i = 0; i < MicBlockSamples; i++)
            {
                _accumulator[_accumulatorFill + i] = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(i * 4, 4));
            }
            _accumulatorFill += MicBlockSamples;
            _totalMicSamples += MicBlockSamples;

            while (_accumulatorFill >= blockSize)
            {
                Array.Copy(_accumulator, 0, _scratchMic, 0, blockSize);
                int produced = engine.ProcessTxBlock(
                    new ReadOnlySpan<float>(_scratchMic, 0, blockSize),
                    new Span<float>(_scratchIq, 0, 2 * iqOut));
                if (produced > 0)
                {
                    var iqSpan = new ReadOnlySpan<float>(_scratchIq, 0, 2 * produced);
                    // Only push the modulated IQ to the radio while MOX is
                    // asserted. When the chain is running for monitor-only
                    // (audition without keying) the IQ has been generated for
                    // the engine's monitor RXA channel to demod inside
                    // ProcessTxBlock — but it must NOT hit the wire, otherwise
                    // a monitor toggle would put the radio on the air.
                    if (moxNow)
                    {
                        // P1 path — EP2 packer in Protocol1Client drains the ring.
                        _ring.Write(iqSpan);
                        // P2 path — Protocol2Client's 1029-port DUC sender. No-op
                        // when P2 isn't the active backend so both protocols share
                        // this seam cleanly. Mirrors TxTuneDriver's dual-write.
                        _forwardP2?.Invoke(new ReadOnlyMemory<float>(_scratchIq, 0, 2 * produced));
                    }
                    _totalTxBlocks++;
                    var onConsumed = Volatile.Read(ref _onWdspConsumed);
                    onConsumed?.Invoke(blockSize);

                    // Accumulate peaks for the 1 Hz diagnostic log.
                    float micPeak = 0f;
                    for (int s = 0; s < blockSize; s++)
                    {
                        float a = _scratchMic[s];
                        if (a < 0) a = -a;
                        if (a > micPeak) micPeak = a;
                    }
                    float iqPeak = 0f;
                    for (int s = 0; s < 2 * produced; s++)
                    {
                        float a = _scratchIq[s];
                        if (a < 0) a = -a;
                        if (a > iqPeak) iqPeak = a;
                    }
                    if (micPeak > _peakMicAccum) _peakMicAccum = micPeak;
                    if (iqPeak > _peakIqAccum) _peakIqAccum = iqPeak;
                    _peakBlocksAccum++;
                    var now = DateTime.UtcNow;
                    if (now - _lastPeakLogUtc >= TimeSpan.FromSeconds(1))
                    {
                        _log.LogInformation(
                            "tx.peaks blocks={Blocks} mic={Mic:F4} iq={Iq:F4}",
                            _peakBlocksAccum, _peakMicAccum, _peakIqAccum);
                        _lastPeakLogUtc = now;
                        _peakMicAccum = 0f;
                        _peakIqAccum = 0f;
                        _peakBlocksAccum = 0;
                    }
                }
                // Shift remainder down — typically ~64 leftover samples (960 %
                // 1024 carry). Array.Copy handles overlapping source/dest.
                int remainder = _accumulatorFill - blockSize;
                if (remainder > 0)
                    Array.Copy(_accumulator, blockSize, _accumulator, 0, remainder);
                _accumulatorFill = remainder;
            }
        }
    }
}
