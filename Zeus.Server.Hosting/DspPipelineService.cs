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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Dsp.Wdsp;
using Zeus.Protocol1;

namespace Zeus.Server;

public class DspPipelineService : BackgroundService,
    Zeus.Protocol1.IRxPacketSink,
    Zeus.Protocol2.IRxPacketSink
{
    private const int Width = 2048;
    private const int SyntheticSampleRateHz = 192_000;
    public const int AudioOutputRateHz = 48_000;
    private const int AudioDrainCapacity = 2048;
    private const float DisplayInvalidBinDb = -200f;
    private static readonly TimeSpan TickPeriod = TimeSpan.FromMilliseconds(1000.0 / 30.0);

    private readonly RadioService _radio;
    private readonly StreamingHub _hub;
    private readonly IRxAudioSink[] _audioSinks;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DspPipelineService> _log;

    /// <summary>
    /// Raised when an RX S-meter reading is available (approximately 5 Hz).
    /// Arguments: (channelId, dBm)
    /// </summary>
    public event Action<int, double>? RxMeterUpdated;

    /// <summary>
    /// Raised on every decoded RX IQ frame, after it has been fed to WDSP.
    /// Arguments: (receiver, sampleRateHz, interleavedIQ).
    /// The memory references a pooled buffer and is only valid for the
    /// duration of the synchronous handler — copy if retention is needed.
    /// </summary>
    public event Action<int, int, ReadOnlyMemory<double>>? RxIqAvailable;

    /// <summary>
    /// Raised when demodulated RX audio samples are available (~30 Hz ticks,
    /// 48 kHz mono FLOAT32). Arguments: (receiver, sampleRateHz, samples).
    /// The memory references a local buffer and is only valid for the
    /// duration of the synchronous handler — copy if retention is needed.
    /// </summary>
    public event Action<int, int, ReadOnlyMemory<float>>? RxAudioAvailable;

    /// <summary>
    /// Raised when TX-monitor audio is available — the processed transmit audio
    /// demodulated back from the TX IQ (post EQ / compressor / leveler / CFC),
    /// i.e. what actually goes on the air. Only fires while the TX monitor /
    /// preview path is running. 48 kHz mono float32; args (receiver,
    /// sampleRateHz, samples). Memory valid only for the synchronous handler.
    /// </summary>
    public event Action<int, int, ReadOnlyMemory<float>>? TxMonitorAudioAvailable;

    /// <summary>
    /// Delegate for the RX audio plugin insert seam (<c>rx.post-demod</c> slot).
    /// Invoked once per <see cref="Tick"/> over the demodulated 48 kHz mono RX
    /// audio block, IN PLACE, after the MOX fade ramp and BEFORE the CW
    /// sidetone is mixed in and the block is published to sinks — so a filter
    /// shapes the received band audio without touching the locally-generated
    /// sidetone. The <c>audio</c> span is both input and output.
    /// </summary>
    public delegate void RxAudioBlockHandler(Span<float> audio, int frames, int sampleRate);

    // RX audio plugin insert handler. Wired by AudioPluginBridge when an
    // rx.post-demod audio plugin is attached; null (the default) makes the RX
    // path bit-identical to before this seam existed — single volatile read in
    // the Tick hot path, no cost when no RX plugin is loaded. The handler runs
    // on the DSP pipeline thread and MUST be realtime-disciplined (no alloc /
    // lock / IO) — AudioChain.Process honours that contract.
    private volatile RxAudioBlockHandler? _rxAudioPluginHandler;

    /// <summary>Install (or clear, with <c>null</c>) the RX audio plugin
    /// insert handler. Single volatile write; safe from the control thread.</summary>
    public void SetRxAudioPluginHandler(RxAudioBlockHandler? handler) => _rxAudioPluginHandler = handler;

    /// <summary>Engage (or release) meter-only TX monitor for Auto Tune. When
    /// engaged together with TX Monitor, the TXA chain runs (stage meters
    /// animate) but the demodulated monitor audio is not broadcast, so the
    /// operator hears nothing while Auto Tune samples. Single volatile write;
    /// safe from the request thread.</summary>
    public void SetTxMonitorMeterOnly(bool on) => _txMonitorMeterOnly = on;

    internal struct RxAudioLevelerState
    {
        public double GainDb;
        public bool DiagnosticsValid;
        public double InputRmsDbfs;
        public double InputPeakDbfs;
        public double OutputRmsDbfs;
        public double OutputPeakDbfs;
        public double DesiredGainDb;
        public double AppliedGainDb;
        public double GainDeltaDb;
        public double PeakHeadroomDb;
        public double PreLimitPeakDbfs;
        public double OutputLimitReductionDb;
        public int OutputLimitSampleCount;
        public int PauseHoldBlocks;
        public bool BoostSlewLimited;
        public bool PeakLimited;
        public bool OutputLimited;
    }

    private RxAudioLevelerState _rxAudioLeveler;

    internal sealed class AdaptiveSquelchState
    {
        internal readonly double[] Window = new double[AdaptiveSquelchWindowSamples];
        internal readonly double[] Scratch = new double[AdaptiveSquelchWindowSamples];
        public int WindowIndex;
        public int WindowFill;
        public double NoiseFloorDbm = double.NaN;
        public double LastSignalDbm = double.NaN;
        public bool Open;
        public int CloseHoldBlocks;
        public double Gain;
    }

    private AdaptiveSquelchState _adaptiveSquelch = new();

    private const double RxLevelerTargetRmsDb = -18.0;
    // Softened (issue #733): raise the gate so near-noise-floor signals pass
    // through unprocessed (clean static), and cap the boost so the leveler
    // gently lifts weak audio instead of pumping it ~36 dB toward target — the
    // big boost + fast slews were the "crackle on anything above the noise
    // floor" zipper. The CUT/peak-guard safety below is unchanged, so loud
    // signals are still caught.
    private const double RxLevelerGateRmsDb = -50.0;
    // Soft-gate window above the hard gate: the upward boost ramps 0 -> full
    // across [GateRms, GateRms + GateSoftWindow] instead of snapping on at the
    // gate. Without this, a hair-trigger AGC-T move that nudges a weak signal
    // across the gate toggled the full boost on/off — an ~18 dB output step
    // heard as crackle / "audio fell off the planet". WDSP's own AGC-T is 1:1
    // and Thetis-faithful; this always-on leveler (which Thetis lacks) was the
    // amplifier. The boost ramp is continuous with the belowGate=0 region.
    private const double RxLevelerGateSoftWindowDb = 8.0;
    // Cap the upward boost low (was 10 dB) so the leveler no longer fights the
    // operator's AGC-T: AGC-T sets weak-signal loudness (Thetis-like) and the
    // leveler only nudges. The downward CUT below is unchanged — it stays the
    // blast-guard that catches a sudden strong signal.
    private const double RxLevelerMaxBoostDb = 3.0;
    private const double RxLevelerMaxCutDb = -24.0;
    private const double RxLevelerBoostSlewDbPerBlock = 2.0;
    private const double RxLevelerFastBoostSlewDbPerBlock = 2.5;
    private const double RxLevelerFastBoostHeadroomDb = 6.0;
    private const double RxLevelerVeryFastBoostSlewDbPerBlock = 3.0;
    private const double RxLevelerVeryFastBoostHeadroomDb = 10.0;
    private const double RxLevelerVeryFastBoostGateRmsDb = -45.0;
    private const double RxLevelerCrestCatchupBoostSlewDbPerBlock = 3.0;
    private const double RxLevelerCrestCatchupHeadroomDb = 16.0;
    private const double RxLevelerCrestCatchupMinCrestDb = 8.0;
    private const double RxLevelerCrestCatchupMaxRmsDb = -28.0;
    private const double RxLevelerCrestCatchupMinPeakDb = -52.0;
    private const double RxLevelerCrestCatchupMinGainGapDb = 6.0;
    private const double RxLevelerMemoryCatchupGateRmsDb = -66.0;
    private const double RxLevelerMemoryCatchupGatePeakDb = -56.0;
    private const double RxLevelerMemoryCatchupMinGainDb = 3.0;
    private const double RxLevelerSmoothCutDb = 6.0;
    private const int RxLevelerPauseHoldBlocks = 18;
    private const double RxLevelerPauseMemoryDecayDbPerBlock = 4.5;
    private const int RxLevelerGainRampMaxSamples = 256;
    private const double RxLevelerPeakTarget = 0.74;
    private const double RxLevelerOutputSoftKnee = 0.74;
    private const double RxLevelerOutputPeakCeiling = 0.84;
    private const int AdaptiveSquelchWindowSamples = 12;
    private const int AdaptiveSquelchMinSamples = 2;
    private const double AdaptiveSquelchFloorPercentile = 0.20;
    private const double AdaptiveSquelchFloorRiseSlewDb = 0.25;
    private const double AdaptiveSquelchFloorFallSlewDb = 7.0;
    private const int AdaptiveSquelchCloseHoldBlocks = 12;
    private const double AdaptiveSquelchAttackPerBlock = 1.0;
    private const double AdaptiveSquelchReleasePerBlock = 0.14;
    private const double AdaptiveSquelchOpenMarginDb = 2.5;
    private const double AdaptiveSquelchOpenInitialGain = 0.35;

    internal static float SanitizeAudioSample(float sample)
    {
        if (!float.IsFinite(sample)) return 0f;
        return Math.Clamp(sample, -1f, 1f);
    }

    internal static void SanitizeAudioBuffer(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = SanitizeAudioSample(samples[i]);
        }
    }

    internal static void LimitRxAudioBuffer(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i];
            samples[i] = float.IsFinite(s) ? SoftLimitRxAudioSample(s) : 0f;
        }
    }

    internal static void SanitizeDisplayBuffer(Span<float> dbBins)
    {
        for (int i = 0; i < dbBins.Length; i++)
        {
            if (!float.IsFinite(dbBins[i]))
                dbBins[i] = DisplayInvalidBinDb;
        }
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 0.0;
        double sumSq = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            double s = samples[i];
            sumSq += s * s;
        }
        return Math.Sqrt(sumSq / samples.Length);
    }

    private static double PeakAbs(ReadOnlySpan<float> samples)
    {
        double peak = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            double value = Math.Abs(samples[i]);
            if (double.IsFinite(value) && value > peak) peak = value;
        }
        return peak;
    }

    private static double ClampUnit(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;

    private static double DbToLinear(double db) =>
        double.IsFinite(db) ? Math.Pow(10.0, db / 20.0) : 1.0;

    internal static void ApplyRxAudioLeveler(
        Span<float> samples,
        ref RxAudioLevelerState state)
    {
        if (samples.Length == 0) return;

        double sumSq = 0.0;
        double peak = 0.0;
        for (int i = 0; i < samples.Length; i++)
        {
            float s = SanitizeAudioSample(samples[i]);
            double a = Math.Abs(s);
            if (a > peak) peak = a;
            sumSq += (double)s * s;
        }

        double rms = Math.Sqrt(sumSq / samples.Length);
        double inputRmsDbfs = AudioLinearToDbfsRaw(rms);
        double inputPeakDbfs = AudioLinearToDbfsRaw(peak);
        bool belowGate = rms <= 0.0 || !double.IsFinite(inputRmsDbfs) || inputRmsDbfs < RxLevelerGateRmsDb;

        double desiredDb = belowGate
            ? 0.0
            : RxLevelerTargetRmsDb - inputRmsDbfs;
        desiredDb = Math.Clamp(desiredDb, RxLevelerMaxCutDb, RxLevelerMaxBoostDb);

        // Soft gate: taper the upward BOOST to zero as the input approaches the
        // floor, so crossing the gate can never snap the full boost on/off (the
        // AGC-T hair-trigger). Cuts (desiredDb < 0, the loud-signal blast-guard)
        // are NEVER tapered. Continuous with belowGate: at the gate the factor is
        // 0, reaching full boost RxLevelerGateSoftWindowDb above it.
        if (!belowGate && desiredDb > 0.0)
        {
            double gateFactor = Math.Clamp(
                (inputRmsDbfs - RxLevelerGateRmsDb) / RxLevelerGateSoftWindowDb,
                0.0, 1.0);
            desiredDb *= gateFactor;
        }

        double peakHeadroomDb = double.NaN;
        bool peakLimited = false;
        if (peak > 1.0e-9)
        {
            peakHeadroomDb = 20.0 * Math.Log10(Math.Max(RxLevelerPeakTarget, 1.0e-9) / peak);
            if (double.IsFinite(peakHeadroomDb) && desiredDb > peakHeadroomDb)
            {
                desiredDb = Math.Clamp(peakHeadroomDb, RxLevelerMaxCutDb, RxLevelerMaxBoostDb);
                peakLimited = true;
            }
        }

        double currentDb = double.IsFinite(state.GainDb) ? state.GainDb : 0.0;
        currentDb = Math.Clamp(currentDb, RxLevelerMaxCutDb, RxLevelerMaxBoostDb);

        // True when the gain we are currently holding would, on its own, drive
        // this block's peak past the limiter target — i.e. a gain "held" high
        // across a quiet gap (pause memory / catch-up) is about to be dumped onto
        // a louder block. The raw input-peak gate below (peak > target) misses
        // this case because the danger is gain × peak, not peak alone: a signal
        // whose input peak is modest still blasts the speaker if the held gain is
        // large. When this is set we cut as urgently as a clipping peak would.
        bool currentGainOverdrivesPeak =
            double.IsFinite(peakHeadroomDb) && currentDb > peakHeadroomDb;

        double nextDb = currentDb;
        if (belowGate)
        {
            bool holdMemory = state.PauseHoldBlocks > 0;
            if (holdMemory)
                state.PauseHoldBlocks--;

            double releaseStep = RxLevelerPauseMemoryDecayDbPerBlock;
            if (holdMemory)
            {
                nextDb = currentDb;
            }
            else if (Math.Abs(currentDb) <= releaseStep)
            {
                nextDb = 0.0;
            }
            else
            {
                nextDb = currentDb + (currentDb > 0.0 ? -releaseStep : releaseStep);
            }
        }
        else
        {
            state.PauseHoldBlocks = RxLevelerPauseHoldBlocks;
        }

        bool boostSlewLimited = false;
        if (!belowGate && desiredDb > currentDb)
        {
            double boostSlewDb = RxLevelerBoostSlewDbPerBlock;
            if (double.IsFinite(peakHeadroomDb) && peakHeadroomDb >= RxLevelerFastBoostHeadroomDb)
            {
                boostSlewDb = Math.Max(boostSlewDb, RxLevelerFastBoostSlewDbPerBlock);
            }
            if (double.IsFinite(peakHeadroomDb) &&
                peakHeadroomDb >= RxLevelerVeryFastBoostHeadroomDb &&
                inputRmsDbfs >= RxLevelerVeryFastBoostGateRmsDb)
            {
                boostSlewDb = Math.Max(boostSlewDb, RxLevelerVeryFastBoostSlewDbPerBlock);
            }

            double crestDb = inputPeakDbfs - inputRmsDbfs;
            if (double.IsFinite(crestDb) &&
                crestDb >= RxLevelerCrestCatchupMinCrestDb &&
                inputRmsDbfs <= RxLevelerCrestCatchupMaxRmsDb &&
                inputPeakDbfs >= RxLevelerCrestCatchupMinPeakDb &&
                desiredDb - currentDb >= RxLevelerCrestCatchupMinGainGapDb)
            {
                boostSlewDb = Math.Max(boostSlewDb, RxLevelerCrestCatchupBoostSlewDbPerBlock);
            }
            if (state.GainDb >= RxLevelerMemoryCatchupMinGainDb &&
                inputRmsDbfs >= RxLevelerMemoryCatchupGateRmsDb &&
                inputPeakDbfs >= RxLevelerMemoryCatchupGatePeakDb)
            {
                boostSlewDb = Math.Max(boostSlewDb, RxLevelerFastBoostSlewDbPerBlock);
            }

            nextDb = Math.Min(desiredDb, currentDb + boostSlewDb);
            boostSlewLimited = nextDb + 1.0e-9 < desiredDb;
        }
        else if (!belowGate)
        {
            double cutSlewDb = (peak > RxLevelerPeakTarget || peakLimited || currentGainOverdrivesPeak)
                ? Math.Max(RxLevelerSmoothCutDb, currentDb - desiredDb)
                : RxLevelerSmoothCutDb;
            nextDb = Math.Max(desiredDb, currentDb - cutSlewDb);
        }
        nextDb = Math.Clamp(nextDb, RxLevelerMaxCutDb, RxLevelerMaxBoostDb);

        // Hard per-block peak guard. The smooth boost/cut slews above track
        // loudness gently; this is the safety floor that stops a held-high gain
        // from ever being *applied* to a block whose peak would then exceed the
        // limiter target. Without it, gain banked across a quiet gap gets dumped
        // onto the first loud-ish block of a new signal and rides the soft-limit
        // ceiling for several blocks before the slew catches up — the "sudden
        // strong signal blasts the speaker" failure this leveler exists to stop.
        // Cutting straight to the peak-safe gain is inaudible next to that blast.
        if (!belowGate && double.IsFinite(peakHeadroomDb) && nextDb > peakHeadroomDb)
            nextDb = Math.Clamp(peakHeadroomDb, RxLevelerMaxCutDb, RxLevelerMaxBoostDb);

        int rampSamples = Math.Clamp(Math.Min(samples.Length, RxLevelerGainRampMaxSamples), 1, Math.Max(1, samples.Length));
        double preLimitPeak = 0.0;
        int outputLimitSampleCount = 0;
        double appliedEndDb = belowGate ? 0.0 : nextDb;
        bool emergencyCut = !belowGate && nextDb < currentDb && (peak > RxLevelerPeakTarget || peakLimited || currentGainOverdrivesPeak);
        for (int i = 0; i < samples.Length; i++)
        {
            float clean = SanitizeAudioSample(samples[i]);
            double ramp = i < rampSamples
                ? (i + 1) / (double)rampSamples
                : 1.0;
            double gainDb = belowGate
                ? 0.0
                : emergencyCut
                    ? nextDb
                    : currentDb + (nextDb - currentDb) * ramp;
            double scaled = clean * DbToLinear(gainDb);
            double absScaled = Math.Abs(scaled);
            if (absScaled > preLimitPeak) preLimitPeak = absScaled;

            float limited = SoftLimitRxAudioSample((float)scaled);
            if (Math.Abs(limited) + 1.0e-6 < absScaled) outputLimitSampleCount++;
            samples[i] = limited;
        }

        double outputRms = Rms(samples);
        double outputPeak = PeakAbs(samples);
        double outputRmsDbfs = AudioLinearToDbfsRaw(outputRms);
        double outputPeakDbfs = AudioLinearToDbfsRaw(outputPeak);
        double preLimitPeakDbfs = AudioLinearToDbfsRaw(preLimitPeak);
        double outputLimitReductionDb = preLimitPeak > outputPeak && outputPeak > 0.0
            ? 20.0 * Math.Log10(preLimitPeak / outputPeak)
            : 0.0;

        state.GainDb = nextDb;
        state.DiagnosticsValid = true;
        state.InputRmsDbfs = inputRmsDbfs;
        state.InputPeakDbfs = inputPeakDbfs;
        state.OutputRmsDbfs = outputRmsDbfs;
        state.OutputPeakDbfs = outputPeakDbfs;
        state.DesiredGainDb = desiredDb;
        state.AppliedGainDb = appliedEndDb;
        state.GainDeltaDb = nextDb - currentDb;
        state.PeakHeadroomDb = peakHeadroomDb;
        state.PreLimitPeakDbfs = preLimitPeakDbfs;
        state.OutputLimitReductionDb = outputLimitReductionDb;
        state.OutputLimitSampleCount = outputLimitSampleCount;
        state.BoostSlewLimited = boostSlewLimited;
        state.PeakLimited = peakLimited;
        state.OutputLimited = outputLimitSampleCount > 0;
    }


    internal static double AudioRmsToFallbackDbm(double rms)
    {
        if (!double.IsFinite(rms)) return double.NaN;
        double dbfs = 20.0 * Math.Log10(Math.Max(rms, 1e-10));
        return dbfs - 50.0;
    }

    internal static double AdaptiveSquelchMarginDb() => AdaptiveSquelchOpenMarginDb;

    private static double AdaptiveSquelchCloseHysteresisDb(double marginDb) =>
        Math.Clamp(marginDb * 0.5, 1.5, 4.0);

    internal static void UpdateAdaptiveSquelchMeter(
        AdaptiveSquelchState state,
        SquelchConfig cfg,
        double signalDbm)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(cfg);
        if (!double.IsFinite(signalDbm) || signalDbm <= -250.0) return;

        signalDbm = Math.Clamp(signalDbm, -200.0, 60.0);
        state.LastSignalDbm = signalDbm;
        state.Window[state.WindowIndex] = signalDbm;
        state.WindowIndex = (state.WindowIndex + 1) % state.Window.Length;
        if (state.WindowFill < state.Window.Length) state.WindowFill++;

        if (state.WindowFill >= AdaptiveSquelchMinSamples)
        {
            Array.Copy(state.Window, state.Scratch, state.WindowFill);
            Array.Sort(state.Scratch, 0, state.WindowFill);
            int floorIndex = Math.Clamp(
                (int)Math.Round((state.WindowFill - 1) * AdaptiveSquelchFloorPercentile),
                0,
                state.WindowFill - 1);
            double candidateFloor = state.Scratch[floorIndex];
            if (!double.IsFinite(state.NoiseFloorDbm))
            {
                state.NoiseFloorDbm = candidateFloor;
            }
            else if (candidateFloor > state.NoiseFloorDbm)
            {
                state.NoiseFloorDbm = Math.Min(candidateFloor, state.NoiseFloorDbm + AdaptiveSquelchFloorRiseSlewDb);
            }
            else
            {
                state.NoiseFloorDbm = Math.Max(candidateFloor, state.NoiseFloorDbm - AdaptiveSquelchFloorFallSlewDb);
            }
        }

        if (!cfg.Enabled || !cfg.Adaptive || state.WindowFill < AdaptiveSquelchMinSamples
            || !double.IsFinite(state.NoiseFloorDbm))
        {
            state.Open = false;
            state.CloseHoldBlocks = 0;
            return;
        }

        double marginDb = AdaptiveSquelchMarginDb();
        double openThreshold = state.NoiseFloorDbm + marginDb;
        double closeThreshold = openThreshold - AdaptiveSquelchCloseHysteresisDb(marginDb);

        if (signalDbm >= openThreshold)
        {
            state.Open = true;
            state.CloseHoldBlocks = AdaptiveSquelchCloseHoldBlocks;
        }
        else if (state.Open)
        {
            if (signalDbm >= closeThreshold)
            {
                state.CloseHoldBlocks = AdaptiveSquelchCloseHoldBlocks;
            }
            else if (state.CloseHoldBlocks > 0)
            {
                state.CloseHoldBlocks--;
            }
            else
            {
                state.Open = false;
            }
        }
    }

    internal static void ApplyAdaptiveSquelch(
        Span<float> samples,
        SquelchConfig cfg,
        AdaptiveSquelchState state)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(state);
        if (samples.Length == 0 || !cfg.Enabled || !cfg.Adaptive) return;

        double target = state.WindowFill >= AdaptiveSquelchMinSamples && state.Open ? 1.0 : 0.0;
        double start = Math.Clamp(double.IsFinite(state.Gain) ? state.Gain : 0.0, 0.0, 1.0);
        if (target > 0.0 && start < AdaptiveSquelchOpenInitialGain)
        {
            start = AdaptiveSquelchOpenInitialGain;
        }
        double end = target > start
            ? Math.Min(target, start + AdaptiveSquelchAttackPerBlock)
            : Math.Max(target, start - AdaptiveSquelchReleasePerBlock);
        double delta = end - start;
        double denom = samples.Length;

        for (int i = 0; i < samples.Length; i++)
        {
            double gain = start + delta * ((i + 1) / denom);
            samples[i] *= (float)gain;
        }

        if (end <= 0.0001 && target == 0.0)
        {
            samples.Clear();
            end = 0.0;
        }
        state.Gain = end;
    }

    private static float SoftLimitRxAudioSample(float sample)
    {
        float a = Math.Abs(sample);
        if (a <= RxLevelerOutputSoftKnee) return sample;

        double kneeWidth = Math.Max(1.0e-6, RxLevelerOutputPeakCeiling - RxLevelerOutputSoftKnee);
        double over = (a - RxLevelerOutputSoftKnee) / kneeWidth;
        double limited = RxLevelerOutputSoftKnee + kneeWidth * Math.Tanh(over);
        return MathF.CopySign((float)limited, sample);
    }

    // Local-playback monitor inject (e.g. the Recorder plugin playing a clip
    // back locally). SPSC ring: producer = plugin playback thread via
    // EnqueueMonitorAudio, consumer = Tick. Mixed into the RX audio block so a
    // clip is audible on EVERY sink (browser WS + native) in any host mode —
    // unlike the desktop-only preview path. Power-of-two capacity for masking.
    private const int MonitorInjectCapacity = 1 << 14; // 16384 floats (~340 ms @ 48 kHz)
    private const int MonitorInjectMask = MonitorInjectCapacity - 1;
    private readonly float[] _monitorInject = new float[MonitorInjectCapacity];
    private long _monInjW;
    private long _monInjR;

    /// <summary>
    /// Enqueue mono float32 samples to be mixed into the local RX audio output
    /// (the operator's monitor) on the next ticks. Realtime-safe, lock-free.
    /// Returns <c>false</c> (writing nothing) when the ring can't fit the block
    /// — the caller should retry rather than drop, so the consumer (RX tick
    /// clock) paces the producer and the playback stays glitch-free. Used by
    /// <see cref="Zeus.Plugins.Contracts.Audio.IAudioPlaybackSink.PlayLocal"/>.
    /// </summary>
    public bool EnqueueMonitorAudio(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return true;
        long w = _monInjW;
        long r = Volatile.Read(ref _monInjR);
        if (MonitorInjectCapacity - (w - r) < samples.Length) return false; // full — caller retries
        int start = (int)(w & MonitorInjectMask);
        int first = Math.Min(samples.Length, MonitorInjectCapacity - start);
        samples[..first].CopyTo(_monitorInject.AsSpan(start, first));
        if (first < samples.Length)
            samples[first..].CopyTo(_monitorInject.AsSpan(0, samples.Length - first));
        Volatile.Write(ref _monInjW, w + samples.Length);
        return true;
    }

    /// <summary>Samples still queued in the monitor-inject ring (for a player
    /// to wait out the tail before declaring playback finished).</summary>
    public long MonitorBacklog => Volatile.Read(ref _monInjW) - Volatile.Read(ref _monInjR);

    // Mix any queued monitor-inject audio into the RX block (consumer side).
    private void MixMonitorInject(Span<float> dest)
    {
        long w = Volatile.Read(ref _monInjW);
        long r = _monInjR;
        long avail = w - r;
        if (avail <= 0) return;
        int n = (int)Math.Min(avail, dest.Length);
        for (int i = 0; i < n; i++)
            dest[i] += _monitorInject[(int)((r + i) & MonitorInjectMask)];
        Volatile.Write(ref _monInjR, r + n);
    }

    // _engineLock serialises CONCURRENT WRITERS to _engine / _channelId /
    // _sampleRateHz on the rare connect/disconnect path. After iter5 the
    // hot path (OnIqFrame / OnPsFeedbackFrame / Tick) reads these fields
    // LOCK-FREE via Volatile.Read — the lock is here only because multiple
    // writer threads (RadioService.Connected / Disconnected events,
    // ConnectP2Async / DisconnectP2Async HTTP handlers) can race against
    // each other, and we want the swap to be atomic from the writer side.
    //
    // Single-thread WDSP ownership on the hot path is now provided by:
    //   (a) AttachRxSink AFTER the engine swap is committed, so the sink
    //       only ever observes the freshly-installed engine,
    //   (b) Volatile.Read inside the sink callbacks (acquire fence pairs
    //       with the release fence on lock release),
    //   (c) cross-thread mutators (SetMox / SetTxTune) routing through
    //       PostDspCommand instead of touching the engine directly.
    //
    // OnRadioStateChanged still calls engine.* methods under _engineLock —
    // documented at the call site; that's a rare operator-edge path, not the
    // per-packet hot path. CurrentEngine and the IDspEngine endpoint setters
    // (e.g. /api/mic-gain) also fall outside the hot path and keep the lock.
    private readonly object _engineLock = new();
    private IDspEngine? _engine;
    private int _channelId;
    private int _rx2ChannelId = -1;
    // RX2's hardware DDC centre (the second receiver's analogue of RadioLoHz).
    // Under CTUN it stays frozen while VFO B roams within the window (dial moves,
    // panel stays put); with CTUN off it follows VFO B (panel recentres on the
    // dial). Auto-recentres when the dial would leave the captured DDC bandwidth.
    // Server-side only — not in StateDto — so RX2 gets RX1's CTUN feel without a
    // wire-contract change. See UpdateRx2Lo.
    private long _rx2LoHz;
    private bool _rx2LoInit;
    private int _sampleRateHz;

    // Protocol 2 path (parallel to the RadioService-owned P1 path). Held
    // directly here because RadioService is Protocol1Client-shaped and
    // growing a P2 variant there would require a larger refactor; for now
    // keeping it isolated avoids touching any P1 behavior.
    private Zeus.Protocol2.Protocol2Client? _p2Client;

    private RxMode _appliedMode = RxMode.USB;
    private int _appliedLowHz;
    private int _appliedHighHz;
    // WDSP RX filter shift currently applied (Hz). Equals
    // (EffectiveLoHz(VfoHz) - RadioLoHz) — the always-frozen-NCO model.
    // Tracked separately from FilterLowHz/HighHz so re-pushing the filter
    // when the dial moves doesn't require Mutate-ing the StateDto.
    // See docs/prd/panfall_behavior.md.
    private int _appliedCtunOffsetHz;
    private int _appliedTxLowHz;
    private int _appliedTxHighHz;

    // Derive the TX bandpass edges WDSP should use from the live mode, ignoring
    // the sign already stored in StateDto. WDSP selects the SSB sideband from
    // the sign of the bandpass (negative = LSB-family, positive = USB-family),
    // so the sign MUST track the current mode — not whatever a stale prefs DB or
    // a mode writer that forgot to re-sign happened to leave behind. Re-deriving
    // from the magnitudes via the single source of truth (SignedFilterForMode)
    // is idempotent for well-formed state, so this never overrides an operator's
    // deliberate width edit.
    private static (int low, int high) SignedTxFilterFor(StateDto s)
    {
        int loAbs = Math.Min(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz));
        int hiAbs = Math.Max(Math.Abs(s.TxFilterLowHz), Math.Abs(s.TxFilterHighHz));
        return RadioService.SignedFilterForMode(s.Mode, loAbs, hiAbs);
    }
    private double _appliedAgcTopDb;
    private double _appliedAgcOffsetDb;
    private double _appliedRxAfGainDb;
    // TX mic gain change-detect cache. NaN sentinel forces the first apply
    // even when the persisted value happens to equal 0 dB (the engine seam
    // expects an explicit unity SetTxPanelGain call so the TX chain leaves
    // its uninitialised state in a known place after channel-open).
    private double _appliedTxMicGainLinear = double.NaN;
    // Same NaN-first-apply sentinel for the Leveler ceiling so a channel-open
    // with the persisted value matching the 8 dB default still re-pushes it.
    private double _appliedTxLevelerMaxGainDb = double.NaN;
    private NrConfig _appliedNr = new();
    // AGC mode + custom params latch (issue: DSP controls Thetis parity §4).
    // Same change-detect pattern as _appliedNr — SetAgc only fires when the
    // config actually moves. Seeded to Med so a connect landing on the Med
    // default still matches what ApplyStateToNewChannel force-pushed.
    private AgcConfig _appliedAgc = new(AgcMode.Med);
    // RX squelch latch (issue: DSP controls Thetis parity §5). Same
    // change-detect pattern as _appliedAgc — SetSquelch only fires when the
    // config actually moves. Seeded to the off default so a connect landing on
    // squelch-off still matches what ApplyStateToNewChannel force-pushed.
    private SquelchConfig _appliedSquelch = new();
    // TX leveling latch (issue: DSP controls Thetis parity §6.1-6.3). Same
    // change-detect pattern as _appliedAgc/_appliedSquelch — SetTxLeveling only
    // fires when the config actually moves. Seeded to the TxLevelingConfig
    // defaults so a connect landing on defaults still matches what
    // ApplyStateToNewChannel force-pushed.
    private TxLevelingConfig _appliedTxLeveling = new();
    private int _appliedZoomLevel = 1;
    // PureSignal latched values — same change-detect pattern as the others
    // so OnRadioStateChanged only fires the (possibly heavy)
    // SetPsIntsAndSpi / SetPsRunCal calls when the value actually moves.
    private bool _appliedPsEnabled;
    private bool _appliedPsAuto = true;
    private bool _appliedPsSingle;
    private bool _appliedPsPtol;
    private double _appliedPsMoxDelaySec = 0.2;
    private double _appliedPsLoopDelaySec;
    private double _appliedPsAmpDelayNs = 150.0;
    private double _appliedPsHwPeak = 0.4072;
    private string _appliedPsIntsSpiPreset = "16/256";
    private PsFeedbackSource _appliedPsFeedbackSource = PsFeedbackSource.Internal;
    // PS-Monitor toggle (issue #121). Pure source-routing flag — Tick reads
    // it on each tick to choose between the TX analyzer (predistorted IQ)
    // and the PS-feedback analyzer (post-PA loopback IQ). volatile because
    // OnRadioStateChanged writes from the state-handler thread and Tick
    // reads from the pipeline thread — no compound mutation, just a bool.
    private volatile bool _psMonitorEnabled;
    private long _psMonitorTickCount;
    // TX Monitor latch (issue #106 follow-up). Same change-detect pattern as
    // _psMonitorEnabled — UpdateState writes when StateDto.TxMonitorEnabled
    // flips, and the latch fires engine.SetTxMonitorEnabled exactly once per
    // edge so we don't spam the engine on every tick with the same value.
    private bool _appliedTxMonitorEnabled;
    // Meter-only TX monitor (Auto Tune). When true AND the monitor is on, the
    // TXA chain still runs (so the stage meters from ProcessTxBlock animate and
    // Auto Tune can sample), but the demodulated monitor audio is NOT broadcast
    // to the operator's playback path — the metering "happens in the background"
    // with no audible preview. volatile: written from the preview-endpoint
    // request thread, read on the pipeline tick thread. Self-clears whenever the
    // monitor latch turns off (below).
    private volatile bool _txMonitorMeterOnly;
    // Set by DisconnectP2Async so the next OnRadioStateChanged after a
    // fresh ConnectP2Async re-pushes every PS field regardless of equality
    // — necessary because the new WdspDspEngine instance starts with field
    // defaults that don't match the cached `_appliedPs*` state.
    private bool _psResyncRequired;
    // TwoTone latched fields (protocol-agnostic, drives PostGen mode=1).
    private bool _appliedTwoToneEnabled;
    private double _appliedTwoToneFreq1 = 700.0;
    private double _appliedTwoToneFreq2 = 1900.0;
    private double _appliedTwoToneMag = 0.49;
    // CFC (Continuous Frequency Compressor) — issue #123. Default-OFF so a
    // fresh state-change push (no Cfc field on the wire) doesn't flip the
    // engine into a partial config. _psResyncRequired piggybacks: when a P2
    // reconnect tears down the engine, we re-push the CFC profile too so the
    // new WdspDspEngine instance picks up the operator's persisted config.
    private CfcConfig _appliedCfc = CfcConfig.Default;

    // RX front-end (step attenuator + Mercury preamp). Mirrored to a live
    // Protocol2Client when the value moves; on P1 these go through
    // RadioService.ActiveClient directly. Issue #126 — without this
    // forwarding the S-ATT slider and PRE button were inert on Angelia /
    // ANAN-100D. Effective atten = StateDto.AttenDb + AttOffsetDb (auto-ATT
    // offset), so the existing overload control loop continues to drive the
    // radio on P2. Sentinel -1 forces the first push regardless of value.
    private int _appliedEffectiveAttDb = -1;
    private bool _appliedPreampOn;

    private uint _seq;
    private uint _audioSeq;
    // Latched from MoxChanged so Tick can route the panadapter to the TX
    // analyzer during keying without snapshotting RadioService. TUN also flips
    // MOX on (TxService.cs:153-155), so this single flag covers both paths —
    // see issue #81. volatile because MoxChanged fires on the caller's thread
    // and Tick reads from the pipeline thread.
    private volatile bool _keyed;
    // Issue #597 Phase 0: display-EMA fast-attack latch. OnRadioStateChanged
    // arms it when RadioLoHz moves (the operator is tuning) and Tick restores
    // the default tau once the LO has been quiet for FastAttackRestoreMs.
    // Debounced by design: one arm P/Invoke at gesture start, one restore
    // P/Invoke at gesture end — NOT per wheel notch. Skipped entirely while
    // _keyed so the TX display path is never touched (PS safety; the engine
    // method is additionally scoped to the RX analyzer). long.MinValue
    // sentinel suppresses the arm on the first state callback after connect.
    // _fastAttackLastLoHz is only touched on the state-handler thread;
    // _fastAttackLoChangedAt crosses to the RX thread via Interlocked.
    private long _fastAttackLastLoHz = long.MinValue;
    private long _fastAttackLoChangedAt;
    private volatile bool _fastAttackActive;
    private const int FastAttackRestoreMs = 250;
    private static readonly long FastAttackRestoreTicks =
        (long)(FastAttackRestoreMs / 1000.0 * Stopwatch.Frequency);
    // Issue #597 Phase 2: delay-compensated CenterHz stamp (Thetis pixel_ref
    // emulation). The display pixels broadcast at tick time were computed
    // from IQ captured ~D earlier; stamping them with the LO from
    // LookupAt(now − D) makes frames self-describing — the client renders
    // data where it actually belongs, killing the mislabeled-frame
    // snap-back at the root (no wire change: same CenterHz field).
    // D = ½·FFT-fill + display-EMA lag + per-protocol transport. Override
    // the transport+EMA constant with ZEUS_CENTER_STAMP_LAG_MS for bench
    // tuning at 48/96/192/384 kHz on P1 (HL2) and P2 (G2). When the LO is
    // stable longer than D the stamp equals live RadioLoHz — WWV cal
    // (#325) and every stable-LO consumer is byte-identical (see
    // LoHistoryRingTests regression).
    private readonly LoHistoryRing _loHistory = new();
    private const int AnalyzerFftSizeForStamp = 16_384; // WdspDspEngine.AnalyzerFftSize
    private const double CenterStampEmaLagMs = 20.0;    // fast-attack tau during gestures (Phase 0)
    private const double CenterStampTransportP1Ms = 40.0;
    private const double CenterStampTransportP2Ms = 15.0;
    private static readonly double? CenterStampLagOverrideMs = ReadCenterStampLagOverrideMs();

    private static double? ReadCenterStampLagOverrideMs()
    {
        var raw = Environment.GetEnvironmentVariable("ZEUS_CENTER_STAMP_LAG_MS");
        return double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var ms) && ms >= 0
            ? ms
            : null;
    }
    // RX S-meter broadcast throttle. Pipeline ticks at 30 Hz; broadcasting
    // every 6 ticks = 5 Hz gives a smoother meter than Thetis's 4 Hz baseline
    // without spamming the WS (30 Hz dBm readouts add nothing a UI can use).
    private int _rxMeterTickMod;
    private const int RxMeterTickModulus = 6;

    // RX audio fade envelope across MOX edges. WDSP's RXA SetChannelState
    // (dmp=1 on TX-engage) damps the outgoing side internally, but the resume
    // edge (dmp=0 at MOX-off) and the buffer-drain endpoint in the browser
    // audio-client both produce audible clicks under some setups (audio
    // interfaces, USB-DAC headphones). Smoothing here is cheap insurance: the
    // first ~5 ms after each edge gets a linear ramp applied before the
    // AudioFrame is broadcast. Both flags are pipeline-thread-only after the
    // initial volatile read in OnRadioMoxChanged sets them.
    private const int RxFadeSamples = 240;          // 5 ms @ 48 kHz
    private volatile bool _rxFadeOutPending;        // first RX block after MOX↑
    private volatile bool _rxFadeInPending;         // first RX block after MOX↓

    // ---- iter5 single-DSP-thread scaffolding -----------------------------
    // The pipeline now owns its hot path via IRxPacketSink: when a radio
    // connects we AttachRxSink to the protocol client and every IQ/PS-feedback
    // packet flows synchronously into OnIqFrame/OnPsFeedbackFrame on the RX
    // OS thread. WDSP calls happen inline on that thread. The 30 Hz display
    // Tick is piggybacked: OnIqFrame checks Stopwatch.GetTimestamp() and
    // fires Tick inline when >= 33.33 ms have elapsed since the last tick.
    //
    // While a sink is attached the ExecuteAsync PeriodicTimer skips Tick
    // (the "watcher" pauses). With no sink attached (synthetic mode, pre-
    // connect, or post-disconnect) the PeriodicTimer drives Tick at 30 Hz
    // so the display chain stays live even when no IQ is flowing.
    //
    // Cross-thread mutations that should run on the DSP thread post Action
    // commands here; the DSP thread drains the queue at the top of every
    // IqFrame (and every Tick when no sink is attached). After pass 2:
    // SetMox / SetTxTune route through this queue so WDSP TXA state edges
    // happen on the same thread that feeds RX IQ. OnRadioStateChanged still
    // calls engine.* directly (rare operator-edge path — the engine's own
    // disposed-check guards cover engine-swap-mid-call); engine swaps
    // serialise through _engineLock (writer side only).
    private volatile bool _rxSinkAttached;
    // Reference to the protocol client this pipeline is currently sinking RX
    // packets from. Cached so we can explicitly DetachRxSink on disconnect —
    // RadioService nulls its ActiveClient before raising Disconnected, so the
    // event handler can't pull the client off that surface.
    private IProtocol1Client? _attachedSinkP1;
    private Zeus.Protocol2.Protocol2Client? _attachedSinkP2;
    private long _lastTickStopwatchTicks;
    private static readonly long TickPeriodStopwatchTicks =
        (long)(Stopwatch.Frequency / 30.0);
    private readonly ConcurrentQueue<Action> _dspCommands = new();

    // DSP-thread-owned scratch buffers. Allocated once at construction so
    // both the PeriodicTimer-driven Tick (synthetic mode) and the inline
    // RX-thread Tick (sink mode) share the same memory. Sink-mode and
    // timer-mode are mutually exclusive (see _rxSinkAttached gate in
    // ExecuteAsync), so no synchronisation is needed.
    private readonly float[] _panBuf = new float[Width];
    private readonly float[] _wfBuf = new float[Width];
    private readonly float[] _rx2PanBuf = new float[Width];
    private readonly float[] _rx2WfBuf = new float[Width];
    // RX2 frozen-panadapter probe: 1 Hz tally of how often each receiver's
    // pan/wf pixout reports fresh data. If rx2wf advances but rx2pan stays ~0,
    // the freeze is backend (pan pixout never goes fresh); if both advance like
    // RX1, the freeze is frontend rendering.
    private long _dispFlagLogMs;
    // 1 Hz throttle for the TX pixel dB-range diagnostic (see Tick).
    private long _txPixelDbgMs;
    private int _dispTicks, _rx1PanCnt, _rx1WfCnt, _rx2PanCnt, _rx2WfCnt;
    private readonly float[] _audioBuf = new float[AudioDrainCapacity];
    private readonly float[] _rx2AudioBuf = new float[AudioDrainCapacity];

    // Cached panadapter snapshot for the frequency-calibration service
    // (issue #325). Tick fills this every cycle that produced a valid
    // pan frame; the cal service reads it without racing for the WDSP
    // "fresh frame" flag. Single-writer (Tick) + occasional reader
    // (cal) — protected by _calPanLock.
    private readonly float[] _calPanSnapshot = new float[Width];
    private float _calPanHzPerPixel;
    private long _calPanCenterHz;
    private long _calPanSnapshotMs;
    private readonly object _calPanLock = new();
    private readonly float[] _diagWfSnapshot = new float[Width];
    private long _diagWfSnapshotMs;
    private long _diagDisplayFrameMs;
    private uint _diagDisplaySeq;
    private long _diagDisplayFrameCount;
    private bool _diagLastPanValid;
    private bool _diagLastWfValid;
    private string _diagLastPanSource = "none";
    private string _diagLastWfSource = "none";
    private bool _diagLastKeyed;
    private bool _diagLastPsMonitorRequested;
    private bool _diagLastPsFeedbackCorrecting;
    private const long DisplayFreshMs = 2_000;
    private const long DisplayAgingMs = 5_000;
    private readonly object _rxMeterDiagLock = new();
    private bool _diagRxMetersValid;
    private long _diagRxMetersMs;
    private int _diagRxMetersChannelId;
    private double _diagRxDbm = double.NaN;
    private RxMetersV2Frame _diagRxMeters;
    private const long RxMetersFreshMs = 2_500;
    private const long RxMetersAgingMs = 10_000;
    private readonly object _audioDiagLock = new();
    private bool _diagAudioValid;
    private long _diagAudioFrameMs;
    private uint _diagAudioSeq;
    private long _diagAudioFrameCount;
    private string _diagAudioSource = "none";
    private int _diagAudioSampleRateHz;
    private int _diagAudioSampleCount;
    private double _diagAudioRms = double.NaN;
    private double _diagAudioPeak = double.NaN;
    private bool _diagAudioLevelerValid;
    private double _diagAudioLevelerInputRmsDbfs = double.NaN;
    private double _diagAudioLevelerOutputRmsDbfs = double.NaN;
    private double _diagAudioLevelerInputPeakDbfs = double.NaN;
    private double _diagAudioLevelerOutputPeakDbfs = double.NaN;
    private double _diagAudioLevelerDesiredGainDb = double.NaN;
    private double _diagAudioLevelerAppliedGainDb = double.NaN;
    private double _diagAudioLevelerGainDeltaDb = double.NaN;
    private double _diagAudioLevelerPeakHeadroomDb = double.NaN;
    private double _diagAudioLevelerPreLimitPeakDbfs = double.NaN;
    private double _diagAudioLevelerOutputLimitReductionDb = double.NaN;
    private int _diagAudioLevelerOutputLimitSampleCount;
    private int _diagAudioLevelerPauseHoldBlocks;
    private bool _diagAudioLevelerBoostSlewLimited;
    private bool _diagAudioLevelerPeakLimited;
    private bool _diagAudioLevelerOutputLimited;
    private bool _diagAudioTxMonitorRequested;
    private bool _diagAudioSquelchEnabled;
    private bool _diagAudioSquelchOpen;
    private bool _diagAudioSquelchTailActive;
    private double _diagAudioSquelchGain = double.NaN;
    private string _diagAudioSquelchMode = "off";
    private string _diagAudioSquelchGateSource = "disabled";
    private bool _diagAudioSquelchOpenKnown = true;
    private long _diagAudioMonitorBacklogSamples;
    private int _diagAudioSinkCount;
    private const long AudioFreshMs = 2_000;
    private const long AudioAgingMs = 5_000;
    private const double AudioClippingRiskLinear = 0.98;
    private const double AudioSilentRmsDbfs = -90.0;

    // CW sidetone source mixed into the RX audio bus while a CW keying
    // path (CwEngine macros / cw_msg / raw-key, or ExternalPttService
    // hardware key in CW mode) holds the keyed state. Optional in DI so
    // tests that build the pipeline without the CW services don't have
    // to register a stub. See CwSidetoneSource for the keying contract.
    private readonly CwSidetoneSource? _sidetone;
    private readonly FrontendDspSceneDiagnosticsService? _frontendDspScene;

    public DspPipelineService(
        RadioService radio,
        StreamingHub hub,
        IEnumerable<IRxAudioSink> audioSinks,
        ILoggerFactory loggerFactory,
        CwSidetoneSource? sidetone = null,
        FrontendDspSceneDiagnosticsService? frontendDspScene = null,
        DisplaySettingsStore? displaySettings = null)
    {
        _radio = radio;
        _hub = hub;
        // Materialise once at construction so the per-tick fan-out is an
        // array-index loop (no enumerator allocation, no LINQ on the hot path).
        _audioSinks = audioSinks.ToArray();
        _sidetone = sidetone;
        _frontendDspScene = frontendDspScene;
        _loggerFactory = loggerFactory;
        _displaySettings = displaySettings;
        _log = loggerFactory.CreateLogger<DspPipelineService>();
    }

    // Persisted TX display analyzer config (live TX waterfall feature). Optional
    // so test constructions of DspPipelineService keep working; when null the
    // engine just runs at its built-in defaults. Display-only — never affects
    // the transmitted signal.
    private readonly DisplaySettingsStore? _displaySettings;
    // dB added to the TX panadapter/waterfall pixels (Thetis TXDisplayCalOffset).
    // Read on the hot Tick path; written from the connect path + endpoint.
    private double _txDisplayCalOffsetDb;

    // Default TX display analyzer params — mirror WdspDspEngine's constants and
    // the frontend's TX_DISPLAY_* defaults. Used when the persisted value is null.
    private const int DefaultTxDisplayFftSize = 16384;
    private const int DefaultTxDisplayWindow = 2;
    private const double DefaultTxDisplayAvgTauMs = 175.0;
    private const double TxDisplayCalOffsetAbsDb = 60.0;

    /// <summary>Seed a freshly constructed engine with the persisted TX display
    /// config BEFORE its TX channel opens, so the analyzer comes up with the
    /// operator's FFT/window/smoothing rather than engine defaults. Also seeds
    /// the cal-offset field read by <see cref="Tick"/>. Display-only — never
    /// touches the transmitted signal.</summary>
    private void SeedTxDisplayConfig(WdspDspEngine wdsp)
    {
        var dto = _displaySettings?.Get();
        Volatile.Write(ref _txDisplayCalOffsetDb, ResolveCalOffset(dto));
        wdsp.ConfigureTxDisplayAnalyzer(
            dto?.TxDisplayFftSize ?? DefaultTxDisplayFftSize,
            dto?.TxDisplayWindow ?? DefaultTxDisplayWindow,
            (dto?.TxDisplayAvgTauMs ?? DefaultTxDisplayAvgTauMs) / 1000.0);
    }

    /// <summary>Live update from the /api/display-settings endpoint — pushes the
    /// new cal offset + analyzer config to the running engine (if any). Safe to
    /// call with no radio connected; the values are re-seeded on next connect.
    /// Display-only.</summary>
    public void ApplyTxDisplaySettings(DisplaySettingsDto dto)
    {
        Volatile.Write(ref _txDisplayCalOffsetDb, ResolveCalOffset(dto));
        int fft = dto.TxDisplayFftSize ?? DefaultTxDisplayFftSize;
        int win = dto.TxDisplayWindow ?? DefaultTxDisplayWindow;
        double tauSec = (dto.TxDisplayAvgTauMs ?? DefaultTxDisplayAvgTauMs) / 1000.0;
        var engine = CurrentEngine;
        if (engine is null) return;
        lock (_engineLock)
        {
            engine.ConfigureTxDisplayAnalyzer(fft, win, tauSec);
        }
    }

    private static double ResolveCalOffset(DisplaySettingsDto? dto)
    {
        double v = dto?.TxDisplayCalOffsetDb ?? 0.0;
        if (double.IsNaN(v) || double.IsInfinity(v)) return 0.0;
        return Math.Clamp(v, -TxDisplayCalOffsetAbsDb, TxDisplayCalOffsetAbsDb);
    }

    // Add a dB offset to every pixel of a display buffer (TX cal offset).
    private static void AddDbOffset(float[] buf, double db)
    {
        if (db == 0.0) return;
        float d = (float)db;
        for (int i = 0; i < buf.Length; i++) buf[i] += d;
    }

    private void PublishAudio(in AudioFrame frame)
    {
        for (int i = 0; i < _audioSinks.Length; i++)
            _audioSinks[i].Publish(in frame);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        OpenSynthetic();
        _radio.Connected += OnRadioConnected;
        _radio.Disconnected += OnRadioDisconnected;
        _radio.StateChanged += OnRadioStateChanged;
        _radio.PaSnapshotChanged += OnPaSnapshotChanged;
        _radio.MoxChanged += OnRadioMoxChanged;
        _radio.TunActiveChanged += OnRadioTunActiveChanged;
        _radio.PreampChanged += OnRadioPreampChanged;
        _radio.SampleRateChanged += OnRadioSampleRateChanged;
        _radio.NotchesChanged += OnRadioNotchesChanged;
        // Frequency-correction factor (issue #325) — RadioService can't
        // push to the P2 client directly (ActiveClient is P1-only), so we
        // listen for changes here and forward them to the live P2 client.
        _radio.FrequencyCorrectionFactorChanged += OnFrequencyCorrectionFactorChanged;
        using var timer = new PeriodicTimer(TickPeriod);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // iter5: when a radio is connected, the sink (called on the
                // RX OS thread) drives Tick inline via Stopwatch elapsed
                // checks — see OnIqFrame. Skip the timer-driven Tick to avoid
                // a double-tick and keep WDSP truly single-thread-owned on
                // the hot path. The "no sink attached" branch keeps the
                // synthetic-mode display alive when there's no radio.
                if (_rxSinkAttached) continue;
                // Drain any cross-thread commands posted while no sink was
                // attached (rare — most commands arrive while a radio is
                // connected and the sink is the consumer).
                DrainDspCommands();
                Tick(_panBuf, _wfBuf, _audioBuf);
                _lastTickStopwatchTicks = Stopwatch.GetTimestamp();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _radio.Connected -= OnRadioConnected;
            _radio.Disconnected -= OnRadioDisconnected;
            _radio.StateChanged -= OnRadioStateChanged;
            _radio.PaSnapshotChanged -= OnPaSnapshotChanged;
            _radio.MoxChanged -= OnRadioMoxChanged;
            _radio.TunActiveChanged -= OnRadioTunActiveChanged;
            _radio.PreampChanged -= OnRadioPreampChanged;
            _radio.SampleRateChanged -= OnRadioSampleRateChanged;
            _radio.NotchesChanged -= OnRadioNotchesChanged;
            _radio.FrequencyCorrectionFactorChanged -= OnFrequencyCorrectionFactorChanged;
            // iter5: no more pump tasks to stop — the sink path runs on the
            // protocol client's RX thread, which the protocol client tears
            // down via its own StopAsync. Detach defensively in case a
            // disconnect didn't fire (e.g., abrupt host shutdown).
            DetachRxSinkP1();
            DetachRxSinkP2();
            CloseCurrentEngine();
        }
    }

    public virtual void SetMox(bool on)
    {
        // Direct call, not queued: HL2 stops RX while MOX is asserted, so a
        // PostDspCommand queued from the HTTP thread would not drain until
        // MOX releases — TXA stays in RX state and TX produces buzz. WDSP
        // tolerates concurrent state edges from the HTTP thread vs the RX
        // sink thread via its own internal locking, and SetMox/SetTxTune
        // are rare operator-edge events (not the per-frame hot path).
        lock (_engineLock) { _engine?.SetMox(on); }
    }

    public virtual void SetTxTune(bool on)
    {
        lock (_engineLock) { _engine?.SetTxTune(on); }
    }

    /// <summary>Current engine snapshot (may be <see cref="SyntheticDspEngine"/>
    /// while disconnected). TxAudioIngest calls ProcessTxBlock on this; the
    /// engine handles a disposed-during-call race internally by returning 0.
    /// Virtual so tests can subclass this service and substitute a stub engine
    /// without running the full Synthetic/WDSP lifecycle.
    ///
    /// iter5 pass-2: read lock-free via Volatile.Read. The previous
    /// _engineLock-guarded getter provided pointer-atomic reads only —
    /// Volatile.Read provides the same guarantee on .NET reference types
    /// without acquiring the lock. Engine swap writers continue to take
    /// _engineLock to serialise themselves against each other.</summary>
    public virtual IDspEngine? CurrentEngine => Volatile.Read(ref _engine);

    public DspNrRuntimeSnapshot SnapshotNrRuntime()
    {
        var engine = Volatile.Read(ref _engine);
        var state = _radio.Snapshot();
        return BuildNrRuntime(engine, state);
    }

    public object SnapshotDiagnostics(WdspWisdomInitializer wisdom)
    {
        var engine = Volatile.Read(ref _engine);
        var state = _radio.Snapshot();
        int channelId = Volatile.Read(ref _channelId);
        var nrRuntime = BuildNrRuntime(engine, state);
        bool wdspActive = nrRuntime.WdspActive;
        bool synthetic = engine is SyntheticDspEngine;
        var squelchConfig = state.Squelch ?? new SquelchConfig();
        var rxDsp = BuildRxDspChainDiagnostics(
            state,
            _radio.Notches,
            nrRuntime,
            _appliedNr,
            _appliedAgc,
            _appliedSquelch);
        var rxMeters = SnapshotRxMetersDiagnostics();
        var adcProtection = _radio.GetAdcProtectionStatus();
        var squelch = SnapshotAdaptiveSquelchDiagnostics(squelchConfig);
        var audio = SnapshotAudioDiagnostics();
        var display = SnapshotDisplayDiagnostics(engine);
        var secondReceiverHealth = SnapshotSecondReceiverHealth(state);
        return new
        {
            schemaVersion = 1,
            engine = engine?.GetType().Name ?? "None",
            engineKind = wdspActive ? "WDSP" : synthetic ? "Synthetic" : engine is null ? "None" : "Other",
            wdspActive = nrRuntime.WdspActive,
            synthetic,
            wdspNativeLoadable = nrRuntime.WdspNativeLoadable,
            wdspEmnrPost2Available = nrRuntime.WdspEmnrPost2Available,
            wdspNr4SbnrAvailable = nrRuntime.WdspNr4SbnrAvailable,
            nr4Readiness = nrRuntime.Nr4Readiness,
            requestedNrMode = nrRuntime.RequestedNrMode,
            effectiveNrMode = nrRuntime.EffectiveNrMode,
            rxDsp,
            rxMeters,
            rxDynamicRange = BuildRxDynamicRangeDiagnostics(state, rxMeters, adcProtection),
            squelch,
            filterGeometry = BuildFilterGeometryDiagnostics(
                state,
                engine,
                wisdom,
                BoardCapabilitiesTable.For(_radio.EffectiveBoardKind, _radio.EffectiveOrionMkIIVariant),
                _radio.ConnectedBoardKind,
                _radio.EffectiveBoardKind,
                _radio.EffectiveOrionMkIIVariant,
                state.Status == ConnectionStatus.Connected && _radio.ActiveClient is null),
            channelId = Volatile.Read(ref _channelId),
            sampleRateHz = Volatile.Read(ref _sampleRateHz),
            displayWidth = Width,
            tickRateHz = Math.Round(1.0 / TickPeriod.TotalSeconds, 1),
            audioOutputRateHz = AudioOutputRateHz,
            txBlockSamples = engine?.TxBlockSamples ?? 0,
            txOutputSamples = engine?.TxOutputSamples ?? 0,
            txMonitorRequested = engine?.IsTxMonitorOn ?? false,
            rxSinkAttached = _rxSinkAttached,
            audioSinkCount = _audioSinks.Length,
            monitorBacklogSamples = MonitorBacklog,
            audio,
            listenability = BuildRxListenabilityDiagnostics(rxMeters, audio, squelchConfig),
            display,
            secondReceiverHealth,
            wdspWisdomPhase = wisdom.Phase.ToString(),
            wdspWisdomStatus = wisdom.Status,
            readiness = wdspActive
                ? "wdsp-active"
                : synthetic
                    ? "synthetic-idle-or-fallback"
                    : "no-engine",
        };
    }

    public DspLiveRuntimeEvidenceDto SnapshotLiveRuntimeEvidence()
    {
        var rxMeters = SnapshotRxMetersDiagnostics();
        var audio = SnapshotAudioDiagnostics();
        string status = LiveRuntimeEvidenceStatus(rxMeters, audio);

        return new DspLiveRuntimeEvidenceDto(
            SchemaVersion: 4,
            GeneratedUtc: DateTimeOffset.UtcNow,
            Status: status,
            RxMetersFresh: rxMeters.Fresh,
            RxMetersStale: rxMeters.Stale,
            RxMetersAgeMs: rxMeters.AgeMs,
            RxDbm: rxMeters.RxDbm,
            AdcHeadroomDb: rxMeters.AdcHeadroomDb,
            AgcGainDb: rxMeters.AgcGainDb,
            AudioFresh: audio.Fresh,
            AudioStale: audio.Stale,
            AudioAgeMs: audio.AgeMs,
            AudioStatus: audio.Status,
            AudioSource: audio.Source,
            AudioFramesBroadcast: audio.FramesBroadcast,
            AudioLastSeq: audio.LastSeq,
            AudioSampleRateHz: audio.SampleRateHz,
            AudioSampleCount: audio.SampleCount,
            AudioRmsDbfs: audio.RmsDbfs,
            AudioPeakDbfs: audio.PeakDbfs,
            TxMonitorRequested: audio.TxMonitorRequested,
            SquelchEnabled: audio.SquelchEnabled,
            SquelchOpen: audio.SquelchOpen,
            SquelchTailActive: audio.SquelchTailActive,
            SquelchGateGain: audio.SquelchGateGain,
            RxAudioLevelerInputRmsDbfs: audio.RxAudioLevelerInputRmsDbfs,
            RxAudioLevelerOutputRmsDbfs: audio.RxAudioLevelerOutputRmsDbfs,
            RxAudioLevelerInputPeakDbfs: audio.RxAudioLevelerInputPeakDbfs,
            RxAudioLevelerOutputPeakDbfs: audio.RxAudioLevelerOutputPeakDbfs,
            RxAudioLevelerDesiredGainDb: audio.RxAudioLevelerDesiredGainDb,
            RxAudioLevelerAppliedGainDb: audio.RxAudioLevelerAppliedGainDb,
            RxAudioLevelerGainDeltaDb: audio.RxAudioLevelerGainDeltaDb,
            RxAudioLevelerPeakHeadroomDb: audio.RxAudioLevelerPeakHeadroomDb,
            RxAudioLevelerPreLimitPeakDbfs: audio.RxAudioLevelerPreLimitPeakDbfs,
            RxAudioLevelerOutputLimitReductionDb: audio.RxAudioLevelerOutputLimitReductionDb,
            RxAudioLevelerOutputLimitSampleCount: audio.RxAudioLevelerOutputLimitSampleCount,
            RxAudioLevelerPauseHoldBlocks: audio.RxAudioLevelerPauseHoldBlocks,
            RxAudioLevelerBoostSlewLimited: audio.RxAudioLevelerBoostSlewLimited,
            RxAudioLevelerPeakLimited: audio.RxAudioLevelerPeakLimited,
            RxAudioLevelerOutputLimited: audio.RxAudioLevelerOutputLimited,
            MonitorBacklogSamples: audio.MonitorBacklogSamples,
            AudioSinkCount: audio.AudioSinkCount,
            DiagnosticRecommendation: LiveRuntimeEvidenceRecommendation(status, rxMeters, audio));
    }

    private static string LiveRuntimeEvidenceStatus(RxMetersDiagnosticsDto rxMeters, AudioPathDiagnosticsDto audio)
    {
        if (!audio.Fresh)
            return $"audio-{audio.Status}";
        if (audio.Status is not "fresh")
            return $"audio-{audio.Status}";
        if (!rxMeters.Fresh)
            return rxMeters.Stale ? "rx-meters-stale" : "rx-meters-missing";
        if (rxMeters.AdcHeadroomDb is < 6.0)
            return "adc-headroom-low";
        return "fresh";
    }

    private static string LiveRuntimeEvidenceRecommendation(
        string status,
        RxMetersDiagnosticsDto rxMeters,
        AudioPathDiagnosticsDto audio) =>
        status switch
        {
            "fresh" => "Final RX audio and RXA meters are fresh; use AGC gain/headroom, audio RMS/peak, and squelch state with fixture metrics before changing DSP behavior.",
            "adc-headroom-low" => "ADC headroom is low; add attenuation or reduce front-end gain before judging NR/AGC improvements.",
            "rx-meters-stale" or "rx-meters-missing" => rxMeters.DiagnosticRecommendation,
            _ => audio.DiagnosticRecommendation,
        };

    private static object BuildFilterGeometryDiagnostics(
        StateDto state,
        IDspEngine? engine,
        WdspWisdomInitializer wisdom,
        BoardCapabilities caps,
        HpsdrBoardKind connectedBoard = HpsdrBoardKind.Unknown,
        HpsdrBoardKind effectiveBoard = HpsdrBoardKind.Unknown,
        OrionMkIIVariant variant = OrionMkIIVariant.G2,
        bool protocol2Active = false)
    {
        bool wdsp = engine is WdspDspEngine;
        int txBlock = engine?.TxBlockSamples ?? 0;
        int txOut = engine?.TxOutputSamples ?? 0;
        string status = wdsp ? "runtime-rate-writable-fixed-profile" : engine is SyntheticDspEngine ? "synthetic-profile" : "engine-unavailable";
        int[] sampleRates = [48_000, 96_000, 192_000, 384_000, 768_000, 1_536_000];
        int[] iqBufferSizes = [64, 128, 256, 512, 1024];
        int[] filterTapSizes = [64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144];
        string[] filterTypes = ["Linear Phase", "Low Latency"];
        object[] filterWindows =
        [
            new { id = 0, label = "BH-4", notes = "Thetis default in DSP Options; sharper transition." },
            new { id = 1, label = "BH-7", notes = "Deeper cutoff; this is the current Zeus WDSP call." },
        ];
        var receiverBandwidth = BuildReceiverBandwidthDiagnostics(
            state,
            caps,
            connectedBoard,
            effectiveBoard,
            variant,
            protocol2Active);

        return new
        {
            schemaVersion = 1,
            status,
            operatorConfigurable = true,
            hardwareLimits = new
            {
                rxAdcCount = caps.RxAdcCount,
                maxRxSampleRateHz = caps.MaxRxSampleRateHz,
                activeSampleRateHz = state.SampleRate,
                sampleRates = sampleRates.Select(rate => new
                {
                    sampleRateHz = rate,
                    label = $"{rate / 1000} kHz",
                    boardSupported = rate <= caps.MaxRxSampleRateHz,
                    protocol2Required = rate > 384_000,
                    active = rate == state.SampleRate,
                    status = rate <= caps.MaxRxSampleRateHz
                        ? rate > 384_000 ? "hardware-supported-p2-only" : "hardware-supported"
                        : "above-board-capability",
                }).ToArray(),
            },
            runtimeSampleRateControl = BuildRuntimeSampleRateControlDiagnostics(
                state,
                caps,
                protocol2Active),
            optionCatalog = new
            {
                iqBufferSizes,
                filterTapSizes,
                filterTypes,
                filterWindows,
                slowModeChangeWarning = "Thetis warns that different buffer sizes, tap sizes, or filter types can force a slow mode change; Zeus keeps these fixed until RXA/TXA/analyzer rebuild can be made atomic.",
                source = "Thetis DSP Options mode defaults + Zeus WDSPwisdom 64..262144 startup planning ladder",
            },
            activeRx = new
            {
                mode = state.Mode.ToString(),
                filterLowHz = state.FilterLowHz,
                filterHighHz = state.FilterHighHz,
                filterPresetName = state.FilterPresetName,
                inputBufferSize = 1024,
                dspBufferSize = 1024,
                filterWindowId = 1,
                filterWindow = "BH-7",
                filterType = "Low Latency",
                filterTaps = (int?)null,
                status = wdsp ? "wired-fixed" : "not-wdsp",
            },
            activeTx = new
            {
                mode = state.Mode.ToString(),
                filterLowHz = state.TxFilterLowHz,
                filterHighHz = state.TxFilterHighHz,
                inputBufferSize = txBlock,
                dspBufferSize = txBlock > 0 ? 1024 : 0,
                outputBufferSize = txOut,
                filterWindowId = 1,
                filterWindow = "BH-7",
                filterType = "profile-fixed",
                filterTaps = (int?)null,
                cfirCompensation = txOut > txBlock && txBlock > 0,
                status = wdsp ? "wired-fixed" : "not-wdsp",
            },
            receiverBandwidth,
            thetisMatrix = new[]
            {
                ThetisFilterRow("SSB/AM", "RX", 1024, 16384, "Low Latency", "BH-4", "reference"),
                ThetisFilterRow("SSB/AM", "TX", 1024, 16384, "Linear Phase", "BH-4", "reference"),
                ThetisFilterRow("FM", "RX", 256, 4096, "Low Latency", "BH-4", "reference"),
                ThetisFilterRow("FM", "TX", 128, 1024, "Low Latency", "BH-4", "reference"),
                ThetisFilterRow("CW", "RX", 64, 4096, "Low Latency", "BH-4", "reference"),
                ThetisFilterRow("CW", "TX", null, null, "Mode generated", "BH-4", "reference-no-separate-tx-row"),
                ThetisFilterRow("Digital", "RX", 64, 4096, "Low Latency", "BH-4", "reference"),
                ThetisFilterRow("Digital", "TX", 64, 4096, "Low Latency", "BH-4", "reference"),
            },
            impulseCache = new
            {
                fftwWisdomPhase = wisdom.Phase.ToString(),
                fftwWisdomStatus = wisdom.Status,
                fftwWisdomCache = true,
                filterImpulseCache = false,
                saveRestoreImpulseCacheFile = false,
                status = "fftw-wisdom-only",
                notes = "Zeus initializes WDSP FFTW wisdom at startup. Thetis's separate Filter Impulse Cache and save/restore cache-file controls are not runtime settings in Zeus yet.",
            },
            highResolutionFilterDisplay = new
            {
                enabled = false,
                status = "not-exposed-as-filter-display-setting",
                notes = "Zeus exposes live filter edges, presets, panadapter scale, and mini-pan visuals, but not Thetis's separate high-resolution filter-characteristics display toggle yet.",
            },
            diagnosticRecommendation = "All verified hardware sample-rate sizes, Thetis mode-default DSP sizes, and the full Zeus WDSP planning ladder are visible. The live DDC sample rate is operator-writable through Settings > DSP > Bandwidth and /api/sampleRate; RXA/TXA buffer/tap/window geometry remains fixed until OpenChannel/DSP buffer/tap/window changes can be rebuilt atomically across RXA, TXA, monitor, and analyzers.",
            source = "Thetis DSP Options filter matrix + Zeus WdspDspEngine OpenChannel/SetRXABandpassWindow/SetTXABandpassWindow profile",
        };
    }

    private static object BuildRuntimeSampleRateControlDiagnostics(
        StateDto state,
        BoardCapabilities caps,
        bool protocol2Active)
    {
        bool connected = state.Status == ConnectionStatus.Connected;
        int activeRate = Math.Max(0, state.SampleRate);
        int boardMax = Math.Max(48_000, caps.MaxRxSampleRateHz);
        int protocolMax = protocol2Active ? boardMax : Math.Min(boardMax, 384_000);
        bool p2WidebandCapable = boardMax > 384_000;
        bool widebandWritable = connected && protocol2Active && p2WidebandCapable;
        string status;
        string recommendation;

        if (!connected)
        {
            status = "waiting-for-connection";
            recommendation = "Connect a radio before changing the runtime DDC sample rate.";
        }
        else if (p2WidebandCapable && !protocol2Active)
        {
            status = "wideband-requires-p2";
            recommendation = "This radio can use 768/1536 kHz DDC rates, but the current connection is not Protocol 2; reconnect over P2 before widening the span.";
        }
        else if (p2WidebandCapable && activeRate < boardMax)
        {
            status = "wideband-control-ready";
            recommendation = "Wider 768/1536 kHz spans are available now; increase the DDC sample rate when weak-signal search bandwidth matters and host/network headroom is clean.";
        }
        else if (p2WidebandCapable)
        {
            status = "max-wideband-active";
            recommendation = "The active DDC sample rate is already at the verified board maximum; improve copy with filters, dynamic range, and display intelligence rather than widening the span.";
        }
        else
        {
            status = "board-capability-limited";
            recommendation = "The verified board ceiling is 384 kHz or lower; keep the DDC rate within the P1/P2 baseline ladder and optimize with front-end staging and DSP.";
        }

        return new
        {
            status,
            writable = connected,
            requiresReconnect = false,
            activeSampleRateHz = activeRate,
            maxBoardSampleRateHz = boardMax,
            maxWritableSampleRateHz = protocolMax,
            protocol2Active,
            widebandWritable,
            settingsSurface = "Settings > DSP > Bandwidth",
            apiRoute = "/api/sampleRate",
            diagnosticRecommendation = recommendation,
        };
    }

    private static object BuildReceiverBandwidthDiagnostics(
        StateDto state,
        BoardCapabilities caps,
        HpsdrBoardKind connectedBoard,
        HpsdrBoardKind effectiveBoard,
        OrionMkIIVariant variant,
        bool protocol2Active)
    {
        bool connected = state.Status == ConnectionStatus.Connected;
        bool g2Class = effectiveBoard == HpsdrBoardKind.OrionMkII
            && variant is OrionMkIIVariant.G2 or OrionMkIIVariant.G2_1K
            && caps.MaxRxSampleRateHz >= 1_536_000;
        int activeRate = Math.Max(0, state.SampleRate);
        int maxRate = Math.Max(48_000, caps.MaxRxSampleRateHz);
        double utilization = maxRate > 0
            ? Math.Round(Math.Clamp(activeRate / (double)maxRate, 0.0, 1.0) * 100.0, 1)
            : 0.0;
        bool p2WidebandCapable = maxRate > 384_000;
        bool widebandActive = connected && activeRate > 384_000;
        int activeSoftwareReceivers = connected ? 1 : 0;
        int manualReceiverCapacity = g2Class ? 10 : Math.Max(1, caps.RxAdcCount);
        int unexposedReceivers = Math.Max(0, manualReceiverCapacity - activeSoftwareReceivers);
        HpsdrBoardKind wireBoard = connectedBoard != HpsdrBoardKind.Unknown
            ? connectedBoard
            : effectiveBoard;
        int? activeUserDdcIndex = connected && protocol2Active
            ? Zeus.Protocol2.Protocol2Client.RxBaseDdc(wireBoard)
            : null;
        object[] activeSlots = activeUserDdcIndex.HasValue
            ? [DdcSlot(activeUserDdcIndex.Value, "RX1", "active", "Primary operator receive DDC feeding WDSP RXA and the panadapter/waterfall.")]
            : [];
        object[] reservedSlots = activeUserDdcIndex == 2
            ? [
                DdcSlot(0, "PureSignal RX feedback", "reserved", "Saturn/G2 P2 convention reserves DDC0 for post-PA feedback when PureSignal is armed."),
                DdcSlot(1, "PureSignal TX reference", "reserved", "Saturn/G2 P2 convention reserves DDC1 for TX-DAC reference feedback when PureSignal is armed."),
            ]
            : [];

        string status;
        string tone;
        string recommendation;
        if (!connected)
        {
            status = "waiting-for-connection";
            tone = "verify";
            recommendation = "Connect the radio before judging receiver bandwidth utilization or DDC-slot assignment.";
        }
        else if (p2WidebandCapable && !protocol2Active)
        {
            status = "wideband-requires-p2";
            tone = "verify";
            recommendation = "This board can use P2 wideband rates above 384 kHz, but the current runtime is not on a P2 wideband path.";
        }
        else if (p2WidebandCapable && activeRate < maxRate)
        {
            status = "wideband-underused";
            tone = "ready";
            recommendation = "Receiver hardware has unused DDC bandwidth; use the existing Settings > DSP > Bandwidth control to test wider 768 kHz or 1536 kHz spans when host/network load allows.";
        }
        else if (p2WidebandCapable)
        {
            status = "max-wideband-active";
            tone = "ready";
            recommendation = "Receiver DDC bandwidth is at the board maximum; refine copy with filters, dynamic-range staging, and display intelligence rather than widening the span.";
        }
        else
        {
            status = "board-capability-limited";
            tone = "standby";
            recommendation = "This board's verified DDC bandwidth ceiling is 384 kHz or lower; dynamic-range gains should come from front-end staging, filters, and DSP rather than P2 wideband rates.";
        }

        return new
        {
            schemaVersion = 1,
            status,
            tone,
            connected,
            protocol2Active,
            p2WidebandCapable,
            widebandActive,
            activeSampleRateHz = activeRate,
            maxSampleRateHz = maxRate,
            activeNyquistHz = activeRate / 2,
            maxNyquistHz = maxRate / 2,
            utilizationPct = utilization,
            unusedSampleRateHz = Math.Max(0, maxRate - activeRate),
            unusedNyquistHz = Math.Max(0, (maxRate - activeRate) / 2),
            activeSoftwareReceivers,
            manualReceiverCapacity,
            unexposedReceiverCount = unexposedReceivers,
            activeUserDdcIndex,
            activeSlots,
            reservedSlots,
            source = "ANAN G2 manual receiver architecture + Protocol2Client DDC map + BoardCapabilities",
            diagnosticRecommendation = recommendation,
        };
    }

    private static object DdcSlot(int slot, string purpose, string status, string notes) => new
    {
        slot,
        purpose,
        status,
        notes,
    };

    private static object ThetisFilterRow(
        string modeFamily,
        string direction,
        int? iqBufferSize,
        int? filterTaps,
        string filterType,
        string filterWindow,
        string status) => new
        {
            modeFamily,
            direction,
            iqBufferSize,
            filterTaps,
            filterType,
            filterWindow,
            status,
        };

    private object SnapshotDisplayDiagnostics(IDspEngine? engine)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int clients = _hub.ClientCount;

        lock (_calPanLock)
        {
            long? frameAgeMs = _diagDisplayFrameMs > 0 ? Math.Max(0, nowMs - _diagDisplayFrameMs) : null;
            long? panAgeMs = _calPanSnapshotMs > 0 ? Math.Max(0, nowMs - _calPanSnapshotMs) : null;
            long? wfAgeMs = _diagWfSnapshotMs > 0 ? Math.Max(0, nowMs - _diagWfSnapshotMs) : null;
            string status = DisplayHealthStatus(engine, clients, frameAgeMs, _diagLastPanValid, _diagLastWfValid);

            return new
            {
                schemaVersion = 1,
                status,
                clientCount = clients,
                framesBroadcast = _diagDisplayFrameCount,
                lastSeq = _diagDisplaySeq,
                lastFrameAgeMs = frameAgeMs,
                lastFrameUnixMs = _diagDisplayFrameMs > 0 ? _diagDisplayFrameMs : (long?)null,
                panValid = _diagLastPanValid,
                waterfallValid = _diagLastWfValid,
                panSource = _diagLastPanSource,
                waterfallSource = _diagLastWfSource,
                keyed = _diagLastKeyed,
                psMonitorRequested = _diagLastPsMonitorRequested,
                psFeedbackCorrecting = _diagLastPsFeedbackCorrecting,
                width = Width,
                centerHz = _calPanCenterHz == 0 ? (long?)null : _calPanCenterHz,
                hzPerPixel = _calPanHzPerPixel > 0 ? Math.Round(_calPanHzPerPixel, 3) : (double?)null,
                pan = BuildDisplayBufferDiagnostics(_diagLastPanValid, _calPanSnapshot, panAgeMs),
                waterfall = BuildDisplayBufferDiagnostics(_diagLastWfValid, _diagWfSnapshot, wfAgeMs),
                diagnosticRecommendation = DisplayDiagnosticRecommendation(status, clients, _diagLastPanValid, _diagLastWfValid, _diagLastPanSource, _diagLastWfSource),
            };
        }
    }

    private object SnapshotSecondReceiverHealth(StateDto state)
    {
        var connectedBoard = _radio.ConnectedBoardKind;
        var effectiveBoard = _radio.EffectiveBoardKind;
        var wireBoard = connectedBoard != HpsdrBoardKind.Unknown ? connectedBoard : effectiveBoard;
        int rx1Ddc = Zeus.Protocol2.Protocol2Client.RxBaseDdc(wireBoard);
        int rx2Ddc = Zeus.Protocol2.Protocol2Client.Rx2Ddc(wireBoard);
        long[] rates = _attachedSinkP2?.SnapshotRxPortPacketRates() ?? [];

        long PacketRate(int ddc) =>
            ddc >= 0 && ddc < rates.Length ? Math.Max(0, rates[ddc]) : 0;

        long rx1PacketRate = PacketRate(rx1Ddc);
        long rx2PacketRate = PacketRate(rx2Ddc);
        bool protocol2Attached = _attachedSinkP2 is not null;
        string status = !state.Rx2Enabled
            ? "rx2-disabled"
            : !protocol2Attached
                ? "rx2-waiting-for-protocol2"
                : rx2PacketRate > 0
                    ? "rx2-streaming"
                    : "rx2-streaming-missing";

        return new
        {
            schemaVersion = 1,
            status,
            rx2Enabled = state.Rx2Enabled,
            protocol2Attached,
            rx1Ddc,
            rx2Ddc,
            rx1UdpPort = 1035 + rx1Ddc,
            rx2UdpPort = 1035 + rx2Ddc,
            displayFramesPerWindow = new
            {
                rx1Panadapter = Math.Max(0, Volatile.Read(ref _rx1PanCnt)),
                rx1Waterfall = Math.Max(0, Volatile.Read(ref _rx1WfCnt)),
                rx2Panadapter = Math.Max(0, Volatile.Read(ref _rx2PanCnt)),
                rx2Waterfall = Math.Max(0, Volatile.Read(ref _rx2WfCnt)),
            },
            iqSignal = new
            {
                rx1 = new
                {
                    rms = (double?)null,
                    peak = (double?)null,
                },
                rx2 = new
                {
                    rms = (double?)null,
                    peak = (double?)null,
                },
            },
            ddcPacketRatePerSec = new Dictionary<string, long>
            {
                [$"ddc{rx1Ddc}_port{1035 + rx1Ddc}"] = rx1PacketRate,
                [$"ddc{rx2Ddc}_port{1035 + rx2Ddc}"] = rx2PacketRate,
            },
            diagnosticRecommendation = status switch
            {
                "rx2-disabled" => "RX2 is disabled; enable RX2 before evaluating second-receiver DDC, display, or audio health.",
                "rx2-streaming" => "RX2 DDC packets are arriving; compare RX2 display and audio counters with RX1 before diagnosing frontend rendering.",
                "rx2-streaming-missing" => "RX2 is enabled but no RX2 DDC packets were observed in the latest Protocol 2 packet-rate window.",
                _ => "Protocol 2 is not attached, so RX2 DDC packet health cannot be evaluated yet.",
            },
        };
    }

    private RxMetersDiagnosticsDto SnapshotRxMetersDiagnostics()
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool valid;
        long sampleMs;
        int channelId;
        double rxDbm;
        RxMetersV2Frame meters;
        lock (_rxMeterDiagLock)
        {
            valid = _diagRxMetersValid;
            sampleMs = _diagRxMetersMs;
            channelId = _diagRxMetersChannelId;
            rxDbm = _diagRxDbm;
            meters = _diagRxMeters;
        }

        long? ageMs = valid && sampleMs > 0 ? Math.Max(0, nowMs - sampleMs) : null;
        return BuildRxMetersDiagnostics(valid, ageMs, channelId, rxDbm, meters);
    }

    private AudioPathDiagnosticsDto SnapshotAudioDiagnostics()
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool valid;
        long frameMs;
        uint lastSeq;
        long framesBroadcast;
        string source;
        int sampleRateHz;
        int sampleCount;
        double rms;
        double peak;
        bool txMonitorRequested;
        bool squelchEnabled;
        bool squelchOpen;
        bool squelchTailActive;
        double squelchGain;
        bool levelerValid;
        double levelerInputRmsDbfs;
        double levelerOutputRmsDbfs;
        double levelerInputPeakDbfs;
        double levelerOutputPeakDbfs;
        double levelerDesiredGainDb;
        double levelerAppliedGainDb;
        double levelerGainDeltaDb;
        double levelerPeakHeadroomDb;
        double levelerPreLimitPeakDbfs;
        double levelerOutputLimitReductionDb;
        int levelerOutputLimitSampleCount;
        int levelerPauseHoldBlocks;
        bool levelerBoostSlewLimited;
        bool levelerPeakLimited;
        bool levelerOutputLimited;
        string squelchMode;
        string squelchGateSource;
        bool squelchOpenKnown;
        long monitorBacklogSamples;
        int audioSinkCount;

        lock (_audioDiagLock)
        {
            valid = _diagAudioValid;
            frameMs = _diagAudioFrameMs;
            lastSeq = _diagAudioSeq;
            framesBroadcast = _diagAudioFrameCount;
            source = _diagAudioSource;
            sampleRateHz = _diagAudioSampleRateHz;
            sampleCount = _diagAudioSampleCount;
            rms = _diagAudioRms;
            peak = _diagAudioPeak;
            txMonitorRequested = _diagAudioTxMonitorRequested;
            squelchEnabled = _diagAudioSquelchEnabled;
            squelchOpen = _diagAudioSquelchOpen;
            squelchTailActive = _diagAudioSquelchTailActive;
            squelchGain = _diagAudioSquelchGain;
            levelerValid = _diagAudioLevelerValid;
            levelerInputRmsDbfs = _diagAudioLevelerInputRmsDbfs;
            levelerOutputRmsDbfs = _diagAudioLevelerOutputRmsDbfs;
            levelerInputPeakDbfs = _diagAudioLevelerInputPeakDbfs;
            levelerOutputPeakDbfs = _diagAudioLevelerOutputPeakDbfs;
            levelerDesiredGainDb = _diagAudioLevelerDesiredGainDb;
            levelerAppliedGainDb = _diagAudioLevelerAppliedGainDb;
            levelerGainDeltaDb = _diagAudioLevelerGainDeltaDb;
            levelerPeakHeadroomDb = _diagAudioLevelerPeakHeadroomDb;
            levelerPreLimitPeakDbfs = _diagAudioLevelerPreLimitPeakDbfs;
            levelerOutputLimitReductionDb = _diagAudioLevelerOutputLimitReductionDb;
            levelerOutputLimitSampleCount = _diagAudioLevelerOutputLimitSampleCount;
            levelerPauseHoldBlocks = _diagAudioLevelerPauseHoldBlocks;
            levelerBoostSlewLimited = _diagAudioLevelerBoostSlewLimited;
            levelerPeakLimited = _diagAudioLevelerPeakLimited;
            levelerOutputLimited = _diagAudioLevelerOutputLimited;
            squelchMode = _diagAudioSquelchMode;
            squelchGateSource = _diagAudioSquelchGateSource;
            squelchOpenKnown = _diagAudioSquelchOpenKnown;
            monitorBacklogSamples = _diagAudioMonitorBacklogSamples;
            audioSinkCount = _diagAudioSinkCount;
        }

        long? ageMs = valid && frameMs > 0 ? Math.Max(0, nowMs - frameMs) : null;
        return BuildAudioPathDiagnostics(
            valid,
            ageMs,
            lastSeq,
            framesBroadcast,
            source,
            sampleRateHz,
            sampleCount,
            rms,
            peak,
            txMonitorRequested,
            squelchEnabled,
            squelchOpen,
            squelchTailActive,
            squelchGain,
            monitorBacklogSamples,
            audioSinkCount,
            levelerValid,
            levelerInputRmsDbfs,
            levelerOutputRmsDbfs,
            levelerInputPeakDbfs,
            levelerOutputPeakDbfs,
            levelerDesiredGainDb,
            levelerAppliedGainDb,
            levelerGainDeltaDb,
            levelerPeakHeadroomDb,
            levelerPreLimitPeakDbfs,
            levelerOutputLimitReductionDb,
            levelerOutputLimitSampleCount,
            levelerPauseHoldBlocks,
            levelerBoostSlewLimited,
            levelerPeakLimited,
            levelerOutputLimited,
            squelchMode,
            squelchGateSource,
            squelchOpenKnown);
    }

    internal static AudioPathDiagnosticsDto BuildAudioPathDiagnostics(
        bool valid,
        long? ageMs,
        uint lastSeq,
        long framesBroadcast,
        string? source,
        int sampleRateHz,
        int sampleCount,
        double rms,
        double peak,
        bool txMonitorRequested,
        bool squelchEnabled,
        bool squelchOpen,
        bool squelchTailActive,
        double squelchGain,
        long monitorBacklogSamples,
        int audioSinkCount,
        bool levelerValid = false,
        double levelerInputRmsDbfs = double.NaN,
        double levelerOutputRmsDbfs = double.NaN,
        double levelerInputPeakDbfs = double.NaN,
        double levelerOutputPeakDbfs = double.NaN,
        double levelerDesiredGainDb = double.NaN,
        double levelerAppliedGainDb = double.NaN,
        double levelerGainDeltaDb = double.NaN,
        double levelerPeakHeadroomDb = double.NaN,
        double levelerPreLimitPeakDbfs = double.NaN,
        double levelerOutputLimitReductionDb = double.NaN,
        int levelerOutputLimitSampleCount = 0,
        int levelerPauseHoldBlocks = 0,
        bool levelerBoostSlewLimited = false,
        bool levelerPeakLimited = false,
        bool levelerOutputLimited = false,
        string? squelchMode = null,
        string? squelchGateSource = null,
        bool? squelchOpenKnown = null)
    {
        source = string.IsNullOrWhiteSpace(source) ? "none" : source;
        squelchMode = NormalizeSquelchMode(squelchMode, squelchEnabled);
        squelchGateSource = NormalizeSquelchGateSource(squelchGateSource, squelchMode);
        bool openKnown = squelchOpenKnown ?? (!squelchEnabled || string.Equals(squelchMode, "adaptive", StringComparison.OrdinalIgnoreCase));
        bool fresh = valid && ageMs is <= AudioFreshMs;
        bool stale = !valid || ageMs is null || ageMs > AudioAgingMs;
        bool clippingRisk = valid && double.IsFinite(peak) && peak >= AudioClippingRiskLinear;
        bool mutedBySquelch = valid && string.Equals(source, "rx", StringComparison.OrdinalIgnoreCase)
            && squelchEnabled && !squelchOpen;
        double? rmsDbfs = AudioLinearToDbfs(rms);
        double? peakDbfs = AudioLinearToDbfs(peak);
        bool silent = valid && (rmsDbfs is null || rmsDbfs <= AudioSilentRmsDbfs);
        bool monitorBacklog = valid && monitorBacklogSamples > Math.Max(sampleRateHz / 10, sampleCount * 3L);

        string status;
        string recommendation;
        if (!valid)
        {
            status = "missing";
            recommendation = "No RX audio frame has been published yet; connect the radio or attach an audio client before judging receive audio fidelity.";
        }
        else if (stale)
        {
            status = "stale";
            recommendation = "RX audio frames are stale; verify the DSP tick path, audio sinks, and websocket/native audio consumers before tuning weak-signal audio.";
        }
        else if (clippingRisk)
        {
            status = "clipping-risk";
            recommendation = "RX audio is approaching full scale; reduce RX leveler boost, front-end gain, or plugin output before evaluating fidelity.";
        }
        else if (string.Equals(source, "tx-monitor", StringComparison.OrdinalIgnoreCase))
        {
            status = "tx-monitor";
            recommendation = "TX monitor audio is replacing RX audio, so listen-time diagnostics are currently showing the processed transmit monitor path.";
        }
        else if (mutedBySquelch)
        {
            status = "muted-by-squelch";
            recommendation = "RX audio is fresh but gated by adaptive squelch; lower the threshold or disable squelch before using silence as weak-signal evidence.";
        }
        else if (monitorBacklog)
        {
            status = "monitor-backlog";
            recommendation = "Local playback monitor audio is queued faster than the RX bus is draining; reduce injected playback level/rate before judging band audio.";
        }
        else if (silent)
        {
            status = "silent";
            recommendation = squelchEnabled && string.Equals(squelchMode, "fixed", StringComparison.OrdinalIgnoreCase)
                ? "RX audio frames are fresh but near silence while fixed SQL is active; WDSP fixed squelch may be closed, so verify fixed threshold/sensitivity before treating silence as no-signal evidence."
                : "RX audio frames are fresh but near silence; cross-check S-meter, panadapter peaks, squelch, mode/filter, and audio sink volume.";
        }
        else
        {
            status = "fresh";
            recommendation = "RX audio frames are fresh; use RMS/peak dBFS with RXA meters, squelch state, and display SNR to tune weak-signal fidelity.";
        }

        return new AudioPathDiagnosticsDto(
            SchemaVersion: 1,
            Status: status,
            Source: source,
            Fresh: fresh,
            Stale: stale,
            AgeMs: ageMs,
            FramesBroadcast: framesBroadcast,
            LastSeq: lastSeq,
            SampleRateHz: sampleRateHz,
            SampleCount: sampleCount,
            RmsLinear: valid && double.IsFinite(rms) ? Math.Round(rms, 6) : null,
            PeakLinear: valid && double.IsFinite(peak) ? Math.Round(peak, 6) : null,
            RmsDbfs: rmsDbfs,
            PeakDbfs: peakDbfs,
            TxMonitorRequested: txMonitorRequested,
            SquelchEnabled: squelchEnabled,
            SquelchOpen: squelchOpen,
            SquelchTailActive: squelchTailActive,
            SquelchGateGain: double.IsFinite(squelchGain) ? Math.Round(Math.Clamp(squelchGain, 0.0, 1.0), 3) : null,
            RxAudioLevelerInputRmsDbfs: RoundLevelerDb(levelerValid, levelerInputRmsDbfs),
            RxAudioLevelerOutputRmsDbfs: RoundLevelerDb(levelerValid, levelerOutputRmsDbfs),
            RxAudioLevelerInputPeakDbfs: RoundLevelerDb(levelerValid, levelerInputPeakDbfs),
            RxAudioLevelerOutputPeakDbfs: RoundLevelerDb(levelerValid, levelerOutputPeakDbfs),
            RxAudioLevelerDesiredGainDb: RoundLevelerDb(levelerValid, levelerDesiredGainDb),
            RxAudioLevelerAppliedGainDb: RoundLevelerDb(levelerValid, levelerAppliedGainDb),
            RxAudioLevelerGainDeltaDb: RoundLevelerDb(levelerValid, levelerGainDeltaDb),
            RxAudioLevelerPeakHeadroomDb: RoundLevelerDb(levelerValid, levelerPeakHeadroomDb),
            RxAudioLevelerPreLimitPeakDbfs: RoundLevelerDb(levelerValid, levelerPreLimitPeakDbfs),
            RxAudioLevelerOutputLimitReductionDb: RoundLevelerDb(levelerValid, levelerOutputLimitReductionDb),
            RxAudioLevelerOutputLimitSampleCount: levelerValid ? Math.Max(0, levelerOutputLimitSampleCount) : null,
            RxAudioLevelerPauseHoldBlocks: levelerValid ? Math.Max(0, levelerPauseHoldBlocks) : null,
            RxAudioLevelerBoostSlewLimited: levelerValid ? levelerBoostSlewLimited : null,
            RxAudioLevelerPeakLimited: levelerValid ? levelerPeakLimited : null,
            RxAudioLevelerOutputLimited: levelerValid ? levelerOutputLimited : null,
            SquelchMode: squelchMode,
            SquelchGateSource: squelchGateSource,
            SquelchOpenKnown: openKnown,
            MonitorBacklogSamples: monitorBacklogSamples,
            AudioSinkCount: audioSinkCount,
            DiagnosticRecommendation: recommendation);
    }

    private static double? RoundLevelerDb(bool valid, double value) =>
        valid && double.IsFinite(value) ? Math.Round(value, 1) : null;

    private static double AudioLinearToDbfsRaw(double value) =>
        double.IsFinite(value) && value > 0.0
            ? 20.0 * Math.Log10(Math.Max(value, 1e-12))
            : double.NaN;

    private static string NormalizeSquelchMode(string? mode, bool enabled)
    {
        if (!enabled) return "off";
        if (string.Equals(mode, "fixed", StringComparison.OrdinalIgnoreCase)) return "fixed";
        if (string.Equals(mode, "adaptive", StringComparison.OrdinalIgnoreCase)) return "adaptive";
        return "adaptive";
    }

    private static string NormalizeSquelchGateSource(string? source, string mode)
    {
        if (string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase)) return "disabled";
        if (string.Equals(source, "wdsp-fixed", StringComparison.OrdinalIgnoreCase)) return "wdsp-fixed";
        if (string.Equals(source, "backend-adaptive", StringComparison.OrdinalIgnoreCase)) return "backend-adaptive";
        return string.Equals(mode, "fixed", StringComparison.OrdinalIgnoreCase)
            ? "wdsp-fixed"
            : "backend-adaptive";
    }

    private static double? AudioLinearToDbfs(double value) =>
        double.IsFinite(value) && value > 0.0
            ? Math.Round(AudioLinearToDbfsRaw(value), 1)
            : null;

    private void CaptureAudioDiagnostics(
        string source,
        in AudioFrame frame,
        double rms,
        double peak,
        bool txMonitorRequested,
        SquelchConfig squelch)
    {
        string squelchMode = !squelch.Enabled ? "off" : squelch.Adaptive ? "adaptive" : "fixed";
        string squelchGateSource = squelchMode switch
        {
            "adaptive" => "backend-adaptive",
            "fixed" => "wdsp-fixed",
            _ => "disabled",
        };
        bool squelchOpen = !squelch.Enabled || !squelch.Adaptive || _adaptiveSquelch.Open;
        bool squelchTailActive = IsAdaptiveSquelchTailActive(squelch, _adaptiveSquelch);
        double squelchGain = squelch.Enabled && squelch.Adaptive ? _adaptiveSquelch.Gain : 1.0;
        long monitorBacklogSamples = MonitorBacklog;
        var leveler = _rxAudioLeveler;
        bool levelerValid = string.Equals(source, "rx", StringComparison.OrdinalIgnoreCase)
            && leveler.DiagnosticsValid;

        lock (_audioDiagLock)
        {
            _diagAudioValid = true;
            _diagAudioFrameMs = (long)frame.TsUnixMs;
            _diagAudioSeq = frame.Seq;
            _diagAudioFrameCount++;
            _diagAudioSource = source;
            _diagAudioSampleRateHz = checked((int)frame.SampleRateHz);
            _diagAudioSampleCount = frame.SampleCount;
            _diagAudioRms = rms;
            _diagAudioPeak = peak;
            _diagAudioTxMonitorRequested = txMonitorRequested;
            _diagAudioSquelchEnabled = squelch.Enabled;
            _diagAudioSquelchOpen = squelchOpen;
            _diagAudioSquelchTailActive = squelchTailActive;
            _diagAudioSquelchGain = squelchGain;
            _diagAudioLevelerValid = levelerValid;
            _diagAudioLevelerInputRmsDbfs = levelerValid ? leveler.InputRmsDbfs : double.NaN;
            _diagAudioLevelerOutputRmsDbfs = levelerValid ? leveler.OutputRmsDbfs : double.NaN;
            _diagAudioLevelerInputPeakDbfs = levelerValid ? leveler.InputPeakDbfs : double.NaN;
            _diagAudioLevelerOutputPeakDbfs = levelerValid ? leveler.OutputPeakDbfs : double.NaN;
            _diagAudioLevelerDesiredGainDb = levelerValid ? leveler.DesiredGainDb : double.NaN;
            _diagAudioLevelerAppliedGainDb = levelerValid ? leveler.AppliedGainDb : double.NaN;
            _diagAudioLevelerGainDeltaDb = levelerValid ? leveler.GainDeltaDb : double.NaN;
            _diagAudioLevelerPeakHeadroomDb = levelerValid ? leveler.PeakHeadroomDb : double.NaN;
            _diagAudioLevelerPreLimitPeakDbfs = levelerValid ? leveler.PreLimitPeakDbfs : double.NaN;
            _diagAudioLevelerOutputLimitReductionDb = levelerValid ? leveler.OutputLimitReductionDb : double.NaN;
            _diagAudioLevelerOutputLimitSampleCount = levelerValid ? leveler.OutputLimitSampleCount : 0;
            _diagAudioLevelerPauseHoldBlocks = levelerValid ? leveler.PauseHoldBlocks : 0;
            _diagAudioLevelerBoostSlewLimited = levelerValid && leveler.BoostSlewLimited;
            _diagAudioLevelerPeakLimited = levelerValid && leveler.PeakLimited;
            _diagAudioLevelerOutputLimited = levelerValid && leveler.OutputLimited;
            _diagAudioSquelchMode = squelchMode;
            _diagAudioSquelchGateSource = squelchGateSource;
            _diagAudioSquelchOpenKnown = !squelch.Enabled || squelch.Adaptive;
            _diagAudioMonitorBacklogSamples = monitorBacklogSamples;
            _diagAudioSinkCount = _audioSinks.Length;
        }
    }

    private static bool IsAdaptiveSquelchTailActive(SquelchConfig cfg, AdaptiveSquelchState state)
    {
        if (!cfg.Enabled || !cfg.Adaptive || !state.Open || state.CloseHoldBlocks <= 0) return false;
        if (!double.IsFinite(state.NoiseFloorDbm) || !double.IsFinite(state.LastSignalDbm)) return false;
        double marginDb = AdaptiveSquelchMarginDb();
        double closeThreshold = state.NoiseFloorDbm + marginDb - AdaptiveSquelchCloseHysteresisDb(marginDb);
        return state.LastSignalDbm < closeThreshold;
    }

    internal static RxMetersDiagnosticsDto BuildRxMetersDiagnostics(
        bool valid,
        long? ageMs,
        int channelId,
        double rxDbm,
        RxMetersV2Frame meters)
    {
        double? signalPk = valid ? RxStageLevelDb(meters.SignalPk) : null;
        double? signalAv = valid ? RxStageLevelDb(meters.SignalAv) : null;
        double? adcPk = valid ? RxStageLevelDb(meters.AdcPk) : null;
        double? adcAv = valid ? RxStageLevelDb(meters.AdcAv) : null;
        double? agcGain = valid && double.IsFinite(meters.AgcGain) ? Math.Round(meters.AgcGain, 1) : null;
        double? agcEnvPk = valid ? RxStageLevelDb(meters.AgcEnvPk) : null;
        double? agcEnvAv = valid ? RxStageLevelDb(meters.AgcEnvAv) : null;
        double? rxDbmOut = valid && double.IsFinite(rxDbm) ? Math.Round(rxDbm, 1) : null;
        double? adcHeadroomDb = adcPk is { } pk ? Math.Round(Math.Max(0.0, -pk), 1) : null;
        bool fresh = valid && ageMs is <= RxMetersFreshMs;
        bool stale = !valid || ageMs is null || ageMs > RxMetersAgingMs;
        bool signalUsable = signalPk.HasValue || signalAv.HasValue || rxDbmOut.HasValue;
        bool adcUsable = adcPk.HasValue || adcAv.HasValue;
        bool agcEnvUsable = agcEnvPk.HasValue || agcEnvAv.HasValue;

        string status;
        string recommendation;
        if (!valid)
        {
            status = "missing";
            recommendation = "No RXA stage-meter frame has been captured yet; connect the radio and confirm IQ/audio ticks before judging RX fidelity.";
        }
        else if (stale)
        {
            status = "stale";
            recommendation = "RXA stage meters are stale; verify the DSP tick path and active websocket/radio connection before tuning weak-signal or AGC settings.";
        }
        else if (adcPk is > -3.0)
        {
            status = "adc-hot";
            recommendation = "RX ADC peak is within 3 dB of full scale; add attenuation or reduce preamp/front-end gain before increasing NR or AGC boost.";
        }
        else if (agcGain is < -20.0 && adcHeadroomDb is <= 15.0)
        {
            status = "agc-cutting";
            recommendation = "RX AGC is cutting heavily, which indicates a hot signal or overload-prone front end; restore ADC/AGC headroom before judging recovered audio.";
        }
        else if (agcGain is < -20.0)
        {
            status = "agc-normalizing";
            recommendation = "RX AGC is normalizing a strong signal while ADC headroom is clean; keep AGC-T and RF gain stable, and judge recovered audio with RX audio RMS/peak and scene SNR.";
        }
        else if (agcGain is > 35.0 && (signalPk is null || signalPk < -90.0))
        {
            status = "weak-signal-boost";
            recommendation = "RX AGC is strongly boosting a weak signal; use Smart NR and narrow filtering carefully while watching ADC headroom and coherent SNR.";
        }
        else if (!signalUsable && !adcUsable && !agcEnvUsable)
        {
            status = "sentinel";
            recommendation = "RXA meter fields are still at sentinel/bypassed values; wait for WDSP RXA meters to tick or use the fallback S-meter only as proof of audio activity.";
        }
        else
        {
            status = "fresh";
            recommendation = "RXA stage meters are fresh; use S-meter, ADC dBFS, AGC gain, and AGC envelope together when tuning weak-signal fidelity.";
        }

        return new RxMetersDiagnosticsDto(
            SchemaVersion: 1,
            Status: status,
            Source: "wdsp-rxa-meter-ring",
            Fresh: fresh,
            Stale: stale,
            AgeMs: ageMs,
            ChannelId: channelId,
            RxDbm: rxDbmOut,
            SignalPkDbm: signalPk,
            SignalAvDbm: signalAv,
            AdcPkDbfs: adcPk,
            AdcAvDbfs: adcAv,
            AdcHeadroomDb: adcHeadroomDb,
            AgcGainDb: agcGain,
            AgcEnvPkDbm: agcEnvPk,
            AgcEnvAvDbm: agcEnvAv,
            SignalUsable: signalUsable,
            AdcUsable: adcUsable,
            AgcEnvelopeUsable: agcEnvUsable,
            DiagnosticRecommendation: recommendation);
    }

    internal static RxDynamicRangeDiagnosticsDto BuildRxDynamicRangeDiagnostics(
        StateDto state,
        RxMetersDiagnosticsDto rxMeters,
        AdcProtectionStatusDto adc)
    {
        const double targetMinDb = 6.0;
        const double targetMaxDb = 30.0;
        const double weakSignalHeadroomDb = 32.0;
        const double weakSignalFloorDbm = -92.0;

        double? headroom = rxMeters.AdcHeadroomDb;
        double? adcPk = rxMeters.AdcPkDbfs;
        double? agcGain = rxMeters.AgcGainDb;
        double? signalPk = rxMeters.SignalPkDbm;
        bool fresh = rxMeters.Fresh && !rxMeters.Stale;
        bool missingMeters = string.Equals(rxMeters.Status, "missing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rxMeters.Status, "unavailable", StringComparison.OrdinalIgnoreCase);
        bool overloadRisk = state.AdcOverloadWarning || adc.Warning || headroom is <= targetMinDb || adcPk is > -targetMinDb;
        bool frontEndHot = fresh && agcGain is < -20.0 && headroom is <= 15.0;
        bool weakSignalOpportunity = fresh
            && !overloadRisk
            && headroom is >= weakSignalHeadroomDb
            && (agcGain is >= 30.0 || signalPk is null || signalPk <= weakSignalFloorDbm);
        bool frontEndUnderused = weakSignalOpportunity
            && (!state.PreampOn || adc.EffectiveDb > 0);
        bool headroomOptimal = fresh
            && !overloadRisk
            && headroom is >= targetMinDb and <= targetMaxDb
            && agcGain is > -20.0 and < 35.0;

        var reasons = new List<string>();
        var actions = new List<RxDynamicRangeActionDto>();

        if (!fresh)
        {
            reasons.Add(missingMeters ? "rx-meters-missing" : "rx-meters-stale");
            actions.Add(new RxDynamicRangeActionDto(
                "verify-rx-meter-feed",
                "Verify RXA meters",
                "required",
                "Wait for fresh RXA stage-meter frames before using dynamic-range guidance."));
        }
        else
        {
            if (overloadRisk)
            {
                reasons.Add(state.AdcOverloadWarning || adc.Warning ? "adc-overload-warning" : "adc-headroom-low");
                actions.Add(new RxDynamicRangeActionDto(
                    "add-attenuation",
                    "Add 3-6 dB attenuation",
                    state.AutoAttEnabled ? "auto-or-manual" : "manual",
                    state.AutoAttEnabled
                        ? "Auto-ATT is enabled; confirm it is raising offset quickly enough, or add manual attenuation if the ADC remains hot."
                        : "Increase S-ATT or reduce external/front-end gain before applying more AGC or NR."));
                if (state.PreampOn)
                {
                    actions.Add(new RxDynamicRangeActionDto(
                        "disable-preamp",
                        "Disable preamp",
                        "candidate",
                        "The preamp is on while ADC headroom is limited; turn it off before adding large attenuation."));
                }
            }

            if (frontEndHot)
            {
                reasons.Add("agc-cutting-with-limited-headroom");
                actions.Add(new RxDynamicRangeActionDto(
                    "restore-agc-headroom",
                    "Restore AGC headroom",
                    "candidate",
                    "AGC is cutting hard while ADC headroom is limited; lower RF gain first, then judge recovered audio."));
            }

            if (weakSignalOpportunity)
            {
                reasons.Add(frontEndUnderused ? "front-end-underused" : "weak-signal-headroom-available");
                if (adc.EffectiveDb > 0)
                {
                    actions.Add(new RxDynamicRangeActionDto(
                        "reduce-attenuation",
                        "Reduce attenuation 3-6 dB",
                        "candidate",
                        "ADC headroom is large and the signal is weak; reduce S-ATT in small steps while watching overload bits."));
                }
                if (!state.PreampOn)
                {
                    actions.Add(new RxDynamicRangeActionDto(
                        "enable-preamp",
                        "Try preamp",
                        "candidate",
                        "ADC headroom is large enough to test the hardware preamp for weak-signal lift; keep it off if band noise jumps without copy improvement."));
                }
                actions.Add(new RxDynamicRangeActionDto(
                    "hold-narrow-nr",
                    "Use narrow filter / Smart NR",
                    "active",
                    "The front end has headroom; use coherent display evidence, narrower filters, and low-artifact NR before chasing more gain."));
            }

            if (headroomOptimal)
            {
                reasons.Add("adc-headroom-in-target-window");
                actions.Add(new RxDynamicRangeActionDto(
                    "hold-current-rf-chain",
                    "Hold RF chain",
                    "active",
                    "ADC headroom is in the target window; tune copy with filters, AGC mode/top, and Smart NR rather than changing preamp or attenuation."));
            }

            if (actions.Count == 0)
            {
                reasons.Add("observe");
                actions.Add(new RxDynamicRangeActionDto(
                    "observe",
                    "Observe",
                    "standby",
                    "RX dynamic-range telemetry is fresh but does not call for an RF-chain change."));
            }
        }

        string status;
        string tone;
        string recommendation;
        if (!fresh)
        {
            status = missingMeters ? "missing" : "stale";
            tone = "verify";
            recommendation = "RX dynamic-range advisor is waiting for fresh RXA stage meters before recommending RF-chain changes.";
        }
        else if (overloadRisk)
        {
            status = "adc-headroom-limited";
            tone = "danger";
            recommendation = "ADC headroom is limited; protect the converter first with attenuation/preamp changes before increasing AGC, NR, or audio gain.";
        }
        else if (frontEndHot)
        {
            status = "front-end-hot";
            tone = "warning";
            recommendation = "AGC is cutting hard with limited converter headroom; reduce RF gain until both ADC and AGC have room.";
        }
        else if (frontEndUnderused)
        {
            status = "weak-signal-rf-chain-underused";
            tone = "ready";
            recommendation = "The weak-signal path has spare ADC headroom; try less attenuation or preamp in small steps while watching overload telemetry.";
        }
        else if (weakSignalOpportunity)
        {
            status = "weak-signal-headroom-ready";
            tone = "ready";
            recommendation = "Weak-signal evidence has adequate ADC headroom; hold RF gain and refine with filter width, AGC, and Smart NR.";
        }
        else if (headroomOptimal)
        {
            status = "dynamic-range-ready";
            tone = "ready";
            recommendation = "RX front-end dynamic range is in the target window; preserve this RF-chain state while tuning DSP.";
        }
        else
        {
            status = "watching";
            tone = "standby";
            recommendation = "RX dynamic-range telemetry is fresh; keep watching ADC headroom, AGC gain, and signal level as band conditions change.";
        }

        return new RxDynamicRangeDiagnosticsDto(
            SchemaVersion: 1,
            Status: status,
            Tone: tone,
            Fresh: fresh,
            Stale: rxMeters.Stale,
            AgeMs: rxMeters.AgeMs,
            Source: "rx-meters+radio-state+adc-protection",
            SampleRateHz: state.SampleRate,
            AttenDb: adc.AttenDb,
            AttOffsetDb: adc.OffsetDb,
            EffectiveAttenDb: adc.EffectiveDb,
            PreampOn: state.PreampOn,
            AutoAttEnabled: state.AutoAttEnabled,
            AdcProtectionEnabled: adc.Config.Enabled,
            AdcOverloadWarning: state.AdcOverloadWarning || adc.Warning,
            AdcOverloadLevel: adc.OverloadLevel,
            TargetHeadroomMinDb: targetMinDb,
            TargetHeadroomMaxDb: targetMaxDb,
            RxDbm: rxMeters.RxDbm,
            SignalPkDbm: signalPk,
            AdcPkDbfs: adcPk,
            AdcHeadroomDb: headroom,
            AgcGainDb: agcGain,
            HeadroomOptimal: headroomOptimal,
            OverloadRisk: overloadRisk,
            WeakSignalOpportunity: weakSignalOpportunity,
            FrontEndUnderused: frontEndUnderused,
            Reasons: reasons.ToArray(),
            Actions: actions.ToArray(),
            DiagnosticRecommendation: recommendation);
    }

    internal static RxListenabilityDiagnosticsDto BuildRxListenabilityDiagnostics(
        RxMetersDiagnosticsDto rxMeters,
        AudioPathDiagnosticsDto audio,
        SquelchConfig squelch)
    {
        bool rxFresh = rxMeters.Fresh && !rxMeters.Stale;
        bool audioFresh = audio.Fresh && !audio.Stale;
        bool signalPresent = rxFresh
            && (Above(rxMeters.SignalPkDbm, -120.0)
                || Above(rxMeters.SignalAvDbm, -125.0)
                || Above(rxMeters.RxDbm, -125.0));
        bool audioRecovered = audioFresh
            && string.Equals(audio.Source, "rx", StringComparison.OrdinalIgnoreCase)
            && (Above(audio.RmsDbfs, -60.0) || Above(audio.PeakDbfs, -45.0));

        string status;
        string tone;
        string blocker;
        string recommendation;

        if (!rxFresh)
        {
            status = "waiting-for-rx-meters";
            tone = "verify";
            blocker = "rx-meters";
            recommendation = "RX listenability cannot be scored until WDSP RXA meters are fresh; verify radio connection, DSP tick, and RX meter feed.";
        }
        else if (!audioFresh)
        {
            status = "waiting-for-audio";
            tone = "verify";
            blocker = "audio";
            recommendation = "RX signal evidence is available, but final audio frames are missing or stale; verify audio sinks and websocket/native audio delivery before tuning NR or AGC.";
        }
        else if (string.Equals(audio.Source, "tx-monitor", StringComparison.OrdinalIgnoreCase))
        {
            status = "tx-monitor-active";
            tone = "standby";
            blocker = "tx-monitor";
            recommendation = "TX monitor audio is replacing listen audio; disable TX monitor before using RX listenability to tune weak-signal copy.";
        }
        else if (string.Equals(audio.Status, "clipping-risk", StringComparison.OrdinalIgnoreCase))
        {
            status = "audio-clipping-risk";
            tone = "protect";
            blocker = "audio-headroom";
            recommendation = "Recovered RX audio is near full scale; reduce RX leveler/plugin output before optimizing NR, AGC, or squelch.";
        }
        else if (string.Equals(rxMeters.Status, "adc-hot", StringComparison.OrdinalIgnoreCase))
        {
            status = "adc-headroom-limited";
            tone = "protect";
            blocker = "adc-headroom";
            recommendation = "RX ADC headroom is limiting listenability; add attenuation or reduce preamp/front-end gain before increasing weak-signal processing.";
        }
        else if (string.Equals(audio.Status, "muted-by-squelch", StringComparison.OrdinalIgnoreCase))
        {
            status = "adaptive-squelch-muted";
            tone = "optimize";
            blocker = "adaptive-squelch";
            recommendation = "Backend adaptive squelch is muting fresh RX audio; lower the DYN SQL threshold or disable squelch while evaluating weak-signal copy.";
        }
        else if (signalPresent && !audioRecovered && squelch.Enabled && !squelch.Adaptive)
        {
            status = "fixed-squelch-suspect";
            tone = "optimize";
            blocker = "fixed-squelch";
            recommendation = "Signal evidence is present but recovered RX audio is silent while fixed SQL is active; lower fixed SQL level/sensitivity or disable SQL before judging NR and weak-signal fidelity.";
        }
        else if (signalPresent && !audioRecovered)
        {
            status = "signal-audio-silent";
            tone = "verify";
            blocker = "audio-path";
            recommendation = "RXA meters show signal evidence but final audio is still near silence; verify mode/filter placement, audio gain, plugins, and sink volume before changing RF/DSP settings.";
        }
        else if (signalPresent && audioRecovered)
        {
            status = "audio-recovered";
            tone = "ready";
            blocker = "none";
            recommendation = "RX signal evidence and recovered audio agree; use coherent SNR, AGC gain, and audio RMS/peak trends to fine-tune NR and filters.";
        }
        else if (audioRecovered)
        {
            status = "audio-without-meter-evidence";
            tone = "verify";
            blocker = "rx-meter-correlation";
            recommendation = "Recovered audio is present but RXA signal meters do not show clear signal evidence; cross-check S-meter calibration, filter passband, and meter freshness.";
        }
        else
        {
            status = "no-signal-evidence";
            tone = "standby";
            blocker = "none";
            recommendation = "No clear RX signal or recovered audio is present; keep weak-signal automation conservative until panadapter or RXA evidence rises above the floor.";
        }

        return new RxListenabilityDiagnosticsDto(
            SchemaVersion: 1,
            Status: status,
            Tone: tone,
            SignalPresent: signalPresent,
            AudioRecovered: audioRecovered,
            Blocker: blocker,
            Recommendation: recommendation);
    }

    private static bool Above(double? value, double threshold) =>
        value is { } v && double.IsFinite(v) && v > threshold;

    private static double? RxStageLevelDb(float value) =>
        float.IsFinite(value) && value > -199.5f
            ? Math.Round(value, 1)
            : null;

    private static string DisplayHealthStatus(
        IDspEngine? engine,
        int clientCount,
        long? frameAgeMs,
        bool panValid,
        bool wfValid)
    {
        if (engine is null) return "no-engine";
        if (engine is SyntheticDspEngine) return "synthetic-idle";
        if (clientCount <= 0) return "idle-no-clients";
        if (frameAgeMs is null) return "missing";
        if (frameAgeMs <= DisplayFreshMs && (panValid || wfValid)) return "fresh";
        if (frameAgeMs <= DisplayAgingMs) return "aging";
        return "stale";
    }

    private static string DisplayDiagnosticRecommendation(
        string status,
        int clientCount,
        bool panValid,
        bool wfValid,
        string panSource,
        string wfSource) =>
        status switch
        {
            "fresh" => $"Display analyzer frames are fresh; panadapter={panSource} valid={panValid}, waterfall={wfSource} valid={wfValid}.",
            "aging" => "Display analyzer frames are aging; watch for UI disconnects, analyzer starvation, or a paused frontend display path.",
            "stale" => "Display analyzer frames are stale; verify a Zeus client is connected and that the DSP pipeline is receiving IQ frames.",
            "idle-no-clients" => "No realtime clients are attached to the streaming hub, so the server is skipping panadapter/waterfall frame generation to save DSP work.",
            "synthetic-idle" => "The DSP engine is synthetic; connect a radio before judging panadapter or waterfall fidelity.",
            "no-engine" => "No DSP engine is active; connect or restart the DSP pipeline before judging display telemetry.",
            _ when clientCount > 0 && !panValid && !wfValid => "A realtime client is attached but no valid panadapter or waterfall frame has been captured yet; wait for the next analyzer tick or inspect WDSP readiness.",
            _ => "Display analyzer telemetry is not ready yet.",
        };

    internal static object BuildDisplayBufferDiagnostics(bool valid, ReadOnlySpan<float> samples, long? ageMs)
    {
        int validBins = 0;
        double sum = 0.0;
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;

        if (valid)
        {
            foreach (float sample in samples)
            {
                if (!float.IsFinite(sample)) continue;
                validBins++;
                sum += sample;
                if (sample < min) min = sample;
                if (sample > max) max = sample;
            }
        }

        bool hasStats = valid && validBins > 0;
        return new
        {
            valid,
            ageMs,
            validBins,
            minDb = hasStats ? Math.Round(min, 1) : (double?)null,
            maxDb = hasStats ? Math.Round(max, 1) : (double?)null,
            meanDb = hasStats ? Math.Round(sum / validBins, 1) : (double?)null,
            dynamicRangeDb = hasStats ? Math.Round(max - min, 1) : (double?)null,
        };
    }

    private static double? FiniteOrNull(double value) =>
        double.IsFinite(value) ? Math.Round(value, 2) : null;

    private object SnapshotAdaptiveSquelchDiagnostics(SquelchConfig cfg)
    {
        var s = _adaptiveSquelch;
        double marginDb = AdaptiveSquelchMarginDb();
        double hysteresisDb = AdaptiveSquelchCloseHysteresisDb(marginDb);
        double? floorDbm = FiniteOrNull(s.NoiseFloorDbm);
        double? signalDbm = FiniteOrNull(s.LastSignalDbm);
        double? openThresholdDbm = floorDbm + marginDb;
        double? closeThresholdDbm = openThresholdDbm - hysteresisDb;
        double? deltaDb = signalDbm - floorDbm;
        bool ready = s.WindowFill >= AdaptiveSquelchMinSamples && floorDbm.HasValue;
        bool adaptiveGateActive = cfg.Enabled && cfg.Adaptive;
        bool effectiveOpen = !cfg.Enabled || !cfg.Adaptive || s.Open;
        double effectiveGain = adaptiveGateActive ? s.Gain : 1.0;
        string gateSource = !cfg.Enabled
            ? "disabled"
            : cfg.Adaptive
                ? "backend-adaptive"
                : "wdsp-fixed";
        bool tailActive = ready
            && s.Open
            && signalDbm.HasValue
            && closeThresholdDbm.HasValue
            && signalDbm.Value < closeThresholdDbm.Value
            && s.CloseHoldBlocks > 0;
        string status = !cfg.Enabled
            ? "disabled"
            : !cfg.Adaptive
                ? "fixed-mode"
                : !ready
                    ? "learning-floor"
                    : tailActive
                        ? "tail-hold"
                        : s.Open
                            ? "open"
                            : "closed";

        double blockMs = TickPeriod.TotalMilliseconds;
        double holdMs = Math.Round(s.CloseHoldBlocks * blockMs, 0);
        double configuredHoldMs = Math.Round(AdaptiveSquelchCloseHoldBlocks * blockMs, 0);
        double configuredReleaseMs = Math.Round(Math.Ceiling(1.0 / AdaptiveSquelchReleasePerBlock) * blockMs, 0);

        return new
        {
            schemaVersion = 1,
            enabled = cfg.Enabled,
            adaptive = cfg.Adaptive,
            status,
            ready,
            open = effectiveOpen,
            openKnown = !cfg.Enabled || cfg.Adaptive,
            gateSource,
            adaptiveGateOpen = s.Open,
            adaptiveGateGain = Math.Round(Math.Clamp(double.IsFinite(s.Gain) ? s.Gain : 0.0, 0.0, 1.0), 3),
            gateGain = Math.Round(Math.Clamp(double.IsFinite(effectiveGain) ? effectiveGain : 0.0, 0.0, 1.0), 3),
            signalDbm,
            noiseFloorDbm = floorDbm,
            signalOverFloorDb = deltaDb,
            openThresholdDbm = FiniteOrNull(openThresholdDbm ?? double.NaN),
            closeThresholdDbm = FiniteOrNull(closeThresholdDbm ?? double.NaN),
            marginDb,
            hysteresisDb,
            tailActive,
            closeHoldBlocks = s.CloseHoldBlocks,
            closeHoldMs = holdMs,
            configuredHoldMs,
            configuredReleaseMs,
            windowFill = s.WindowFill,
            windowSamples = AdaptiveSquelchWindowSamples,
            attackPerBlock = AdaptiveSquelchAttackPerBlock,
            releasePerBlock = AdaptiveSquelchReleasePerBlock,
            source = "rx-audio-rms",
            diagnosticRecommendation = status switch
            {
                "learning-floor" => "DYN SQL is learning the current audio noise floor.",
                "tail-hold" => "DYN SQL is holding the gate open briefly to preserve word endings.",
                "open" => "DYN SQL is open on a signal above the learned noise floor.",
                "closed" => "DYN SQL is closed; signal is below the learned open threshold.",
                "fixed-mode" => "Fixed SQL is active in WDSP; backend DYN diagnostics are learning but not gating, so fixed gate closure must be inferred from final RX audio and WDSP state.",
                _ => "SQL is disabled; DYN diagnostics are learning but not gating.",
            },
        };
    }

    private static DspNrRuntimeSnapshot BuildNrRuntime(
        IDspEngine? engine,
        StateDto state)
    {
        bool wdspActive = engine is WdspDspEngine;
        bool wdspNativeLoadable = WdspDspEngine.NativeLibraryLoadable;
        bool wdspEmnrPost2Available = WdspDspEngine.EmnrPost2Available;
        bool wdspNr4SbnrAvailable = WdspDspEngine.Nr4SbnrAvailable;
        var nr = NormalizeNrConfig(state.Nr ?? new NrConfig());
        string requestedNrMode = nr.NrMode.ToString();
        string effectiveNrMode = wdspActive
            ? nr.NrMode switch
            {
                NrMode.Sbnr when !wdspNr4SbnrAvailable => NrMode.Off.ToString(),
                _ => requestedNrMode,
            }
            : NrMode.Off.ToString();
        return new(
            WdspActive: wdspActive,
            WdspNativeLoadable: wdspNativeLoadable,
            WdspEmnrPost2Available: wdspEmnrPost2Available,
            WdspNr4SbnrAvailable: wdspNr4SbnrAvailable,
            Nr4Readiness: wdspNr4SbnrAvailable
                ? "available"
                : wdspNativeLoadable
                    ? "missing-sbnr-exports"
                    : "wdsp-native-unloadable",
            RequestedNrMode: requestedNrMode,
            EffectiveNrMode: effectiveNrMode);
    }

    private static NrConfig NormalizeNrConfig(NrConfig cfg) =>
        IsSupportedNrMode(cfg.NrMode) ? cfg : cfg with { NrMode = NrMode.Off };

    private static bool IsSupportedNrMode(NrMode mode) =>
        mode is NrMode.Off or NrMode.Anr or NrMode.Emnr or NrMode.Sbnr;

    internal static DspRxChainDiagnosticsDto BuildRxDspChainDiagnostics(
        StateDto state,
        IReadOnlyList<NotchDto>? notches,
        DspNrRuntimeSnapshot nrRuntime,
        NrConfig? appliedNr = null,
        AgcConfig? appliedAgc = null,
        SquelchConfig? appliedSquelch = null)
    {
        var nr = NormalizeNrConfig(state.Nr ?? new NrConfig());
        var agc = state.Agc ?? new AgcConfig(AgcMode.Med);
        var squelch = state.Squelch ?? new SquelchConfig();
        int notchCount = notches?.Count ?? 0;
        int activeNotchCount = notches?.Count(static n => n.Active) ?? 0;
        bool effectiveNbpRun = nr.NbpNotchesEnabled || activeNotchCount > 0;
        bool requestedNr = nr.NrMode != NrMode.Off;
        bool effectiveNr = !string.Equals(nrRuntime.EffectiveNrMode, NrMode.Off.ToString(), StringComparison.OrdinalIgnoreCase);
        bool nrCapabilityLimited = requestedNr && !effectiveNr;
        bool weakSignalAssist = effectiveNr || nr.AnfEnabled || nr.SnbEnabled;
        bool impulseControl = nr.NbMode != NbMode.Off;
        bool notchControl = effectiveNbpRun || activeNotchCount > 0;
        bool appliedNrMatches = appliedNr is null || nr.Equals(appliedNr);
        bool appliedAgcMatches = appliedAgc is null || agc.Equals(appliedAgc);
        bool appliedSquelchMatches = appliedSquelch is null || squelch.Equals(appliedSquelch);
        double effectiveAgcTopDb = Math.Round(state.AgcTopDb + state.AgcOffsetDb, 1);

        var activeFeatures = new List<string>();
        if (effectiveNr) activeFeatures.Add($"nr-{nrRuntime.EffectiveNrMode.ToLowerInvariant()}");
        if (nr.AnfEnabled) activeFeatures.Add("anf");
        if (nr.SnbEnabled) activeFeatures.Add("snb");
        if (effectiveNbpRun) activeFeatures.Add("nbp-notches");
        if (activeNotchCount > 0) activeFeatures.Add("manual-notches");
        if (impulseControl) activeFeatures.Add(nr.NbMode.ToString().ToLowerInvariant());
        if (squelch.Enabled) activeFeatures.Add(squelch.Adaptive ? "adaptive-squelch" : "fixed-squelch");
        if (state.AutoAgcEnabled) activeFeatures.Add("auto-agc");
        if (state.AutoAttEnabled) activeFeatures.Add("auto-att");

        var reasons = new List<string>();
        reasons.Add(nrRuntime.WdspActive ? "wdsp-active" : "wdsp-inactive");
        reasons.Add(effectiveNr ? "nr-effective" : requestedNr ? "nr-requested-not-effective" : "nr-off");
        if (nrCapabilityLimited) reasons.Add("nr-capability-limited");
        if (nr.AnfEnabled) reasons.Add("anf-enabled");
        if (nr.SnbEnabled) reasons.Add("snb-enabled");
        if (effectiveNbpRun) reasons.Add("nbp-notches-running");
        if (activeNotchCount > 0) reasons.Add("manual-notches-active");
        if (impulseControl) reasons.Add("noise-blanker-enabled");
        if (squelch.Enabled) reasons.Add(squelch.Adaptive ? "adaptive-squelch-enabled" : "fixed-squelch-enabled");
        if (state.AutoAgcEnabled) reasons.Add("auto-agc-enabled");
        if (state.AgcOffsetDb != 0.0) reasons.Add("agc-offset-active");
        if (!appliedNrMatches) reasons.Add("nr-apply-pending");
        if (!appliedAgcMatches) reasons.Add("agc-apply-pending");
        if (!appliedSquelchMatches) reasons.Add("squelch-apply-pending");

        string status;
        string recommendation;
        if (!nrRuntime.WdspActive)
        {
            status = "dsp-engine-unavailable";
            recommendation = "WDSP RX processing is not active; connect or restart the DSP engine before judging NR, notch, blanker, or AGC fidelity.";
        }
        else if (nrCapabilityLimited)
        {
            status = "nr-capability-limited";
            recommendation = "The requested NR mode is not effective on the active WDSP build; use NR2/EMNR or update the bundled WDSP NR4 exports before relying on newer weak-signal cleanup modes.";
        }
        else if (!appliedNrMatches || !appliedAgcMatches || !appliedSquelchMatches)
        {
            status = "apply-pending";
            recommendation = "The requested RX DSP state has not fully matched the applied engine latch yet; wait for the next state apply before evaluating signal quality.";
        }
        else if (weakSignalAssist && impulseControl && notchControl)
        {
            status = "full-cleanup-chain-active";
            recommendation = "NR/ANF/SNB, impulse blanking, and notch control are active; tune by watching scene SNR, RX headroom, AGC gain, and display ridge stability together.";
        }
        else if (weakSignalAssist)
        {
            status = "weak-signal-assist-active";
            recommendation = "Weak-signal DSP assistance is active; verify that Smart NR scene evidence improves coherent SNR without masking speech or CW edges.";
        }
        else if (impulseControl || notchControl)
        {
            status = "interference-cleanup-active";
            recommendation = "Interference cleanup is active without NR2/NR4; use this for pulse noise or carriers, and enable Smart NR only if the scene evidence shows weak coherent signal structure.";
        }
        else if (squelch.Enabled)
        {
            status = "squelch-gated";
            recommendation = "Squelch is gating RX audio; verify the threshold before using silence as evidence that no weak signal is present.";
        }
        else
        {
            status = "baseline";
            recommendation = "RX DSP cleanup is baseline; for weak signals, use Smart NR suggestions plus targeted ANF/manual notches/NB only when scene and ADC-headroom diagnostics support it.";
        }

        return new DspRxChainDiagnosticsDto(
            SchemaVersion: 1,
            Status: status,
            Mode: state.Mode.ToString(),
            FilterLowHz: state.FilterLowHz,
            FilterHighHz: state.FilterHighHz,
            FilterPresetName: state.FilterPresetName,
            AgcMode: agc.Mode.ToString(),
            AgcTopDb: Math.Round(state.AgcTopDb, 1),
            AutoAgcEnabled: state.AutoAgcEnabled,
            AgcOffsetDb: Math.Round(state.AgcOffsetDb, 1),
            EffectiveAgcTopDb: effectiveAgcTopDb,
            SquelchEnabled: squelch.Enabled,
            SquelchAdaptive: squelch.Adaptive,
            SquelchLevel: squelch.Level,
            RequestedNrMode: nrRuntime.RequestedNrMode,
            EffectiveNrMode: nrRuntime.EffectiveNrMode,
            AnfEnabled: nr.AnfEnabled,
            SnbEnabled: nr.SnbEnabled,
            NbpNotchesEnabled: nr.NbpNotchesEnabled,
            EffectiveNbpNotchesRun: effectiveNbpRun,
            NbMode: nr.NbMode.ToString(),
            NbThreshold: Math.Round(nr.NbThreshold, 1),
            ManualNotchCount: notchCount,
            ActiveManualNotchCount: activeNotchCount,
            WdspActive: nrRuntime.WdspActive,
            WdspNativeLoadable: nrRuntime.WdspNativeLoadable,
            WdspEmnrPost2Available: nrRuntime.WdspEmnrPost2Available,
            WdspNr4SbnrAvailable: nrRuntime.WdspNr4SbnrAvailable,
            Nr4Readiness: nrRuntime.Nr4Readiness,
            AppliedNrMatchesRequested: appliedNrMatches,
            AppliedAgcMatchesRequested: appliedAgcMatches,
            AppliedSquelchMatchesRequested: appliedSquelchMatches,
            ActiveFeatures: activeFeatures.ToArray(),
            QualityReasons: reasons.ToArray(),
            DiagnosticRecommendation: recommendation);
    }

    /// <summary>Raised after the engine instance is swapped (Synthetic ↔ WDSP).
    /// Subscribers receive the new <see cref="IDspEngine"/> (never null).</summary>
    public event Action<IDspEngine>? EngineChanged;

    private void RaiseEngineChanged(IDspEngine engine)
    {
        try { EngineChanged?.Invoke(engine); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "dsp.pipeline EngineChanged subscriber threw");
        }
    }

    /// <summary>Snapshot of the active Protocol2 client, or null on P1 / no
    /// connection. Exposed for the PS auto-attenuate service which needs to
    /// call <c>SetTxAttenuationDb</c> on the same client this pipeline is
    /// driving. Non-virtual — auto-attenuate is hard-gated on a P2 connection
    /// and tests don't exercise it.</summary>
    public Zeus.Protocol2.Protocol2Client? CurrentP2Client => _p2Client;

    /// <summary>
    /// Manually set the PS TX feedback attenuation (operator alternative to
    /// AutoAttenuate). Pushes the value to the connected radio — HL2 via the
    /// AD9866 TX-PGA step, every other board via the P2 step attenuator — then
    /// persists it per board and surfaces it in state via RadioService. This
    /// is what lets an operator on a fixed external-tap chain dial the
    /// feedback into calcc's range once and run with AutoAttenuate off.
    /// Clamped to the connected board's range.
    /// </summary>
    public void SetPsFeedbackAttenuationDb(int db)
    {
        if (_radio.ConnectedBoardKind == HpsdrBoardKind.HermesLite2)
        {
            int clamped = Math.Clamp(db, -28, 31);
            _radio.ActiveClient?.SetHl2TxStepAttenuationDb(clamped);
            _radio.SetPsTxAttenuationDb(clamped);
        }
        else
        {
            int clamped = Math.Clamp(db, 0, 31);
            _p2Client?.SetTxAttenuationDb((byte)clamped);
            _radio.SetPsTxAttenuationDb(clamped);
        }
    }

    private void OpenSynthetic()
    {
        var engine = new SyntheticDspEngine();
        int channelId = engine.OpenChannel(SyntheticSampleRateHz, Width);
        ApplyStateToNewChannel(engine, channelId);
        // iter5 pass-2: _engineLock serialises CONCURRENT WRITERS. Volatile.Write
        // is used so a lock-free sink-side Volatile.Read sees the new engine
        // pointer; the lock-release fence also publishes the writes, but
        // explicit Volatile.Write documents intent and survives any future
        // refactor that drops the outer lock.
        lock (_engineLock)
        {
            Volatile.Write(ref _engine, engine);
            Volatile.Write(ref _channelId, channelId);
            Volatile.Write(ref _rx2ChannelId, -1);
            Volatile.Write(ref _sampleRateHz, SyntheticSampleRateHz);
        }
        _log.LogInformation("dsp.pipeline engine=synthetic channel={Id}", channelId);
        RaiseEngineChanged(engine);
    }

    private void OnRadioConnected(IProtocol1Client client)
    {
        var state = _radio.Snapshot();
        int rate = state.SampleRate;

        var wdsp = new WdspDspEngine(_loggerFactory.CreateLogger<WdspDspEngine>());
        int channelId = wdsp.OpenChannel(rate, Width);
        // Seed the operator's persisted TX display config before TXA opens so
        // the analyzer comes up at their FFT/window/smoothing. Display-only.
        SeedTxDisplayConfig(wdsp);
        // P1 DAC runs at 48 kHz; keep TXA at the 48/48/48 profile Hermes is
        // calibrated against.
        wdsp.OpenTxChannel(outputRateHz: 48_000);
        ApplyStateToNewChannel(wdsp, channelId);

        IDspEngine? old;
        int oldChannel;
        lock (_engineLock)
        {
            old = _engine;
            oldChannel = _channelId;
            Volatile.Write(ref _engine, wdsp);
            Volatile.Write(ref _channelId, channelId);
            Volatile.Write(ref _rx2ChannelId, -1);
            Volatile.Write(ref _sampleRateHz, rate);
        }

        TeardownEngine(old, oldChannel);
        _log.LogInformation("dsp.pipeline engine=wdsp channel={Id} rate={Rate}", channelId, rate);
        RaiseEngineChanged(wdsp);

        // iter5: attach as the synchronous RX sink. Protocol1Client.RxLoop
        // calls OnIqFrame / OnPsFeedbackFrame directly on its OS thread —
        // no Channel<T> hop, no Task.Run pump, no _engineLock acquisition
        // on the hot path. The Tick is piggybacked on OnIqFrame via a
        // Stopwatch.GetTimestamp() check.
        AttachRxSinkP1(client);
        // Force the next OnRadioStateChanged to re-push every PS field into
        // the freshly-opened WdspDspEngine instance — same rationale as the
        // P2 reconnect path. Without this, a P1 reconnect leaves the engine
        // sitting at field defaults (hwPeak=0.4072) and calcc never sees
        // the operator's HL2 0.233 / hardware-correct numbers.
        _psResyncRequired = true;
        _appliedTxMonitorEnabled = false;
        // Apply the per-board PS HW peak default so the engine sees the
        // right curve scale before the operator arms PS. Mirrors P2's
        // ApplyPsHwPeakForConnection call. ConnectedBoardKind returns the
        // currently-active board (HL2, Hermes, ANAN-class…) — the value
        // is per-board (HL2 → 0.233, others → 0.4072) and only fires a
        // StateChanged when the value actually changes.
        _radio.ApplyPsHwPeakForConnection(isProtocol2: false, _radio.ConnectedBoardKind);
        // Restore the persisted PS feedback attenuation so a hot external-tap
        // chain isn't sitting at 0 dB on a fresh connect — at 0 dB the
        // feedback ADC rails and calcc can never fit. HL2 only on the P1 side
        // (it owns the AD9866 TX-PGA step attenuator). No-op when nothing was
        // saved for this board yet.
        if (_radio.ConnectedBoardKind == HpsdrBoardKind.HermesLite2
            && _radio.GetPersistedPsTxAttnDb() is int hl2Attn)
        {
            _radio.ActiveClient?.SetHl2TxStepAttenuationDb(hl2Attn);
        }
        // P1's Connected event is raised after RadioService already broadcast
        // Status=Connected, so the first state callback can hit the synthetic
        // engine we are replacing here. Replay the canonical state now so a
        // persisted PS arm actually reaches the freshly-opened WDSP engine even
        // when ApplyPsHwPeakForConnection did not move any StateDto fields.
        OnRadioStateChanged(_radio.Snapshot());
    }

    private void OnRadioDisconnected()
    {
        // iter5: detach the synchronous RX sink. Protocol1Client's RxLoop
        // thread is wound down by the protocol client itself (during
        // TearDownClientAsync) — we just clear the sink reference and let
        // the timer-driven Tick take over for synthetic-mode display.
        DetachRxSinkP1();

        var synth = new SyntheticDspEngine();
        int channelId = synth.OpenChannel(SyntheticSampleRateHz, Width);
        ApplyStateToNewChannel(synth, channelId);

        IDspEngine? old;
        int oldChannel;
        lock (_engineLock)
        {
            old = _engine;
            oldChannel = _channelId;
            Volatile.Write(ref _engine, synth);
            Volatile.Write(ref _channelId, channelId);
            Volatile.Write(ref _rx2ChannelId, -1);
            Volatile.Write(ref _sampleRateHz, SyntheticSampleRateHz);
        }

        TeardownEngine(old, oldChannel);
        _log.LogInformation("dsp.pipeline engine=synthetic channel={Id}", channelId);
        RaiseEngineChanged(synth);
    }

    private void OnRadioStateChanged(StateDto s)
    {
        lock (_engineLock)
        {
        // Forward VFO changes to the P2 client when it's active. RadioService
        // does this for P1 via ActiveClient?.SetVfoAHz() inside SetVfo, but
        // ActiveClient is null for P2 connections, so the radio never learns
        // about tune changes without this forward. Sample rate / mode follow
        // here too when P2-side support is added.
        //
        // Frozen-NCO model: the hardware always sits at RadioLoHz; dial
        // movements stay confined to the WDSP filter-shift path. Push
        // RadioLoHz to the P2 client (the P1 client gets the same push from
        // RadioService.SetRadioLo). See docs/prd/panfall_behavior.md.
        var p2 = _p2Client;
        p2?.SetVfoAHz(s.RadioLoHz);
        // RX2 (true second receiver): enable/disable its DDC and tune its NCO to
        // VFO B's effective LO so it demodulates its own band, independent of
        // RX1. SetRx2Enabled is idempotent (only re-sends on a real change);
        // SetVfoBHz no-ops on the wire while RX2 is disabled.
        p2?.SetRx2Enabled(s.Rx2Enabled);
        // Tune RX2's DDC to its (CTUN-frozen) centre, not the raw dial, so the
        // panel holds still while VFO B roams under CTUN. The WDSP shift in
        // ApplyStateToRx2Channel moves the dial within that window.
        UpdateRx2Lo(s);
        p2?.SetVfoBHz(_rx2LoHz);

        // Issue #597 Phase 0: arm the RX display fast-attack when the LO
        // moves. First callback after construction only records the LO
        // (sentinel) so connect itself doesn't trigger a pointless arm.
        if (_fastAttackLastLoHz == long.MinValue)
        {
            _fastAttackLastLoHz = s.RadioLoHz;
        }
        else if (s.RadioLoHz != _fastAttackLastLoHz)
        {
            _fastAttackLastLoHz = s.RadioLoHz;
            long nowTicks = Stopwatch.GetTimestamp();
            // Issue #597 Phase 2: the LO history feeding the delay-compensated
            // CenterHz stamp. O(1) append, LO changes only.
            _loHistory.Append(nowTicks, s.RadioLoHz);
            Interlocked.Exchange(ref _fastAttackLoChangedAt, nowTicks);
            if (!_keyed && !_fastAttackActive)
            {
                _engine?.SetRxDisplayFastAttack(_channelId, fast: true);
                _fastAttackActive = true;
            }
        }

        var engine = _engine;
        int channel = _channelId;
        if (engine is null) return;
        int rx2Channel = EnsureRx2Channel(engine, s);

        if (s.Mode != _appliedMode)
        {
            engine.SetMode(channel, s.Mode);
            // Keep TXA modulator mode in sync with the RX side. On Synthetic
            // and before OpenTxChannel has run this is a no-op.
            engine.SetTxMode(s.Mode);
            _appliedMode = s.Mode;
        }
        if (s.FilterLowHz != _appliedLowHz || s.FilterHighHz != _appliedHighHz)
        {
            engine.SetFilter(channel, s.FilterLowHz, s.FilterHighHz);
            _appliedLowHz = s.FilterLowHz;
            _appliedHighHz = s.FilterHighHz;
        }
        // Frozen-NCO frequency shift. The dial sits off-centre on the WDSP
        // IF (the radio's NCO is frozen at RadioLoHz); WDSP's `shift` stage
        // moves the IF by shiftHz before demodulation so the unmodified
        // bandpass filter sees the tuned signal at baseband. This is the
        // seam Thetis uses (radio.cs:1419-1420); shifting SetRXABandpassFreqs
        // directly broke SSB demod because the nbp0 stage rejects
        // sign-inverted ranges. See docs/prd/panfall_behavior.md.
        int ctunShiftHz = (int)(CwOffset.EffectiveLoHz(s.Mode, s.VfoHz) - s.RadioLoHz);
        if (ctunShiftHz != _appliedCtunOffsetHz)
        {
            engine.SetCtunShift(channel, ctunShiftHz);
            _appliedCtunOffsetHz = ctunShiftHz;
        }
        // Keep WDSP's manual-notch database positioned against the live LO so
        // notches hold their absolute RF frequency across a retune. The engine
        // no-ops when the value is unchanged, so this is cheap to call here.
        engine.SetNotchTuneFrequencyHz(s.RadioLoHz);
        // Re-sign the TX bandpass from the LIVE mode instead of trusting the
        // sign stored in StateDto. WDSP picks the SSB sideband from the sign of
        // the bandpass edges; a state that comes up with Mode=LSB but a positive
        // TX filter (legacy prefs DB, or a writer that set the mode without
        // re-signing the TX width) would otherwise transmit USB. This is
        // idempotent for well-formed state, so it never fights an operator edit.
        var (txLow, txHigh) = SignedTxFilterFor(s);
        if (txLow != _appliedTxLowHz || txHigh != _appliedTxHighHz)
        {
            engine.SetTxFilter(txLow, txHigh);
            _appliedTxLowHz = txLow;
            _appliedTxHighHz = txHigh;
        }
        if (s.AgcTopDb != _appliedAgcTopDb || s.AgcOffsetDb != _appliedAgcOffsetDb)
        {
            double effectiveAgc = s.AgcTopDb + s.AgcOffsetDb;
            engine.SetAgcTop(channel, effectiveAgc);
            if (rx2Channel >= 0) engine.SetAgcTop(rx2Channel, effectiveAgc);
            _appliedAgcTopDb = s.AgcTopDb;
            _appliedAgcOffsetDb = s.AgcOffsetDb;
        }
        // (Removed: the manual AGC "knee" push. WDSP's threshold and AGC-T are
        // the SAME register (max_gain) — driving both independently clobbered
        // each other and made AGC-T hair-trigger. AGC-T is now the single
        // manual control via SetRXAAGCTop above; Auto-AGC tracks the noise floor
        // on top of it. See the AGC knee removal commit.)
        if (s.RxAfGainDb != _appliedRxAfGainDb)
        {
            engine.SetRxAfGainDb(channel, s.RxAfGainDb);
            _appliedRxAfGainDb = s.RxAfGainDb;
        }
        // TX mic gain: dB → linear (10^(db/20)) at the engine seam. Conversion
        // matches the historical /api/mic-gain inline (Math.Pow(10.0, db/20.0));
        // moved here so the operator-friendly dB is what gets stored and broadcast.
        double micLinear = Math.Pow(10.0, s.MicGainDb / 20.0);
        if (micLinear != _appliedTxMicGainLinear)
        {
            engine.SetTxPanelGain(micLinear);
            _appliedTxMicGainLinear = micLinear;
        }
        if (s.LevelerMaxGainDb != _appliedTxLevelerMaxGainDb)
        {
            engine.SetTxLevelerMaxGain(s.LevelerMaxGainDb);
            _appliedTxLevelerMaxGainDb = s.LevelerMaxGainDb;
        }
        var nr = NormalizeNrConfig(s.Nr ?? new NrConfig());
        if (!nr.Equals(_appliedNr))
        {
            engine.SetNoiseReduction(channel, nr);
            if (rx2Channel >= 0) engine.SetNoiseReduction(rx2Channel, nr);
            _appliedNr = nr;
        }
        var agc = s.Agc ?? new AgcConfig(AgcMode.Med);
        if (!agc.Equals(_appliedAgc))
        {
            engine.SetAgc(channel, agc);
            if (rx2Channel >= 0) engine.SetAgc(rx2Channel, agc);
            _appliedAgc = agc;
        }
        var squelch = s.Squelch ?? new SquelchConfig();
        if (!squelch.Equals(_appliedSquelch))
        {
            engine.SetSquelch(channel, squelch);
            if (rx2Channel >= 0) engine.SetSquelch(rx2Channel, squelch);
            _appliedSquelch = squelch;
        }
        var txLeveling = s.TxLeveling ?? new TxLevelingConfig();
        if (!txLeveling.Equals(_appliedTxLeveling))
        {
            engine.SetTxLeveling(channel, txLeveling);
            _appliedTxLeveling = txLeveling;
        }
        if (s.ZoomLevel != _appliedZoomLevel)
        {
            engine.SetZoom(channel, s.ZoomLevel);
            if (rx2Channel >= 0) engine.SetZoom(rx2Channel, s.ZoomLevel);
            _appliedZoomLevel = s.ZoomLevel;
        }

        // ---- TwoTone (protocol-agnostic; PostGen mode=1 inside TXA) ----
        // TwoTone is safe on P1 even though PS itself is P2-only in v1
        // because it touches only the TXA stage, not the wire format.
        if (s.TwoToneEnabled != _appliedTwoToneEnabled
            || s.TwoToneFreq1 != _appliedTwoToneFreq1
            || s.TwoToneFreq2 != _appliedTwoToneFreq2
            || s.TwoToneMag != _appliedTwoToneMag)
        {
            engine.SetTwoTone(s.TwoToneEnabled, s.TwoToneFreq1, s.TwoToneFreq2, s.TwoToneMag);
            _appliedTwoToneEnabled = s.TwoToneEnabled;
            _appliedTwoToneFreq1 = s.TwoToneFreq1;
            _appliedTwoToneFreq2 = s.TwoToneFreq2;
            _appliedTwoToneMag = s.TwoToneMag;
        }

        // ---- PureSignal ----
        // Apply HW-peak first because SetPsAdvanced may also touch it; then
        // advanced timing/preset; then control mode; then master arm last so
        // the engine is fully configured before the cal state machine starts.
        // _psResyncRequired (set by DisconnectP2Async) forces every push on
        // the first state-change after a P2 reconnect so the new engine
        // instance picks up the canonical state instead of running on its
        // field defaults.
        bool resync = _psResyncRequired;
        // All three blocks below issue WDSP calls that perturb calcc state —
        // SetPSHWPeak rewrites hw_scale and forces an internal re-bin;
        // SetPsAdvanced/SetPsControl issue SetPSControl(reset=1, ...) which
        // flips the calcc state machine back through LRESET, truncating any
        // in-flight polynomial fit. Doing any of that mid-MOX is the
        // sporadic-splatter trigger: any unrelated Mutate() during a live
        // key-down (e.g. RX ADC overload nudging _attOffsetDb at 10 Hz, S-meter
        // retracking, panadapter zoom, operator UI nudge) would otherwise
        // reset PS and bloom IMD3 sidebands for 50-500 ms until calcc
        // walked back to LSTAYON. Thetis avoids this by construction —
        // PSForm only issues SetPSControl from explicit state-machine
        // transitions, never from a generic dispatcher.
        //
        // While _keyed is true (MOX or TUN), defer the apply; OnRadioMoxChanged
        // re-invokes OnRadioStateChanged on the falling edge to pick up
        // anything that was deferred during the key-down. SetPsEnabled
        // (arm/disarm) is intentionally NOT guarded — the operator must
        // be able to disable PS mid-TX to stop a splatter event.
        var psApplyDeferred = _keyed;
        if (!psApplyDeferred && (resync || s.PsHwPeak != _appliedPsHwPeak))
        {
            engine.SetPsHwPeak(s.PsHwPeak);
            _appliedPsHwPeak = s.PsHwPeak;
        }
        if (!psApplyDeferred && (resync
            || s.PsPtol != _appliedPsPtol
            || s.PsMoxDelaySec != _appliedPsMoxDelaySec
            || s.PsLoopDelaySec != _appliedPsLoopDelaySec
            || s.PsAmpDelayNs != _appliedPsAmpDelayNs
            || s.PsIntsSpiPreset != _appliedPsIntsSpiPreset))
        {
            (int ints, int spi) = ParseIntsSpi(s.PsIntsSpiPreset);
            engine.SetPsAdvanced(
                s.PsPtol,
                s.PsMoxDelaySec,
                s.PsLoopDelaySec,
                s.PsAmpDelayNs,
                s.PsHwPeak,
                ints,
                spi);
            _appliedPsPtol = s.PsPtol;
            _appliedPsMoxDelaySec = s.PsMoxDelaySec;
            _appliedPsLoopDelaySec = s.PsLoopDelaySec;
            _appliedPsAmpDelayNs = s.PsAmpDelayNs;
            _appliedPsIntsSpiPreset = s.PsIntsSpiPreset;
        }
        if (!psApplyDeferred && (resync || s.PsAuto != _appliedPsAuto || s.PsSingle != _appliedPsSingle))
        {
            engine.SetPsControl(s.PsAuto, s.PsSingle);
            _appliedPsAuto = s.PsAuto;
            _appliedPsSingle = s.PsSingle;
        }
        if (resync || s.PsEnabled != _appliedPsEnabled)
        {
            // pihpsdr transmitter.c:2467-2473 inverts the order: write the
            // wire (RxSpec / HighPriority with PS bits set) FIRST, then sleep
            // 100 ms to let the radio firmware spin up DDC0/DDC1 sync, then
            // arm the engine. Without the settle window, the first 5-20
            // pscc calls receive partial / glitched samples, scheck flags
            // binfo[6], bs_count climbs to 2, calcc resets to LRESET — and
            // the loop sometimes thrashes instead of converging.
            //
            // Disarm path stays engine-first: drop the engine run flag, then
            // close the wire, then drain any in-flight paired frames so they
            // don't arrive after PS has shut down.
            //
            // Task.Delay(100).Wait() is acceptable here — OnRadioStateChanged
            // runs on a state-change handler thread, not the request path.
            //
            // P1 sibling (issue #172): the active P1 client gets the same
            // arm/disarm sequencing — flip the wire bit (which also
            // bumps NumReceiversMinusOne in the next Config frame so the
            // gateware switches to the 2-DDC paired layout), wait the
            // same 100 ms settle window, then arm the engine. On a
            // non-HL2 P1 board this is harmless: SetPsEnabled stores the
            // flag locally and the C0=0x14 wire byte is unaffected
            // (board-gated in WriteAttenuatorPayload).
            var p1Active = _radio.ActiveClient;
            if (s.PsEnabled && _keyed)
            {
                // Defer the ARM while transmitting. Arming mid-MOX fires
                // calcc's SetPSControl(reset) into a live fit, which races the
                // feedback stream and wedges calcc in LCALC on a stale curve
                // (the mid-TX arm/disarm wedge — frozen info5, cor=1 but never
                // updating → splatter). Re-arming mid-over isn't a real need;
                // leaving _appliedPsEnabled stale here makes the
                // OnRadioMoxChanged falling-edge re-apply arm it cleanly on
                // key-up. Disarm (abort) below stays immediate.
            }
            else if (s.PsEnabled)
            {
                _p2Client?.SetPsFeedbackEnabled(true);
                p1Active?.SetPsEnabled(true);
                // PS engine arm requires a feedback path that delivers paired
                // samples. On P2 ANAN-class that's SetPsFeedbackEnabled above.
                // On P1, only HermesLite2 delivers the 2-DDC paired layout
                // PS needs — Protocol1Client.cs:643 (NumReceiversMinusOne
                // wire bump) and :1004 (4-DDC parser path) are both HL2-gated.
                // On a non-HL2 P1 board WDSP arms with no possible feedback
                // source, sits in COLLECT waiting on paired samples that
                // never arrive, and the blocking 100 ms settle below stacks
                // on the state-change thread — together that freezes RX
                // audio + waterfall (GH #426). Skip the engine arm in that
                // case; the wire calls above are no-ops on non-HL2 P1
                // (board-gated in WriteAttenuatorPayload + SnapshotState).
                bool p1Connected = p1Active is not null;
                bool psEngineSupported = !p1Connected
                    || _radio.ConnectedBoardKind == HpsdrBoardKind.HermesLite2;
                if (psEngineSupported)
                {
                    try { Task.Delay(100).Wait(); } catch { /* ignore */ }
                    engine.SetPsEnabled(true);
                }
            }
            else
            {
                engine.SetPsEnabled(false);
                _p2Client?.SetPsFeedbackEnabled(false);
                p1Active?.SetPsEnabled(false);
                DrainPsFeedback();
            }
            // Mark applied only when we actually armed or disarmed. A deferred
            // (keyed) arm leaves _appliedPsEnabled stale on purpose so the
            // MOX-off re-apply re-enters this block and arms.
            if (!(s.PsEnabled && _keyed))
                _appliedPsEnabled = s.PsEnabled;
        }
        if (resync || s.PsFeedbackSource != _appliedPsFeedbackSource)
        {
            // Wire-only change — flips ALEX_RX_ANTENNA_BYPASS in alex0 on
            // the next CmdHighPriority emission. WDSP is unaffected.
            _p2Client?.SetPsFeedbackSource(s.PsFeedbackSource == PsFeedbackSource.External);
            _appliedPsFeedbackSource = s.PsFeedbackSource;
        }

        // ---- CFC (Continuous Frequency Compressor) ---------------------
        // issue #123. Same resync rule as PS: a P2 disconnect tears down the
        // engine, so the next state-change push has to re-assert the operator
        // CFC config even when the StateDto value hasn't changed. Equality
        // check uses CfcConfig record value semantics (the Bands array length
        // is fixed at 10, contents compared element-wise via the auto-record
        // Equals — but `record` only does reference equality on arrays, so
        // value-compare manually). null on the wire (legacy state frame)
        // falls back to CfcConfig.Default → engine sees a clean OFF profile.
        var cfc = s.Cfc ?? CfcConfig.Default;
        if (resync || !CfcConfigsEqual(cfc, _appliedCfc))
        {
            engine.SetCfcConfig(cfc);
            _appliedCfc = cfc;
        }

        // ---- RX step attenuator (operator + auto-ATT offset) -----------
        // Issue #126. Mirror RadioService's effective-atten composition
        // (operator baseline AttenDb + auto-ATT overload offset AttOffsetDb,
        // clamped 0..31) onto a live Protocol2Client. RadioService already
        // pushes the same value to the P1 client directly via
        // ActiveClient?.SetAttenuator on every operator change AND every
        // auto-ATT tick — but on a P2 connection ActiveClient is null, so
        // without this forward the S-ATT slider and the auto-ATT overload
        // ramp both fail silently on Angelia / ANAN-100D. RadioService
        // raises StateChanged whenever AttOffsetDb moves, so the auto-ATT
        // control loop reaches the wire through this block too.
        int effectiveAttDb = Math.Clamp(s.AttenDb + s.AttOffsetDb, 0, 31);
        if (resync || effectiveAttDb != _appliedEffectiveAttDb)
        {
            _p2Client?.SetAttenuator(effectiveAttDb);
            _appliedEffectiveAttDb = effectiveAttDb;
        }

        // PS-Monitor (issue #121) — pure UI source routing. No engine call,
        // no wire write; Tick reads _psMonitorEnabled and prefers the
        // PS-feedback analyzer when on + PS armed + correcting. Latched
        // here so the volatile read in Tick stays cheap.
        if (_psMonitorEnabled != s.PsMonitorEnabled)
        {
            _log.LogInformation("psMonitor.latch enabled={Enabled}", s.PsMonitorEnabled);
            _psMonitorEnabled = s.PsMonitorEnabled;
        }

        // TX Monitor (issue #106 follow-up) — engages the engine's parallel
        // demod path on the post-CFIR TX IQ. Edge-triggered call to the
        // engine so a re-tick with the same flag is a no-op. The engine
        // tolerates being called before TXA is open (lazy-open inside) so
        // ordering vs SetTxMode/SetTxFilter above doesn't matter.
        if (_appliedTxMonitorEnabled != s.TxMonitorEnabled)
        {
            _log.LogInformation("txMonitor.latch enabled={Enabled}", s.TxMonitorEnabled);
            engine.SetTxMonitorEnabled(s.TxMonitorEnabled);
            _appliedTxMonitorEnabled = s.TxMonitorEnabled;
            // Meter-only is a per-monitor-session flag — drop it whenever the
            // monitor turns off so a later audible Preview is never silenced by
            // a stale Auto Tune sample that ended abnormally.
            if (!s.TxMonitorEnabled)
                _txMonitorMeterOnly = false;
        }

        // Resync done — clear the flag so subsequent state changes use
        // normal change-detect (no spurious wire writes on each tick).
        _psResyncRequired = false;
        }
    }

    // CfcConfig auto-generated record Equals does reference equality on the
    // Bands array, which would always trigger a re-push on every tick where
    // the panel rebuilt the array. Explicit element-wise compare so a no-op
    // POST round-trip stays cheap.
    private static bool CfcConfigsEqual(CfcConfig a, CfcConfig b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Enabled != b.Enabled) return false;
        if (a.PostEqEnabled != b.PostEqEnabled) return false;
        if (a.PreCompDb != b.PreCompDb) return false;
        if (a.PrePeqDb != b.PrePeqDb) return false;
        if (a.Bands is null || b.Bands is null) return ReferenceEquals(a.Bands, b.Bands);
        if (a.Bands.Length != b.Bands.Length) return false;
        for (int i = 0; i < a.Bands.Length; i++)
        {
            if (a.Bands[i].FreqHz != b.Bands[i].FreqHz) return false;
            if (a.Bands[i].CompLevelDb != b.Bands[i].CompLevelDb) return false;
            if (a.Bands[i].PostGainDb != b.Bands[i].PostGainDb) return false;
        }
        return true;
    }

    // "16/256" → (16, 256). Falls back to (16, 256) on any parse failure
    // because that's the only ints/spi pair WDSP allows save/restore on
    // (Thetis PSForm.cs:865) — a safe default.
    private static (int Ints, int Spi) ParseIntsSpi(string preset)
    {
        if (string.IsNullOrWhiteSpace(preset)) return (16, 256);
        var slash = preset.IndexOf('/');
        if (slash <= 0) return (16, 256);
        if (!int.TryParse(preset.AsSpan(0, slash), out int ints)) return (16, 256);
        if (!int.TryParse(preset.AsSpan(slash + 1), out int spi)) return (16, 256);
        if (ints <= 0 || spi <= 0) return (16, 256);
        return (ints, spi);
    }

    private void ApplyStateToNewChannel(IDspEngine engine, int channelId)
    {
        _rxAudioLeveler = default;
        _adaptiveSquelch = new AdaptiveSquelchState();
        var s = _radio.Snapshot();
        var nr = NormalizeNrConfig(s.Nr ?? new NrConfig());
        var agc = s.Agc ?? new AgcConfig(AgcMode.Med);
        var squelch = s.Squelch ?? new SquelchConfig();
        var txLeveling = s.TxLeveling ?? new TxLevelingConfig();
        engine.SetMode(channelId, s.Mode);
        // Sync TXA modulator with RX mode at engine-open time so the first
        // key-down lands with the correct sideband (no-op on Synthetic / pre-
        // OpenTxChannel).
        engine.SetTxMode(s.Mode);
        engine.SetFilter(channelId, s.FilterLowHz, s.FilterHighHz);
        // Sign the TX bandpass from the live mode (see SignedTxFilterFor) so a
        // fresh engine doesn't key up with a USB-positive default while in LSB.
        var (txOpenLow, txOpenHigh) = SignedTxFilterFor(s);
        engine.SetTxFilter(txOpenLow, txOpenHigh);
        engine.SetVfoHz(channelId, s.VfoHz);
        // Replay the WDSP shift on fresh-channel open so a connect landing
        // with VfoHz != RadioLoHz (persisted across restart) is demodulating
        // the same dial the operator saw last session.
        // See docs/prd/panfall_behavior.md.
        int ctunShiftHz = (int)(CwOffset.EffectiveLoHz(s.Mode, s.VfoHz) - s.RadioLoHz);
        engine.SetCtunShift(channelId, ctunShiftHz);
        double effectiveAgc = s.AgcTopDb + s.AgcOffsetDb;
        engine.SetAgcTop(channelId, effectiveAgc);
        engine.SetRxAfGainDb(channelId, s.RxAfGainDb);
        // Re-push TX mic gain + Leveler on every fresh engine so the channel
        // doesn't sit at the WDSP open-time defaults when the operator's last
        // values differ. The engine's TXA reopen path resets PanelGain1=1.0 and
        // LevelerTop=8.0 internally; without this re-push, a relaunch would
        // ignore the just-hydrated StateDto values.
        double micLinearInit = Math.Pow(10.0, s.MicGainDb / 20.0);
        engine.SetTxPanelGain(micLinearInit);
        engine.SetTxLevelerMaxGain(s.LevelerMaxGainDb);
        engine.SetNoiseReduction(channelId, nr);
        // Force-apply AGC mode/custom params on a fresh engine so the operator's
        // persisted choice survives a reconnect. The engine's channel-open path
        // installs the Med default (ApplyAgcDefaults); this overrides it with the
        // hydrated config. The max-gain (top) is pushed separately above.
        engine.SetAgc(channelId, agc);
        // Force-apply squelch on a fresh engine so the operator's persisted
        // choice survives a reconnect. Mode is already set above so the engine
        // routes run/threshold to the correct stage (SSQL/AMSQ/FMSQ).
        engine.SetSquelch(channelId, squelch);
        // Force-apply TX leveling on a fresh engine so the operator's persisted
        // ALC/Leveler/Compressor config survives a reconnect. The TXA-open path
        // installs the TxLevelingConfig defaults; this overrides with the
        // hydrated config (and re-arms the engine's _txLevelerEnabled so the
        // TUN/two-tone Leveler restore honours the operator's on/off). The
        // Leveler max-gain is pushed separately above (SetTxLevelerMaxGain).
        engine.SetTxLeveling(channelId, txLeveling);
        // Manual notches: feed the LO first (notch positioning reference), then
        // re-apply the operator's notch set onto the fresh engine. A reconnect
        // builds a brand-new engine whose notch DB is empty; RadioService holds
        // the authoritative list so EMF notches survive the reconnect.
        engine.SetNotchTuneFrequencyHz(s.RadioLoHz);
        engine.SetNotches(_radio.Notches);
        engine.SetZoom(channelId, s.ZoomLevel);
        _appliedMode = s.Mode;
        _appliedLowHz = s.FilterLowHz;
        _appliedHighHz = s.FilterHighHz;
        _appliedCtunOffsetHz = ctunShiftHz;
        _appliedTxLowHz = txOpenLow;
        _appliedTxHighHz = txOpenHigh;
        _appliedAgcTopDb = s.AgcTopDb;
        _appliedAgcOffsetDb = s.AgcOffsetDb;
        _appliedRxAfGainDb = s.RxAfGainDb;
        _appliedTxMicGainLinear = micLinearInit;
        _appliedTxLevelerMaxGainDb = s.LevelerMaxGainDb;
        _appliedNr = nr;
        _appliedAgc = agc;
        _appliedSquelch = squelch;
        _appliedTxLeveling = txLeveling;
        _appliedZoomLevel = s.ZoomLevel;
    }

    private int EnsureRx2Channel(IDspEngine engine, StateDto s)
    {
        int rx2Channel = Volatile.Read(ref _rx2ChannelId);
        if (!s.Rx2Enabled)
        {
            CloseRx2Channel(engine, rx2Channel);
            return -1;
        }

        if (rx2Channel >= 0)
        {
            ApplyStateToRx2Channel(engine, rx2Channel, s);
            return rx2Channel;
        }

        int rateHz = Volatile.Read(ref _sampleRateHz);
        if (rateHz <= 0) rateHz = s.SampleRate > 0 ? s.SampleRate : SyntheticSampleRateHz;
        int opened = engine.OpenChannel(rateHz, Width);
        try
        {
            ApplyStateToRx2Channel(engine, opened, s);
            Volatile.Write(ref _rx2ChannelId, opened);
            _log.LogInformation(
                "dsp.pipeline rx2 opened channel={Channel} rate={Rate} vfoBHz={VfoBHz}",
                opened,
                rateHz,
                s.VfoBHz);
            return opened;
        }
        catch
        {
            try { engine.CloseChannel(opened); } catch { /* best-effort */ }
            throw;
        }
    }

    private void CloseRx2Channel(IDspEngine engine, int rx2Channel)
    {
        if (rx2Channel < 0) return;
        Volatile.Write(ref _rx2ChannelId, -1);
        try { engine.CloseChannel(rx2Channel); }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "dsp.pipeline rx2 close failed channel={Channel}", rx2Channel);
        }
        _log.LogInformation("dsp.pipeline rx2 closed channel={Channel}", rx2Channel);
    }

    /// <summary>
    /// Recompute RX2's DDC centre (<see cref="_rx2LoHz"/>) for the current state,
    /// mirroring RX1's RadioLoHz/CTUN model. CTUN off → follow VFO B (recentre);
    /// CTUN on → freeze, so the dial roams within the window — unless the dial
    /// would leave the captured DDC bandwidth, in which case recentre (the
    /// stand-in for RX1's band-button SetRadioLo, which RX2 has no UI for).
    /// Idempotent and deterministic, so it can run from several tick paths.
    /// </summary>
    private void UpdateRx2Lo(StateDto s)
    {
        if (!s.Rx2Enabled)
        {
            _rx2LoInit = false; // re-enabling recentres from scratch
            return;
        }
        long effB = CwOffset.EffectiveLoHz(s.ModeB, s.VfoBHz);
        long span = Volatile.Read(ref _sampleRateHz);
        if (span <= 0) span = s.SampleRate > 0 ? s.SampleRate : SyntheticSampleRateHz;
        long edge = (long)(span * 0.45); // recentre before the dial hits the DDC edge
        if (!_rx2LoInit || !s.CtunEnabled || Math.Abs(effB - _rx2LoHz) > edge)
        {
            _rx2LoHz = effB;
        }
        _rx2LoInit = true;
    }

    internal static int ComputeRx2CtunShiftHz(
        StateDto s,
        long rx2LoHz,
        bool protocol2)
    {
        long effectiveVfoBHz = CwOffset.EffectiveLoHz(s.ModeB, s.VfoBHz);
        return protocol2
            ? (int)(effectiveVfoBHz - rx2LoHz)
            : (int)(effectiveVfoBHz - s.RadioLoHz);
    }

    private void ApplyStateToRx2Channel(IDspEngine engine, int channelId, StateDto s)
    {
        var nr = NormalizeNrConfig(s.Nr ?? new NrConfig());
        var agc = s.Agc ?? new AgcConfig(AgcMode.Med);
        var squelch = s.Squelch ?? new SquelchConfig();
        engine.SetMode(channelId, s.ModeB);
        engine.SetFilter(channelId, s.FilterLowHzB, s.FilterHighHzB);
        engine.SetVfoHz(channelId, s.VfoBHz);
        UpdateRx2Lo(s);
        // P2 true-DDC: RX2's hardware DDC sits at _rx2LoHz, so the WDSP shift
        // roams the dial within that window — EffectiveLoHz(VfoB) − _rx2LoHz.
        // Under CTUN off, _rx2LoHz == EffectiveLoHz(VfoB) so the shift is 0 and
        // the panel recentres on the dial. P1 / synthetic RX2 is a sub-receiver
        // of RX1's window, so it still shifts against RadioLoHz.
        int shiftHz = ComputeRx2CtunShiftHz(
            s,
            _rx2LoHz,
            protocol2: _p2Client is not null);
        engine.SetCtunShift(channelId, shiftHz);
        engine.SetAgcTop(channelId, s.AgcTopDb + s.AgcOffsetDb);
        engine.SetRxAfGainDb(channelId, s.Rx2AfGainDb);
        engine.SetNoiseReduction(channelId, nr);
        engine.SetAgc(channelId, agc);
        engine.SetSquelch(channelId, squelch);
        engine.SetZoom(channelId, s.ZoomLevel);
    }

    // iter5 (task #4): the four channel pumps that used to live here
    //   - StartIqPump            (P1 IQ → engine.FeedIq)
    //   - StartIqPumpP2          (P2 IQ → engine.FeedIq)
    //   - StartPsFeedbackPumpP1  (P1 PS paired blocks → engine.FeedPsFeedbackBlock)
    //   - StartPsFeedbackPumpP2  (P2 PS paired blocks → engine.FeedPsFeedbackBlock)
    // ...have been replaced by the synchronous IRxPacketSink path. Each
    // pump did one `await Channel.WaitToReadAsync` + drain + `lock(_engineLock)`
    // per packet — burning ~52% of busy CPU on swtch_pri /
    // ThreadNative_SpinWait by perf3 iter4 sampling. Their work now happens
    // INLINE on Protocol1Client / Protocol2Client's RxLoop thread via
    // OnIqFrame / OnPsFeedbackFrame above. The ArrayPool return for P1 IQ
    // happens in the OnIqFrame finally block (same contract).

    // Best-effort drain of any in-flight paired frames after PS disarm.
    // Called synchronously from OnRadioStateChanged so the channel is empty
    // by the next re-arm. Iter5: with the sink path live, the protocol
    // clients invoke OnPsFeedbackFrame INSTEAD of writing the channel, so
    // the channels here are normally empty already — this function is a
    // near-no-op (one TryRead returning false) but stays as defensive
    // belt-and-suspenders for the rare case where a sink swap is in
    // flight or a non-sink consumer (test, probe) is in use.
    // Drains either active client (P1 or P2 — only one is non-null at a time).
    private void DrainPsFeedback()
    {
        var p2 = _p2Client;
        if (p2 is not null)
        {
            var reader = p2.PsFeedbackFrames;
            while (reader.TryRead(out _)) { }
            return;
        }
        var p1 = _radio.ActiveClient;
        if (p1 is not null)
        {
            var reader = p1.PsFeedbackFrames;
            while (reader.TryRead(out _)) { }
        }
    }

    /// <summary>
    /// Connect to a Protocol 2 radio and start streaming RX IQ into the DSP
    /// engine. Parallel path to RadioService.ConnectAsync (which is Protocol 1
    /// only); both swap the engine to WDSP and attach this pipeline as the
    /// synchronous RX sink on the client (iter5 — no more Task.Run pumps).
    /// Only one client at a time.
    /// </summary>
    public async Task<int> ConnectP2Async(
        IPEndPoint radioEndpoint,
        int sampleRateKhz,
        byte numAdc,
        CancellationToken ct,
        HpsdrBoardKind boardKind = HpsdrBoardKind.Unknown)
    {
        if (_p2Client is not null)
            throw new InvalidOperationException("Already connected (P2).");
        if (_radio.ActiveClient is not null)
            throw new InvalidOperationException("Already connected (P1). Disconnect first.");

        var client = new Zeus.Protocol2.Protocol2Client(
            _loggerFactory.CreateLogger<Zeus.Protocol2.Protocol2Client>());
        client.SetNumAdc(numAdc);
        // Tell the P2 client which board it's talking to so RX-decode quirks
        // (Hermes-on-P2 48 kHz IQ gain correction; future per-board branches)
        // are gated correctly. boardKind == Unknown leaves all quirks off.
        client.SetBoardKind(boardKind);
        // 0x0A wire-byte alias variant (issue #218). For non-OrionMkII
        // boards the value is ignored; for OrionMkII it picks the right
        // calibration/PA constants AND unlocks the Anvelina-PRO3 DX OC
        // byte-1397 write (issue #407) when the operator has selected
        // AnvelinaPro3 in the radio chooser.
        client.SetOrionMkIIVariant(_radio.EffectiveOrionMkIIVariant);
        await client.ConnectAsync(radioEndpoint, ct).ConfigureAwait(false);
        // Seed the operator's RX front-end (preamp + step attenuator) BEFORE
        // StartAsync so the very first CmdHighPriority emitted inside the
        // start sequence carries the correct values. SetPreamp/SetAttenuator
        // pre-StartAsync only stash into private fields (the early-return on
        // _rxTask==null path), so no wire packets fly here — they ride the
        // CmdHighPriority(run=1) inside StartAsync below. Without this seed
        // a P2 reconnect would leave the radio at preamp=off / atten=0
        // until the operator nudged either control. Issue #126.
        bool initialPreamp = _radio.PreampOn;
        int initialAttDb = _radio.EffectiveAttenDb;
        client.SetPreamp(initialPreamp);
        client.SetAttenuator(initialAttDb);
        // Frequency-correction factor (issue #325) — rehydrate before the
        // first CmdHighPriority(run=1) so the operator's calibration applies
        // to the very first NCO phase-word. 1.0 = factory default, no-op.
        client.SetFrequencyCorrectionFactor(_radio.GetFrequencyCorrectionFactor());
        // ANAN-G2/Saturn ADC dither/random options live in CmdRx bytes 5/6.
        // Seed before StartAsync so the first receive-specific command
        // matches the persisted setting; RadioService also replays after
        // MarkProtocol2Connected and on live setting changes.
        _radio.ApplyG2AdcOptionsToP2Client(client, boardKind);

        int rateHz = _radio.ResolveConnectSampleRateHz(
            boardKind,
            sampleRateKhz * 1000,
            protocol2: true);
        sampleRateKhz = rateHz / 1000;
        await client.StartAsync(sampleRateKhz, ct).ConfigureAwait(false);

        IDspEngine newEngine;
        int newChannelId;
        try
        {
            var wdsp = new WdspDspEngine(_loggerFactory.CreateLogger<WdspDspEngine>());
            newChannelId = wdsp.OpenChannel(rateHz, Width);
            // Seed the operator's persisted TX display config before TXA opens
            // so the analyzer comes up at their FFT/window/smoothing. Display-only.
            SeedTxDisplayConfig(wdsp);
            // G2 MkII DUC on P2 expects 192 kHz TX IQ. WDSP upsamples internally
            // (48k mic → 96k DSP → 192k out) and CFIR compensates the sinc
            // droop. Feeding 48 kHz IQ to a 192 kHz DUC as we did before
            // produced 8-10 kHz close-in spurs around the carrier.
            wdsp.OpenTxChannel(outputRateHz: 192_000);
            // Best-effort apply. Some local WDSP builds are missing newer
            // entry points (e.g. SetRXAEMNRpost2Run); the channel itself is
            // open and capable of spectrum work even if a noise-reduction
            // toggle can't be set. Narrow catch so a genuinely broken engine
            // still surfaces via the outer handler.
            try { ApplyStateToNewChannel(wdsp, newChannelId); }
            catch (EntryPointNotFoundException ex)
            {
                _log.LogWarning(ex, "dsp.pipeline p2 wdsp missing entry point — partial config applied");
            }
            newEngine = wdsp;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "dsp.pipeline p2 wdsp open failed, falling back to synthetic engine");
            var synth = new SyntheticDspEngine();
            newChannelId = synth.OpenChannel(rateHz, Width);
            try { ApplyStateToNewChannel(synth, newChannelId); }
            catch (EntryPointNotFoundException) { }
            newEngine = synth;
        }

        IDspEngine? old;
        int oldChannel;
        lock (_engineLock)
        {
            old = _engine;
            oldChannel = _channelId;
            Volatile.Write(ref _engine, newEngine);
            Volatile.Write(ref _channelId, newChannelId);
            Volatile.Write(ref _rx2ChannelId, -1);
            Volatile.Write(ref _sampleRateHz, rateHz);
        }
        TeardownEngine(old, oldChannel);
        _log.LogInformation("dsp.pipeline p2 engine={Engine} rate={Rate}", newEngine.GetType().Name, rateHz);
        RaiseEngineChanged(newEngine);

        _p2Client = client;
        // Sync the change-detect cache with the values we just seeded so the
        // first OnRadioStateChanged after connect doesn't redundantly re-push
        // (which would emit a duplicate CmdHighPriority). Re-read in case the
        // operator changed either control during the connect window — the
        // PreampChanged / StateChanged handlers would have early-returned on
        // _p2Client==null. Comparing here recovers any drift before the cache
        // settles.
        _appliedPreampOn = initialPreamp;
        _appliedEffectiveAttDb = initialAttDb;
        bool nowPreamp = _radio.PreampOn;
        int nowAttDb = _radio.EffectiveAttenDb;
        if (nowPreamp != initialPreamp)
        {
            client.SetPreamp(nowPreamp);
            _appliedPreampOn = nowPreamp;
        }
        if (nowAttDb != initialAttDb)
        {
            client.SetAttenuator(nowAttDb);
            _appliedEffectiveAttDb = nowAttDb;
        }
        // iter5: attach as the synchronous RX sink. See AttachRxSinkP1 in
        // OnRadioConnected for full rationale — same lock-free hot path.
        AttachRxSinkP2(client);
        // Force the next OnRadioStateChanged to re-push every PS field into
        // the freshly-opened WdspDspEngine instance, regardless of whether
        // the canonical state in StateDto has changed since the prior
        // session. The new engine starts with field defaults (hwPeak=0.4072,
        // ptol=0.8, etc.) and the change-detect cache `_appliedPs*` doesn't
        // know that — without this flag the engine never gets the operator's
        // settings back, calcc runs on wrong hw_scale, and PS doesn't
        // converge after a reconnect. See `project_ps_reconnect_state_loss.md`.
        _psResyncRequired = true;
        // TX-monitor: same re-push problem as PS — the new engine starts at
        // monitor=off, so if the operator had it on the latch's change-detect
        // would skip the push. Reset the latch so the next UpdateState fires.
        _appliedTxMonitorEnabled = false;
        // Pass the live client so RadioService can fire P2Connected with a
        // reference to the freshly-opened Protocol2Client. TxMetersService
        // subscribes through that event to hook hi-priority status (#174).
        _radio.MarkProtocol2Connected(radioEndpoint.ToString(), rateHz, client, boardKind);
        // P2 G2/MkII default HW peak = 0.6121; ANAN-7000/8000 = 0.2899. The
        // RadioService switch covers both so we don't bake a value in here.
        // ConnectedBoardKind now returns the discovered board kind when the
        // caller plumbed it through (issue #171); falls back to OrionMkII when
        // the byte wasn't supplied.
        _radio.ApplyPsHwPeakForConnection(isProtocol2: true, _radio.ConnectedBoardKind);
        // Restore the persisted PS feedback attenuation (0..31 dB) before the
        // operator arms PS, so a hot external-tap chain (e.g. RF2K-S −55 dB
        // coupler) doesn't boot at 0 dB and rail the feedback ADC — the
        // saturation that left calcc unable to fit on a fresh connect. No-op
        // when nothing was saved for this board yet.
        if (_radio.GetPersistedPsTxAttnDb() is int txAttn)
        {
            CurrentP2Client?.SetTxAttenuationDb((byte)Math.Clamp(txAttn, 0, 31));
        }
        // Push current PA snapshot into the brand-new client so byte 345 /
        // byte 1401 / CmdGeneral[58] reflect PaSettingsStore from frame 1.
        _radio.ReplayPaSnapshot();
        return sampleRateKhz;
    }

    private void OnPaSnapshotChanged(PaRuntimeSnapshot snap)
    {
        var p2 = _p2Client;
        if (p2 is null) return;
        p2.SetDriveByte(snap.DriveByte);
        p2.SetOcMasks(snap.OcTxMask, snap.OcRxMask);
        // Anvelina-PRO3 DX OC masks (#407). Always forwarded; Protocol2Client
        // gates whether they hit byte 1397 on the wire by checking the
        // connected board+variant. Non-Anvelina P2 boards see byte 1397
        // stay at zero per EU2AV's reserved-bit rule.
        p2.SetOcDxMasks(snap.OcDxTxMask, snap.OcDxRxMask);
        p2.SetPaEnabled(snap.PaEnabled);
        // External antenna (antenna slice — #804). HpsdrAntenna.Ant1=0 → wire 1
        // → ALEX_TX_ANTENNA_1, so the +1 maps the 0-based enum to the 1-based
        // wire selector. SetAntennas gates the TX-antenna emission on
        // HasTxAntennaRelays, defers a mid-key relay change to the unkey edge,
        // and routes the operator RX-aux strictly BEFORE the PS coupler OR
        // (the PS-K36 firewall lives in Protocol2Client.SendCmdHighPriority).
        p2.SetAntennas(
            (int)snap.TxAntenna + 1,
            (int)snap.RxAntenna + 1,
            snap.HasTxAntennaRelays,
            snap.RxAuxInput,
            snap.MkiiBpfRxSelect);
    }

    private void OnRadioMoxChanged(bool on)
    {
        _keyed = on;
        // Arm a one-shot fade envelope on the first audio block Tick reads
        // after this edge. Rising edge → ramp current audio out so the post-
        // MOX silent stretch isn't a hard cut. Falling edge → ramp the resume
        // audio in so the dmp=0 RXA up doesn't pop through to the browser.
        if (on) _rxFadeOutPending = true;
        else _rxFadeInPending = true;
        _p2Client?.SetMox(on);
        // Falling edge: pick up any PS knob changes that OnRadioStateChanged
        // deferred while we were keyed (HwPeak / Ptol / Advanced / Control).
        // Without this re-trigger a deferred change would sit unapplied until
        // the next unrelated StateChanged event, which could be several seconds
        // away. The state-change handler is idempotent against equality checks,
        // so re-invoking it when nothing was deferred is harmless.
        if (!on)
        {
            try { OnRadioStateChanged(_radio.Snapshot()); }
            catch (Exception ex) { _log.LogWarning(ex, "dsp.pipeline mox-off restate failed"); }
        }
    }

    private void OnRadioTunActiveChanged(bool on)
    {
        _p2Client?.SetTune(on);
    }

    // Mirror operator preamp toggles into a live Protocol2Client. P1 is
    // pushed by RadioService.SetPreamp directly via ActiveClient. PreampOn
    // isn't on the StateDto wire format, so this event-driven path is the
    // only way the bit reaches CmdHighPriority byte 1403 on P2 (issue #126).
    private void OnRadioPreampChanged(bool on)
    {
        var p2 = _p2Client;
        if (p2 is null) return;
        if (on == _appliedPreampOn) return;
        p2.SetPreamp(on);
        _appliedPreampOn = on;
    }

    // Operator changed the DDC sample rate (display bandwidth) while connected.
    // P1's rate is already on the wire via RadioService → ActiveClient; here we
    // handle the P2 side, which RadioService can't reach (ActiveClient is null
    // on P2). The whole re-rate is posted to the DSP thread (PostDspCommand →
    // DrainDspCommands, run between RX frames on the same thread that calls
    // FeedIq) so it never races the hot path. See RerateRxChannelForP2 for why
    // it must be in-place on the existing engine.
    private void OnRadioSampleRateChanged(int rateHz)
    {
        if (_p2Client is null) return;
        PostDspCommand(() => RerateRxChannelForP2(rateHz));
    }

    // Re-rate the RX channel to a new DDC input rate, IN PLACE on the existing
    // engine instance. Two hazards this avoids, both of which produced the
    // 0xc0000005 native crash on the first naive attempt:
    //
    //   1. Channel aliasing. WdspDspEngine.OpenChannel allocates the first free
    //      id from its *per-instance* _channels dict, but WDSP's channel table
    //      is global/native. A second engine instance would therefore re-open
    //      global channel 0 — the slot the old engine still owns — and tearing
    //      the old engine down would free the channel the new one is using.
    //      Closing then re-opening on the SAME instance reuses id 0 cleanly
    //      (the rebuild WdspDspEngine.OpenChannel was written to support — it
    //      re-applies the notch DB after a "sample-rate or mode change").
    //   2. Hot-path teardown. CloseChannel stops the channel worker; doing it
    //      on the DSP thread (this runs inside DrainDspCommands) means no FeedIq
    //      is in flight on the channel being torn down.
    //
    // FeedIq no-ops on a missing channel id, so the brief CloseChannel→OpenChannel
    // window is safe even though it isn't atomic with _channelId.
    private void RerateRxChannelForP2(int rateHz)
    {
        var p2 = _p2Client;
        if (p2 is null) return; // disconnected between post and drain
        var engine = Volatile.Read(ref _engine);
        if (engine is null) return;

        int oldChannel = Volatile.Read(ref _channelId);
        int oldRx2Channel = Volatile.Read(ref _rx2ChannelId);
        try
        {
            if (oldRx2Channel >= 0)
            {
                Volatile.Write(ref _rx2ChannelId, -1);
                try { engine.CloseChannel(oldRx2Channel); } catch { /* best-effort */ }
            }
            engine.CloseChannel(oldChannel);
            int newChannel = engine.OpenChannel(rateHz, Width);
            try { ApplyStateToNewChannel(engine, newChannel); }
            catch (EntryPointNotFoundException ex)
            {
                _log.LogWarning(ex, "dsp.pipeline p2 re-rate missing entry point — partial config applied");
            }
            Volatile.Write(ref _channelId, newChannel);
            Volatile.Write(ref _sampleRateHz, rateHz);
            var state = _radio.Snapshot();
            if (state.Rx2Enabled) _ = EnsureRx2Channel(engine, state);
            // RX channel is ready at the new rate — now tell the radio to re-rate
            // its DDC (re-emits the RX-spec). Ordering this last means new-rate
            // IQ only starts arriving once the channel can decode it.
            p2.SetSampleRateKhz(rateHz / 1000);
            _log.LogInformation("dsp.pipeline p2 re-rate channel={Ch} rate={Rate}", newChannel, rateHz);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "dsp.pipeline p2 re-rate failed rate={Rate}", rateHz);
        }
    }

    // Operator added/removed/changed a manual notch. Forward the full list to
    // the live engine; the engine rewrites WDSP's notch database. A no-op when
    // no engine is up — ApplyStateToNewChannel re-applies on the next connect.
    private void OnRadioNotchesChanged(IReadOnlyList<NotchDto> notches)
    {
        Volatile.Read(ref _engine)?.SetNotches(notches);
    }

    private void OnFrequencyCorrectionFactorChanged(double factor)
    {
        // RadioService handles the P1 client + the re-tune; we only have
        // to forward to the live P2 client here. No-op when no P2 is up.
        _p2Client?.SetFrequencyCorrectionFactor(factor);
    }

    /// <summary>
    /// Forward a WDSP TXA block of interleaved float IQ to the live P2 client.
    /// No-op when P2 isn't connected; safe to call from TxTuneDriver / future
    /// mic-MOX feeders without branching on protocol.
    /// </summary>
    public void ForwardTxIqToP2(ReadOnlySpan<float> iqInterleaved)
    {
        _p2Client?.SendTxIq(iqInterleaved);
    }

    public async Task DisconnectP2Async(CancellationToken ct)
    {
        var client = _p2Client;
        _p2Client = null;
        if (client is null) return;

        // iter5: detach the sink BEFORE the Protocol2Client teardown so any
        // in-flight RxLoop callback completes against the still-valid engine
        // and no further callbacks land. client.StopAsync joins the RX task,
        // so by the time it returns the RX thread is gone.
        DetachRxSinkP2();
        try { await client.StopAsync(ct).ConfigureAwait(false); } catch { }
        await client.DisposeAsync().ConfigureAwait(false);

        var synth = new SyntheticDspEngine();
        int channelId = synth.OpenChannel(SyntheticSampleRateHz, Width);
        ApplyStateToNewChannel(synth, channelId);

        IDspEngine? old;
        int oldChannel;
        lock (_engineLock)
        {
            old = _engine;
            oldChannel = _channelId;
            Volatile.Write(ref _engine, synth);
            Volatile.Write(ref _channelId, channelId);
            Volatile.Write(ref _rx2ChannelId, -1);
            Volatile.Write(ref _sampleRateHz, SyntheticSampleRateHz);
        }
        TeardownEngine(old, oldChannel);
        RaiseEngineChanged(synth);
        // Mark PS state for forced re-push on the next ConnectP2Async. The
        // change-detect cache (`_appliedPs*`) is preserved across disconnect
        // — by design, so a reconnect with unchanged operator state doesn't
        // generate spurious wire writes — but a fresh WdspDspEngine starts
        // with field defaults (hwPeak=0.4072, ptol=0.8, etc.) that don't
        // match the canonical state. Without this flag, OnRadioStateChanged
        // skips every PS push because s.PsX == _appliedPsX, and the new
        // engine never gets the operator's settings. See
        // `project_ps_reconnect_state_loss.md` for the rack reproduction.
        _psResyncRequired = true;
        _appliedTxMonitorEnabled = false;
        _radio.MarkProtocol2Disconnected();
        _log.LogInformation("dsp.pipeline p2 disconnected, engine=synthetic");
    }

    public Zeus.Protocol2.Protocol2Client? ActiveP2Client => _p2Client;

    /// <summary>
    /// Live per-DDC / per-receiver RX ingest health for the diagnostics surface
    /// (overflow/underrun verification at high sample rate + multi-DDC). Reads
    /// only cached, lock-free snapshots — no realtime/WDSP work on this path:
    ///   * per-WDSP-channel ingest health (queue depth/cap, frames-in, queue-full,
    ///     dropped-oldest, worker avg/max ms vs the per-frame budget, audio
    ///     overrun) from <see cref="WdspDspEngine.SnapshotRxChannels"/>; and
    ///   * per-DDC UDP packet rate (last ~1 s window) from the active P2 client,
    ///     indexed by DDC (0..MaxRxDdc-1) — the ground truth for which DDCs the
    ///     radio is actually streaming.
    /// </summary>
    public object SnapshotRxIngestHealth()
    {
        var channels = (CurrentEngine as WdspDspEngine)?.SnapshotRxChannels()
                       ?? (IReadOnlyList<WdspDspEngine.RxChannelHealth>)System.Array.Empty<WdspDspEngine.RxChannelHealth>();
        long[] portRates = ActiveP2Client?.SnapshotRxPortPacketRates() ?? System.Array.Empty<long>();

        var channelDtos = new object[channels.Count];
        for (int i = 0; i < channels.Count; i++)
        {
            var h = channels[i];
            // Per-frame WDSP budget: a 1024-sample frame must be processed in
            // (1000·1024/rate) ms on average or the queue backs up. workerMaxMs
            // approaching this is the realtime "CPU-bound" signal.
            double frameBudgetMs = h.SampleRateHz > 0 ? 1000.0 * 1024.0 / h.SampleRateHz : 0.0;
            double headroomPct = frameBudgetMs > 0
                ? System.Math.Round(100.0 * (1.0 - h.WorkerMaxMs / frameBudgetMs), 1)
                : 0.0;
            channelDtos[i] = new
            {
                channelId = h.ChannelId,
                sampleRateHz = h.SampleRateHz,
                queueDepth = h.QueueDepth,
                queueCapacity = h.QueueCapacity,
                framesInPerWindow = h.FramesInPerWindow,
                queueFullPerWindow = h.QueueFullPerWindow,
                droppedPerWindow = h.DroppedPerWindow,
                workerFramesPerWindow = h.WorkerFramesPerWindow,
                workerAvgMs = System.Math.Round(h.WorkerAvgMs, 3),
                workerMaxMs = System.Math.Round(h.WorkerMaxMs, 3),
                frameBudgetMs = System.Math.Round(frameBudgetMs, 3),
                workerHeadroomPct = headroomPct,
                audioRingDepth = h.AudioRingDepth,
                audioOverrunPerWindow = h.AudioOverrunPerWindow,
                ageMs = h.AgeMs,
            };
        }

        return new
        {
            schemaVersion = 1,
            maxRxDdc = Zeus.Protocol2.Protocol2Client.MaxRxDdc,
            activeChannels = channels.Count,
            rxPortPacketRates = portRates,
            channels = channelDtos,
        };
    }

    /// <summary>
    /// Panadapter pixel column width — exposed so the frequency-calibration
    /// service (issue #325) can size its capture buffer correctly without
    /// hard-coding the constant.
    /// </summary>
    public static int PanadapterWidth => Width;

    /// <summary>
    /// Reads the latest cached panadapter snapshot (dB values, display
    /// order — low frequency left). Caches are filled by <see cref="Tick"/>
    /// at 30 Hz; the frequency-calibration service (issue #325) reads from
    /// here to avoid racing for WDSP's once-per-frame "fresh data" flag,
    /// which Tick is also consuming and would always win.
    /// </summary>
    /// <param name="dest">Buffer of length <see cref="PanadapterWidth"/>.</param>
    /// <param name="hzPerPixel">Hz spacing between adjacent pixels (out).</param>
    /// <param name="centerHz">Frequency of the centre pixel — the radio's LO
    /// (out). In CW modes this is dial ± cw_pitch; outside CW it equals dial.</param>
    /// <param name="maxAgeMs">Reject the cached snapshot if it is older than
    /// this many milliseconds. Default 200 ms — six analyzer frames at 30 Hz,
    /// generous tolerance for a one-off cal measurement without risking
    /// pre-tune stale data.</param>
    public bool TryCapturePanadapterSnapshot(
        Span<float> dest,
        out float hzPerPixel,
        out long centerHz,
        long maxAgeMs = 200)
    {
        hzPerPixel = 0;
        centerHz = 0;
        if (dest.Length != Width) return false;

        lock (_calPanLock)
        {
            if (_calPanSnapshotMs == 0) return false;
            long ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _calPanSnapshotMs;
            if (ageMs > maxAgeMs) return false;

            _calPanSnapshot.AsSpan().CopyTo(dest);
            hzPerPixel = _calPanHzPerPixel;
            centerHz = _calPanCenterHz;
        }
        return true;
    }

    internal static void FeedProtocol1Iq(
        IDspEngine engine,
        int channel,
        int rx2Channel,
        ReadOnlySpan<double> interleavedIqSamples)
    {
        engine.FeedIq(channel, interleavedIqSamples);
        if (rx2Channel >= 0 && rx2Channel != channel)
            engine.FeedIq(rx2Channel, interleavedIqSamples);
    }

    // ---- IRxPacketSink (Protocol 1) -----------------------------------------
    // Called synchronously on Protocol1Client.RxLoop's OS thread. The body
    // does, in order:
    //   1) drain the cross-thread DSP command queue,
    //   2) read a snapshot of the engine/channel via Volatile.Read (lock-free
    //      — _engineLock is held only by engine-swap writers and never by
    //      readers on the hot path),
    //   3) feed the IQ into WDSP (RX1, and RX2 when P1 is acting as a
    //      sub-receiver inside the same captured window),
    //   4) fire the RxIqAvailable test seam,
    //   5) return the ArrayPool buffer that Protocol1Client.RxLoop rented,
    //   6) check whether 33.33 ms have elapsed since the last Tick and, if
    //      so, run Tick INLINE on this thread (no PeriodicTimer involvement).
    //
    // Exceptions cannot propagate — the protocol client catches and logs at
    // p1.rx.sink_threw, then continues. Sink-thrown exceptions still leak the
    // ArrayPool buffer (the client returns it on our behalf when we throw),
    // so we do our own try/finally inside the body to keep ownership tight.
    void Zeus.Protocol1.IRxPacketSink.OnIqFrame(in Zeus.Protocol1.IqFrame frame)
    {
        try
        {
            DrainDspCommands();
            // iter5 pass-2: lock-free hot path. _engine / _channelId are
            // observed via Volatile.Read; the release fence on _engineLock
            // exit (writer side, OnRadioConnected / ConnectP2Async) plus the
            // full fence on AttachRxSink (Interlocked.Exchange) guarantees
            // the sink sees the freshly-installed engine. See _engineLock
            // doc on the field.
            var engine = Volatile.Read(ref _engine);
            int channel = Volatile.Read(ref _channelId);
            if (engine is not null)
            {
                FeedProtocol1Iq(
                    engine,
                    channel,
                    Volatile.Read(ref _rx2ChannelId),
                    frame.InterleavedSamples.Span);
                RxIqAvailable?.Invoke(0, frame.SampleRateHz, frame.InterleavedSamples);
            }
            MaybeTickInline();
        }
        finally
        {
            // Return the rented buffer regardless of whether the engine was
            // null or the call threw. The protocol client transferred
            // ownership to us on a non-throwing return; we keep ownership
            // here (the try/catch in Protocol1Client.RxLoop will also try
            // to return on our throw, but we don't re-throw — sink-side
            // exceptions are swallowed by the try block above via the
            // MaybeTickInline path catching nothing extra, and any
            // exceptions inside engine.FeedIq propagate to the client's
            // catch which then returns the array — a tolerated rare race).
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(
                    frame.InterleavedSamples, out var seg) && seg.Array is { } arr)
            {
                System.Buffers.ArrayPool<double>.Shared.Return(arr);
            }
        }
    }

    void Zeus.Protocol1.IRxPacketSink.OnPsFeedbackFrame(in Zeus.Protocol1.PsFeedbackFrame frame)
    {
        DrainDspCommands();
        var engine = Volatile.Read(ref _engine);
        engine?.FeedPsFeedbackBlock(frame.TxI, frame.TxQ, frame.RxI, frame.RxQ);
        // No Tick on PS-feedback — display cadence is paced by IQ frames.
    }

    // ---- IRxPacketSink (Protocol 2) -----------------------------------------
    // Same shape as P1, but the buffer lifetime is owned by the PRODUCER: when a
    // sink is attached, Protocol2Client.HandleDdcPacket rents the IQ buffer and
    // returns it to the pool in its own finally right after this call returns
    // (safe because everything below consumes the span synchronously). So this
    // sink must NOT retain frame.InterleavedSamples past the call and does not
    // return any buffer itself.
    void Zeus.Protocol2.IRxPacketSink.OnIqFrame(in Zeus.Protocol2.IqFrame frame)
    {
        DrainDspCommands();
        var engine = Volatile.Read(ref _engine);
        if (engine is not null)
        {
            if (frame.ReceiverIndex == 1)
            {
                // RX2's own DDC stream (true second receiver). Feed ONLY the RX2
                // channel — RX2 used to be fed RX1's DDC, which made both
                // waterfalls identical. Display/TX cadence is paced by the RX1
                // frames below, so this path takes no tick.
                int rx2Channel = Volatile.Read(ref _rx2ChannelId);
                LogRxIqRms(1, frame.InterleavedSamples.Span, ref _rx2IqRmsLogMs);
                if (rx2Channel >= 0)
                {
                    engine.FeedIq(rx2Channel, frame.InterleavedSamples.Span);
                }
                return;
            }
            int channel = Volatile.Read(ref _channelId);
            LogRxIqRms(0, frame.InterleavedSamples.Span, ref _rx1IqRmsLogMs);
            engine.FeedIq(channel, frame.InterleavedSamples.Span);
            RxIqAvailable?.Invoke(0, frame.SampleRateHz, frame.InterleavedSamples);
        }
        MaybeTickInline();
    }

    void Zeus.Protocol2.IRxPacketSink.OnPsFeedbackFrame(in Zeus.Protocol2.PsFeedbackFrame frame)
    {
        DrainDspCommands();
        var engine = Volatile.Read(ref _engine);
        engine?.FeedPsFeedbackBlock(frame.TxI, frame.TxQ, frame.RxI, frame.RxQ);
    }

    // RX2 bring-up probe: 1 Hz log of incoming IQ RMS/peak per receiver. A live
    // DDC reads grainy noise (rms ~1e-4+); a dead/unconnected ADC reads ~0 even
    // while packets stream at full rate — distinguishing "radio not streaming"
    // from "streaming silence" (wrong ADC source) for RX2.
    private long _rx1IqRmsLogMs;
    private long _rx2IqRmsLogMs;
    private void LogRxIqRms(int rx, ReadOnlySpan<double> iq, ref long lastMs)
    {
        long now = Environment.TickCount64;
        if (now - lastMs < 1000) return;
        lastMs = now;
        double sumSq = 0; double peak = 0;
        for (int i = 0; i < iq.Length; i++)
        {
            double v = iq[i];
            sumSq += v * v;
            double a = v < 0 ? -v : v;
            if (a > peak) peak = a;
        }
        double rms = iq.Length > 0 ? Math.Sqrt(sumSq / iq.Length) : 0;
        _log.LogInformation("p2.rx.iqrms rx={Rx} n={N} rms={Rms:E3} peak={Peak:E3}", rx, iq.Length, rms, peak);
    }

    /// <summary>
    /// Drain every queued cross-thread command synchronously on the calling
    /// thread (the DSP thread — either the RxLoop thread when a sink is
    /// attached, or the ExecuteAsync PeriodicTimer thread otherwise).
    /// ConcurrentQueue.TryDequeue is wait-free; an exception in a command
    /// is logged and the remaining commands still drain.
    /// </summary>
    private void DrainDspCommands()
    {
        while (_dspCommands.TryDequeue(out var cmd))
        {
            try { cmd(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "dsp.pipeline command threw");
            }
        }
    }

    /// <summary>
    /// Post a command for execution on the DSP thread (the RX OS thread
    /// when a sink is attached, or the ExecuteAsync PeriodicTimer thread
    /// otherwise). Used by <see cref="SetMox"/> and <see cref="SetTxTune"/>
    /// so WDSP TXA-state edges happen on the same thread that feeds RX IQ.
    /// </summary>
    internal void PostDspCommand(Action cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        _dspCommands.Enqueue(cmd);
    }

    private void MaybeTickInline()
    {
        long now = Stopwatch.GetTimestamp();
        long last = _lastTickStopwatchTicks;
        if (last == 0 || (now - last) >= TickPeriodStopwatchTicks)
        {
            _lastTickStopwatchTicks = now;
            Tick(_panBuf, _wfBuf, _audioBuf);
        }
    }

    /// <summary>
    /// Attach this pipeline as the synchronous RX sink for a Protocol-1
    /// client. Must be called AFTER the engine has been swapped to point at
    /// the new client's WDSP instance — once this returns, the RxLoop will
    /// start firing OnIqFrame on the DSP thread and any older engine reference
    /// must already be unused.
    /// </summary>
    private void AttachRxSinkP1(IProtocol1Client client)
    {
        // Reset the tick clock so the first IQ frame on the new connection
        // gets a fresh display tick (avoids a stale ~33 ms gap if the timer
        // was running synthetic ticks just before connect).
        _lastTickStopwatchTicks = 0;
        _attachedSinkP1 = client;
        client.AttachRxSink(this);
        _rxSinkAttached = true;
        _log.LogInformation("dsp.pipeline rx-sink attached protocol=p1");
    }

    private void DetachRxSinkP1()
    {
        var client = _attachedSinkP1;
        _attachedSinkP1 = null;
        _rxSinkAttached = false;
        client?.DetachRxSink();
        _log.LogInformation("dsp.pipeline rx-sink detached protocol=p1");
    }

    private void AttachRxSinkP2(Zeus.Protocol2.Protocol2Client client)
    {
        _lastTickStopwatchTicks = 0;
        _attachedSinkP2 = client;
        client.AttachRxSink(this);
        _rxSinkAttached = true;
        _log.LogInformation("dsp.pipeline rx-sink attached protocol=p2");
    }

    private void DetachRxSinkP2()
    {
        var client = _attachedSinkP2;
        _attachedSinkP2 = null;
        _rxSinkAttached = false;
        client?.DetachRxSink();
        _log.LogInformation("dsp.pipeline rx-sink detached protocol=p2");
    }

    private void CloseCurrentEngine()
    {
        IDspEngine? engine;
        int channel;
        lock (_engineLock)
        {
            engine = _engine;
            channel = _channelId;
            Volatile.Write(ref _engine, null);
            Volatile.Write(ref _channelId, 0);
            Volatile.Write(ref _rx2ChannelId, -1);
        }
        TeardownEngine(engine, channel);
    }

    private static void TeardownEngine(IDspEngine? engine, int channelId)
    {
        if (engine is null) return;
        try { engine.CloseChannel(channelId); } catch { /* best-effort */ }
        engine.Dispose();
    }

    internal static int SelectRxAudio(
        Rx2AudioMode mode,
        Span<float> rx1,
        int rx1Count,
        ReadOnlySpan<float> rx2,
        int rx2Count)
    {
        rx1Count = Math.Clamp(rx1Count, 0, rx1.Length);
        rx2Count = Math.Clamp(rx2Count, 0, rx2.Length);
        return mode switch
        {
            Rx2AudioMode.Rx2 => rx2Count > 0 ? CopyRx2Audio(rx1, rx2, rx2Count) : rx1Count,
            Rx2AudioMode.Both => MixRxAudio(rx1, rx1Count, rx2, rx2Count),
            _ => rx1Count,
        };
    }

    private static int CopyRx2Audio(Span<float> output, ReadOnlySpan<float> rx2, int rx2Count)
    {
        int count = Math.Min(output.Length, rx2Count);
        rx2[..count].CopyTo(output);
        return count;
    }

    private static int MixRxAudio(
        Span<float> output,
        int rx1Count,
        ReadOnlySpan<float> rx2,
        int rx2Count)
    {
        if (rx2Count <= 0) return rx1Count;
        if (rx1Count <= 0) return CopyRx2Audio(output, rx2, rx2Count);

        int count = Math.Min(output.Length, Math.Max(rx1Count, rx2Count));
        for (int i = 0; i < count; i++)
        {
            float a = i < rx1Count ? output[i] : 0f;
            float b = i < rx2Count ? rx2[i] : 0f;
            output[i] = 0.5f * (a + b);
        }
        return count;
    }

    private void Tick(float[] panBuf, float[] wfBuf, float[] audioBuf)
    {
        // iter5 pass-2: lock-free hot path. Tick runs inline on the RX OS
        // thread when a sink is attached (paced via Stopwatch elapsed in
        // OnIqFrame), and on the PeriodicTimer thread otherwise. Volatile
        // reads are correctly ordered against the writer-side _engineLock
        // release in OnRadioConnected / ConnectP2Async / etc.
        var engine = Volatile.Read(ref _engine);
        int channel = Volatile.Read(ref _channelId);
        int sampleRate = Volatile.Read(ref _sampleRateHz);
        if (engine is null) return;

        var state = _radio.Snapshot();
        // Synthetic engine stays open while disconnected so SetMode/SetFilter
        // etc. have somewhere to land, but its sweep+static placeholder used
        // to render a misleading "fake spectrum" before any radio existed.
        // Gate on the engine type rather than the connection status: status
        // flips to Connected before OnRadioConnected swaps the engine, and a
        // status-only check let one or two synthetic frames leak through that
        // race window — visible as a brief flash of the fake waterfall right
        // when the user clicked Connect. The synthetic engine never produces
        // real-radio data, so suppressing it unconditionally is correct.
        if (engine is SyntheticDspEngine) return;

        // Issue #597 Phase 0: restore the default display tau once the LO has
        // been quiet for FastAttackRestoreMs. Runs on the RX/pipeline thread;
        // the engine call is idempotent and channel-guarded, so a race with a
        // simultaneous re-arm on the state thread is harmless (the re-arm
        // refreshes _fastAttackLoChangedAt and the restore simply fires later).
        if (_fastAttackActive &&
            Stopwatch.GetTimestamp() - Interlocked.Read(ref _fastAttackLoChangedAt) >= FastAttackRestoreTicks)
        {
            engine.SetRxDisplayFastAttack(channel, fast: false);
            _fastAttackActive = false;
        }

        engine.SetVfoHz(channel, state.VfoHz);
        int rx2Channel = Volatile.Read(ref _rx2ChannelId);
        if (state.Rx2Enabled && rx2Channel >= 0)
            engine.SetVfoHz(rx2Channel, state.VfoBHz);

        // Skip the entire display pipeline unless at least one client has a
        // mounted spectrum consumer. Saves: 2× engine.TryGet*DisplayPixels
        // P/Invoke per tick (each reads from the WDSP analyzer slot under its
        // lock), Array.Reverse on two 2 048-float buffers, the DisplayFrame
        // record construction, and the 16 KB-ish byte[] payload fanout would
        // allocate. Control-only clients still receive meters/state/audio as
        // appropriate; they just do not pin the high-rate display stream on.
        bool hasDisplaySubscribers = _hub.DisplayStreamRequested;
        // Audio path uses nowMs too (it runs even when no clients are connected,
        // for in-process RxAudioAvailable subscribers like TCI). Hoisted above
        // the display gate to keep one timestamp call per tick.
        double nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool pan = false, wf = false;
        bool psFbPanUsed = false, psFbWfUsed = false;
        bool psFeedbackCorrecting = false;
        string panSource = "none";
        string wfSource = "none";
        double rxAudioRmsForMeter = double.NaN;
        if (hasDisplaySubscribers)
        {
            // While keyed (MOX or TUN — see _keyed comment) pull from the TX
            // analyzer so the panadapter shows the transmitted signal instead of
            // the RX front end's TX bleed (issue #81). If the TX analyzer isn't
            // ready (not yet produced an FFT, or engine doesn't have a TX
            // analyzer — e.g. Synthetic), TryGetTxDisplayPixels returns false and
            // we fall through to the RX analyzer, matching the pre-issue-#81
            // behaviour. This fallback also covers the first ~1 tick after
            // keying before the analyzer averaging has settled.
            //
            // Issue #121 layered on top: if the operator has the "Monitor PA
            // output" toggle on AND PS is armed AND PS has converged
            // (info[14]==1, surfaced via GetPsStageMeters().Correcting), prefer
            // the PS-feedback analyzer (post-PA loopback IQ). Falls back to the
            // TX analyzer if the PS-FB analyzer hasn't produced a fresh FFT yet
            // — same shape as the existing TX → RX fallback. Default-off
            // toggle: when off the codepath is identical to pre-#121, byte for
            // byte, on every board.
            if (_keyed)
            {
                if (_appliedPsEnabled && _psMonitorEnabled
                    && (psFeedbackCorrecting = engine.GetPsStageMeters().Correcting))
                {
                    pan = engine.TryGetPsFeedbackDisplayPixels(DisplayPixout.Panadapter, panBuf);
                    wf = engine.TryGetPsFeedbackDisplayPixels(DisplayPixout.Waterfall, wfBuf);
                    psFbPanUsed = pan;
                    psFbWfUsed = wf;
                    if (pan) panSource = "ps-feedback";
                    if (wf) wfSource = "ps-feedback";
                }
                if (!pan)
                {
                    pan = engine.TryGetTxDisplayPixels(DisplayPixout.Panadapter, panBuf);
                    if (pan) panSource = "tx";
                }
                if (!wf)
                {
                    wf = engine.TryGetTxDisplayPixels(DisplayPixout.Waterfall, wfBuf);
                    if (wf) wfSource = "tx";
                }
            }
            if (_keyed && _psMonitorEnabled)
            {
                _psMonitorTickCount++;
                if (_psMonitorTickCount % 30 == 0)
                {
                    _log.LogInformation(
                        "psMonitor.gate keyed=1 psEn={PsEn} mon=1 corr={Corr} psFbPan={Pan} psFbWf={Wf}",
                        _appliedPsEnabled, psFeedbackCorrecting, psFbPanUsed, psFbWfUsed);
                }
            }
            else
            {
                _psMonitorTickCount = 0;
            }
            if (!pan)
            {
                pan = engine.TryGetDisplayPixels(channel, DisplayPixout.Panadapter, panBuf);
                if (pan) panSource = "rx";
            }
            if (!wf)
            {
                wf = engine.TryGetDisplayPixels(channel, DisplayPixout.Waterfall, wfBuf);
                if (wf) wfSource = "rx";
            }

            // TX display calibration offset (Thetis TXDisplayCalOffset). Pure
            // dB shift of the transmitted-signal trace/waterfall so the operator
            // can sit the in-passband level where they want it — display-only,
            // never the air. Applied only to TX-sourced pixels (tx / PS
            // feedback); RX pixels keep their own calibration.
            double txCal = Volatile.Read(ref _txDisplayCalOffsetDb);
            if (txCal != 0.0)
            {
                if (pan && (panSource == "tx" || panSource == "ps-feedback")) AddDbOffset(panBuf, txCal);
                if (wf && (wfSource == "tx" || wfSource == "ps-feedback")) AddDbOffset(wfBuf, txCal);
            }

            // Diagnostic (1 Hz): log the actual TX panadapter pixel dB range so
            // we can confirm where the transmitted signal sits relative to the
            // display window (the TX analyzer reads far hotter than RX). Helps
            // verify the frontend TX auto-range is fitting sane values.
            if (pan && panSource == "tx")
            {
                long txDbgNow = Environment.TickCount64;
                if (txDbgNow - _txPixelDbgMs >= 1000)
                {
                    _txPixelDbgMs = txDbgNow;
                    float pmin = float.PositiveInfinity, pmax = float.NegativeInfinity;
                    double psum = 0; int pcnt = 0;
                    for (int i = 0; i < panBuf.Length; i++)
                    {
                        float v = panBuf[i];
                        if (!float.IsFinite(v)) continue;
                        if (v < pmin) pmin = v;
                        if (v > pmax) pmax = v;
                        psum += v; pcnt++;
                    }
                    if (pcnt > 0)
                        _log.LogInformation(
                            "tx.display.pixels min={Min:F1} max={Max:F1} mean={Mean:F1} dB (window default {Lo}..{Hi}; calOffset={Cal:F1})",
                            pmin, pmax, psum / pcnt, -80, 20, txCal);
                }
            }

            // Flip to display order (low freq left, high freq right). WDSP emits
            // pixel 0 = highest positive frequency — see doc 03 §10 and
            // doc 08 §3 "Pixel axis reversal". SyntheticDspEngine already emits
            // in WDSP order so this reversal applies to both engines. Guarded by
            // the freshness flag: TryGetDisplayPixels leaves the buffer untouched
            // when no new FFT is ready, so an unconditional reverse would alternate
            // the orientation on every stale tick and broadcast mirrored garbage
            // (still flagged invalid, but bandwidth wasted and timing-sensitive).
            if (pan) Array.Reverse(panBuf);
            if (wf) Array.Reverse(wfBuf);
            if (pan) SanitizeDisplayBuffer(panBuf);
            if (wf) SanitizeDisplayBuffer(wfBuf);

            var flags = DisplayBodyFlags.None;
            if (pan) flags |= DisplayBodyFlags.PanValid;
            if (wf) flags |= DisplayBodyFlags.WfValid;

            // Zoom narrows the analyzer's display span to sampleRate/level around
            // the VFO, so hzPerPixel shrinks by the same factor. Client re-uses
            // this for axis labels and planWaterfallUpdate horizontal shift — no
            // extra contract field needed, per task #7 scope note.
            int zoomLevel = Math.Max(1, state.ZoomLevel);
            float hzPerPixel = (float)((double)sampleRate / zoomLevel / Width);
            // Panadapter centre: the LO the pixels were actually computed
            // at (issue #597 Phase 2). The analyzer output broadcast this
            // tick reflects IQ captured ~stampLag earlier; LookupAt rewinds
            // the LO history by that much so mid-retune frames carry the
            // frequency their data belongs to instead of the live NCO.
            // Stable LO (≥ stampLag with no tune) ⇒ identical to the old
            // `state.RadioLoHz` stamp, byte for byte.
            double fftFillMs = sampleRate > 0
                ? AnalyzerFftSizeForStamp / (double)sampleRate * 1000.0
                : 0.0;
            double stampLagMs = 0.5 * fftFillMs
                + (CenterStampLagOverrideMs
                   ?? (CenterStampEmaLagMs
                       + (_p2Client is not null ? CenterStampTransportP2Ms : CenterStampTransportP1Ms)));
            long stampLagTicks = (long)(stampLagMs / 1000.0 * Stopwatch.Frequency);
            long centerHz = _loHistory.LookupAt(
                Stopwatch.GetTimestamp() - stampLagTicks,
                fallbackLoHz: state.RadioLoHz);

            // Cache for the frequency-calibration service (issue #325). The
            // cal reads from this cache to avoid racing for WDSP's "fresh
            // frame" flag — Tick consumes that flag at 30 Hz, leaving no
            // window for a parallel consumer. Cache only when we actually
            // got pan data this tick.
            if (pan)
            {
                lock (_calPanLock)
                {
                    Array.Copy(panBuf, _calPanSnapshot, Width);
                    _calPanHzPerPixel = hzPerPixel;
                    _calPanCenterHz = centerHz;
                    _calPanSnapshotMs = (long)nowMs;
                }
            }
            if (wf)
            {
                lock (_calPanLock)
                {
                    Array.Copy(wfBuf, _diagWfSnapshot, Width);
                    _diagWfSnapshotMs = (long)nowMs;
                }
            }

            var frame = new DisplayFrame(
                Seq: ++_seq,
                TsUnixMs: nowMs,
                RxId: 0,
                BodyFlags: flags,
                Width: Width,
                // Panadapter centres on the radio's actual LO, which equals
                // VfoHz outside CW and VfoHz ∓ cw_pitch in CWU/CWL. The CW filter
                // (audio passband centred on cw_pitch) then renders on top of
                // the dial line via PassbandOverlay's `centerHz + filterLow..high`.
                CenterHz: centerHz,
                HzPerPixel: hzPerPixel,
                PanDb: panBuf,
                WfDb: wfBuf);

            lock (_calPanLock)
            {
                _diagDisplayFrameMs = (long)nowMs;
                _diagDisplaySeq = frame.Seq;
                _diagDisplayFrameCount++;
                _diagLastPanValid = pan;
                _diagLastWfValid = wf;
                _diagLastPanSource = panSource;
                _diagLastWfSource = wfSource;
                _diagLastKeyed = _keyed;
                _diagLastPsMonitorRequested = _psMonitorEnabled;
                _diagLastPsFeedbackCorrecting = psFeedbackCorrecting;
            }

            _hub.Broadcast(frame);

            if (state.Rx2Enabled && rx2Channel >= 0)
            {
                bool rx2Pan = engine.TryGetDisplayPixels(rx2Channel, DisplayPixout.Panadapter, _rx2PanBuf);
                bool rx2Wf = engine.TryGetDisplayPixels(rx2Channel, DisplayPixout.Waterfall, _rx2WfBuf);
                if (rx2Pan) Array.Reverse(_rx2PanBuf);
                if (rx2Wf) Array.Reverse(_rx2WfBuf);
                if (rx2Pan) SanitizeDisplayBuffer(_rx2PanBuf);
                if (rx2Wf) SanitizeDisplayBuffer(_rx2WfBuf);

                var rx2Flags = DisplayBodyFlags.None;
                if (rx2Pan) rx2Flags |= DisplayBodyFlags.PanValid;
                if (rx2Wf) rx2Flags |= DisplayBodyFlags.WfValid;

                _dispTicks++;
                if (pan) _rx1PanCnt++;
                if (wf) _rx1WfCnt++;
                if (rx2Pan) _rx2PanCnt++;
                if (rx2Wf) _rx2WfCnt++;
                long dispNowMs = Environment.TickCount64;
                if (dispNowMs - _dispFlagLogMs >= 1000)
                {
                    _dispFlagLogMs = dispNowMs;
                    _log.LogInformation(
                        "p2.display.flags ticks={T} rx1pan={A} rx1wf={B} rx2pan={C} rx2wf={D}",
                        _dispTicks, _rx1PanCnt, _rx1WfCnt, _rx2PanCnt, _rx2WfCnt);
                    _dispTicks = _rx1PanCnt = _rx1WfCnt = _rx2PanCnt = _rx2WfCnt = 0;
                }

                // Stamp the RX2 frame with its DDC centre (CTUN-frozen under
                // CTUN), so the panel holds still and the dial roams — matching
                // RX1. UpdateRx2Lo is idempotent; calling it here guarantees the
                // centre is fresh for this frame regardless of the tick path.
                UpdateRx2Lo(state);
                var rx2Frame = new DisplayFrame(
                    Seq: ++_seq,
                    TsUnixMs: nowMs,
                    RxId: 1,
                    BodyFlags: rx2Flags,
                    Width: Width,
                    CenterHz: _rx2LoHz,
                    HzPerPixel: hzPerPixel,
                    PanDb: _rx2PanBuf,
                    WfDb: _rx2WfBuf);

                _hub.Broadcast(rx2Frame);
            }
        }
        else
        {
            // Still reset the PS-monitor tick counter on no-client ticks so a
            // fresh client doesn't pick up a stale gate counter.
            _psMonitorTickCount = 0;
        }

        // Audio broadcast — when TX monitor is on, replace RX audio with the
        // monitor channel's demodulated TX audio so the operator hears the
        // chain output (post-bandpass / post-CFIR, demodulated back to mono)
        // instead of band RX. This unifies "monitor while keyed" (Thetis MON
        // semantics) and "preview without keying" (audio passes through the
        // chain so VST plugins receive samples and their meters animate). RX
        // is drained anyway so the WDSP audio ring doesn't back up — we just
        // don't broadcast it. The VST RX seam still fires on the drained RX
        // so RX-side plugins keep running even while monitor is on.
        bool txMonitorOn = engine.IsTxMonitorOn;
        int audioSampleCount = engine.ReadAudio(channel, audioBuf);
        int rx2AudioSampleCount = 0;
        if (state.Rx2Enabled && rx2Channel >= 0)
        {
            // RX1 is the audio clock-master when both receivers are mixed: read
            // at most rx1Count samples from RX2 so the mixed stream is fed at
            // exactly the RX sample rate. Draining RX2's ring fully and mixing
            // max(rx1,rx2) over-feeds the sink — the two channels' per-tick
            // counts jitter independently, so E[max] exceeds the true rate
            // (~5% measured on a G2), saturating the output ring → overrun →
            // dual-RX clicking (#787). The unread RX2 remainder stays buffered
            // for the next tick, so no RX2 audio is dropped. RX2-only mode still
            // drains RX2 fully (RX2 is the master there).
            Span<float> rx2Span = state.Rx2AudioMode == Rx2AudioMode.Both
                ? _rx2AudioBuf.AsSpan(0, Math.Min(audioSampleCount, _rx2AudioBuf.Length))
                : _rx2AudioBuf.AsSpan();
            rx2AudioSampleCount = engine.ReadAudio(rx2Channel, rx2Span);
        }
        audioSampleCount = SelectRxAudio(
            state.Rx2AudioMode,
            audioBuf,
            audioSampleCount,
            _rx2AudioBuf,
            rx2AudioSampleCount);
        if (audioSampleCount > 0)
        {
            SanitizeAudioBuffer(audioBuf.AsSpan(0, audioSampleCount));
            rxAudioRmsForMeter = Rms(audioBuf.AsSpan(0, audioSampleCount));

            // MOX-edge fade envelope. Ramps the first ~5 ms of this block
            // either down (rising edge: last block before TX silence) or up
            // (falling edge: first block of RX resume). Each flag is a
            // one-shot — cleared after applying so steady-state audio is
            // untouched. See _rxFadeOutPending / _rxFadeInPending declarations
            // for the click pathology this addresses.
            if (_rxFadeOutPending)
            {
                int n = Math.Min(RxFadeSamples, audioSampleCount);
                for (int i = 0; i < n; i++)
                {
                    float ramp = 1f - (float)(i + 1) / n;
                    audioBuf[i] *= ramp;
                }
                if (audioSampleCount > n)
                    Array.Clear(audioBuf, n, audioSampleCount - n);
                _rxFadeOutPending = false;
            }
            else if (_rxFadeInPending)
            {
                int n = Math.Min(RxFadeSamples, audioSampleCount);
                for (int i = 0; i < n; i++)
                {
                    float ramp = (float)(i + 1) / n;
                    audioBuf[i] *= ramp;
                }
                _rxFadeInPending = false;
            }

            // The TX voice-processing audio chain (Compressor/EQ/VST etc.)
            // stays TX-only by design (operator decision 2026-04-30) — those
            // plugins are tuned for the mic path and share TXA-side instances.
            // RX audio plugins are a SEPARATE chain, declared by the
            // rx.post-demod manifest slot, wired through _rxAudioPluginHandler
            // below. The two never share plugin instances or IIR state.

            if (!txMonitorOn)
            {
                var squelch = state.Squelch ?? new SquelchConfig();
                UpdateAdaptiveSquelchMeter(
                    _adaptiveSquelch,
                    squelch,
                    AudioRmsToFallbackDbm(rxAudioRmsForMeter));

                // RX audio plugin insert (rx.post-demod slot, e.g. a CW SCAF
                // audio filter). Runs in place over the demodulated band audio
                // AFTER the MOX fade and BEFORE the sidetone mix, so the filter
                // shapes received audio without distorting the clean local
                // sidetone. Null handler (no RX plugin attached) is the common
                // case and a no-op — the RX path stays bit-identical.
                var rxAudioHandler = _rxAudioPluginHandler;
                if (rxAudioHandler is not null && audioSampleCount > 0)
                    rxAudioHandler(audioBuf.AsSpan(0, audioSampleCount), audioSampleCount, AudioOutputRateHz);

                ApplyAdaptiveSquelch(
                    audioBuf.AsSpan(0, audioSampleCount),
                    squelch,
                    _adaptiveSquelch);

                // Final receive loudness guard. WDSP AGC and supported NR have
                // already run by this point (and any RX audio plugin has had
                // its shot), so weak cleaned audio can be lifted without
                // letting a sudden strong signal blast the speaker.
                ApplyRxAudioLeveler(
                    audioBuf.AsSpan(0, audioSampleCount),
                    ref _rxAudioLeveler);

                // CW sidetone is mixed (+=) into the RX block so every
                // downstream sink — browser WS, native audio, TCI audio
                // stream — hears it on the same bus as band RX. The MOX
                // fade above silences the RXA contribution while keying;
                // when the sidetone source is idle, RenderInto returns
                // false immediately without touching the buffer.
                _sidetone?.RenderInto(audioBuf.AsSpan(0, audioSampleCount));

                // Mix any queued local-playback monitor audio (e.g. the Recorder
                // plugin playing a clip back while not transmitting) into the RX
                // block, so it reaches every sink in browser and desktop modes
                // alike. No-op (one volatile read) when nothing is queued.
                MixMonitorInject(audioBuf.AsSpan(0, audioSampleCount));
                LimitRxAudioBuffer(audioBuf.AsSpan(0, audioSampleCount));
                double finalAudioRms = Rms(audioBuf.AsSpan(0, audioSampleCount));
                double finalAudioPeak = PeakAbs(audioBuf.AsSpan(0, audioSampleCount));

                var audioFrame = new AudioFrame(
                    Seq: ++_audioSeq,
                    TsUnixMs: nowMs,
                    RxId: 0,
                    Channels: 1,
                    SampleRateHz: (uint)AudioOutputRateHz,
                    SampleCount: (ushort)audioSampleCount,
                    Samples: new ReadOnlyMemory<float>(audioBuf, 0, audioSampleCount));
                CaptureAudioDiagnostics("rx", in audioFrame, finalAudioRms, finalAudioPeak, txMonitorOn, squelch);
                PublishAudio(in audioFrame);
                RxAudioAvailable?.Invoke(0, AudioOutputRateHz, new ReadOnlyMemory<float>(audioBuf, 0, audioSampleCount));
            }
        }
        if (txMonitorOn)
        {
            // Drain whatever the monitor RXA produced this tick. The buffer
            // shape matches the RX path (mono float32 @ 48 kHz) so it slots
            // into the same AudioFrame format with no front-end change. When
            // the chain is idle (no MOX, no mic) the monitor channel produces
            // silence, which is the correct behaviour for "preview mode but
            // operator isn't talking".
            int monCount = engine.ReadTxMonitorAudio(audioBuf.AsSpan());
            if (monCount > 0)
            {
                SanitizeAudioBuffer(audioBuf.AsSpan(0, monCount));
                double finalAudioRms = Rms(audioBuf.AsSpan(0, monCount));
                double finalAudioPeak = PeakAbs(audioBuf.AsSpan(0, monCount));

                var monFrame = new AudioFrame(
                    Seq: ++_audioSeq,
                    TsUnixMs: nowMs,
                    RxId: 0,
                    Channels: 1,
                    SampleRateHz: (uint)AudioOutputRateHz,
                    SampleCount: (ushort)monCount,
                    Samples: new ReadOnlyMemory<float>(audioBuf, 0, monCount));
                CaptureAudioDiagnostics("tx-monitor", in monFrame, finalAudioRms, finalAudioPeak, txMonitorOn, state.Squelch ?? new SquelchConfig());
                // Meter-only monitor (Auto Tune): the chain ran and the stage
                // meters animated above, but suppress the broadcast so the
                // operator hears nothing while the sample is captured in the
                // background. The TX-air tap below still fires (read-only).
                if (!_txMonitorMeterOnly)
                    PublishAudio(in monFrame);
                // TX-air tap source: the processed transmit audio (what goes on
                // the air). Read-only fan-out to IRxAudioTapPlugin/ITxAudioTapPlugin
                // taps; null subscriber list = no cost.
                TxMonitorAudioAvailable?.Invoke(0, AudioOutputRateHz, new ReadOnlyMemory<float>(audioBuf, 0, monCount));
            }
        }

        if (++_rxMeterTickMod >= RxMeterTickModulus)
        {
            _rxMeterTickMod = 0;
            double rxCalOffsetDb = RadioCalibrations.RxMeterOffsetDb(
                _radio.EffectiveBoardKind,
                _radio.EffectiveOrionMkIIVariant);

            // Prefer WDSP's S-meter when it's ticking. In this
            // integration the meter tap reads -400 ("didn't run") — needs
            // deeper WDSP state debugging to chase down. Until then, fall
            // back to RMS of the already-flowing post-demod audio ring, which
            // gives a "proof of life" meter that moves with band activity.
            double rawDbm = engine.GetRxaSignalDbm(channel);
            double dbm;
            if (double.IsFinite(rawDbm) && rawDbm > -399.0)
            {
                dbm = ApplyRxMeterCalibration(rawDbm, rxCalOffsetDb);
            }
            else
            {
                // 0 dBFS audio ~= S9+ signal; calibrate against ambient band
                // noise later. Empirical offset of -50 dBm puts typical 20m
                // band noise near S2/S3 instead of pinning at S0.
                double rms = double.IsFinite(rxAudioRmsForMeter) ? rxAudioRmsForMeter : 0.0;
                dbm = AudioRmsToFallbackDbm(rms);
            }
            if (!double.IsFinite(dbm)) dbm = -160.0;
            _hub.Broadcast(new RxMeterFrame((float)dbm));
            RxMeterUpdated?.Invoke(channel, dbm);

            // Additive 0x19 broadcast (RxMetersV2Frame). Carries the full
            // set of WDSP RXA stage readings so the configurable Meters
            // Panel can render any of them; older clients that only know
            // 0x14 ignore this frame. Same 5 Hz cadence as 0x14 above.
            //
            var rx = engine.GetRxStageMeters(channel);
            var v2 = BuildRxMetersV2(rx, rxCalOffsetDb);
            _radio.HandleRxMetersForAutoAgc(dbm, v2.AdcPk, v2.AgcGain, Environment.TickCount64);
            lock (_rxMeterDiagLock)
            {
                _diagRxMetersValid = true;
                _diagRxMetersMs = (long)nowMs;
                _diagRxMetersChannelId = channel;
                _diagRxDbm = dbm;
                _diagRxMeters = v2;
            }
            _hub.Broadcast(v2);
            RxMetersV2Updated?.Invoke(channel, v2);
        }
    }

    /// <summary>
    /// Raised when an RXA stage-meter snapshot is broadcast (approximately
    /// 5 Hz, alongside <see cref="RxMeterUpdated"/>). Arguments:
    /// (channelId, frame). Test seam — the broadcast itself is a no-op
    /// when no clients are attached, so this event lets unit tests
    /// observe the encoded frame without instantiating a WebSocket.
    /// </summary>
    public event Action<int, RxMetersV2Frame>? RxMetersV2Updated;

    /// <summary>
    /// Build the wire frame from a raw <see cref="RxStageMeters"/>
    /// snapshot, applying <paramref name="calOffsetDb"/> only to the
    /// dBm-scale fields (Signal*, AgcEnv*). ADC* is dBFS (raw ADC,
    /// board-independent) and AgcGain is dB of insertion gain — both get
    /// the raw value. Exposed for unit tests so the encoding rule can be
    /// asserted without spinning up a hub or pipeline tick.
    /// </summary>
    public static RxMetersV2Frame BuildRxMetersV2(in RxStageMeters rx, double calOffsetDb)
    {
        float cal = (float)calOffsetDb;
        return new RxMetersV2Frame(
            SignalPk: ApplyRxMeterCalibration(rx.SignalPk, cal),
            SignalAv: ApplyRxMeterCalibration(rx.SignalAv, cal),
            AdcPk: rx.AdcPk,
            AdcAv: rx.AdcAv,
            AgcGain: rx.AgcGain,
            AgcEnvPk: ApplyRxMeterCalibration(rx.AgcEnvPk, cal),
            AgcEnvAv: ApplyRxMeterCalibration(rx.AgcEnvAv, cal));
    }

    private static double ApplyRxMeterCalibration(double value, double calOffsetDb) =>
        value <= -199.5 ? value : value + calOffsetDb;

    private static float ApplyRxMeterCalibration(float value, float calOffsetDb) =>
        value <= -199.5f ? value : value + calOffsetDb;
}

