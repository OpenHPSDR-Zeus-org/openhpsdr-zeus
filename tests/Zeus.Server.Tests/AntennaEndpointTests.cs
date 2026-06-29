// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// External-ports plan — antenna slice (#804): capability-gated
// /api/radio/antenna. Derives from IsolatedPrefsFactory so each test gets a
// fresh prefs DB (Linux LiteDB shared-mode isolation — GH #682).

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
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

    // ---- RX-aux gating ----

    [Fact]
    public async Task AuxBoard_Exposes_AvailableRxAux_And_Accepts_Bypass()
    {
        SetBoard(HpsdrBoardKind.OrionMkII); // RxAuxInputs.All
        using var client = _factory.CreateClient();

        var resp = await client.PutAsJsonAsync("/api/radio/antenna",
            new { band = "20m", txAnt = "Ant1", rxAnt = "Ant1", rxAux = "Bypass" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var dto = await resp.Content.ReadFromJsonAsync<AntennaSettingsDto>();
        Assert.NotNull(dto);
        Assert.Contains("Bypass", dto!.AvailableRxAux!);
        Assert.Equal("Modern", dto.AlexRevision);
        Assert.Equal("Bypass", dto.Bands.First(b => b.Band == "20m").RxAux);
    }

    [Fact]
    public async Task Hl2_Rejects_RxAux_With_409()
    {
        SetBoard(HpsdrBoardKind.HermesLite2); // RxAuxInputs.None
        using var client = _factory.CreateClient();

        var resp = await client.PutAsJsonAsync("/api/radio/antenna",
            new { band = "20m", txAnt = "Ant1", rxAnt = "Ant1", rxAux = "Ext1" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        // The GET advertises NO aux inputs on HL2.
        var got = await client.GetFromJsonAsync<AntennaSettingsDto>("/api/radio/antenna");
        Assert.Empty(got!.AvailableRxAux!);
    }

    public sealed class Factory : IsolatedPrefsFactory { }
}
