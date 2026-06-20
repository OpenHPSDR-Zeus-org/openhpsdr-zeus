// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class DspTxOutputHeadroomProfileEndpointTests
{
    [Fact]
    public async Task Get_DefaultsToCurrentProfile()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var root = await client.GetFromJsonAsync<JsonElement>("/api/dsp/tx-output-headroom-profile");

        Assert.Equal("current", root.GetProperty("profile").GetString());
        Assert.Equal("current", root.GetProperty("activeProfile").GetString());
        Assert.Equal("current", root.GetProperty("defaultProfile").GetString());
        Assert.False(root.GetProperty("experimental").GetBoolean());
        Assert.Equal(0.0, root.GetProperty("trimDb").GetDouble(), precision: 2);
        Assert.False(root.GetProperty("pureSignalBypassActive").GetBoolean());
        Assert.Equal("post-wdsp-mic-wire-output", root.GetProperty("integrationPoint").GetString());
        Assert.Contains(root.GetProperty("supportedProfiles").EnumerateArray(), p => p.GetString() == "current");
        Assert.Contains(root.GetProperty("supportedProfiles").EnumerateArray(), p => p.GetString() == "headroom-trim-candidate");
    }

    [Fact]
    public async Task Put_InvalidProfileRejectsWithoutChangingSelection()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        var dsp = factory.Services.GetRequiredService<DspPipelineService>();

        var before = dsp.TxOutputHeadroomRequestedProfile;

        using var response = await client.PutAsJsonAsync(
            "/api/dsp/tx-output-headroom-profile",
            new { profile = "always-on" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, dsp.TxOutputHeadroomRequestedProfile);
    }

    [Fact]
    public async Task Put_HeadroomTrimCandidateRoundTripsAsExplicitOptIn()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        var dsp = factory.Services.GetRequiredService<DspPipelineService>();

        using var response = await client.PutAsJsonAsync(
            "/api/dsp/tx-output-headroom-profile",
            new { profile = "headroom-trim-candidate" });
        var root = await JsonSerializer.DeserializeAsync<JsonElement>(
            await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("headroom-trim-candidate", root.GetProperty("profile").GetString());
        Assert.Equal("headroom-trim-candidate", root.GetProperty("activeProfile").GetString());
        Assert.True(root.GetProperty("experimental").GetBoolean());
        Assert.Equal(-0.35, root.GetProperty("trimDb").GetDouble(), precision: 2);
        Assert.Equal(
            DspPipelineService.TxOutputHeadroomProfile.HeadroomTrimCandidate,
            dsp.TxOutputHeadroomRequestedProfile);
    }

    [Fact]
    public async Task Get_WithPureSignalArmedReportsCandidateBypassed()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        var dsp = factory.Services.GetRequiredService<DspPipelineService>();
        var radio = factory.Services.GetRequiredService<RadioService>();

        dsp.SetTxOutputHeadroomProfile(DspPipelineService.TxOutputHeadroomProfile.HeadroomTrimCandidate);
        radio.SetPs(new PsControlSetRequest(Enabled: true, Auto: true, Single: false));

        var root = await client.GetFromJsonAsync<JsonElement>("/api/dsp/tx-output-headroom-profile");

        Assert.Equal("headroom-trim-candidate", root.GetProperty("profile").GetString());
        Assert.Equal("current", root.GetProperty("activeProfile").GetString());
        Assert.True(root.GetProperty("experimental").GetBoolean());
        Assert.True(root.GetProperty("pureSignalBypassActive").GetBoolean());
        Assert.Equal(0.0, root.GetProperty("trimDb").GetDouble(), precision: 2);
    }

    [Fact]
    public async Task Get_ClearsPureSignalBypassWhenDeferredArmIsCancelledBeforeKeyUp()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        var dsp = factory.Services.GetRequiredService<DspPipelineService>();
        var radio = factory.Services.GetRequiredService<RadioService>();

        dsp.SetTxOutputHeadroomProfile(DspPipelineService.TxOutputHeadroomProfile.HeadroomTrimCandidate);
        radio.SetMox(true);
        radio.SetPs(new PsControlSetRequest(Enabled: true, Auto: true, Single: false));

        var bypassed = await client.GetFromJsonAsync<JsonElement>("/api/dsp/tx-output-headroom-profile");
        Assert.Equal("current", bypassed.GetProperty("activeProfile").GetString());
        Assert.True(bypassed.GetProperty("pureSignalBypassActive").GetBoolean());

        radio.SetPs(new PsControlSetRequest(Enabled: false, Auto: true, Single: false));

        var root = await client.GetFromJsonAsync<JsonElement>("/api/dsp/tx-output-headroom-profile");
        Assert.Equal("headroom-trim-candidate", root.GetProperty("profile").GetString());
        Assert.Equal("headroom-trim-candidate", root.GetProperty("activeProfile").GetString());
        Assert.False(root.GetProperty("pureSignalBypassActive").GetBoolean());
        Assert.Equal(-0.35, root.GetProperty("trimDb").GetDouble(), precision: 2);
    }

    private sealed class Factory : IsolatedPrefsFactory
    {
    }
}