public sealed record DspNrRuntimeSnapshot(
    bool WdspActive,
    bool WdspNativeLoadable,
    bool WdspEmnrPost2Available,
    bool WdspNr4SbnrAvailable,
    string Nr4Readiness,
    string RequestedNrMode,
    string EffectiveNrMode);

internal sealed record DspRxChainDiagnosticsDto(
    int SchemaVersion,
    string Status,
    string Mode,
    int FilterLowHz,
    int FilterHighHz,
    string? FilterPresetName,
    string AgcMode,
    double AgcTopDb,
    bool AutoAgcEnabled,
    double AgcOffsetDb,
    double EffectiveAgcTopDb,
    bool SquelchEnabled,
    bool SquelchAdaptive,
    int SquelchLevel,
    string RequestedNrMode,
    string EffectiveNrMode,
    bool AnfEnabled,
    bool SnbEnabled,
    bool NbpNotchesEnabled,
    bool EffectiveNbpNotchesRun,
    string NbMode,
    double NbThreshold,
    int ManualNotchCount,
    int ActiveManualNotchCount,
    bool WdspActive,
    bool WdspNativeLoadable,
    bool WdspEmnrPost2Available,
    bool WdspNr4SbnrAvailable,
    string Nr4Readiness,
    bool AppliedNrMatchesRequested,
    bool AppliedAgcMatchesRequested,
    bool AppliedSquelchMatchesRequested,
    string[] ActiveFeatures,
    string[] QualityReasons,
    string DiagnosticRecommendation);

