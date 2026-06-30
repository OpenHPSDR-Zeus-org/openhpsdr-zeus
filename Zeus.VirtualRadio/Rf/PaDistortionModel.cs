// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.VirtualRadio.Rf;

/// <summary>
/// A mild memoryless power-amplifier model — AM/AM compression plus AM/PM phase
/// rotation — applied to the clean TX-DAC reference to synthesize the
/// PureSignal feedback "coupler" sample. With distortion disabled the model is
/// the identity, so PS converges to a unity correction (a useful smoke test);
/// with it enabled WDSP <c>calcc</c> has a non-trivial curve to fit, exercising
/// the full convergence path against a radio that will never saturate.
///
/// This is a SIMULATION of a PA for a software radio with no real RF — it is
/// deliberately gentle and bounded so the feedback can never blow past full
/// scale. It is NOT a calibration model and carries no operator-felt defaults.
/// </summary>
internal sealed class PaDistortionModel
{
    private readonly bool _enabled;

    // AM/AM: third-order compression coefficient (gain droops as |x|² grows).
    // AM/PM: phase rotates a few degrees at full envelope. Both small so the
    // feedback stays inside [-1, 1] and the correction is well-conditioned.
    private const double Am3 = 0.18;     // compression depth
    private const double AmPmRad = 0.20; // radians of phase rotation at unit envelope

    public PaDistortionModel(bool enabled) => _enabled = enabled;

    /// <summary>Whether this model applies any distortion (false = identity).</summary>
    public bool Enabled => _enabled;

    /// <summary>
    /// Map a clean reference sample (<paramref name="i"/>, <paramref name="q"/>)
    /// to the coupler sample the radio would see through a real PA. Identity
    /// when disabled.
    /// </summary>
    public (double I, double Q) Apply(double i, double q)
    {
        if (!_enabled)
            return (i, q);

        double mag2 = i * i + q * q;

        // AM/AM: gain = 1 - Am3·|x|², clamped so it never inverts.
        double gain = 1.0 - Am3 * mag2;
        if (gain < 0.05) gain = 0.05;

        // AM/PM: rotate by an envelope-dependent angle.
        double theta = AmPmRad * mag2;
        double c = Math.Cos(theta);
        double s = Math.Sin(theta);

        double di = gain * (i * c - q * s);
        double dq = gain * (i * s + q * c);
        return (di, dq);
    }
}
