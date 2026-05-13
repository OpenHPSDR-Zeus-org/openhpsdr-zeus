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

using System.Collections.Concurrent;
using Zeus.Contracts;

namespace Zeus.Dsp;

public sealed class SyntheticDspEngine : IDspEngine
{
    private sealed class ChannelState
    {
        public int SampleRateHz;
        public int Width;
        public long VfoHz;
        public RxMode Mode = RxMode.USB;
        public int FilterLowHz;
        public int FilterHighHz = 3000;
        public long StartTimestampTicks = Environment.TickCount64;
        public readonly float[] WfHistory;
        public readonly Random Rng = new(0xC0FFEE);
        public ChannelState(int width)
        {
            Width = width;
            WfHistory = new float[width];
            Array.Fill(WfHistory, -90f);
        }
    }

    private readonly ConcurrentDictionary<int, ChannelState> _channels = new();
    private int _nextId;

    public int OpenChannel(int sampleRateHz, int pixelWidth)
    {
        if (pixelWidth <= 0) throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        int id = Interlocked.Increment(ref _nextId);
        _channels[id] = new ChannelState(pixelWidth) { SampleRateHz = sampleRateHz };
        return id;
    }

    public void CloseChannel(int channelId) => _channels.TryRemove(channelId, out _);

    public void FeedIq(int channelId, ReadOnlySpan<double> interleavedIqSamples) { /* no-op for Phase 0 */ }

    public void SetMode(int channelId, RxMode mode)
    {
        if (_channels.TryGetValue(channelId, out var s)) s.Mode = mode;
    }

    public void SetFilter(int channelId, int lowHz, int highHz)
    {
        if (_channels.TryGetValue(channelId, out var s)) { s.FilterLowHz = lowHz; s.FilterHighHz = highHz; }
    }

    public void SetVfoHz(int channelId, long vfoHz)
    {
        if (_channels.TryGetValue(channelId, out var s)) s.VfoHz = vfoHz;
    }

    public void SetAgcTop(int channelId, double topDb) { /* synthetic has no AGC */ }

    public void SetRxAfGainDb(int channelId, double db) { /* synthetic has no audio path */ }

    public void SetNoiseReduction(int channelId, NrConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        // Validate the enum shape so callers get a fast failure when sending
        // garbage values — synthetic runs during dev and in CI, so this is where
        // bad client payloads show up first. No audio side-effect.
        if (!Enum.IsDefined(cfg.NrMode)) throw new ArgumentException($"unknown NrMode {cfg.NrMode}", nameof(cfg));
        if (!Enum.IsDefined(cfg.NbMode)) throw new ArgumentException($"unknown NbMode {cfg.NbMode}", nameof(cfg));
    }

    // Synthetic doesn't render from a real analyzer so zoom is a no-op for the
    // pixel output, but we still validate so bogus levels surface during dev.
    public void SetZoom(int channelId, int level)
    {
        ValidateZoomLevel(level);
    }

    public const int MinZoomLevel = 1;
    // Cap at 32× — at AnalyzerFftSize=16384 that leaves 512 bins after the
    // centre clip, below typical pan pixel widths so the trace softens at
    // 32× but stays usable for narrow-signal hunting (CW). Bump
    // AnalyzerFftSize to 32768 if 32× sharpness becomes a problem.
    public const int MaxZoomLevel = 32;

    internal static void ValidateZoomLevel(int level)
    {
        if (level < MinZoomLevel || level > MaxZoomLevel)
            throw new ArgumentException($"zoom level must be in [{MinZoomLevel},{MaxZoomLevel}]; got {level}", nameof(level));
    }

    public int ReadAudio(int channelId, Span<float> output)
    {
        output.Clear();
        // PERF_PASS_3_DEBUG: emit silence frames (matching the WDSP cadence) so
        // synthetic mode exercises the client audio-scheduling path end-to-end.
        // Uncommitted local edit; stash before merge.
        if (Environment.GetEnvironmentVariable("ZEUS_PERF_TEST") == "1")
        {
            return output.Length;
        }
        return 0;
    }

