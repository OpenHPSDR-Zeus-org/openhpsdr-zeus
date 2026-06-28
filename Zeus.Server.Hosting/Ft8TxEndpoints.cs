// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Ft8TxEndpoints — the REST control surface for the FT8/FT4 + WSPR ARMED
// auto-sequence keyers. Kept in its own extension file (called from
// MapZeusEndpoints) so it doesn't pile onto the already-large ZeusEndpoints.cs.
//
// SAFETY: arming is ONLY via the explicit /arm endpoints (default false, no
// auto-arm anywhere); staging a message never arms; /halt is the panic path. No
// PureSignal, drive, or power state is read or written here.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Zeus.Server;

/// <summary>POST /api/ft8/tx/arm — the FT8/FT4 ENABLE-TX master.</summary>
public sealed record Ft8TxArmRequest(bool Enabled);

/// <summary>POST /api/ft8/tx — stage the next FT8/FT4 transmission.</summary>
public sealed record Ft8TxStageRequest(string Message, int? AudioHz, string? Slot, string? Mode);

/// <summary>POST /api/wspr/tx/arm — the WSPR ENABLE-TX master.</summary>
public sealed record WsprTxArmRequest(bool Enabled);

/// <summary>POST /api/wspr/tx/settings — beacon content + cadence.</summary>
public sealed record WsprTxSettingsRequest(
    string Call, string Grid4, int? DBm, int? AudioHz, double? TxPercent);

public static class Ft8TxEndpoints
{
    /// <summary>Maps the FT8/FT4 + WSPR keyer endpoints onto <paramref name="app"/>.</summary>
    public static WebApplication MapFt8TxEndpoints(this WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILogger<object>>();

        // ---- FT8/FT4 keyer -------------------------------------------------

        app.MapGet("/api/ft8/tx", (Ft8TxService svc) => Results.Ok(svc.Status()));

        app.MapPost("/api/ft8/tx/arm", (Ft8TxArmRequest body, Ft8TxService svc) =>
        {
            svc.SetArmed(body.Enabled);
            log.LogInformation("api.ft8.tx.arm enabled={Enabled}", body.Enabled);
            return Results.Ok(svc.Status());
        });

        app.MapPost("/api/ft8/tx", (Ft8TxStageRequest body, Ft8TxService svc) =>
        {
            string? err = svc.Stage(
                body.Message, body.AudioHz ?? 1500, body.Slot ?? "even", body.Mode ?? "FT8");
            if (err is not null) return Results.BadRequest(new { error = err });
            log.LogInformation("api.ft8.tx.stage msg='{Msg}' slot={Slot} mode={Mode}",
                body.Message, body.Slot, body.Mode);
            return Results.Ok(svc.Status());
        });

        app.MapPost("/api/ft8/tx/halt", (Ft8TxService svc) =>
        {
            svc.Halt();
            log.LogInformation("api.ft8.tx.halt");
            return Results.Ok(svc.Status());
        });

        // ---- WSPR beacon ---------------------------------------------------

        app.MapGet("/api/wspr/tx", (WsprTxService svc) => Results.Ok(svc.Status()));

        app.MapPost("/api/wspr/tx/arm", (WsprTxArmRequest body, WsprTxService svc) =>
        {
            svc.SetArmed(body.Enabled);
            log.LogInformation("api.wspr.tx.arm enabled={Enabled}", body.Enabled);
            return Results.Ok(svc.Status());
        });

        app.MapPost("/api/wspr/tx/settings", (WsprTxSettingsRequest body, WsprTxService svc) =>
        {
            string? err = svc.SetSettings(
                body.Call, body.Grid4, body.DBm ?? 30, body.AudioHz ?? 1500, body.TxPercent ?? 0.2);
            if (err is not null) return Results.BadRequest(new { error = err });
            log.LogInformation("api.wspr.tx.settings call={Call} grid={Grid} pct={Pct}",
                body.Call, body.Grid4, body.TxPercent);
            return Results.Ok(svc.Status());
        });

        app.MapPost("/api/wspr/tx/halt", (WsprTxService svc) =>
        {
            svc.Halt();
            log.LogInformation("api.wspr.tx.halt");
            return Results.Ok(svc.Status());
        });

        return app;
    }
}
