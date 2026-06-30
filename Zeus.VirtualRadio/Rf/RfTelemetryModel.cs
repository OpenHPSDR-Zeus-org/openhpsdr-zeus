// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Contracts;
using Zeus.Server; // internal RadioCalibrations / PaDefaults / BoardCapabilitiesTable, via InternalsVisibleTo

namespace Zeus.VirtualRadio.Rf;

/// <summary>
/// One computed RF telemetry reading: the synthesized ADC counts the host will
/// read back, plus the watts/SWR they correspond to.
/// </summary>
/// <param name="FwdAdc">Forward-power ADC count (0..4095) for the EP6 C&amp;C echo / P2 hi-pri status.</param>
/// <param name="RefAdc">Reflected-power ADC count (0..4095).</param>
/// <param name="FwdWatts">Forward power in watts the reading represents.</param>
/// <param name="RefWatts">Reflected power in watts.</param>
/// <param name="Swr">SWR derived from FWD/REF.</param>
public readonly record struct RfTelemetry(
    ushort FwdAdc,
    ushort RefAdc,
    double FwdWatts,
    double RefWatts,
    double Swr);

/// <summary>
/// Inverts Zeus's own forward-power math so its UNMODIFIED <c>TxMetersService</c>
/// reads back the watts the emulator intends:
/// <c>targetW = f(driveByte, band, MaxWatts)</c> →
/// <c>volts = sqrt(targetW · BridgeVolt)</c> →
/// <c>fwdAdc = clamp(volts/RefVoltage·4095 + AdcCalOffset, 0..4095)</c>, with REF
/// from the bridge identity at a chosen SWR. Constants come from
/// <c>RadioCalibrations.For(Board, Variant)</c> and <c>PaDefaults</c> (consumed
/// here via <c>InternalsVisibleTo</c>), so this is a genuine round-trip test of
/// Zeus's calibration path. Telemetry is PTT/MOX-gated.
/// </summary>
internal sealed class RfTelemetryModel
{
    private readonly RadioCalibration _calibration;

    // Rated PA output the meter lands on at full drive. We deliberately use the
    // calibration record's MaxWatts (the meter-scaling ceiling tied to the SAME
    // bridge constants we invert below) rather than PaDefaults.GetMaxPowerWatts:
    // for the ANAN-10E / HermesII bridge that is 10 W — the board's real rated
    // output — so "push 100 % drive" reads ~10 W, not the 100 W PA-gain-bracket
    // reference. See the return note for the Integrator on the two distinct
    // "max watts" numbers Zeus carries.
    private readonly double _ratedWatts;

    private const int AdcFullScale = 4095;

    /// <summary>Default modelled SWR when no explicit reflection is requested (~1.1:1).</summary>
    public const double DefaultSwr = 1.1;

    public RfTelemetryModel(VirtualRadioProfile profile)
    {
        // RadioCalibrations.For is the calibration table we invert. The other two
        // internal-table calls are the compile-time proof that the emulator can
        // reach Zeus's per-board PA / capability tables through InternalsVisibleTo
        // (discarded — the inversion only needs the bridge constants + rated W).
        _calibration = RadioCalibrations.For(profile.Board, profile.Variant);
        _ratedWatts = _calibration.MaxWatts;
        _ = PaDefaults.GetMaxPowerWatts(profile.Board, profile.Variant);
        _ = BoardCapabilitiesTable.For(profile.Board, profile.Variant);
    }

    /// <summary>
    /// Compute the FWD/REF ADC counts (and the watts they represent) for the
    /// given drive byte and band. Returns an all-zero reading when not keyed.
    ///
    /// Drive→power model: the Protocol-1 drive byte scales the exciter output
    /// amplitude linearly, so RF power ∝ amplitude² ∝ (driveByte/255)². Full
    /// drive lands on <see cref="_ratedWatts"/>. The forward inversion mirrors
    /// <c>TxMetersService.ComputeMeters</c> exactly:
    /// <c>volts = sqrt(W · BridgeVolt)</c> →
    /// <c>adc = volts / RefVoltage · 4095 + AdcCalOffset</c>. REF is derived from
    /// the requested SWR via the bridge identity <c>rho = (s−1)/(s+1)</c>,
    /// <c>refW = fwdW · rho²</c>. The reported watts/SWR are then read BACK out of
    /// the integer ADC counts through Zeus's own <c>ComputeMeters</c>, so
    /// <c>/status</c> mirrors exactly what the client renders and the reading is a
    /// genuine round-trip of the calibration path.
    /// </summary>
    /// <param name="driveByte">Commanded transmit drive level (0..255).</param>
    /// <param name="bandHz">Operating frequency in Hz. Reserved for a future
    /// per-band PA-gain refinement; the Phase-1 curve is band-flat (Zeus already
    /// folds per-band gain into the drive byte it sends).</param>
    /// <param name="mox">Whether the host has the radio keyed (telemetry gate —
    /// FWD/REF are PTT-gated in the firmware, so an unkeyed reading is all-zero).</param>
    /// <param name="swr">Modelled SWR for the reflected reading (default ~1.1:1).</param>
    public RfTelemetry Compute(byte driveByte, long bandHz, bool mox, double swr = DefaultSwr)
    {
        _ = bandHz; // reserved for per-band PA-gain modelling (see remarks)

        // Firmware gates FWD/REF on PTT: an unkeyed radio reports no power.
        if (!mox)
            return new RfTelemetry(0, 0, 0.0, 0.0, 1.0);

        double drive = driveByte / 255.0;
        double targetWatts = _ratedWatts * drive * drive;

        double s = swr < 1.0 ? 1.0 : swr;
        double rho = (s - 1.0) / (s + 1.0);
        double refWatts = targetWatts * rho * rho;

        ushort fwdAdc = WattsToAdc(targetWatts);
        ushort refAdc = WattsToAdc(refWatts);

        // Read the watts/SWR back out of the integer ADC counts using Zeus's own
        // forward math — the reported figures then equal what the client will
        // render, and the unit test that feeds these ADCs through ComputeMeters
        // and recovers targetWatts IS the calibration round-trip guarantee.
        var (fwdW, refW, swrOut) = TxMetersService.ComputeMeters(fwdAdc, refAdc, _calibration);
        return new RfTelemetry(fwdAdc, refAdc, fwdW, refW, swrOut);
    }

    /// <summary>
    /// Invert the forward-power formula to an ADC count. Zero (or negative) watts
    /// map to the bridge baseline <c>AdcCalOffset</c>, which the forward math
    /// subtracts back to exactly 0 W.
    /// </summary>
    private ushort WattsToAdc(double watts)
    {
        if (watts <= 0.0)
            return (ushort)Math.Clamp(_calibration.AdcCalOffset, 0, AdcFullScale);

        double volts = Math.Sqrt(watts * _calibration.BridgeVolt);
        double adc = volts / _calibration.RefVoltage * AdcFullScale + _calibration.AdcCalOffset;
        long rounded = (long)Math.Round(adc, MidpointRounding.AwayFromZero);
        return (ushort)Math.Clamp(rounded, 0, AdcFullScale);
    }
}
