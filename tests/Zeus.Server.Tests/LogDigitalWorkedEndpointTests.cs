// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Pins GET /api/log/digital-worked — the core endpoint the Zeus Digital plugin
// UI shell fetches to decorate decode rows with the worked-B4 highlight. It
// must wrap LogService.GetDigitalWorkedCallsignsAsync verbatim: a {calls}
// array (the shape digital-worked-store.ts pins) containing every call with a
// prior FT8/FT4 QSO and NOTHING worked only on phone/CW (KB2UKA standing
// order — see LogServiceDigitalWorkedTests).
// Drives the real handler via WebApplicationFactory against a throw-away
// logbook DB (fresh per test, per the IsolatedPrefsFactory pattern); no
// sockets beyond the in-memory test server.

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class LogDigitalWorkedEndpointTests
{
    private sealed record DigitalWorkedResponse(string[] Calls);

    private static CreateLogEntryRequest Qso(string call, string mode) => new(
        Callsign: call,
        Name: "Test Op", // pre-filled so the endpoint's QRZ name enrichment never fires
        FrequencyMhz: 14.074,
        Band: "20m",
        Mode: mode,
        RstSent: "-10",
        RstRcvd: "-12",
        Grid: null,
        Country: null,
        Dxcc: null,
        CqZone: null,
        ItuZone: null,
        State: null,
        Comment: null,
        QsoDateTimeUtc: new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task EmptyLogbook_ReturnsEmptyCallsignsArray()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/log/digital-worked");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<DigitalWorkedResponse>();
        Assert.NotNull(body);
        Assert.Empty(body!.Calls);
    }

    [Fact]
    public async Task ReturnsDigitalWorkedCalls_ExcludingPhoneAndCw()
    {
        using var factory = new Factory();
        using var scope = factory.Services.CreateScope();
        var logService = scope.ServiceProvider.GetRequiredService<LogService>();
        await logService.CreateLogEntryAsync(Qso("K1FT8", "FT8"));
        await logService.CreateLogEntryAsync(Qso("K4FT4", "FT4"));
        await logService.CreateLogEntryAsync(Qso("W2SSB", "SSB"));
        await logService.CreateLogEntryAsync(Qso("N3CW", "CW"));
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/log/digital-worked");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<DigitalWorkedResponse>();
        Assert.NotNull(body);
        Assert.Contains("K1FT8", body!.Calls);
        Assert.Contains("K4FT4", body.Calls);
        Assert.DoesNotContain("W2SSB", body.Calls);
        Assert.DoesNotContain("N3CW", body.Calls);
    }

    private sealed class Factory : IsolatedPrefsFactory
    {
        private readonly string _logDbPath = Path.Combine(
            Path.GetTempPath(), $"zeus-log-digworked-ep-{Guid.NewGuid():N}.db");

        protected override void ConfigureExtra(IWebHostBuilder builder)
        {
            // Point LogService at a throw-away logbook DB so the test never
            // touches (or leaks into) the operator's real zeus-logbook.db.
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<LogService>();
                services.AddSingleton(sp => new LogService(
                    sp.GetRequiredService<ILogger<LogService>>(), _logDbPath));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(_logDbPath)) File.Delete(_logDbPath); } catch { }
            try { if (File.Exists(_logDbPath + "-log")) File.Delete(_logDbPath + "-log"); } catch { }
        }
    }
}