internal sealed record RxMetersDiagnosticsDto(
    int SchemaVersion,
    string Status,
    string Source,
    bool Fresh,
    bool Stale,
    long? AgeMs,
    int ChannelId,
    double? RxDbm,
    double? SignalPkDbm,
    double? SignalAvDbm,
    double? AdcPkDbfs,
    double? AdcAvDbfs,
    double? AdcHeadroomDb,
    double? AgcGainDb,
    double? AgcEnvPkDbm,
    double? AgcEnvAvDbm,
    bool SignalUsable,
    bool AdcUsable,
    bool AgcEnvelopeUsable,
    string DiagnosticRecommendation);

internal sealed record RxDynamicRangeActionDto(
    string Id,
    string Label,
    string Status,
    string Notes);

internal sealed record RxDynamicRangeDiagnosticsDto(
    int SchemaVersion,
    string Status,
    string Tone,
    bool Fresh,
    bool Stale,
    long? AgeMs,
    string Source,
    int SampleRateHz,
    int AttenDb,
    int AttOffsetDb,
    int EffectiveAttenDb,
    bool PreampOn,
    bool AutoAttEnabled,
    bool AdcProtectionEnabled,
    bool AdcOverloadWarning,
    int AdcOverloadLevel,
    double TargetHeadroomMinDb,
    double TargetHeadroomMaxDb,
    double? RxDbm,
    double? SignalPkDbm,
    double? AdcPkDbfs,
    double? AdcHeadroomDb,
    double? AgcGainDb,
    bool HeadroomOptimal,
    bool OverloadRisk,
    bool WeakSignalOpportunity,
    bool FrontEndUnderused,
    string[] Reasons,
    RxDynamicRangeActionDto[] Actions,
    string DiagnosticRecommendation);

