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

namespace Zeus.Protocol1;

/// <summary>
/// Server-side single-frequency sinewave IQ generator used to prove the TX
/// chain end-to-end without an uplink mic path. 48 kHz IQ stream with a
/// continuous phase accumulator so successive packets splice together without
/// clicks at the frame boundary.
/// </summary>
public sealed class TestToneGenerator : ITxIqSource
{
    public const int DefaultSampleRateHz = 48_000;
    public const double DefaultFrequencyHz = 1_000.0;

    private readonly double _phaseIncrement;
    private double _phase;

    public TestToneGenerator(double frequencyHz = DefaultFrequencyHz, int sampleRateHz = DefaultSampleRateHz)
    {
        _phaseIncrement = 2.0 * Math.PI * frequencyHz / sampleRateHz;
    }

    public double Phase => _phase;

    /// <summary>
    /// Emit the next IQ sample. <paramref name="amplitude"/> is in 0..1 and
    /// scales the full-scale s16 range; the TX path treats the radio as
    /// expecting full-scale IQ, so 1.0 here means s16 max. 0.5 leaves 6 dB of
    /// headroom which is where the task #3 spec parks us at 100% drive.
    /// </summary>
    public (short i, short q) Next(double amplitude)
    {
        double a = Math.Clamp(amplitude, 0.0, 1.0);
        double cos = Math.Cos(_phase);
        double sin = Math.Sin(_phase);
        _phase += _phaseIncrement;
        if (_phase >= 2.0 * Math.PI) _phase -= 2.0 * Math.PI;

        short i = (short)Math.Round(a * cos * short.MaxValue);
        short q = (short)Math.Round(a * sin * short.MaxValue);
        return (i, q);
    }
}
