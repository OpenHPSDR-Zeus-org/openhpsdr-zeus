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

using Zeus.Contracts;

namespace Zeus.Dsp;

public enum DisplayPixout : byte
{
    Panadapter = 0,
    Waterfall = 1,
}

public readonly record struct IqFrame(ReadOnlyMemory<double> InterleavedIq, int SampleRateHz);

public interface IDspEngine : IDisposable
{
    int OpenChannel(int sampleRateHz, int pixelWidth);
    void CloseChannel(int channelId);
    void FeedIq(int channelId, ReadOnlySpan<double> interleavedIqSamples);
    void SetMode(int channelId, RxMode mode);
    void SetFilter(int channelId, int lowHz, int highHz);
    void SetVfoHz(int channelId, long vfoHz);

    /// <summary>
    /// CTUN frequency shift — moves the IF by <paramref name="shiftHz"/>
    /// before demodulation so the bandpass filter sees the tuned signal at
    /// baseband while the radio's hardware NCO stays put. Pass 0 to disable
    /// the shift stage (legacy behaviour). Mirrors Thetis radio.cs:1419-1420
    /// (the WDSP <c>SetRXAShiftFreq</c> + <c>RXANBPSetShiftFrequency</c>
    /// pair). Issue #427.
    /// </summary>
    void SetCtunShift(int channelId, int shiftHz);
    void SetAgcTop(int channelId, double topDb);

    /// <summary>
    /// Briefly tighten the RX display's averaging time-constant after a
    /// retune so the spectrum settles at the new frequency in ~100 ms
    /// instead of melting over ~300-400 ms (issue #597; Thetis parity —
    /// display.cs fast-attacks its averaging after a center change). RX
    /// display channel ONLY: the TX and PS-feedback analyzers keep their
    /// own configured tau (TxAvgTauSec) untouched, so tuning while keyed
    /// never disturbs the TX trace or the PureSignal monitor. Restoring
    /// (<paramref name="fast"/> = false) re-applies the RX channel's
    /// configured default tau. No-op on Synthetic.
    /// </summary>
    void SetRxDisplayFastAttack(int channelId, bool fast);

    /// <summary>Set the RX master AF gain in dB. Drives WDSP's
    /// <c>SetRXAPanelGain1</c> (linear) after a dB→linear conversion. 0 dB
    /// equals the engine's open-time default (linear 1.0). No-op on
    /// Synthetic.</summary>
    void SetRxAfGainDb(int channelId, double db);

    void SetNoiseReduction(int channelId, NrConfig cfg);
    void SetZoom(int channelId, int level);
    int ReadAudio(int channelId, Span<float> output);

    bool TryGetDisplayPixels(int channelId, DisplayPixout which, Span<float> dbOut);

    /// <summary>TX panadapter / waterfall pixels in dBm, sourced from a
    /// dedicated WDSP analyzer fed with the post-CFIR TX IQ. Returns false
    /// when TXA is not open or no fresh FFT is ready. The TX analyzer is
    /// configured to display the same frequency span as the RXA analyzer
    /// (via bin clipping) so the panadapter axis does not move on MOX —
    /// see issue #81. No-op on Synthetic.</summary>
    bool TryGetTxDisplayPixels(DisplayPixout which, Span<float> dbOut);

    /// <summary>PureSignal-feedback panadapter / waterfall pixels in dBm,
    /// sourced from a separate WDSP analyzer fed with the post-PA loopback
    /// IQ pumped through <see cref="FeedPsFeedbackBlock"/>. Returns false
    /// when PS isn't armed (analyzer slot closed), TXA isn't open, or no
    /// fresh FFT is ready. Caller is expected to also check that PS has
    /// converged (info[14]==1) before showing this trace — pre-correction
    /// the loopback shows the real PA splatter. See issue #121.
    /// No-op on Synthetic.</summary>
    bool TryGetPsFeedbackDisplayPixels(DisplayPixout which, Span<float> dbOut);

