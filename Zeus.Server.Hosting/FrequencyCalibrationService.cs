// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

using Zeus.Contracts;

namespace Zeus.Server;

// Per-radio frequency calibration (issue #325). One-shot procedure
// modelled on Thetis's `Console.WWVCalibration` (console.cs:9779-9854):
//
//   1. Snapshot operator state (VFO / mode / filter / zoom).
//   2. Tune to a known reference (WWV 10 MHz by default).
//   3. Set USB mode + narrow filter + high zoom so the WWV carrier sits
//      inside the panadapter's centre bin range.
//   4. Wait for the WDSP analyzer to settle, capture the panadapter.
//   5. Find the spectral peak. Reject when below the noise-floor sanity
//      threshold or further from LO than the ±100 ppm operating range.
//   6. Compute correction factor = 1 + (peak_offset_Hz / reference_Hz)
//      and persist it via RadioService.SetFrequencyCorrectionFactor
//      (write-through to PreferredRadioStore, push to live P1/P2 client,
//      re-tune so the new factor takes effect immediately).
//   7. Restore operator state — VFO / mode / filter / zoom — in finally,
//      so a failed cal leaves the operator exactly where they were.
//
// All four reference clients (piHPSDR, deskHPSDR, Thetis mainline,
// mi0bot HL2 fork) use the same multiplicative-correction-at-tune-write
// model; the per-board variation is in *where* the factor is applied
// (host-side, never on a clock register), which is what Zeus already
// does at `Protocol1Client.SetVfoAHz` + `Protocol2Client.SetVfoAHz`.
public sealed class FrequencyCalibrationService
{
    public const double DefaultReferenceFrequencyHz = 10_000_000.0;

    // Lower bound on peak strength relative to the analyzer noise floor.
    // -90 dBFS rejects a typical empty band on the panadapter; WWV at a
    // city QTH normally lands -60..-30 dBFS in a 3 Hz/pixel bin.
    private const float NoiseFloorDbThreshold = -90f;

    // ±100 ppm at 10 MHz = ±1000 Hz; matches piHPSDR's frequency_calibration
    // bounds. Anything wider almost certainly means the operator is far off
    // the reference station, not a real calibration condition.
    private const double MaxAcceptableOffsetHz = 1000.0;

    private const int SettleMs = 1500;
    private const int CaptureRetries = 12;
    private const int CaptureRetryDelayMs = 40;

    // Zoom 16 at 48 kHz P1: ±1500 Hz visible, ~1.5 Hz/pixel resolution.
    // Zoom 16 at 192 kHz P2: ±6000 Hz visible, ~5.9 Hz/pixel resolution.
    // Wide enough to catch the entire ±100 ppm range, narrow enough that
    // a single peak dominates the spectrum at WWV signal strengths.
    private const int CalZoomLevel = 16;

    private readonly RadioService _radio;
    private readonly DspPipelineService _pipeline;
    private readonly ILogger<FrequencyCalibrationService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FrequencyCalibrationService(
        RadioService radio,
        DspPipelineService pipeline,
        ILogger<FrequencyCalibrationService> log)
    {
        _radio = radio;
        _pipeline = pipeline;
        _log = log;
    }

    /// <summary>
    /// Run the auto-calibration procedure. Concurrent invocations are
    /// rejected — only one cal at a time. The radio must be connected; if
    /// not, returns <see cref="CalibrationResult.NotConnected"/>.
    /// </summary>
    public async Task<CalibrationResult> CalibrateAsync(
        double referenceFrequencyHz = DefaultReferenceFrequencyHz,
        CancellationToken ct = default)
    {
        if (referenceFrequencyHz <= 0 || referenceFrequencyHz > 60_000_000)
            throw new ArgumentOutOfRangeException(nameof(referenceFrequencyHz));

        // Single-shot lock. WaitAsync(0) — fail fast if a previous cal is
        // still running rather than queueing a second attempt.
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return CalibrationResult.Busy;

        try
        {
            var startSnap = _radio.Snapshot();
            if (startSnap.Status != ConnectionStatus.Connected)
                return CalibrationResult.NotConnected;

            // Snapshot operator state — restore in finally regardless of outcome.
            long origVfoHz = startSnap.VfoHz;
            RxMode origMode = startSnap.Mode;
            int origFilterLo = startSnap.FilterLowHz;
            int origFilterHi = startSnap.FilterHighHz;
            int origZoom = startSnap.ZoomLevel;

            _log.LogInformation(
                "freqcal.start ref={Ref}Hz origVfo={Vfo} origMode={Mode} origZoom={Zoom}",
                referenceFrequencyHz, origVfoHz, origMode, origZoom);

            try
            {
                // Configure for calibration. USB at 10 MHz puts the WWV
                // carrier exactly at LO (panadapter centre); a narrow filter
                // keeps audio CPU low and predictable.
                _radio.SetMode(RxMode.USB);
                _radio.SetFilter(100, 2700);
                _radio.SetZoom(CalZoomLevel);
                _radio.SetVfo((long)referenceFrequencyHz);

                await Task.Delay(SettleMs, ct).ConfigureAwait(false);

                var pixels = new float[DspPipelineService.PanadapterWidth];
                if (!await TryCaptureWithRetryAsync(pixels, ct).ConfigureAwait(false))
                    return CalibrationResult.CaptureFailed;

                long centerHz;
                float hzPerPixel;
                _pipeline.TryCapturePanadapterSnapshot(pixels, out hzPerPixel, out centerHz);

                // Peak detection. Pixels are in low-left/high-right display
                // order; centre pixel maps to LO. The carrier offset is
                // (peakIndex - width/2) * hzPerPixel.
                int peakIndex = 0;
                float peakDb = float.NegativeInfinity;
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i] > peakDb)
                    {
                        peakDb = pixels[i];
                        peakIndex = i;
                    }
                }

