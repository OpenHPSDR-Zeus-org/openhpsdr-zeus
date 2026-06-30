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

namespace Zeus.VirtualRadio;

/// <summary>
/// Bit flags for the set of protocols a board can speak.
/// </summary>
[Flags]
public enum ProtocolSupport : byte
{
    None = 0,
    P1 = 1,
    P2 = 2,
    Both = P1 | P2,
}

/// <summary>
/// Per-board "which protocols does this board actually speak" fact. This does
/// NOT exist in Zeus's <c>BoardCapabilitiesTable</c> — Zeus-the-client never
/// needed it (it just connects to whatever answers discovery). The emulator
/// needs it to reject illegal <c>{Board, Protocol}</c> startup combinations
/// (e.g. HL2 + P2, Metis + P2). Kept local to the emulator on purpose: do NOT
/// promote it into the operator-felt <c>BoardCapabilities</c> table — that is a
/// separate, red-light decision.
///
/// Seed (per the Virtual HPSDR Radio plan):
/// <list type="bullet">
/// <item>Metis (0x00), Hermes (0x01), Hermes-Lite 2 (0x06) → P1 only.</item>
/// <item>HermesII (0x02), Angelia (0x04), Orion (0x05), OrionMkII (0x0A),
///       HermesC10 (0x14) → both protocols.</item>
/// </list>
/// </summary>
public static class BoardProtocolSupport
{
    /// <summary>The set of protocols <paramref name="board"/> can speak.</summary>
    public static ProtocolSupport For(HpsdrBoardKind board) => board switch
    {
        HpsdrBoardKind.Metis       => ProtocolSupport.P1,
        HpsdrBoardKind.Hermes      => ProtocolSupport.P1,
        HpsdrBoardKind.HermesLite2 => ProtocolSupport.P1,
        HpsdrBoardKind.HermesII    => ProtocolSupport.Both,
        HpsdrBoardKind.Angelia     => ProtocolSupport.Both,
        HpsdrBoardKind.Orion       => ProtocolSupport.Both,
        HpsdrBoardKind.OrionMkII   => ProtocolSupport.Both,
        HpsdrBoardKind.HermesC10   => ProtocolSupport.Both,
        _                          => ProtocolSupport.None,
    };

    /// <summary>True when <paramref name="board"/> can speak <paramref name="protocol"/>.</summary>
    public static bool Supports(HpsdrBoardKind board, ProtocolVersion protocol)
    {
        ProtocolSupport flag = protocol == ProtocolVersion.P1 ? ProtocolSupport.P1 : ProtocolSupport.P2;
        return (For(board) & flag) != 0;
    }
}