    /// <summary>Open the TXA channel. Idempotent — calling twice returns the existing id.
    /// Must be called after at least one OpenChannel(RXA). For Synthetic, returns -1 and is a no-op.
    /// <paramref name="outputRateHz"/> picks the TXA profile: 48000 for P1 (48k in/out, CFIR off),
    /// 192000 for P2 (48k mic / 96k dsp / 192k out, CFIR on). Defaults to P1 for tests and
    /// bring-up code that doesn't care.</summary>
    int OpenTxChannel(int outputRateHz = 48_000);

    /// <summary>Flip MOX. When on: SetChannelState(RXA,0,1) then SetChannelState(TXA,1,0).
    /// When off: SetChannelState(TXA,0,1) then SetChannelState(RXA,1,0). For Synthetic, no-op.
    /// If OpenTxChannel has not been called (no TXA), this is a no-op.</summary>
    void SetMox(bool moxOn);

    /// <summary>RXA signal-strength meter in dBm (Thetis rxaMeterType.RXA_S_AV, idx 1).
    /// Returns a frozen −140 dBm from the synthetic engine. Safe to call from the
    /// pipeline tick; WDSP's meter struct is lock-guarded internally.</summary>
    double GetRxaSignalDbm(int channelId);

    /// <summary>RXA per-stage readings (signal peak/avg, ADC peak/avg, AGC
    /// gain, AGC envelope peak/avg) sampled from the WDSP metering ring in a
    /// single pass. Returns <see cref="RxStageMeters.Silent"/> on the
    /// synthetic engine or when the channel is closed. Cal offset is NOT
    /// applied here — caller (DspPipelineService) decides whether to add the
    /// per-board offset before broadcasting, so unit tests can assert raw
    /// WDSP output. Safe to call from the pipeline tick.</summary>
    RxStageMeters GetRxStageMeters(int channelId);

    /// <summary>Set TXA modulator mode (USB/LSB/FM/AM/...). Calls
    /// SetTXAMode internally on WdspDspEngine; no-op for Synthetic and when no
    /// TXA is open.</summary>
    void SetTxMode(RxMode mode);

    /// <summary>Set TXA bandpass (SetTXABandpassFreqs). <paramref name="lowHz"/>
    /// / <paramref name="highHz"/> are signed Hz around baseband — LSB-style
    /// passbands are negative, DSB/AM/FM symmetric. No-op for Synthetic and
    /// when TXA is not open.</summary>
    void SetTxFilter(int lowHz, int highHz);

    /// <summary>Process one WDSP-sized block of mic audio through TXA and return
    /// the modulated IQ. <paramref name="micMono"/> must contain exactly
    /// <see cref="TxBlockSamples"/> float samples (48 kHz mono). <paramref name="iqInterleaved"/>
    /// receives 2 × <see cref="TxOutputSamples"/> floats ([I0, Q0, I1, Q1, …]).
    /// For P1 the TXA output rate equals 48 kHz so input count == output count;
    /// for P2 the TXA upsamples to 192 kHz so output count == 4 × input count.
    /// Returns the number of IQ complex samples produced (0 if TXA not open, MOX
    /// off, or the engine does not implement TX processing like Synthetic).</summary>
    int ProcessTxBlock(ReadOnlySpan<float> micMono, Span<float> iqInterleaved);

    /// <summary>WDSP TXA mic-input block size in mono samples. Mic ingest buffers
    /// accumulate this many samples before calling ProcessTxBlock.</summary>
    int TxBlockSamples { get; }

    /// <summary>WDSP TXA IQ-output block size in complex samples. Equals
    /// <see cref="TxBlockSamples"/> for P1 (48 kHz out); for P2 the TXA upsamples
    /// 48k → 192k so this is 4 × <see cref="TxBlockSamples"/>.</summary>
    int TxOutputSamples { get; }

    /// <summary>Set TXA mic-side linear gain (Thetis audio.cs:218-224 wires the
    /// mic-gain dB slider via <c>SetTXAPanelGain1(TXA, 10^(db/20))</c>).
    /// <paramref name="linearGain"/> is already linear. No-op on Synthetic
    /// and when TXA is not open.</summary>
    void SetTxPanelGain(double linearGain);

