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
/// Read-only snapshot of the virtual radio's live state, served by the
/// <c>StatusJsonServer</c> at <c>GET /status</c> and returned by
/// <c>IVirtualRadio.Snapshot()</c>. An agent (or KB2UKA) polls this and diffs
/// it against Zeus's own <c>GET /api/state</c> — that diff IS the analysis
/// workflow.
/// </summary>
/// <param name="Profile">The configured board / variant / protocol triple.</param>
/// <param name="ConnectedHost">The host endpoint currently driving the radio
/// (ip:port), or null when no host has connected.</param>
/// <param name="Mox">Whether the host currently has the radio keyed.</param>
/// <param name="FwdWatts">Synthesized forward power in watts (PTT/MOX-gated).</param>
/// <param name="RefWatts">Synthesized reflected power in watts.</param>
/// <param name="Swr">Synthesized SWR derived from FWD/REF.</param>
/// <param name="Ep6PacketsSent">Count of RX-IQ packets sent to the host.</param>
/// <param name="Ep2PacketsReceived">Count of host command/TX packets received.</param>
/// <param name="SeqGaps">Count of detected inbound sequence gaps.</param>
/// <param name="LastCommands">The most-recent decoded host commands (newest last).</param>
public sealed record VirtualRadioStatus(
    VirtualRadioProfile Profile,
    string? ConnectedHost,
    bool Mox,
    double FwdWatts,
    double RefWatts,
    double Swr,
    long Ep6PacketsSent,
    long Ep2PacketsReceived,
    long SeqGaps,
    IReadOnlyList<DecodedHostCommand> LastCommands);
