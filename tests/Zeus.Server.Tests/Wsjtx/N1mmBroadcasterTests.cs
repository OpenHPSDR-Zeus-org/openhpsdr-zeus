// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.Wsjtx;

namespace Zeus.Server.Tests.Wsjtx;

public sealed class N1mmBroadcasterTests
{
    [Fact]
    public void ConfigStore_DefaultsToOff()
    {
        using var tmp = new TempDb();
        using var store = new N1mmConfigStore(NullLogger<N1mmConfigStore>.Instance, tmp.Path);
        Assert.Null(store.Get()); // nothing persisted -> caller uses default (OFF, port 2333)
    }

    [Fact]
    public void ConfigStore_RoundTrips()
    {
        using var tmp = new TempDb();
        using (var store = new N1mmConfigStore(NullLogger<N1mmConfigStore>.Instance, tmp.Path))
            store.Set(new N1mmConfig(Enabled: true, Host: "10.0.0.9", Port: 12060));
        using (var store = new N1mmConfigStore(NullLogger<N1mmConfigStore>.Instance, tmp.Path))
        {
            var c = store.Get();
            Assert.NotNull(c);
            Assert.True(c!.Enabled);
            Assert.Equal("10.0.0.9", c.Host);
            Assert.Equal(12060, c.Port);
        }
    }

    [Fact]
    public void Broadcaster_DefaultConfigIsOffPort2333()
    {
        using var tmp = new TempDb();
        using var store = new N1mmConfigStore(NullLogger<N1mmConfigStore>.Instance, tmp.Path);
        using var b = new N1mmBroadcaster(NullLogger<N1mmBroadcaster>.Instance, store);
        var c = b.GetConfig();
        Assert.False(c.Enabled);
        Assert.Equal(2333, c.Port);
        Assert.Equal("127.0.0.1", c.Host);
    }

    [Fact]
    public void Broadcaster_SetConfig_NormalizesAndPersists()
    {
        using var tmp = new TempDb();
        using var store = new N1mmConfigStore(NullLogger<N1mmConfigStore>.Instance, tmp.Path);
        using var b = new N1mmBroadcaster(NullLogger<N1mmBroadcaster>.Instance, store);

        var c = b.SetConfig(new N1mmConfig(Enabled: true, Host: "  ", Port: 0));
        Assert.Equal("127.0.0.1", c.Host); // blank -> loopback
        Assert.Equal(2333, c.Port);        // invalid -> default
        Assert.Equal(2333, store.Get()!.Port); // persisted
    }

    private sealed class TempDb : IDisposable
    {
        public string Path { get; }
        private readonly string _dir;
        public TempDb()
        {
            _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"zeus-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            Path = System.IO.Path.Combine(_dir, "zeus-prefs.db");
        }
        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