    // TX interface — synthetic engine has no TXA and no outbound path. Returning
    // -1 lets callers distinguish "no TXA open" from any real id. SetMox is a
    // no-op so TxService calls while disconnected don't throw.
    public int OpenTxChannel(int outputRateHz = 48_000) => -1;

    public void SetMox(bool moxOn) { }

    // No live radio behind the synthetic engine; a frozen −140 dBm reads as
    // "below noise floor" on the S-meter.
    public double GetRxaSignalDbm(int channelId) => -140.0;

    // No WDSP RXA chain on synthetic — return the silent record so the
    // 0x19 broadcast publishes a consistent (sentinel-shaped) frame even
    // when the real engine isn't loaded.
    public RxStageMeters GetRxStageMeters(int channelId) => RxStageMeters.Silent;

    // Synthetic has no TXA chain. SetTxMode stashes nothing, ProcessTxBlock
    // reports "no IQ produced" so TX-side callers skip ring writes. Block size
    // mirrors WDSP so tests that round-trip through the interface can assume
    // the same buffering shape.
    public void SetTxMode(RxMode mode) { }
    public void SetTxFilter(int lowHz, int highHz) { }
    public int ProcessTxBlock(ReadOnlySpan<float> micMono, Span<float> iqInterleaved) => 0;
    public int TxBlockSamples => 1024;
    public int TxOutputSamples => 1024;
    public void SetTxPanelGain(double linearGain) { }
    public void SetTxLevelerMaxGain(double maxGainDb) { }
    public void SetTxTune(bool on) { }
    public TxStageMeters GetTxStageMeters() => TxStageMeters.Silent;

    // TwoTone — no PostGen, so no-op on synthetic.
    public void SetTwoTone(bool on, double freq1, double freq2, double mag) { }

    // PureSignal — synthetic has no TXA / calcc / iqc. All setters and the
    // feedback pump are no-ops; GetPsStageMeters returns the silent record.
    public void SetPsEnabled(bool enabled) { }
    public void SetPsControl(bool autoCal, bool singleCal) { }
    public void SetPsAdvanced(bool ptol, double moxDelaySec, double loopDelaySec,
                              double ampDelayNs, double hwPeak, int ints, int spi) { }
    public void SetPsHwPeak(double hwPeak) { }
    public void FeedPsFeedbackBlock(ReadOnlySpan<float> txI, ReadOnlySpan<float> txQ,
                                    ReadOnlySpan<float> rxI, ReadOnlySpan<float> rxQ) { }
    public PsStageMeters GetPsStageMeters() => PsStageMeters.Silent;
    public void ResetPs() { }
    public void SavePsCorrection(string path) { }
    public void RestorePsCorrection(string path) { }

    // CFC — synthetic has no TXA so SetCfcConfig only validates the payload
    // shape so bad client requests fail fast in dev/CI without touching audio.
    public void SetCfcConfig(CfcConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        if (cfg.Bands is null) throw new ArgumentException("Bands must not be null", nameof(cfg));
        if (cfg.Bands.Length != 10)
            throw new ArgumentException($"Bands must have exactly 10 entries; got {cfg.Bands.Length}", nameof(cfg));
    }

    // VST plugin-host seam — synthetic engine has no plugin chain wired and
    // never will. Both methods return false unconditionally so callers take
    // the bypass path and use the original buffer with zero overhead.
    public bool ProcessRxVstChain(Span<float> audio, int frames, int sampleRateHz) => false;
    public bool ProcessTxMicVstChain(Span<float> audio, int frames, int sampleRateHz) => false;

    // TX Monitor — synthetic has no TXA / RXA, no IQ to demodulate. Toggle is
    // a no-op; ReadTxMonitorAudio always returns 0 so the audio-broadcast
    // path falls through to the regular RX AudioFrame.
    public void SetTxMonitorEnabled(bool enabled) { }
    public int ReadTxMonitorAudio(Span<float> output) => 0;
    public bool IsTxMonitorOn => false;

