// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class AudioDeviceSettingsStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"zeus-audio-devices-{Guid.NewGuid():N}.db");

    [Fact]
    public void Get_DefaultsToSystemDevices()
    {
        using var store = NewStore();

        var settings = store.Get();

        Assert.Null(settings.InputDeviceId);
        Assert.Null(settings.OutputDeviceId);
    }

    [Fact]
    public void Set_RoundTripsInputAndOutputDeviceIds()
    {
        using (var store = NewStore())
        {
            store.Set(" input-id ", " output-id ");
        }

        using (var store = NewStore())
        {
            var settings = store.Get();
            Assert.Equal("input-id", settings.InputDeviceId);
            Assert.Equal("output-id", settings.OutputDeviceId);
        }
    }

    [Fact]
    public void PerDeviceSetters_PreserveTheOtherRoute()
    {
        using var store = NewStore();
        store.Set("mic-a", "speaker-a");

        store.SetInputDeviceId("mic-b");
        Assert.Equal("mic-b", store.Get().InputDeviceId);
        Assert.Equal("speaker-a", store.Get().OutputDeviceId);

        store.SetOutputDeviceId(null);
        Assert.Equal("mic-b", store.Get().InputDeviceId);
        Assert.Null(store.Get().OutputDeviceId);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* ignore */ }
    }

    private AudioDeviceSettingsStore NewStore() =>
        new(NullLogger<AudioDeviceSettingsStore>.Instance, _dbPath);
}
