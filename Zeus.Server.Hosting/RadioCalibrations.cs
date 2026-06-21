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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Server;

/// <summary>
/// Dispatch from <see cref="HpsdrBoardKind"/> to the per-board
/// <see cref="RadioCalibration"/> bucket. Mirrors
/// <c>PaDefaults.GetPaGainDb</c>'s seam — board-specific power math goes
/// through this helper rather than being special-cased inside
/// <c>TxMetersService.ComputeMeters</c>.
///
/// Constants come from Thetis <c>console.cs:25053-25118</c>
/// (<c>computeAlexFwdPower</c>). Where Thetis distinguishes flavours that
/// Zeus' single-byte board id collapses (the 0x0A wire-byte family —
/// ANAN-G2 vs Apache OrionMkII vs ANAN-8000D etc.), the operator selects
/// the variant via <see cref="OrionMkIIVariant"/> and the dispatch routes
/// to the right bucket. Default <see cref="OrionMkIIVariant.G2"/> matches
/// pre-#218 dispatch for every operator who never touches the variant
/// setting.
/// </summary>
internal static class RadioCalibrations
{
    // Thetis Console/clsHardwareSpecific.cs:428,
    // RXMeterCalbrationOffsetDefaults. See the local reference matrix at
    // docs/references/protocol-1/thetis-board-matrix.md.
    private const double DefaultRxMeterOffsetDb = 0.98;
    private const double OrionClassRxMeterOffsetDb = 4.841644;
    private const double G2RxMeterOffsetDb = -4.476;

    /// <summary>
    /// Pick the calibration table for a given board. Falls back to
    /// <see cref="RadioCalibration.HermesLite2"/> for unknown boards so a
    /// fresh / disconnected client doesn't divide-by-zero — operator-visible
    /// only on TX, which is gated on a live radio anyway.
    /// </summary>
    public static RadioCalibration For(HpsdrBoardKind board) =>
        For(board, OrionMkIIVariant.G2);

    /// <summary>
    /// Variant-aware overload — when <paramref name="board"/> is
    /// <see cref="HpsdrBoardKind.OrionMkII"/>, the variant routes to the
    /// matching calibration bucket (G2 / 7000DLE / 8000D / OrionMkII-original /
    /// G2-1K / ANVELINA-PRO3 / Red Pitaya). For every other board the
    /// variant is ignored.
    /// </summary>
    public static RadioCalibration For(HpsdrBoardKind board, OrionMkIIVariant variant) => board switch
    {
        HpsdrBoardKind.HermesLite2 => RadioCalibration.HermesLite2,
        HpsdrBoardKind.Hermes      => RadioCalibration.Hermes,
        HpsdrBoardKind.Metis       => RadioCalibration.Hermes,
        HpsdrBoardKind.HermesII     => RadioCalibration.Hermes,
        HpsdrBoardKind.Angelia     => RadioCalibration.Anan100,
        HpsdrBoardKind.Orion       => RadioCalibration.Anan200,
        // Board id 0x0A aliases six radios. Operator selects the variant
        // via PreferredRadioStore.GetOrionMkIIVariant(); default G2
        // preserves Zeus' pre-#218 behaviour. Sources:
        //  - G2 / 7000DLE / G2-1K / ANVELINA / RedPitaya: bridge 0.12 /
        //    ref 5.0 / offset 32 (Thetis console.cs:25079-25088).
        //  - 8000DLE / Apache OrionMkII original: bridge 0.08 / ref 5.0 /
        //    offset 18 (Thetis console.cs:25089-25093).
        HpsdrBoardKind.OrionMkII   => variant switch
        {
            OrionMkIIVariant.Anan8000DLE       => RadioCalibration.OrionMkIIAnan8000,
            OrionMkIIVariant.OrionMkII         => RadioCalibration.OrionMkIIOriginal,
            OrionMkIIVariant.G2_1K             => RadioCalibration.AnanG21K,
            _                                  => RadioCalibration.OrionMkII,
        },
        // ANAN-G2E (HpsdrBoardKind.HermesC10) shares the OrionMkII / G2
        // forward-power calibration constants per Thetis console.cs:25079-
        // 25088 (computeAlexFwdPower lumps ANAN_G2E with ANAN_G2 /
        // ANAN_G2_1K / ANAN7000D / ANVELINAPRO3 / REDPITAYA in the same
        // bridge_volt = 0.12, refvoltage = 5.0, adc_cal_offset = 32 case).
        HpsdrBoardKind.HermesC10   => RadioCalibration.OrionMkII,
        _                          => RadioCalibration.HermesLite2,
    };

    /// <summary>
    /// Per-board RX S-meter calibration offset in dB. Applied to WDSP's raw
    /// RXA_S_* and RXA_AGC_* dBm readings before broadcasting meter frames.
    /// ADC dBFS and AGC gain readings are not S-meter readings and must not
    /// use this offset.
    /// </summary>
    public static double RxMeterOffsetDb(HpsdrBoardKind board) =>
        RxMeterOffsetDb(board, OrionMkIIVariant.G2);

    /// <summary>
    /// Variant-aware RX S-meter calibration offset. The 0x0A OrionMkII wire
    /// byte aliases several radios with materially different meter offsets,
    /// so the operator-selected <see cref="OrionMkIIVariant"/> participates
    /// in the dispatch. Every non-0x0A board follows Thetis' default branch.
    /// </summary>
    public static double RxMeterOffsetDb(HpsdrBoardKind board, OrionMkIIVariant variant) => board switch
    {
        HpsdrBoardKind.OrionMkII => variant switch
        {
            OrionMkIIVariant.G2 or
            OrionMkIIVariant.G2_1K => G2RxMeterOffsetDb,

            OrionMkIIVariant.Anan7000DLE or
            OrionMkIIVariant.Anan8000DLE or
            OrionMkIIVariant.OrionMkII or
            OrionMkIIVariant.AnvelinaPro3 or
            OrionMkIIVariant.RedPitaya => OrionClassRxMeterOffsetDb,

            _ => G2RxMeterOffsetDb,
        },
        _ => DefaultRxMeterOffsetDb,
    };
}
