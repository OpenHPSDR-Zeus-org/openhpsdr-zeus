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
/// Applies an operator-posture snapshot learned over IPC
/// (<see cref="SupportIpcListener.SupportState"/>) to the live broker identity and
/// presence availability. This is the seam that lets the sidecar build its broker
/// identity LAZILY: the broker client exists from launch with a possibly-blank
/// identity, and each IPC update swaps in the current QRZ callsign + session key.
/// Presence only advertises when the L1 switch is on AND the broker is configured
/// (identity present), so a "remote diagnostics on" posture that arrives before
/// QRZ identity simply waits — the next update (QRZ login) flips it on.
///
/// <para>Extracted from the sidecar's top-level flow so the gating is unit-testable
/// without a live pipe or broker. Pure and synchronous: it only mutates the
/// broker's identity field and the presence availability flag.</para>
/// </summary>
public sealed class PresenceCoordinator
{
    private readonly IMutableSupportBrokerClient _broker;
    private readonly PresenceClient _presence;
    private readonly Action<string>? _log;

    /// <param name="broker">The mutable-identity broker client the presence loop drives.</param>
    /// <param name="presence">The presence loop whose availability this gates.</param>
    /// <param name="initialAutoShare">
    /// The launch-time crash auto-share pre-authorisation, surfaced via
    /// <see cref="AutoShareOnCrash"/> until an IPC update supersedes it. The post-crash
    /// share path reads this AFTER the backend (and IPC) is gone.
    /// </param>
    /// <param name="log">Optional breadcrumb sink.</param>
    public PresenceCoordinator(
        IMutableSupportBrokerClient broker,
        PresenceClient presence,
        bool initialAutoShare = false,
        Action<string>? log = null)
    {
        _broker = broker;
        _presence = presence;
        AutoShareOnCrash = initialAutoShare;
        _log = log;
    }

    /// <summary>
    /// The operator's current crash auto-share posture, updated by each IPC state.
    /// Read by the post-crash share gate, which runs after the backend has died and
    /// so cannot consult live IPC — this holds the last value the operator pushed.
    /// </summary>
    public bool AutoShareOnCrash { get; private set; }

    /// <summary>
    /// Apply an IPC posture update: refresh broker identity, recompute presence
    /// availability, and record the crash auto-share posture. Availability is on
    /// only when the L1 switch is on AND the broker now has a usable identity.
    /// </summary>
    public void Apply(SupportIpcListener.SupportState state)
    {
        _broker.UpdateIdentity(state.QrzCallsign, state.QrzSessionKey);
        AutoShareOnCrash = state.AutoShareOnCrash;

        var available = state.RemoteDiagnosticsEnabled && _broker.IsConfigured;
        _presence.SetAvailable(available);

        _log?.Invoke(
            $"ipc: state remoteDiag={state.RemoteDiagnosticsEnabled} " +
            $"autoShare={state.AutoShareOnCrash} " +
            $"identity={(_broker.IsConfigured ? "set" : "-")} -> available={available}");
    }
}
