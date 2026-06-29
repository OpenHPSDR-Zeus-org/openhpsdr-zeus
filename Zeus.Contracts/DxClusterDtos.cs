// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Contracts;

/// <summary>
/// Live connection state of the native Telnet DX-cluster client. Additive wire
/// contract — serialised as a string in the status DTO so adding states later
/// stays backward compatible.
/// </summary>
public enum DxClusterConnectionState
{
    /// <summary>Not connected and not trying (operator disabled or idle).</summary>
    Disconnected = 0,
    /// <summary>Opening the TCP socket / logging in.</summary>
    Connecting = 1,
    /// <summary>Logged in and receiving spot lines.</summary>
    Connected = 2,
    /// <summary>Dropped — waiting out the backoff before the next attempt.</summary>
    Reconnecting = 3,
}

/// <summary>
/// Operator configuration for the DX-cluster client. Persisted in zeus-prefs.db
/// via DxClusterConfigStore. Everything defaults OFF / empty — the client never
/// reaches out to the network until the operator enables it.
///
/// <para><b>Password</b> is stored at-rest as plaintext in zeus-prefs.db,
/// consistent with the existing CredentialStore (QRZ) precedent — see the PR
/// notes. Most DX clusters do not require a password at all.</para>
///
/// <para><b>LoginCommands</b> is a newline-separated list of commands sent once
/// after a successful login (e.g. <c>set/filter</c>, <c>sh/dx</c>).</para>
/// </summary>
public sealed record DxClusterConfig(
    bool Enabled = false,
    string Host = "",
    int Port = 7373,
    string Callsign = "",
    string Password = "",
    string LoginCommands = "",
    bool AutoConnect = false);

/// <summary>Status of the DX-cluster client (config echo + live connection state).</summary>
public sealed record DxClusterStatus(
    bool Enabled,
    string Host,
    int Port,
    string Callsign,
    bool HasPassword,
    string LoginCommands,
    bool AutoConnect,
    DxClusterConnectionState State,
    int SpotsReceived,
    string? LastSpotCallsign,
    string? Error);
