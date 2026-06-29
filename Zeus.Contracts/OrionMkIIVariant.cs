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
/// Operator-selectable variant for the Apache wire byte <c>0x0A</c>, which
/// aliases six physically distinct radios in the OpenHPSDR discovery
/// protocol. Resolves the calibration / PA-gain / rated-watts collision
/// flagged in issue #218: same byte on the wire, materially different
/// hardware behind it.
///
/// Consulted by dispatch helpers (<c>RadioCalibrations.For</c>,
/// <c>PaDefaults.GetMaxPowerWatts</c>, <c>BoardCapabilitiesTable.For</c>)
/// only when <c>HpsdrBoardKind == OrionMkII</c>; ignored for every other
/// board kind. Default value <see cref="G2"/> preserves Zeus' shipping
/// behaviour — operators who never touch this setting see no change.
///
/// Source-of-truth for the per-variant constants:
/// <c>docs/references/protocol-1/thetis-board-matrix.md</c> and Thetis
/// <c>console.cs:25053-25118</c> (computeAlexFwdPower).
/// </summary>
public enum OrionMkIIVariant : byte
{
    /// <summary>Apache ANAN-G2 / G2 MkII (Saturn FPGA). Bridge 0.12 V,
    /// ref 5.0 V, offset 32, 100 W rated. Default — matches Zeus'
    /// pre-#218 behaviour for every 0x0A board.</summary>
    G2 = 0,

    /// <summary>Apache ANAN-G2-1K (Saturn FPGA, 1 kW PA). Same bridge
    /// constants as G2; G8NJJ noted "1K will need different scaling"
    /// in Thetis but ships with the G2 numbers for now. Rated watts
    /// distinguishes it from G2 for meter scaling.</summary>
    G2_1K = 1,

    /// <summary>Apache ANAN-7000DLE. Saturn-class fingerprint, same
    /// bridge constants as G2, 100 W rated.</summary>
    Anan7000DLE = 2,

    /// <summary>Apache ANAN-8000DLE. Distinct bridge constants
    /// (0.08 V / 5.0 V / offset 18) and 200 W rated. The bucket
    /// <c>RadioCalibration.OrionMkIIAnan8000</c> exists for this
    /// variant; selecting it routes meter scaling through the right
    /// bridge so FWD power reads correctly.</summary>
    Anan8000DLE = 3,

    /// <summary>Apache "Orion-MkII" (the original 100 W board with
    /// Orion-MkII firmware, not the umbrella term). Shares ANAN-8000D's
    /// bridge constants per Thetis <c>console.cs:25089-25093</c>; uses
    /// Hermes-class PA gain table per <c>clsHardwareSpecific.cs:484</c>.
    /// 100 W rated.</summary>
    OrionMkII = 4,

    /// <summary>ANVELINA-PRO3 community board (G2-class fingerprint,
    /// same bridge constants as G2).</summary>
    AnvelinaPro3 = 5,

    /// <summary>Red Pitaya running OpenHPSDR / DH1KLM firmware.
    /// Saturn-class hardware fingerprint with MKII BPF intentionally
    /// disabled (DH1KLM note at <c>clsHardwareSpecific.cs:187</c>) for
    /// DIY PA / filter-board compatibility. Same bridge as G2.</summary>
    RedPitaya = 6,
}
