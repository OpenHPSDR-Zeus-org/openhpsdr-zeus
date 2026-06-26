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
/// Persisted config for the outbound WSJT-X "logged QSO" UDP broadcaster. When
/// enabled, every QSO logged through /api/log/entry (auto-logged or manual — NOT
/// ADIF import) is emitted as a WSJT-X NetworkMessage type 12 (LoggedADIF) to
/// <c>Host:Port</c>, which JTAlert / Log4OM / GridTracker / N1MM already
/// understand. NEW network egress — DISABLED by default, loopback target. This
/// is QSO-record push only; CAT/TCI already provide rig control.
/// </summary>
public sealed record WsjtxRuntimeConfig(
    bool Enabled = false,
    string Host = "127.0.0.1",
    int Port = 2237,
    string InstanceId = "WSJT-X");

/// <summary>Status/config view for the WSJT-X broadcaster. UDP has no listener,
/// so config applies live — there is no RequiresRestart, unlike CAT/TCI.</summary>
public sealed record WsjtxStatus(
    bool Enabled,
    string Host,
    int Port,
    string InstanceId);
