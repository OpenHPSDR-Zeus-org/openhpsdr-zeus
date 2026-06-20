// SPDX-License-Identifier: GPL-2.0-or-later
//
// Endpoint coverage for ANAN-G2/Saturn ADC dither/random hardware options.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Zeus.Server.Tests;

public sealed class G2OptionsEndpointTests
{
    [Fact]
    public async Task G2Options_DefaultsOnButUnsupportedWithoutG2Selection()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/radio/g2-options");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;
        Assert.True(root.GetProperty("ditherEnabled").GetBoolean());
        Assert.True(root.GetProperty("randomEnabled").GetBoolean());
        Assert.Equal(60.0, root.GetProperty("maxRxFreqMHz").GetDouble());
        Assert.False(root.GetProperty("supported").GetBoolean());
        Assert.Equal(0, root.GetProperty("rx1AttenuatorDb").GetInt32());
        Assert.Equal(0, root.GetProperty("rx1AttenuatorMinDb").GetInt32());
        Assert.Equal(31, root.GetProperty("rx1AttenuatorMaxDb").GetInt32());
        Assert.False(root.GetProperty("rx1AttenuatorSupported").GetBoolean());
    }

    [Fact]
    public async Task G2Options_PutPersistsAndBecomesSupportedForG2Selection()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync(
            "/api/radio/g2-options",
            new { ditherEnabled = false, randomEnabled = true, rx1AttenuatorDb = 14 });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using (var body = await JsonDocument.ParseAsync(await put.Content.ReadAsStreamAsync()))
        {
            var root = body.RootElement;
            Assert.False(root.GetProperty("ditherEnabled").GetBoolean());
            Assert.True(root.GetProperty("randomEnabled").GetBoolean());
            Assert.False(root.GetProperty("supported").GetBoolean());
            Assert.Equal(14, root.GetProperty("rx1AttenuatorDb").GetInt32());
            Assert.False(root.GetProperty("rx1AttenuatorSupported").GetBoolean());
        }

        var selection = await client.PutAsJsonAsync(
            "/api/radio/selection",
            new { preferred = "OrionMkII", overrideDetection = false });

        Assert.Equal(HttpStatusCode.OK, selection.StatusCode);

        var get = await client.GetAsync("/api/radio/g2-options");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var getBody = await JsonDocument.ParseAsync(await get.Content.ReadAsStreamAsync());
        var afterSelection = getBody.RootElement;
        Assert.False(afterSelection.GetProperty("ditherEnabled").GetBoolean());
        Assert.True(afterSelection.GetProperty("randomEnabled").GetBoolean());
        Assert.Equal(60.0, afterSelection.GetProperty("maxRxFreqMHz").GetDouble());
        Assert.True(afterSelection.GetProperty("supported").GetBoolean());
        Assert.Equal(14, afterSelection.GetProperty("rx1AttenuatorDb").GetInt32());
        Assert.True(afterSelection.GetProperty("rx1AttenuatorSupported").GetBoolean());

        var caps = await client.GetAsync("/api/radio/capabilities");
        Assert.Equal(HttpStatusCode.OK, caps.StatusCode);
        using var capsBody = await JsonDocument.ParseAsync(await caps.Content.ReadAsStreamAsync());
        Assert.True(capsBody.RootElement.GetProperty("supportsG2AdcOptions").GetBoolean());
    }

    [Fact]
    public async Task G2Options_RejectsInvalidRx1Attenuator()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync(
            "/api/radio/g2-options",
            new { rx1AttenuatorDb = 32 });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        using var body = await JsonDocument.ParseAsync(await put.Content.ReadAsStreamAsync());
        Assert.Contains("rx1AttenuatorDb", body.RootElement.GetProperty("error").GetString());
    }

    private sealed class Factory : IsolatedPrefsFactory
    {
    }
}
