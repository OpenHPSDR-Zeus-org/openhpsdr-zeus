// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// External-ports plan, Phase 2 — capability-gated /api/radio/antenna.

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Capability-gating + persistence of the per-band antenna endpoint. The
/// connected board is simulated by writing the PreferredRadioStore override so
/// RadioService.EffectiveBoardKind resolves without a live radio.
/// </summary>
public class AntennaEndpointTests : IClassFixture<AntennaEndpointTests.Factory>
{
    private readonly Factory _factory;
    public AntennaEndpointTests(Factory factory) => _factory = factory;

    private void SetBoard(HpsdrBoardKind board)
    {
        using var scope = _factory.Services.CreateScope();
        var prefs = scope.ServiceProvider.GetRequiredService<PreferredRadioStore>();
        prefs.Set(board, overrideDetection: true);
    }

    [Fact]
    public async Task RelayBoard_Accepts_Ant2_And_Persists()
    {
        SetBoard(HpsdrBoardKind.OrionMkII); // has TX + RX antenna relays
        using var client = _factory.CreateClient();

        var resp = await client.PutAsJsonAsync("/api/radio/antenna",
            new { band = "20m", txAnt = "Ant2", rxAnt = "Ant3" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<AntennaSettingsDto>();
        Assert.NotNull(dto);
        Assert.True(dto!.HasTxAntennaRelays);
        Assert.True(dto.HasRxAntennaRelays);
        var b20 = dto.Bands.First(b => b.Band == "20m");
        Assert.Equal("Ant2", b20.TxAnt);
        Assert.Equal("Ant3", b20.RxAnt);

        // GET reflects the persisted selection.
        var got = await client.GetFromJsonAsync<AntennaSettingsDto>("/api/radio/antenna");
        Assert.Equal("Ant2", got!.Bands.First(b => b.Band == "20m").TxAnt);
    }

    [Fact]
    public async Task NonRelayBoard_Rejects_TxAnt2_With_409()
    {
        SetBoard(HpsdrBoardKind.HermesLite2); // single jack: no TX/RX relays
        using var client = _factory.CreateClient();

        var resp = await client.PutAsJsonAsync("/api/radio/antenna",
            new { band = "20m", txAnt = "Ant2", rxAnt = "Ant1" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task NonRelayBoard_Rejects_RxAnt3_With_409()
    {
        SetBoard(HpsdrBoardKind.HermesLite2);
        using var client = _factory.CreateClient();

        var resp = await client.PutAsJsonAsync("/api/radio/antenna",
            new { band = "20m", txAnt = "Ant1", rxAnt = "Ant3" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task NonRelayBoard_Accepts_Ant1()
    {
        SetBoard(HpsdrBoardKind.HermesLite2);
        using var client = _factory.CreateClient();

        // ANT1 is the hardwired default on every board — always valid.
        var resp = await client.PutAsJsonAsync("/api/radio/antenna",
            new { band = "20m", txAnt = "Ant1", rxAnt = "Ant1" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Unknown_Band_Is_400()
    {
        SetBoard(HpsdrBoardKind.OrionMkII);
        using var client = _factory.CreateClient();

        var resp = await client.PutAsJsonAsync("/api/radio/antenna",
            new { band = "not-a-band", txAnt = "Ant1", rxAnt = "Ant1" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Unparseable_Antenna_Is_400()
    {
        SetBoard(HpsdrBoardKind.OrionMkII);
        using var client = _factory.CreateClient();

        var resp = await client.PutAsJsonAsync("/api/radio/antenna",
            new { band = "20m", txAnt = "Ant9", rxAnt = "Ant1" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            // Only the HTTP handler is under test; keep the hosted pipeline out.
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        }
    }
}
