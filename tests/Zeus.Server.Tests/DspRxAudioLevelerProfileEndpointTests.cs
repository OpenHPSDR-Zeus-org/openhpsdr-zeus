// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class DspRxAudioLevelerProfileEndpointTests
{
    [Fact]
    public async Task Get_DefaultsToCurrentProfile()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var root = await client.GetFromJsonAsync<JsonElement>("/api/dsp/rx-audio-leveler-profile");

        Assert.Equal("current", root.GetProperty("profile").GetString());
        Assert.Equal("current", root.GetProperty("activeProfile").GetString());
        Assert.Equal("current", root.GetProperty("defaultProfile").GetString());
        Assert.False(root.GetProperty("experimental").GetBoolean());
        Assert.Contains(root.GetProperty("supportedProfiles").EnumerateArray(), p => p.GetString() == "current");
        Assert.Contains(root.GetProperty("supportedProfiles").EnumerateArray(), p => p.GetString() == "stable-speech-candidate");
    }

    [Fact]
    public async Task Put_InvalidProfileRejectsWithoutChangingSelection()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        var dsp = factory.Services.GetRequiredService<DspPipelineService>();

        var before = dsp.RxAudioLevelerRequestedProfile;

        using var response = await client.PutAsJsonAsync(
            "/api/dsp/rx-audio-leveler-profile",
            new { profile = "aggressive" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, dsp.RxAudioLevelerRequestedProfile);
    }

    [Fact]
    public async Task Put_StableSpeechCandidateRoundTripsAsExplicitOptIn()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        var dsp = factory.Services.GetRequiredService<DspPipelineService>();

        using var response = await client.PutAsJsonAsync(
            "/api/dsp/rx-audio-leveler-profile",
            new { profile = "stable-speech-candidate" });
        var root = await JsonSerializer.DeserializeAsync<JsonElement>(
            await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("stable-speech-candidate", root.GetProperty("profile").GetString());
        Assert.Equal("current", root.GetProperty("activeProfile").GetString());
        Assert.True(root.GetProperty("experimental").GetBoolean());
        Assert.Equal(
            DspPipelineService.RxAudioLevelerProfile.StableSpeechCandidate,
            dsp.RxAudioLevelerRequestedProfile);
    }

    [Fact]
    public void SelectRxAudioLevelerProfileForBlock_ResetsStateOnProfileChange()
    {
        using var factory = new Factory();
        var dsp = factory.Services.GetRequiredService<DspPipelineService>();
        dsp.SetRxAudioLevelerStateForTest(new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 18.0,
            ControlRmsValid = true,
            ControlRmsDbfs = -28.0,
            ControlRmsHangDb = 4.0,
            DiagnosticsValid = true,
        });

        dsp.SetRxAudioLevelerProfile(DspPipelineService.RxAudioLevelerProfile.StableSpeechCandidate);
        var selected = dsp.SelectRxAudioLevelerProfileForBlock();

        Assert.Equal(DspPipelineService.RxAudioLevelerProfile.StableSpeechCandidate, selected);
        Assert.Equal(DspPipelineService.RxAudioLevelerProfile.StableSpeechCandidate, dsp.RxAudioLevelerActiveProfile);
        Assert.False(dsp.RxAudioLevelerStateForTest.DiagnosticsValid);
        Assert.Equal(0.0, dsp.RxAudioLevelerStateForTest.GainDb, precision: 12);
        Assert.False(dsp.RxAudioLevelerStateForTest.ControlRmsValid);

        dsp.SetRxAudioLevelerStateForTest(new DspPipelineService.RxAudioLevelerState
        {
            GainDb = 12.0,
            ControlRmsValid = true,
            ControlRmsDbfs = -30.0,
            DiagnosticsValid = true,
        });

        dsp.SetRxAudioLevelerProfile(DspPipelineService.RxAudioLevelerProfile.Current);
        selected = dsp.SelectRxAudioLevelerProfileForBlock();

        Assert.Equal(DspPipelineService.RxAudioLevelerProfile.Current, selected);
        Assert.Equal(DspPipelineService.RxAudioLevelerProfile.Current, dsp.RxAudioLevelerActiveProfile);
        Assert.False(dsp.RxAudioLevelerStateForTest.DiagnosticsValid);
        Assert.Equal(0.0, dsp.RxAudioLevelerStateForTest.GainDb, precision: 12);
        Assert.False(dsp.RxAudioLevelerStateForTest.ControlRmsValid);
    }

    private sealed class Factory : IsolatedPrefsFactory
    {
    }
}