    // Synthetic has no TX analyzer; the TX panadapter stays on the RX trace.
    // Returning false tells DspPipelineService.Tick to leave the display alone
    // while MOX is on, matching the existing "no new data" semantics.
    public bool TryGetTxDisplayPixels(DisplayPixout which, Span<float> dbOut) => false;

    // Synthetic has no PS feedback path either — the PS-Monitor toggle is a
    // no-op here, same shape as TryGetTxDisplayPixels.
    public bool TryGetPsFeedbackDisplayPixels(DisplayPixout which, Span<float> dbOut) => false;

    private const float NoiseFloorDb = -90f;
    private const float SweepPeakDb = -25f;
    private const float StaticPeakDb = -35f;
    private const int SweepHalfWidth = 12;    // data columns each side of peak centre
    private const int StaticHalfWidth = 18;
    private const double SweepPeriodSeconds = 6.0;

    public bool TryGetDisplayPixels(int channelId, DisplayPixout which, Span<float> dbOut)
    {
        if (!_channels.TryGetValue(channelId, out var s)) return false;
        if (dbOut.Length != s.Width) throw new ArgumentException($"expected span of {s.Width}", nameof(dbOut));

        long elapsedMs = Environment.TickCount64 - s.StartTimestampTicks;
        double sweepSeconds = elapsedMs / 1000.0;
        double sweepPhase = (sweepSeconds / SweepPeriodSeconds) % 1.0;
        int sweepCol = (int)(sweepPhase * s.Width) % s.Width;
        int staticColA = s.Width / 4;       // fixed reference carriers so the sync diagonal has
        int staticColB = (s.Width * 3) / 4; // a visible stationary backdrop to compare against

        if (which == DisplayPixout.Panadapter)
        {
            FillPanadapter(dbOut, sweepCol, staticColA, staticColB, s);
        }
        else
        {
            FillWaterfall(dbOut, sweepCol, staticColA, staticColB, s);
        }

        // Match WDSP's raw pixel axis (pixel 0 = highest positive frequency;
        // doc 03 §10). The server pipeline unconditionally reverses before
        // serialize, so we emit in WDSP order here to keep that reversal
        // branch-free at the display seam.
        dbOut.Reverse();
        return true;
    }

    private static void FillPanadapter(Span<float> dst, int sweepCol, int staticA, int staticB, ChannelState s)
    {
        for (int i = 0; i < dst.Length; i++)
        {
            float background = NoiseFloorDb + GaussianDb(s.Rng);
            float v = background;
            v = MathF.Max(v, ShapedPeak(i, sweepCol, SweepPeakDb, SweepHalfWidth));
            v = MathF.Max(v, ShapedPeak(i, staticA, StaticPeakDb, StaticHalfWidth));
            v = MathF.Max(v, ShapedPeak(i, staticB, StaticPeakDb, StaticHalfWidth));
            dst[i] = v;
        }
    }

    private static void FillWaterfall(Span<float> dst, int sweepCol, int staticA, int staticB, ChannelState s)
    {
        const float alpha = 0.55f;
        for (int i = 0; i < dst.Length; i++)
        {
            float background = NoiseFloorDb + GaussianDb(s.Rng);
            float v = background;
            v = MathF.Max(v, ShapedPeak(i, sweepCol, SweepPeakDb, SweepHalfWidth));
            v = MathF.Max(v, ShapedPeak(i, staticA, StaticPeakDb, StaticHalfWidth));
            v = MathF.Max(v, ShapedPeak(i, staticB, StaticPeakDb, StaticHalfWidth));
            s.WfHistory[i] = alpha * v + (1f - alpha) * s.WfHistory[i];
            dst[i] = s.WfHistory[i];
        }
    }

    private static float ShapedPeak(int i, int centre, float peakDb, int halfWidth)
    {
        int dist = Math.Abs(i - centre);
        if (dist > halfWidth) return float.NegativeInfinity;
        float t = (float)dist / halfWidth;            // 0 at peak, 1 at skirt
        float falloffDb = t * t * 25f;                 // quadratic, 25 dB by the skirt
        return peakDb - falloffDb;
    }

    private static float GaussianDb(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    public void Dispose() => _channels.Clear();
}
