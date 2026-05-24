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

namespace Zeus.Dsp;

/// <summary>Pure static math for the Zero Beat feature (issue #300).
/// No WDSP, no I/O, no allocations on the hot path beyond what the caller provides.
/// All sizes are in FFT bins; the caller does Hz↔bin conversion.</summary>
public static class ZeroBeatAlgorithm
{
    // Indistinguishable from floating-point noise at dB scale (max |denom|
    // for a strongly peaked dB triplet is ~40; 1e-12 leaves 11+ orders of
    // headroom). Below this, treat the triplet as flat and skip interpolation.
    private const double ParabolicDenomEpsilon = 1e-12;

    /// <summary>Linear scan over <paramref name="bins"/> in the inclusive
    /// range <c>[lowBin, highBin]</c>, returns the bin with the maximum value
    /// and its value. Caller must ensure <c>0 ≤ lowBin ≤ highBin &lt; bins.Length</c>.</summary>
    public static (int bin, double db) FindPeakInPassband(
        ReadOnlySpan<double> bins, int lowBin, int highBin)
    {
        int peakIdx = lowBin;
        double peakDb = bins[lowBin];
        for (int i = lowBin + 1; i <= highBin; i++)
        {
            if (bins[i] > peakDb) { peakDb = bins[i]; peakIdx = i; }
        }
        return (peakIdx, peakDb);
    }

    /// <summary>Sub-bin offset of the true peak between bins
    /// <c>peak-1, peak, peak+1</c>, derived by fitting a parabola through the
    /// three dB magnitudes. Returns a value in <c>[-0.5, +0.5]</c>; positive
    /// means the true peak is between the center bin and the right neighbour.
    /// Caller adds this offset to the integer peak-bin index for sub-bin
    /// frequency resolution.</summary>
    public static double ParabolicInterpolate(double yLeft, double yPeak, double yRight)
    {
        double denom = yLeft - 2.0 * yPeak + yRight;
        if (Math.Abs(denom) < ParabolicDenomEpsilon) return 0.0;            // flat triplet → no info
        double offset = 0.5 * (yLeft - yRight) / denom;
        return Math.Clamp(offset, -0.5, 0.5);
    }

    /// <summary>Median dB value across the passband bins <c>[lowBin, highBin]</c>.
    /// Used as the noise-floor baseline for the SNR gate. Allocates a temporary
    /// copy of the passband; the caller is responsible for keeping passbands
    /// narrow enough (max a few thousand bins) that this stays cheap.</summary>
    public static double MedianDb(ReadOnlySpan<double> bins, int lowBin, int highBin)
    {
        int n = highBin - lowBin + 1;
        var buf = new double[n];
        for (int i = 0; i < n; i++) buf[i] = bins[lowBin + i];
        Array.Sort(buf);
        return buf[(n - 1) / 2];   // lower-middle for even counts
    }
}
