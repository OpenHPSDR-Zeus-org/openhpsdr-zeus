// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// OperatorIdentityResolver — the single, shared operator-identity precedence
// used by every resolver (FT8/FT4 TX, spotting uploaders, FreeDV Reporter, the
// /api/operator endpoint). Order, per field: the shared OperatorIdentity
// override first, then an optional service-local secondary (the legacy per-store
// callsign/grid, kept additively), then the QRZ home station. This collapses the
// three near-identical ResolveOperator copies that had drifted apart.

using Zeus.Contracts;

namespace Zeus.Server;

internal static class OperatorIdentityResolver
{
    /// <summary>
    /// Resolve the effective operator callsign + grid. Precedence per field:
    /// shared override → secondary (service-local) → QRZ home. Returns ("","")
    /// when no source has a value. All outputs are normalized.
    /// </summary>
    public static (string Call, string Grid) Resolve(
        OperatorIdentityStore shared,
        QrzService qrz,
        string? secondaryCall = null,
        string? secondaryGrid = null)
    {
        var id = shared.Get(); // already normalized
        var call = id.Callsign;
        var grid = id.Grid;

        if (string.IsNullOrWhiteSpace(call))
            call = OperatorIdentity.NormalizeCallsign(secondaryCall);
        if (string.IsNullOrWhiteSpace(grid))
            grid = OperatorIdentity.NormalizeGrid(secondaryGrid);

        if (string.IsNullOrWhiteSpace(call) || string.IsNullOrWhiteSpace(grid))
        {
            var home = qrz.GetStatus().Home;
            if (home is not null)
            {
                if (string.IsNullOrWhiteSpace(call))
                    call = OperatorIdentity.NormalizeCallsign(home.Callsign);
                if (string.IsNullOrWhiteSpace(grid))
                    grid = OperatorIdentity.NormalizeGrid(home.Grid);
            }
        }

        return (call, grid);
    }

    /// <summary>
    /// Build the /api/operator status: the saved override plus the effective
    /// resolved identity (override → QRZ home), with per-field flags marking
    /// which resolved values came from the QRZ fallback.
    /// </summary>
    public static OperatorIdentityStatus Status(OperatorIdentityStore shared, QrzService qrz)
    {
        var saved = shared.Get();
        var resolvedCall = saved.Callsign;
        var resolvedGrid = saved.Grid;
        bool callFromQrz = false;
        bool gridFromQrz = false;

        if (string.IsNullOrWhiteSpace(resolvedCall) || string.IsNullOrWhiteSpace(resolvedGrid))
        {
            var home = qrz.GetStatus().Home;
            if (home is not null)
            {
                if (string.IsNullOrWhiteSpace(resolvedCall))
                {
                    var c = OperatorIdentity.NormalizeCallsign(home.Callsign);
                    if (!string.IsNullOrWhiteSpace(c)) { resolvedCall = c; callFromQrz = true; }
                }
                if (string.IsNullOrWhiteSpace(resolvedGrid))
                {
                    var g = OperatorIdentity.NormalizeGrid(home.Grid);
                    if (!string.IsNullOrWhiteSpace(g)) { resolvedGrid = g; gridFromQrz = true; }
                }
            }
        }

        return new OperatorIdentityStatus(
            Callsign: saved.Callsign,
            Grid: saved.Grid,
            ResolvedCallsign: resolvedCall,
            ResolvedGrid: resolvedGrid,
            CallsignFromQrz: callFromQrz,
            GridFromQrz: gridFromQrz);
    }
}
