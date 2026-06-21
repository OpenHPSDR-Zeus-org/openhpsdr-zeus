// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
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
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Protocol1;

namespace Zeus.Server;

/// <summary>
/// Internal tag identifying which producer fed a TX-audio block into
/// <see cref="TxAudioIngest.OnMicPcmBytes"/>. Carried explicitly (NOT inferred
/// from recency) so the in-lock single-select gate can arbitrate the host mic
/// against a radio jack deterministically across a source switch
/// (external-audio-jacks re-port). TCI/WAV remain operator-explicit overrides
/// and bypass the host/radio arbitration — their precedence is the existing
/// recency hysteresis.
/// </summary>
internal enum MicBlockSource
{
    /// <summary>Browser / native host microphone (the default TX-audio source).</summary>
    Host = 0,
    /// <summary>Radio-digitised jack audio (Saturn mic/line-in/XLR via UDP 1026).</summary>
    RadioMic,
    /// <summary>TCI client TX audio (MSHV / WSJT-X …). Operator-explicit override.</summary>
    Tci,
    /// <summary>WAV-recording playback to the air. Operator-explicit override.</summary>
    Wav,
}

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
    // All-zero IQ used to substitute silence during the pre-key (MOX) delay
    // window (issue #630). Same size as _scratchIq, never written, so the
    // modulated samples in _scratchIq stay intact for the peak diagnostic — we
    // mute by writing FROM this buffer, not by zeroing _scratchIq in place.
    private readonly float[] _muteIq = new float[4096];

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
    // WAV-playback-source recency: set on every OnMicPcmBytesFromWav call so a
    // concurrent native-mic frame within TciHysteresisMs is suppressed -- the
    // recording replaces the live mic on the air, they never mix.
    private long _lastWavTickMs;
    // Browser/mobile mic recency: desktop mode has NativeMicCapture running
    // continuously, so remote WebSocket mic frames must temporarily own the
    // "live mic" source. Otherwise mobile PTT mixes phone audio with desktop
    // capture silence and feeds TXA at roughly 2x realtime.
    private long _lastBrowserMicTickMs;

    // The currently-armed TX-audio source for HOST↔RADIO arbitration
    // (external-audio-jacks re-port, "atomic single-select gate"). Read and
    // written ONLY under _sync. OnMicPcmBytes rejects any Host- or
    // RadioMic-tagged block whose tag != _activeSource, so when a radio jack is
    // armed the host mic is dropped immediately by the in-lock compare (no
    // overlap window) and vice versa. TCI/WAV-tagged blocks bypass this compare.
    // Default Host so a fresh / un-switched ingest is byte/behaviour-identical
    // to today.
    private MicBlockSource _activeSource = MicBlockSource.Host;
    // Source whose samples currently occupy the WDSP accumulator. Read/written
    // ONLY under _sync. Enforces that a partially-filled accumulator is NEVER
    // topped up by a different source: between the cheap top-of-method gate and
    // the accumulation lock, _activeSource can flip, so the AUTHORITATIVE
    // arbitration is re-done inside the accumulation lock against this owner.
    private MicBlockSource _accumulatorSource = MicBlockSource.Host;

    // Peak radio-jack input level (linear 0..1) seen on ACCEPTED RadioMic blocks
    // since the last meter read. Read/written ONLY under _sync. The desktop mic-
    // meter heartbeat (NativeMicCapture) consumes this at ~10 Hz so the operator-
    // facing meter shows the ACTUAL radio input when a radio source is armed —
    // and reads silence (0) when no radio audio is arriving (no mic connected /
    // no UDP-1026 stream). Without this, the meter would keep showing the host
    // mic on a radio source (the reported "still listening to host" bug). Reset
    // to 0 on any source switch so a stale value can't bleed across sources.
    private float _radioPeakLinear;

    /// <summary>
    /// Arm the TX-audio source for the HOST↔RADIO single-select gate
    /// (external-audio-jacks re-port). Called by the pipeline when the resolved
    /// <see cref="TxAudioSource"/> changes. Under <see cref="_sync"/> this sets
    /// the new active source AND clears the WDSP accumulator so no half-block of
    /// the old source survives onto the new source — the new source fills from
    /// empty. The 1026 re-blocker (owned upstream) is reset by the same caller.
    /// Maps every radio jack (Mic/Line-In/XLR) onto
    /// <see cref="MicBlockSource.RadioMic"/> because they all arrive on the one
    /// UDP-1026 stream; only Host is distinct here. TCI/WAV are not selectable
    /// sources — they override transiently.
    /// </summary>
    internal void SetActiveSource(TxAudioSource source)
    {
        var mapped = source == TxAudioSource.Host
            ? MicBlockSource.Host
            : MicBlockSource.RadioMic;
        lock (_sync)
        {
            if (_activeSource == mapped) return;
            _activeSource = mapped;
            // Quiesce: drop any partially-accumulated old-source audio so it
            // can't stitch onto the post-switch source mid-WDSP-block.
            _accumulatorFill = 0;
            // Drop any stale radio-meter peak so the meter doesn't carry a value
            // across a source switch (e.g. host→radio shows fresh radio level,
            // radio→host stops surfacing a frozen radio peak).
            _radioPeakLinear = 0f;
        }
    }

    /// <summary>Current armed source for the host/radio gate (test/diagnostic).</summary>
    internal MicBlockSource ActiveSource { get { lock (_sync) return _activeSource; } }

    /// <summary>
    /// Read and reset the peak radio-jack input level (linear 0..1) seen on
    /// accepted radio blocks since the last call. The desktop mic-meter heartbeat
    /// consumes this so the meter reflects the ACTUAL radio input when a radio
    /// source is armed, and reads silence (0) when no radio audio is arriving.
    /// Thread-safe.
    /// </summary>
    internal float ConsumeRadioPeakLinear()
    {
        lock (_sync)
        {
            var peak = _radioPeakLinear;
            _radioPeakLinear = 0f;
            return peak;
        }
    }

    /// <summary>
    /// True when a TCI client is the authoritative source of TX audio right now
    /// (within the hysteresis window). Used by the TX audio plugin bridge to
    /// bypass the operator's insert plugins (EQ, comp, VSTs, etc.) for remote
    /// sources — their audio has already been processed on the client side.
    /// Local mic and WAV playback are never considered "remote" for this check.
    /// </summary>
    internal bool IsTciTxAudioActive
    {
        get
        {
            long last = Volatile.Read(ref _lastTciTickMs);
            return last != 0 && (Environment.TickCount64 - last) < TciHysteresisMs;
        }
    }
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
               txOwnedByTuneDriver: () => tx.IsTunOn || tx.IsTwoToneOn,
               preKeyOpenAtTicks: () => tx.PreKeyOpenAtTicks)
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
        Func<bool>? txOwnedByTuneDriver = null,
        Func<long>? preKeyOpenAtTicks = null)
    {
        _ring = ring;
        _engineProvider = engineProvider;
        _isMoxOn = isMoxOn;
        _forwardP2 = forwardP2;
        _onWdspConsumed = onWdspConsumed;
        _txOwnedByTuneDriver = txOwnedByTuneDriver ?? (static () => false);
        _preKeyOpenAtTicks = preKeyOpenAtTicks ?? (static () => 0L);
        _hub = hub;
        _log = log;
        _handler = OnMicPcmBytesFromBrowserMic;
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
    // Pre-key (MOX) delay window deadline in Stopwatch ticks, supplied by
    // TxService (issue #630). While Stopwatch.GetTimestamp() is below this
    // value AND the block is genuine live-mic IQ (not WAV-over-air playback),
    // we substitute silence for the modulated IQ so an external amp's T/R relay
    // settles before RF appears. 0 = no window (the default-0 setting, CW, TUN,
    // two-tone, TCI, hardware-PTT — all unset or cleared by TxService).
    private readonly Func<long> _preKeyOpenAtTicks;

    // Cross-thread handoff: written from the TCI timer thread (Start/Stop of
    // the TX_CHRONO service), read every audio block from the WDSP worker.
    // x86/TSO hides the missing fence, but Apple-Silicon / Pi-class ARM does
    // not. Mirror the Interlocked.Exchange pattern used for _txChronoTimer.
    internal void SetWdspConsumedCallback(Action<int>? cb)
        => Interlocked.Exchange(ref _onWdspConsumed, cb);

    /// <summary>Raised for every valid mic block (960 samples f32le @ 48 kHz)
    /// as it enters the ingest, before any MOX/monitor gating. The WAV recorder
    /// taps this to capture the raw transmit-side mic audio silently. Payload
    /// is valid only for the synchronous handler — copy if retained.</summary>
    internal event Action<ReadOnlyMemory<byte>>? MicPcmTapped;
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
        OnMicPcmBytes(f32lePayload, MicBlockSource.Tci);
    }

    /// <summary>
    /// Source-tagged entry point for browser/mobile mic audio from
    /// <see cref="StreamingHub.MicPcmReceived"/>. Browser mic is still local
    /// audio for the processing chain, but it owns the live-mic source while
    /// fresh so desktop <see cref="NativeMicCapture"/> cannot double-feed TXA.
    /// </summary>
    internal void OnMicPcmBytesFromBrowserMic(ReadOnlyMemory<byte> f32lePayload)
    {
        long now = Environment.TickCount64;
        Volatile.Write(ref _lastBrowserMicTickMs, now);
        if (ShouldSuppressForAuthoritativeSource(now)) return;
        OnMicPcmBytes(f32lePayload, MicBlockSource.Host);
    }

    /// <summary>
    /// Source-tagged entry point for the desktop native mic path. Drops the
    /// block silently if TCI, WAV playback, or browser/mobile mic fed within
    /// the last <see cref="TciHysteresisMs"/> milliseconds so sources never
    /// mix or overrun the TX path.
    /// </summary>
    internal void OnMicPcmBytesFromMic(ReadOnlyMemory<byte> f32lePayload)
    {
        long now = Environment.TickCount64;
        if (ShouldSuppressForAuthoritativeSource(now)) return;
        long lastBrowserMic = Volatile.Read(ref _lastBrowserMicTickMs);
        if (lastBrowserMic != 0 && now - lastBrowserMic < TciHysteresisMs) return;
        OnMicPcmBytes(f32lePayload, MicBlockSource.Host);
    }

    /// <summary>
    /// Source-tagged entry point for radio-digitised jack audio (Saturn mic /
    /// line-in / balanced-XLR, arriving on UDP 1026 and re-blocked to 960
    /// samples upstream — external-audio-jacks re-port). Tagged
    /// <see cref="MicBlockSource.RadioMic"/>; the in-lock
    /// <see cref="_activeSource"/> compare in <see cref="OnMicPcmBytes"/> drops
    /// it unless a radio jack is the armed source, so it can never leak onto the
    /// air under Host. Like the host mic, it yields to a recent TCI/WAV override
    /// so a remote/playback source is never mixed with radio-jack audio.
    /// </summary>
    internal void OnMicPcmBytesFromRadioMic(ReadOnlyMemory<byte> f32lePayload)
    {
        long now = Environment.TickCount64;
        if (ShouldSuppressForAuthoritativeSource(now)) return;
        OnMicPcmBytes(f32lePayload, MicBlockSource.RadioMic);
    }

    private bool ShouldSuppressForAuthoritativeSource(long now)
    {
        long lastTci = Volatile.Read(ref _lastTciTickMs);
        if (lastTci != 0 && now - lastTci < TciHysteresisMs) return true;
        long lastWav = Volatile.Read(ref _lastWavTickMs);
        return lastWav != 0 && now - lastWav < TciHysteresisMs;
    }

    /// <summary>Source-tagged entry point for WAV-recording playback to the
    /// air (from <see cref="Zeus.Server.Wav.WavRecorderService"/>). Stamps the
    /// WAV recency timestamp so the live native mic is suppressed for the clip,
    /// then feeds the block through the same path as mic audio — so a recording
    /// is processed by the normal TX chain exactly like live speech. The caller
    /// keys MOX; this method does not touch MOX.</summary>
    internal void OnMicPcmBytesFromWav(ReadOnlyMemory<byte> f32lePayload)
    {
        Volatile.Write(ref _lastWavTickMs, Environment.TickCount64);
        OnMicPcmBytes(f32lePayload, MicBlockSource.Wav);
    }

    // Internal so tests can drive the ingest directly without standing up a WS.
    // Untagged overload defaults to Host (host path byte/behaviour-identical to
    // today).
    internal void OnMicPcmBytes(ReadOnlyMemory<byte> f32lePayload)
        => OnMicPcmBytes(f32lePayload, MicBlockSource.Host);

    // The source tag arbitrates the HOST↔RADIO single-select gate IN-LOCK (see
    // _activeSource); TCI/WAV bypass that compare as operator-explicit overrides.
    internal void OnMicPcmBytes(ReadOnlyMemory<byte> f32lePayload, MicBlockSource source)
    {
        if (f32lePayload.Length != MicBlockBytes)
        {
            lock (_sync) _droppedFrames++;
            return;
        }

        // Cheap early-out for the host/radio gate (external-audio-jacks
        // re-port). This is NOT the hard gate — _activeSource can still flip
        // between here and the accumulation lock below, so the AUTHORITATIVE
        // single-select decision is re-checked atomically with the append (see
        // "ATOMIC single-select gate" inside the accumulation lock). Doing the
        // obvious reject here too avoids running the MOX-edge / tap / WDSP-sizing
        // work for a block the authoritative check would drop anyway. TCI/WAV
        // bypass this compare — they are operator-explicit overrides gated by the
        // recency hysteresis above.
        if (source is MicBlockSource.Host or MicBlockSource.RadioMic)
        {
            lock (_sync)
            {
                if (source != _activeSource) { _droppedFrames++; return; }
            }
        }

        // Non-destructive mic tap, fired BEFORE the MOX/monitor gate so a
        // recorder can capture the raw mic whether or not the operator is
        // keyed or monitoring (silent capture). Covers desktop native mic,
        // browser mic, and TCI — every source funnels through here. The event
        // is null (no allocation/cost) unless something is actually recording.
        MicPcmTapped?.Invoke(f32lePayload);

        // Gate: process mic samples when MOX is on (normal TX) OR when the TX
        // monitor is on (preview without keying so the operator can hear
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
            // accumulator + chain feed running for the preview.
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
            // ATOMIC single-select gate (external-audio-jacks re-port, the
            // crux). This is the AUTHORITATIVE host/radio arbitration: it runs
            // in the SAME critical section as the accumulator append, so no flip
            // of _activeSource (by SetActiveSource, also under _sync) can slip
            // between "decide" and "append". A Host/RadioMic block whose tag no
            // longer matches the armed source is dropped, so two producer threads
            // straddling a switch resolve to exactly one contributor per WDSP
            // block — no double-feed. TCI/WAV bypass this (operator-explicit
            // overrides; recency-gated above). Under Host with a Host block this
            // is a pure no-op → host path byte/behaviour-identical to today.
            if (source is MicBlockSource.Host or MicBlockSource.RadioMic
                && source != _activeSource)
            {
                _droppedFrames++;
                return;
            }
            // Guard the accumulator owner: if a half-filled block belongs to a
            // different source than the one now appending (possible when the
            // active source flipped while data sat buffered), drop the stale
            // remainder so the two sources never mix inside one WDSP block.
            // SetActiveSource already clears on the host/radio switch; this
            // covers the TCI/WAV interleave as well.
            if (_accumulatorFill > 0 && _accumulatorSource != source)
                _accumulatorFill = 0;
            _accumulatorSource = source;

            // Track the radio-jack input peak for the source-aware mic meter.
            // Only on accepted RadioMic blocks (the gate above guarantees the
            // radio jack is the armed source) — the host path is untouched. The
            // desktop meter heartbeat consumes this via ConsumeRadioPeakLinear,
            // so the meter shows the real radio level and falls to silence when
            // no radio audio is arriving.
            if (source == MicBlockSource.RadioMic)
            {
                var rspan = f32lePayload.Span;
                float radioPeak = 0f;
                for (int i = 0; i < MicBlockSamples; i++)
                {
                    float a = BinaryPrimitives.ReadSingleLittleEndian(rspan.Slice(i * 4, 4));
                    if (a < 0) a = -a;
                    if (a > radioPeak) radioPeak = a;
                }
                if (radioPeak > _radioPeakLinear) _radioPeakLinear = radioPeak;
            }

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
                float sample = BinaryPrimitives.ReadSingleLittleEndian(src.Slice(i * 4, 4));
                _accumulator[_accumulatorFill + i] = DspPipelineService.SanitizeAudioSample(sample);
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
                    // (preview without keying) the IQ has been generated for
                    // the engine's monitor RXA channel to demod inside
                    // ProcessTxBlock — but it must NOT hit the wire, otherwise
                    // a monitor toggle would put the radio on the air.
                    if (moxNow)
                    {
                        // Pre-key (MOX) delay window (issue #630): if still inside
                        // the window, substitute silence for the modulated IQ so
                        // the amp T/R relay settles before RF appears. We still
                        // WRITE (a zero block of the same length) rather than drop
                        // — dropping would starve the P2 DUC FIFO and produce the
                        // exact bare/gappy carrier the feature exists to prevent.
                        // WAV-over-air playback is exempt: the operator already
                        // keyed and the clip head is position-locked, so muting it
                        // would clip the intro.
                        long openAt = _preKeyOpenAtTicks();
                        bool wavRecent = false;
                        if (openAt != 0L)
                        {
                            long lastWav = Volatile.Read(ref _lastWavTickMs);
                            wavRecent = lastWav != 0
                                && Environment.TickCount64 - lastWav < TciHysteresisMs;
                        }
                        bool mute = openAt != 0L
                            && System.Diagnostics.Stopwatch.GetTimestamp() < openAt
                            && !wavRecent;

                        // P1 path — EP2 packer in Protocol1Client drains the ring.
                        _ring.Write(mute
                            ? new ReadOnlySpan<float>(_muteIq, 0, 2 * produced)
                            : iqSpan);
                        // P2 path — Protocol2Client's 1029-port DUC sender. No-op
                        // when P2 isn't the active backend so both protocols share
                        // this seam cleanly. Mirrors TxTuneDriver's dual-write.
                        _forwardP2?.Invoke(mute
                            ? new ReadOnlyMemory<float>(_muteIq, 0, 2 * produced)
                            : new ReadOnlyMemory<float>(_scratchIq, 0, 2 * produced));
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
