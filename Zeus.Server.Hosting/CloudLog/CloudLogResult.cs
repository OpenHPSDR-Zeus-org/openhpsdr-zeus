// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

namespace Zeus.Server.CloudLog;

/// <summary>
/// Outcome of a single per-QSO cloud-log upload attempt.
/// <para>
/// <see cref="HardStop"/> is set only by Club Log on an HTTP 403 (auth failure):
/// Club Log warns that repeated rejected auth can get the source IP blocked, so
/// the publisher must stop sending and surface a warning rather than keep
/// retrying. A normal failure (bad ADIF, network error, 500) is retryable and
/// does NOT set HardStop.
/// </para>
/// </summary>
public sealed record CloudLogResult(
    string Provider,
    bool Success,
    bool HardStop,
    string? RemoteId,
    string? Message)
{
    public static CloudLogResult Ok(string provider, string? id = null, string? message = null) =>
        new(provider, Success: true, HardStop: false, RemoteId: id, Message: message);

    public static CloudLogResult Fail(string provider, string? message, bool hardStop = false) =>
        new(provider, Success: false, HardStop: hardStop, RemoteId: null, Message: message);

    public static CloudLogResult Skipped(string provider, string reason) =>
        new(provider, Success: false, HardStop: false, RemoteId: null, Message: reason);
}
