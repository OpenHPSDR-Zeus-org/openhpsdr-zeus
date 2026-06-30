// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.SupportAgent;

/// <summary>
/// A broker client whose operator identity can be swapped at runtime, so the
/// sidecar can build presence/crash auth LAZILY as QRZ identity arrives over IPC
/// (rather than depending on a launch-time env var that races QRZ login). Drives
/// <see cref="PresenceCoordinator"/>; the production implementation is
/// <see cref="HttpSupportBrokerClient"/>. Abstracted so the coordinator's gating
/// is unit/integration-testable without a live broker.
/// </summary>
public interface IMutableSupportBrokerClient : ISupportBrokerClient
{
    /// <summary>True only when the client currently holds a usable callsign + session key.</summary>
    bool IsConfigured { get; }

    /// <summary>Swap the operator identity used to authenticate broker calls. Blank parts ⇒ not configured.</summary>
    void UpdateIdentity(string? callsign, string? sessionKey);

    /// <summary>
    /// Swap the operator's radio metadata advertised in the broker presence body
    /// (board name, variant/model, connected state). Static identity (platform /
    /// app version) is set once at construction; this carries the parts that change
    /// as the operator connects/disconnects a radio.
    /// </summary>
    void UpdateMetadata(string? radioBoard, string? radioModel, bool radioConnected);
}
