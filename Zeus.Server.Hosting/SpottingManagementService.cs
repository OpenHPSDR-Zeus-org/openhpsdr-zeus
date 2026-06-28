// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Owns the live config for the digital-mode spotting uploaders (PSK Reporter +
/// WSPRnet). Like the WSJT-X broadcaster (and unlike CAT/TCI) these only SEND, so
/// config changes apply immediately — there is no current/pending split and no
/// RequiresRestart. Persists through <see cref="SpottingSettingsStore"/>.
///
/// Operator identity (callsign + grid) is required by both uploaders. It is
/// resolved exactly like FreeDvReporterService: the persisted override first,
/// then the QRZ home station as a fallback. The reporters additionally no-op when
/// identity is unresolved, on top of the per-uploader enable flag.
/// </summary>
public sealed class SpottingManagementService
{
    private readonly ILogger<SpottingManagementService> _log;
    private readonly SpottingSettingsStore _store;
    private readonly OperatorIdentityStore _identity;
    private readonly QrzService _qrz;
    private readonly object _sync = new();
    private SpottingRuntimeConfig _config;

    public SpottingManagementService(
        ILogger<SpottingManagementService> log,
        SpottingSettingsStore store,
        OperatorIdentityStore identity,
        QrzService qrz)
    {
        _log = log;
        _store = store;
        _identity = identity;
        _qrz = qrz;
        // Default OFF when nothing is persisted — new network egress is opt-in.
        _config = _store.Get() ?? new SpottingRuntimeConfig();
    }

    public SpottingRuntimeConfig GetConfig()
    {
        lock (_sync) return _config;
    }

    public SpottingStatus GetStatus()
    {
        var c = GetConfig();
        var (call, grid) = ResolveOperator();
        return new SpottingStatus(
            PskReporterEnabled: c.PskReporterEnabled,
            WsprnetEnabled: c.WsprnetEnabled,
            Callsign: call,
            Grid: grid,
            IdentityResolved: !string.IsNullOrWhiteSpace(call) && !string.IsNullOrWhiteSpace(grid));
    }

    public SpottingStatus SetConfig(SpottingRuntimeConfig config)
    {
        var normalized = new SpottingRuntimeConfig(
            PskReporterEnabled: config.PskReporterEnabled,
            WsprnetEnabled: config.WsprnetEnabled,
            Callsign: NormalizeCall(config.Callsign),
            Grid: NormalizeGrid(config.Grid));

        lock (_sync) _config = normalized;

        try
        {
            _store.Set(normalized);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "spotting.config.persist failed");
        }

        _log.LogInformation(
            "spotting.config.updated psk={Psk} wsprnet={Wspr} call={Call} grid={Grid}",
            normalized.PskReporterEnabled, normalized.WsprnetEnabled,
            normalized.Callsign, normalized.Grid);

        return GetStatus();
    }

    /// <summary>
    /// Operator callsign/grid. Precedence per field: the shared OperatorIdentity
    /// override first, then this service's own persisted config (legacy/secondary,
    /// kept additively), then the QRZ home station. Returns ("","") when no source
    /// has them. Shared with FreeDvReporterService and /api/operator via
    /// OperatorIdentityResolver.
    /// </summary>
    public (string Call, string Grid) ResolveOperator()
    {
        var c = GetConfig();
        return OperatorIdentityResolver.Resolve(_identity, _qrz, c.Callsign, c.Grid);
    }

    private static string NormalizeCall(string? call) =>
        string.IsNullOrWhiteSpace(call) ? "" : call.Trim().ToUpperInvariant();

    private static string NormalizeGrid(string? grid)
    {
        if (string.IsNullOrWhiteSpace(grid)) return "";
        var g = grid.Trim().ToUpperInvariant();
        return g.Length > 6 ? g[..6] : g;
    }
}