    /// <summary>Set the TXA Leveler maximum-gain ceiling in dB. Calls
    /// <c>SetTXALevelerTop</c> (wcpAGC.c:648), which WDSP converts internally
    /// to a linear cap via <c>pow(10, maxgainDb/20)</c>. Caller is
    /// responsible for range-clamping; this method passes the value through.
    /// No-op on Synthetic and when TXA is not open.</summary>
    void SetTxLevelerMaxGain(double maxGainDb);

    /// <summary>Start or stop the TXA internal-tune post-generator tone
    /// (Thetis console.cs:18648 `chkTUN_CheckedChanged`). When on, TXA emits
    /// a steady unmodulated carrier regardless of mic input. When off, the
    /// post-generator is disabled and normal mic-driven TX resumes.</summary>
    void SetTxTune(bool on);

    /// <summary>Latest per-stage TXA peak meters sampled from the last
    /// ProcessTxBlock call. Returns <see cref="TxStageMeters.Silent"/> when
    /// TXA is not open or MOX is off (no fresh samples). Safe to poll
    /// concurrently with ProcessTxBlock — the engine publishes via an
    /// atomic snapshot so the reader sees a consistent set.</summary>
    TxStageMeters GetTxStageMeters();

    /// <summary>Two-tone test generator. Replaces mic input with summed tones
    /// at <paramref name="freq1"/>/<paramref name="freq2"/> while armed.
    /// Standard PureSignal calibration excitation but useful standalone.
    /// Mutually exclusive with TUN (both share the WDSP PostGen stage).
    /// Protocol-agnostic. No-op on Synthetic.</summary>
    void SetTwoTone(bool on, double freq1, double freq2, double mag);

    // ----------------- PureSignal predistortion (TXA-side) -----------------
    // PS lives inside the TXA channel (txa[ch].calcc.p, txa[ch].iqc.p0/p1
    // allocated by create_txa). The setters here drive the WDSP state
    // machine; FeedPsFeedbackBlock pumps paired TX-modulator + RX-coupler
    // IQ into pscc. Synthetic implements all of these as no-ops; meters
    // return PsStageMeters.Silent.

    /// <summary>Master arm. true → SetPSRunCal(1) and SetPSControl mode-on
    /// (auto vs single is set via <see cref="SetPsControl"/>). false →
    /// pihpsdr's "7× zero-pscc → SetPSRunCal(0) → SetPSControl reset"
    /// shutdown sequence so the iqc stage doesn't latch a stale curve.
    /// </summary>
    void SetPsEnabled(bool enabled);

    /// <summary>Cal-mode select. <paramref name="autoCal"/> = continuous
    /// adaptation; <paramref name="singleCal"/> = one-shot collect-then-stay.
    /// At most one of the two should be true; if both, single takes
    /// precedence (one-shot then auto). Calls <c>SetPSControl</c> directly.
    /// </summary>
    void SetPsControl(bool autoCal, bool singleCal);

    /// <summary>Freeze (true) or resume (false) PureSignal calibration without
    /// disturbing the applied correction. true → <c>SetPSRunCal(0)</c>: calcc's
    /// pscc state machine stops cycling/re-fitting, but the iqc predistorter
    /// keeps applying its current coefficients (SetPSRunCal does NOT trigger the
    /// iqc turn-off ramp). false → <c>SetPSRunCal(1)</c> resumes. Used to "catch
    /// and hold" a converged automode correction so it stops flickering and the
    /// signal holds (matches single-cal/LSTAYON behaviour without re-collecting).
    /// </summary>
    void SetPsHold(bool hold);

    /// <summary>Apply timing + hardware-peak + ints/spi settings as a batch
    /// (each call internally guards against the heavy
    /// <c>SetPSIntsAndSpi</c> path firing when the values haven't changed).
    /// </summary>
    void SetPsAdvanced(bool ptol, double moxDelaySec, double loopDelaySec,
                      double ampDelayNs, double hwPeak, int ints, int spi);

    /// <summary>Set just the hardware-peak. Called from RadioService at
    /// connect time once the protocol/board is known so the right value
    /// (P1=0.4072, P2 G2=0.6121, P2 ANAN-7000=0.2899) lands before the
    /// operator arms PS.</summary>
    void SetPsHwPeak(double hwPeak);

