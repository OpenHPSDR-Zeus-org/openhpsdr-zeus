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

/// <summary>Persisted CAT (Kenwood TS-2000 over TCP) server config. Mirrors
/// <see cref="TciRuntimeConfig"/>. Default port 19090 (operator-configurable —
/// there is no universal TCP-CAT port; match it to your client).</summary>
public sealed record CatRuntimeConfig(
    bool Enabled = false,
    string BindAddress = "127.0.0.1",
    int Port = 19090);

/// <summary>CAT server status response. Mirrors <see cref="TciStatus"/>.</summary>
public sealed record CatStatus(
    bool CurrentlyEnabled,
    int CurrentPort,
    string CurrentBindAddress,
    bool PendingEnabled,
    int PendingPort,
    string PendingBindAddress,
    int ClientCount,
    bool PortAvailable,
    bool RequiresRestart,
    string? Error);

/// <summary>CAT port test request. Mirrors <see cref="TciTestRequest"/>.</summary>
public sealed record CatTestRequest(string BindAddress, int Port);

/// <summary>CAT port test result. Mirrors <see cref="TciTestResult"/>.</summary>
public sealed record CatTestResult(bool Ok, string? Error);