internal sealed record RxListenabilityDiagnosticsDto(
    int SchemaVersion,
    string Status,
    string Tone,
    bool SignalPresent,
    bool AudioRecovered,
    string Blocker,
    string Recommendation);

internal sealed record AudioPathDiagnosticsDto(
    int SchemaVersion,
    string Status,
    string Source,
    bool Fresh,
    bool Stale,
    long? AgeMs,
    long FramesBroadcast,
    uint LastSeq,
    int SampleRateHz,
    int SampleCount,
    double? RmsLinear,
    double? PeakLinear,
    double? RmsDbfs,
    double? PeakDbfs,
    bool TxMonitorRequested,
    bool SquelchEnabled,
    bool SquelchOpen,
    bool SquelchTailActive,
    double? SquelchGateGain,
    double? RxAudioLevelerInputRmsDbfs,
    double? RxAudioLevelerOutputRmsDbfs,
    double? RxAudioLevelerInputPeakDbfs,
    double? RxAudioLevelerOutputPeakDbfs,
    double? RxAudioLevelerDesiredGainDb,
    double? RxAudioLevelerAppliedGainDb,
    double? RxAudioLevelerGainDeltaDb,
    double? RxAudioLevelerPeakHeadroomDb,
    double? RxAudioLevelerPreLimitPeakDbfs,
    double? RxAudioLevelerOutputLimitReductionDb,
    int? RxAudioLevelerOutputLimitSampleCount,
    int? RxAudioLevelerPauseHoldBlocks,
    bool? RxAudioLevelerBoostSlewLimited,
    bool? RxAudioLevelerPeakLimited,
    bool? RxAudioLevelerOutputLimited,
    string SquelchMode,
    string SquelchGateSource,
    bool SquelchOpenKnown,
    long MonitorBacklogSamples,
    int AudioSinkCount,
    string DiagnosticRecommendation);