                if (peakDb < NoiseFloorDbThreshold)
                    return CalibrationResult.NoSignal(peakDb);

                double offsetHz = (peakIndex - pixels.Length / 2.0) * hzPerPixel;

                if (Math.Abs(offsetHz) > MaxAcceptableOffsetHz)
                    return CalibrationResult.OffsetOutOfRange(offsetHz, peakDb);

                // factor = 1 + (offsetHz / referenceHz). Derivation:
                //   actual_LO = commanded * (1 + e)  where e = crystal error
                //   peak appears at (true_freq - actual_LO) on the panadapter,
                //   so offsetHz = -commanded * e  ⇒  e = -offsetHz / commanded
                //   to compensate, factor = 1/(1+e) ≈ 1 - e = 1 + offsetHz/commanded.
                double factor = 1.0 + (offsetHz / referenceFrequencyHz);
                double applied = _radio.SetFrequencyCorrectionFactor(factor);

                _log.LogInformation(
                    "freqcal.success offset={Off:F2}Hz peakDb={Db:F1} factor={Factor:F9} applied={Applied:F9}",
                    offsetHz, peakDb, factor, applied);

                return CalibrationResult.Success(offsetHz, peakDb, applied);
            }
            finally
            {
                // Restore — in order: mode (resets family filter cache),
                // filter (overrides the family default), zoom, VFO.
                try { _radio.SetMode(origMode); } catch (Exception ex) { _log.LogWarning(ex, "freqcal.restore mode"); }
                try { _radio.SetFilter(origFilterLo, origFilterHi); } catch (Exception ex) { _log.LogWarning(ex, "freqcal.restore filter"); }
                try { _radio.SetZoom(origZoom); } catch (Exception ex) { _log.LogWarning(ex, "freqcal.restore zoom"); }
                try { _radio.SetVfo(origVfoHz); } catch (Exception ex) { _log.LogWarning(ex, "freqcal.restore vfo"); }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reset the per-radio correction factor to 1.0 (no correction). Same
    /// write-through-then-push path as SetFrequencyCorrectionFactor.
    /// </summary>
    public void Reset() => _radio.SetFrequencyCorrectionFactor(1.0);

    private async Task<bool> TryCaptureWithRetryAsync(float[] dest, CancellationToken ct)
    {
        // TryGetDisplayPixels returns false when no fresh FFT is ready
        // (the WDSP worker is still producing the first frame after
        // SetVfo's re-tune, or the analyzer reconfig from SetZoom is
        // still settling). Retry briefly to give the pipeline time to
        // produce a fresh frame.
        for (int attempt = 0; attempt < CaptureRetries; attempt++)
        {
            if (_pipeline.TryCapturePanadapterSnapshot(dest, out _, out _))
                return true;
            await Task.Delay(CaptureRetryDelayMs, ct).ConfigureAwait(false);
        }
        return false;
    }
}

/// <summary>
/// Result of a calibration run. Encoded as a discriminated record so the
/// REST surface can serialise both successes and the various failure
/// modes uniformly.
/// </summary>
public sealed record CalibrationResult(
    CalibrationOutcome Outcome,
    double? OffsetHz,
    float? PeakDb,
    double? AppliedFactor,
    string Message)
{
    public static readonly CalibrationResult Busy = new(
        CalibrationOutcome.Busy, null, null, null,
        "A calibration is already in progress.");

    public static readonly CalibrationResult NotConnected = new(
        CalibrationOutcome.NotConnected, null, null, null,
        "No radio is connected. Connect first, then run calibration.");

    public static readonly CalibrationResult CaptureFailed = new(
        CalibrationOutcome.CaptureFailed, null, null, null,
        "Panadapter snapshot was not available — engine offline or pipeline stalled.");

    public static CalibrationResult NoSignal(float peakDb) => new(
        CalibrationOutcome.NoSignal, null, peakDb, null,
        $"No signal detected at the reference frequency (peak {peakDb:F1} dB below noise floor).");

    public static CalibrationResult OffsetOutOfRange(double offsetHz, float peakDb) => new(
        CalibrationOutcome.OffsetOutOfRange, offsetHz, peakDb, null,
        $"Measured offset {offsetHz:F1} Hz exceeds ±1 kHz at 10 MHz — likely tuned to the wrong reference or strong interferer.");

    public static CalibrationResult Success(double offsetHz, float peakDb, double appliedFactor) => new(
        CalibrationOutcome.Success, offsetHz, peakDb, appliedFactor,
        $"Calibration applied: {offsetHz:+0.0;-0.0;0.0} Hz at 10 MHz ({(appliedFactor - 1.0) * 1e6:+0.000;-0.000;0.000} ppm).");
}

public enum CalibrationOutcome
{
    Success,
    Busy,
    NotConnected,
    CaptureFailed,
    NoSignal,
    OffsetOutOfRange,
}
