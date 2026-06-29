// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Contracts;

/// <summary>
/// Canonical HPSDR board-kind enum spanning both Protocol 1 and Protocol 2
/// discovery. Wire-byte values match Apache Labs / OpenHPSDR documentation
/// and Thetis MW0LGE's <c>HPSDRHW</c> enum
/// (<c>Project Files/Source/Console/enums.cs:389-402</c>) where they
/// overlap.
///
/// Phase 4 of issue #218 promoted this enum from per-protocol
/// <c>Zeus.Protocol1.Discovery.HpsdrBoardKind</c> /
/// <c>Zeus.Protocol2.Discovery.HpsdrBoardKind</c> to a single contract
/// type so server-side dispatch (<c>RadioCalibrations.For</c>,
/// <c>PaDefaults.*</c>, <c>BoardCapabilitiesTable.For</c>,
/// <c>RadioDriveProfiles.For</c>) covers every wire ID either protocol
/// can emit, not just the P1 set.
///
/// Naming conventions (per the unification decisions in
/// <c>docs/designs/radio-support-plan.md</c> §Phase 4):
/// <list type="bullet">
/// <item><c>Metis</c> for <c>0x00</c> — Apache Labs' canonical name. P2's
/// historical "Atlas" label collapsed into <c>Metis</c>; same wire byte,
/// same protocol-1 motherboard family (Mercury+Penelope+Metis).</item>
/// <item><c>HermesII</c> for <c>0x02</c> — matches Apache marketing copy
/// and Thetis enum directly. P1's historical "Griffin" label collapsed
/// into <c>HermesII</c>; same firmware family.</item>
/// </list>
///
/// Wire-byte values are STABLE — they're persisted in
/// <c>zeus-prefs.db</c> as bytes (see <c>PreferredRadioStore</c>,
/// <c>PaSettingsStore</c>) and on the wire by both protocol parsers. Old
/// rows hydrate correctly because the byte values are unchanged across
/// the unification.
/// </summary>
public enum HpsdrBoardKind : byte
{
    /// <summary>Original HPSDR Mercury+Penelope+Metis stack (Apache "Atlas"
    /// in P2 nomenclature). Both protocols cast wire byte 0x00 to this.</summary>
    Metis = 0x00,

    /// <summary>Apache Hermes (single-board radio). ANAN-10 / ANAN-100
    /// also report this byte.</summary>
    Hermes = 0x01,

    /// <summary>Apache Hermes-II / Hermes II firmware family. ANAN-10E /
    /// ANAN-100B report this byte. Pre-#218 P1 called this "Griffin"; the
    /// unification adopted Apache's user-facing name.</summary>
    HermesII = 0x02,

    /// <summary>Apache Angelia. ANAN-100D reports this byte.</summary>
    Angelia = 0x04,

    /// <summary>Apache Orion. ANAN-200D reports this byte.</summary>
    Orion = 0x05,

    /// <summary>Hermes-Lite 2 (mi0bot openhpsdr-thetis fork). Out-of-scope
    /// for the MW0LGE Thetis dispatch path; Zeus has its own HL2-specific
    /// drive / calibration / lessons (see
    /// <c>docs/lessons/hl2-drive-model.md</c>).</summary>
    HermesLite2 = 0x06,

    /// <summary>Apache "Orion-MkII" wire byte — collapses six physically
    /// distinct radios (G2 / G2 MkII / G2-1K / 7000DLE / 8000DLE /
    /// ANVELINA-PRO3 / Red Pitaya / original OrionMkII firmware). Operator
    /// disambiguates via <see cref="OrionMkIIVariant"/> per issue #218.</summary>
    OrionMkII = 0x0A,

    /// <summary>Apache ANAN-G2E (N1GP firmware). Thetis HermesC10.</summary>
    HermesC10 = 0x14,

    /// <summary>No board recognised. Used as a sentinel for disconnected
    /// state and as the dispatch fallback for any future wire byte that
    /// hasn't been enumerated yet.</summary>
    Unknown = 0xFF,
}
