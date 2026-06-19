// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Text.Json;

namespace Zeus.Server.Tests;

public sealed class AudioDeviceEndpointTests
{
    [Fact]
    public async Task GetAudioDevices_ReturnsUnsupportedInServerHost()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/audio/devices");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var body = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var root = body.RootElement;
        Assert.False(root.GetProperty("supported").GetBoolean());
        Assert.Null(root.GetProperty("inputDeviceId").GetString());
        Assert.Null(root.GetProperty("outputDeviceId").GetString());
        Assert.Null(root.GetProperty("activeInputDeviceId").GetString());
        Assert.Null(root.GetProperty("activeOutputDeviceId").GetString());
        Assert.Equal(0, root.GetProperty("inputs").GetArrayLength());
        Assert.Equal(0, root.GetProperty("outputs").GetArrayLength());
    }

    private sealed class Factory : IsolatedPrefsFactory
    {
    }
}
