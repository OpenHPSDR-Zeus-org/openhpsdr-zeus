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
/// One decoded host-command event, emitted for the structured command log and
/// the <c>IVirtualRadio.CommandDecoded</c> event. This is the inverse of
/// <c>ControlFrame.WriteCcBytes</c> — it reports exactly what Zeus put on the
/// wire, with every field already decoded into <see cref="Summary"/>.
/// </summary>
/// <param name="Timestamp">When the command was decoded (wall clock).</param>
/// <param name="Protocol">Which wire stack the command arrived on.</param>
/// <param name="CommandKind">The frame / register name, e.g. "Config",
/// "TxFreq", "RxFreq", "DriveFilter", "Start", "Stop".</param>
/// <param name="Summary">Human-readable fully-decoded field dump — the line
/// written to the command log (mirrors the <c>p1.tx.rate</c> cadence).</param>
public sealed record DecodedHostCommand(
    DateTimeOffset Timestamp,
    ProtocolVersion Protocol,
    string CommandKind,
    string Summary);
