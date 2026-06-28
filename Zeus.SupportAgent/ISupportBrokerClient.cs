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
/// The sidecar's outbound calls to the broker's operator-authenticated REST
/// surface (<c>/presence/*</c>, <c>/crash</c>). Abstracted behind an interface so
/// <see cref="PresenceClient"/> and the crash-upload path are unit-testable
/// without a live broker — the production implementation is
/// <see cref="HttpSupportBrokerClient"/>.
///
/// Every method is best-effort: it returns success/failure rather than throwing,
/// so a transient broker outage never destabilises the sidecar (whose first duty
/// is local crash capture).
/// </summary>
public interface ISupportBrokerClient
{
    /// <summary>Register this operator as support-available (first heartbeat of an online streak).</summary>
    Task<bool> RegisterAsync(CancellationToken ct);

    /// <summary>Refresh the operator's presence (keeps them inside the broker's expiry window).</summary>
    Task<bool> HeartbeatAsync(CancellationToken ct);

    /// <summary>Drop the operator's presence immediately (clean shutdown / availability off).</summary>
    Task<bool> DropAsync(CancellationToken ct);

    /// <summary>Upload a (server-redacted) crash record JSON to the broker's crash store.</summary>
    Task<bool> UploadCrashAsync(string crashRecordJson, CancellationToken ct);
}
