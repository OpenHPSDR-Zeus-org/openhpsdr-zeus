// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// External-ports plan, Phase 4 — capability-gated /api/radio/audio.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

/// <summary>
/// Capability-gating + persistence of the global audio front-end endpoint. The
/// connected board is simulated via the PreferredRadioStore override so
/// RadioService.EffectiveBoardKind resolves without a live radio.
/// </summary>
public class AudioEndpointTests : IClassFixture<AudioEndpointTests.Factory>
{
    private readonly Factory _factory;
    public AudioEndpointTests(Factory factory) => _factory = factory;

    // The server serializes enums as their member names (JsonStringEnumConverter
    // is registered globally in ZeusHost). The default test deserializer can't
    // read a string back into TxAudioSource, so mirror the server's converter.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private async Task<AudioFrontEndDto?> GetAudioAsync(HttpClient client) =>
        await client.GetFromJsonAsync<AudioFrontEndDto>("/api/radio/audio", Json);

    private static async Task<AudioFrontEndDto?> ReadAudioAsync(HttpResponseMessage resp) =>
        await resp.Content.ReadFromJsonAsync<AudioFrontEndDto>(Json);

    private void SetBoard(HpsdrBoardKind board)
    {
        using var scope = _factory.Services.CreateScope();
        var prefs = scope.ServiceProvider.GetRequiredService<PreferredRadioStore>();
        prefs.Set(board, overrideDetection: true);
    }

    [Fact]
    public async Task CodecBoard_GET_ReportsCodecGate()
    {
        SetBoard(HpsdrBoardKind.OrionMkII); // has onboard codec
        using var client = _factory.CreateClient();

        var dto = await GetAudioAsync(client);
        Assert.NotNull(dto);
        Assert.True(dto!.HasOnboardCodec);
        Assert.False(dto.HermesLite2MicFrontEnd);
    }

    [Fact]
    public async Task Hl2_GET_ReportsMicFrontEndGate()
    {
        SetBoard(HpsdrBoardKind.HermesLite2);
        using var client = _factory.CreateClient();

        var dto = await GetAudioAsync(client);
        Assert.NotNull(dto);
        Assert.False(dto!.HasOnboardCodec);     // HL2 has no stream codec
        Assert.True(dto.HermesLite2MicFrontEnd); // but DOES have the mic front-end
    }

    [Fact]
    public async Task CodecBoard_GET_ReportsSourceGates()
    {
        // OrionMkII default variant is G2 (Saturn FPGA): line-in + bias + XLR.
        SetBoard(HpsdrBoardKind.OrionMkII);
        using var client = _factory.CreateClient();

        var dto = await GetAudioAsync(client);
        Assert.NotNull(dto);
        Assert.True(dto!.HasRadioLineIn);
        Assert.True(dto.HasBalancedXlr);
        Assert.True(dto.HasMicBias);
    }

    [Fact]
    public async Task CodecBoard_PUT_PersistsAndRoundTrips()
    {
        SetBoard(HpsdrBoardKind.OrionMkII); // G2 — supports RadioLineIn
        using var client = _factory.CreateClient();

        var resp = await client.PutAsJsonAsync("/api/radio/audio",
            new { source = "RadioLineIn", micBoost = false, micBias = false, lineInGain = 18 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var dto = await ReadAudioAsync(resp);
        Assert.NotNull(dto);
        Assert.Equal(TxAudioSource.RadioLineIn, dto!.Source);
        Assert.Equal(18, dto.LineInGain);

        var got = await GetAudioAsync(client);
        Assert.Equal(18, got!.LineInGain);
        Assert.Equal(TxAudioSource.RadioLineIn, got.Source);
    }

    [Fact]
    public async Task LineInGain_ClampedTo31()
    {
        SetBoard(HpsdrBoardKind.OrionMkII); // G2 — supports RadioLineIn
        using var client = _factory.CreateClient();

        var resp = await client.PutAsJsonAsync("/api/radio/audio",
            new { source = "RadioLineIn", micBoost = false, micBias = false, lineInGain = 200 });
        var dto = await ReadAudioAsync(resp);
        Assert.Equal(31, dto!.LineInGain);
    }

    [Fact]
    public async Task NonAudioBoard_PUT_Is409()
    {
        // A board with neither codec nor HL2 mic front-end is rejected — the
        // wire never gets handed audio bytes. (UnknownDefaults has both false.)
        SetBoard(HpsdrBoardKind.Unknown);
        using var client = _factory.CreateClient();

        var resp = await client.PutAsJsonAsync("/api/radio/audio",
            new { source = "RadioMic", micBoost = false, micBias = false, lineInGain = 0 });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task UnsupportedSource_OnBoard_ClampsToHost()
    {
        // HL2 has a mic front-end (so the PUT is accepted, not 409) but no
        // onboard codec → it is Host-only. A RadioMic request must clamp back to
        // Host rather than persist an illegal jack the wire can't emit.
        SetBoard(HpsdrBoardKind.HermesLite2);
        using var client = _factory.CreateClient();

        var resp = await client.PutAsJsonAsync("/api/radio/audio",
            new { source = "RadioMic", micBoost = true, micBias = true, lineInGain = 9 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var dto = await ReadAudioAsync(resp);
        Assert.Equal(TxAudioSource.Host, dto!.Source);
        // Params dropped with the source.
        Assert.False(dto.MicBoost);
        Assert.False(dto.MicBias);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Test");
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        }
    }
}
