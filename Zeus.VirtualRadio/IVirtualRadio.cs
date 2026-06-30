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
/// A running virtual radio engine. One implementation per protocol
/// (<c>Protocol1Engine</c> now; <c>Protocol2Engine</c> later). The host process
/// constructs the engine for the configured profile and drives it via
/// <see cref="RunAsync"/>.
/// </summary>
public interface IVirtualRadio
{
    /// <summary>
    /// Bind the radio sockets, answer discovery, accept a direct connection,
    /// decode inbound host commands, and stream RX IQ + telemetry until
    /// <paramref name="ct"/> is cancelled. Completes when cancelled.
    /// </summary>
    Task RunAsync(CancellationToken ct);

    /// <summary>
    /// Raised for every decoded host command (start/stop, drive, MOX, frequency,
    /// config, …). Used by the command log and any external observer.
    /// </summary>
    event Action<DecodedHostCommand>? CommandDecoded;

    /// <summary>Capture the current read-only state for <c>/status</c>.</summary>
    VirtualRadioStatus Snapshot();
}
