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
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1;

/// <summary>
/// Protocol-1 RX sample rate selector — encoded in C1 bits [1:0] of the
/// config register (CC0=0x00). See docs/prd/02-protocol1-integration.md §6.
/// </summary>
public enum HpsdrSampleRate : byte
{
    Rate48k = 0,
    Rate96k = 1,
    Rate192k = 2,
    Rate384k = 3,
    // Protocol-2 only. The P1 config register encodes the rate in 2 bits
    // (C1 bits [1:0]) and ControlFrame masks `(byte)Rate & 0x03`, so values
    // above Rate384k are NOT representable on Protocol 1 — RadioService guards
    // the P1 path so a P1 board never receives these. Protocol 2 carries the
    // rate as a u16 kHz value (Protocol2Client.ComposeCmdRxBuffer), which holds
    // 768/1536 fine. ANAN G2 (P2) supports the full 48..1536 kHz ladder.
    Rate768k = 4,
    Rate1536k = 5,
}

/// <summary>
/// Extended RX attenuator, 0–31 dB. Wire-encoded in the dedicated attenuator
/// register CC0=0x14. The Db value is the same API across boards; ControlFrame
/// maps it per-board:
/// <list type="bullet">
/// <item>HL2 (<see cref="HpsdrBoardKind.HermesLite2"/>) writes C4 = 0x40 | (60 − Db) — HL2 has no hardware
/// attenuator, so "attenuate by N dB" is expressed as "reduce firmware RX
/// gain by N units from max".</item>
/// <item>Standard HPSDR (ANAN / Hermes / Orion) writes C4 = 0x20 | (Db &amp; 0x1F).</item>
/// </list>
/// </summary>
public readonly record struct HpsdrAtten(int Db)
{
    public const int MinDb = 0;
    public const int MaxDb = 31;

    public static HpsdrAtten Zero { get; } = new(0);

    public int ClampedDb => Math.Clamp(Db, MinDb, MaxDb);
}

/// <summary>
/// Alex RX antenna selector — encoded in C3 bits [7:5]. ANT1 is the default
/// for every supported board.
/// </summary>
public enum HpsdrAntenna : byte
{
    Ant1 = 0,
    Ant2 = 1,
    Ant3 = 2,
}

public sealed record StreamConfig(HpsdrSampleRate Rate, bool PreampOn, HpsdrAtten Atten)
{
    public int SampleRateHz => Rate switch
    {
        HpsdrSampleRate.Rate48k => 48_000,
        HpsdrSampleRate.Rate96k => 96_000,
        HpsdrSampleRate.Rate192k => 192_000,
        HpsdrSampleRate.Rate384k => 384_000,
        _ => throw new ArgumentOutOfRangeException(nameof(Rate), Rate, null),
    };
}
