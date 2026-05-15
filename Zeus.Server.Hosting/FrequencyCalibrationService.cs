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
//   2. Tune the LO to (reference + 2 kHz), so the reference station's
//      carrier lands ~2 kHz LOW of LO on the panadapter — clear of the
//      DC bias spike that HPSDR-class radios always emit at LO. (A naive
//      "tune directly to 10 MHz" approach can't distinguish a perfectly-
//      tuned radio from a radio that just doesn't hear WWV, since both
//      produce a peak at exactly LO.)
//   3. Set USB mode + narrow filter + zoom 8 so the full ±100 ppm
//      operating range fits inside the analyzer span.
//   4. Wait for the WDSP analyzer to settle, capture the panadapter.
//   5. Search for the spectral peak *only* in the expected band
//      (-3000..-1000 Hz from LO — where the carrier lives for any
//      crystal drift inside ±100 ppm). Reject the result unless the
//      peak rises ≥ 6 dB above the spectrum median AND ≥ -90 dBFS in
//      absolute terms.
//   6. Compute correction factor:
//        deviationHz = peakOffsetFromLo - expectedOffsetHz   (Hz)
//        factor = 1 + deviationHz / referenceFrequencyHz
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

    // Intentional LO offset above the reference (Hz). Puts the reference
    // carrier ~2 kHz below LO on the panadapter, well clear of the DC
    // bias spike. Pairs with zoom 8 — see CalZoomLevel.
    private const double IntentionalLoOffsetHz = 2000.0;

    // Search band (relative to LO) where the reference carrier is
    // expected to land. With IntentionalLoOffsetHz = 2000 Hz the
    // carrier sits at -2000 Hz for a perfectly-tuned radio; at
    // -3000 Hz for +100 ppm crystal high; at -1000 Hz for -100 ppm
    // crystal low. Searching outside this band catches noise or
    // unrelated interferers, not a real WWV peak.
    private const double SearchMinHz = -3000.0;
    private const double SearchMaxHz = -1000.0;

    // Peak must stand out from the spectrum median by at least this much.
    // Catches the case where the "peak" is just slightly-warm noise — a
    // real WWV carrier sits well above the surrounding median (typically
    // 20-40 dB). Relative threshold only: an absolute dB floor would
    // reject perfectly real but faint signals on quieter SDRs (P2 at
    // 192 kHz commonly has a -125 dB analyzer median, where even a
    // -99 dB carrier is unambiguously a signal).
    private const float MinPeakAboveMedianDb = 6f;

    private const int SettleMs = 2500;
    private const int CaptureRetries = 12;
    private const int CaptureRetryDelayMs = 40;

    // Zoom 8 at 48 kHz P1: ±3000 Hz visible, ~2.9 Hz/pixel resolution —
    // exactly covers the ±100 ppm search band when paired with
    // IntentionalLoOffsetHz = 2 kHz. Zoom 8 at 192 kHz P2: ±12 kHz
    // visible, ~11.7 Hz/pixel.
    private const int CalZoomLevel = 8;

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
                // Configure for calibration. LO sits 2 kHz ABOVE the
                // reference so the carrier lands at -2 kHz on the
                // panadapter — well clear of the DC spike at LO.
                _radio.SetMode(RxMode.USB);
                _radio.SetFilter(100, 2700);
                _radio.SetZoom(CalZoomLevel);
                long commandedLoHz = (long)(referenceFrequencyHz + IntentionalLoOffsetHz);
                _radio.SetVfo(commandedLoHz);

                await Task.Delay(SettleMs, ct).ConfigureAwait(false);

                var pixels = new float[DspPipelineService.PanadapterWidth];
                if (!await TryCaptureWithRetryAsync(pixels, ct).ConfigureAwait(false))
                    return CalibrationResult.CaptureFailed;

                _ = _pipeline.TryCapturePanadapterSnapshot(pixels, out float hzPerPixel, out _);

                // Peak detection in the expected band only. Pixels are in
                // low-left/high-right display order; centre pixel maps to
                // LO. The carrier offset is (peakIndex - width/2) * hzPerPixel.
                int centerIdx = pixels.Length / 2;
                int searchMinIdx = Math.Max(0, centerIdx + (int)Math.Floor(SearchMinHz / hzPerPixel));
                int searchMaxIdx = Math.Min(pixels.Length - 1, centerIdx + (int)Math.Ceiling(SearchMaxHz / hzPerPixel));

                int peakIndex = -1;
                float peakDb = float.NegativeInfinity;
                for (int i = searchMinIdx; i <= searchMaxIdx; i++)
                {
                    if (pixels[i] > peakDb)
                    {
                        peakDb = pixels[i];
                        peakIndex = i;
                    }
                }

                if (peakIndex < 0) return CalibrationResult.CaptureFailed;

                float medianDb = Median(pixels);

                _log.LogInformation(
                    "freqcal.scan searchMin={Min}px searchMax={Max}px peakIdx={Pk} peakDb={Db:F1} medianDb={Med:F1} hzPerPx={Hpp:F2}",
                    searchMinIdx, searchMaxIdx, peakIndex, peakDb, medianDb, hzPerPixel);

                if (peakDb - medianDb < MinPeakAboveMedianDb)
                    return CalibrationResult.NoSignal(peakDb, medianDb);

                double peakOffsetFromLoHz = (peakIndex - centerIdx) * (double)hzPerPixel;
                double deviationHz = peakOffsetFromLoHz - (-IntentionalLoOffsetHz);

                if (Math.Abs(deviationHz) > 1000.0)
                    return CalibrationResult.OffsetOutOfRange(deviationHz, peakDb);

                // factor = 1 + (deviationHz / referenceHz). Derivation:
                //   commandedLO = referenceHz + IntentionalLoOffsetHz
                //   actualLO    = commandedLO * (1 + e)  (e = crystal error)
                //   peak appears at refHz - actualLO ≈ -IntentionalLoOffsetHz - referenceHz*e
                //   so deviationHz = peakPos - (-IntentionalLoOffsetHz) = -referenceHz*e
                //   ⇒ e = -deviationHz / referenceHz
                //   to compensate, factor = 1/(1+e) ≈ 1 - e = 1 + deviationHz/referenceHz.
                double factor = 1.0 + (deviationHz / referenceFrequencyHz);
                double applied = _radio.SetFrequencyCorrectionFactor(factor);

                _log.LogInformation(
                    "freqcal.success peakOffFromLo={Pol:F1}Hz deviationFromExpected={Dev:F1}Hz peakDb={Db:F1} medianDb={Med:F1} factor={Factor:F9} applied={Applied:F9}",
                    peakOffsetFromLoHz, deviationHz, peakDb, medianDb, factor, applied);

                return CalibrationResult.Success(deviationHz, peakDb, applied);
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

    private static float Median(ReadOnlySpan<float> values)
    {
        // Selection-by-sort: spectrum width is 2048, so a one-shot sort
        // of a copy is cheap (~30 µs) and avoids the boundary trickery
        // of a partial-quickselect for an even count. Cal runs once per
        // click, so the temp array cost is irrelevant.
        var buf = values.ToArray();
        Array.Sort(buf);
        int mid = buf.Length / 2;
        return (buf.Length % 2 == 0) ? (buf[mid - 1] + buf[mid]) / 2f : buf[mid];
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

    public static CalibrationResult NoSignal(float peakDb, float medianDb = float.NaN) => new(
        CalibrationOutcome.NoSignal, null, peakDb, null,
        float.IsNaN(medianDb)
            ? $"No signal detected at the reference frequency (peak {peakDb:F1} dB)."
            : $"No signal detected at the reference frequency (peak {peakDb:F1} dB, median {medianDb:F1} dB — peak must rise ≥ 6 dB above median).");

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