    /// <summary>Push one paired TX-mod-IQ + RX-feedback-IQ block into the
    /// WDSP <c>psccF</c> entry. Block size must match the value pihpsdr
    /// uses (1024 complex samples at 192 kHz). Caller owns the buffers; the
    /// engine copies internally before handing to the native side.</summary>
    void FeedPsFeedbackBlock(ReadOnlySpan<float> txI, ReadOnlySpan<float> txQ,
                             ReadOnlySpan<float> rxI, ReadOnlySpan<float> rxQ);

    /// <summary>Latest PureSignal stage readings (GetPSInfo + GetPSMaxTX).
    /// Returns <see cref="PsStageMeters.Silent"/> when PS isn't armed or
    /// the engine has no TXA. Safe to poll concurrently.</summary>
    PsStageMeters GetPsStageMeters();

    /// <summary>Reset PS state — calls <c>SetPSControl(1,0,0,0)</c>. Useful
    /// after an aborted calibration or when changing radios.</summary>
    void ResetPs();

    /// <summary>Save the current PS correction curve to disk (ints/spi must
    /// be 16/256 — WDSP refuses other shapes per Thetis PSForm.cs:865).
    /// </summary>
    void SavePsCorrection(string path);

    /// <summary>Restore a previously-saved correction curve. Equivalent to
    /// PSForm's "Restore-and-go" with <c>SetPSControl(0,0,0,1)</c>.</summary>
    void RestorePsCorrection(string path);

    // ----------------- CFC (Continuous Frequency Compressor) ---------------
    // Multi-band frequency-domain compressor (xcfcomp) — issue #123. The
    // stage already lives in xtxa between xeqp and xbandpass; this seam just
    // pushes parameters and toggles run flags. Synthetic engine validates
    // and no-ops; the WDSP engine pushes the profile arrays + scalar
    // parameters under the TXA lock and flips Run last so a partial config
    // never lands in the live audio path.

    /// <summary>Apply a CFC profile: per-band frequencies/compression/post-gains
    /// plus scalar pre-comp/pre-EQ/post-EQ-run/master-run toggles. The
    /// <c>cfg.Bands</c> array must have exactly 10 entries (matches pihpsdr
    /// classic-mode shape; the panel layout depends on it). No-op when no TXA
    /// is open or on Synthetic.</summary>
    void SetCfcConfig(CfcConfig cfg);

    // ----------------- TX Monitor (audition path, issue #106 follow-up) ----
    // Lets the operator hear the post-bandpass / post-CFIR TX audio on a local
    // audio sink — with or without keying — so they can dial in the
    // EQ, leveler, and bandwidth profile pre-RF. Implemented in the WDSP
    // engine as a private RXA channel that demodulates the on-air IQ back to
    // 48 kHz mono audio. Synthetic no-ops; ReadTxMonitorAudio returns 0.

    /// <summary>Operator toggle for the TX-monitor audition path. When true,
    /// the engine starts demodulating the post-CFIR TX IQ and exposes the
    /// resulting mono audio via <see cref="ReadTxMonitorAudio"/>. When false,
    /// stops feeding the monitor channel; subsequent ReadTxMonitorAudio calls
    /// return 0 once the ring drains. Idempotent and cheap to call repeatedly.
    /// No-op on Synthetic.</summary>
    void SetTxMonitorEnabled(bool enabled);

    /// <summary>Drain demodulated TX-monitor audio. Same shape as
    /// <see cref="ReadAudio"/> — returns the number of mono float32 samples
    /// written into <paramref name="output"/>. Returns 0 when monitor is off,
    /// when the channel hasn't been opened yet, or when no samples are queued.
    /// Synthetic returns 0 unconditionally.</summary>
    int ReadTxMonitorAudio(Span<float> output);

    /// <summary>Volatile read of the operator's monitor request flag. Used by
    /// the audio-broadcast pipeline to decide whether to substitute monitor
    /// audio for the RX AudioFrame. Reflects the toggle, not whether the
    /// monitor channel is fully spun up. Synthetic returns false.</summary>
    bool IsTxMonitorOn { get; }
}
