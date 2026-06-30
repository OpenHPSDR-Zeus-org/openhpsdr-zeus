// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.VirtualRadio;

/// <summary>
/// Which HPSDR wire stack the emulator (and the board it impersonates) speaks.
/// Zeus.Contracts has no such enum today — Zeus-the-client never needed to name
/// the protocol abstractly because each protocol lives in its own assembly
/// (<c>Zeus.Protocol1</c> / <c>Zeus.Protocol2</c>). The emulator needs to choose
/// at runtime which engine to spin, so the concept is introduced here, local to
/// the virtual-radio projects. If Zeus.Contracts ever grows a canonical
/// <c>ProtocolVersion</c>, collapse this into it.
/// </summary>
public enum ProtocolVersion : byte
{
    /// <summary>HPSDR original protocol (Protocol 1, "Metis"/EP2/EP6 over UDP 1024).</summary>
    P1 = 1,

    /// <summary>HPSDR Protocol 2 (ANAN-class / Orion, multi-port UDP 1024–1029+).</summary>
    P2 = 2,
}
