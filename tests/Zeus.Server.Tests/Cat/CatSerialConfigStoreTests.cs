// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests.Cat;

// Round-trip + normalisation tests for the serial-CAT config store. The key
// invariant beyond persistence: every one of the four ports keeps its OWN baud
// (Thetis has a real bug where CAT4 reports CAT1's baud — we must not repeat it).
public sealed class CatSerialConfigStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-cat-serial-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private CatSerialConfigStore NewStore() =>
        new(NullLogger<CatSerialConfigStore>.Instance, _dbPath);

    [Fact]
    public void Get_OnFreshDb_ReturnsFourDisabledDefaults()
    {
        using var store = NewStore();
        var ports = store.Get();
        Assert.Equal(CatSerialDefaults.PortCount, ports.Count);
        Assert.All(ports, p =>
        {
            Assert.False(p.Enabled);
            Assert.Equal(string.Empty, p.PortName);
            Assert.Equal(115200, p.BaudRate);
            Assert.Equal("None", p.Parity);
            Assert.Equal(8, p.DataBits);
            Assert.Equal("One", p.StopBits);
        });
    }

    [Fact]
    public void Set_Then_Get_RoundTripsEachPortIndependently()
    {
        using (var store = NewStore())
        {
            store.Set(new[]
            {
                new CatSerialPortConfig(true,  "/dev/ttys001", 9600,   "None",  8, "One"),
                new CatSerialPortConfig(true,  "COM3",         19200,  "Even",  7, "Two"),
                new CatSerialPortConfig(false, "/dev/ttys002", 38400,  "Odd",   8, "OnePointFive"),
                new CatSerialPortConfig(true,  "/dev/ttys003", 115200, "None",  8, "One"),
            });
        }

        // New store instance → proves it persisted to disk, not just memory.
        using var reopened = NewStore();
        var ports = reopened.Get();
        Assert.Equal(4, ports.Count);

        Assert.Equal(9600, ports[0].BaudRate);
        Assert.Equal(19200, ports[1].BaudRate);
        Assert.Equal(115200, ports[3].BaudRate);
        // The Thetis CAT4-baud bug guard: port 4 must NOT echo port 1's baud.
        Assert.NotEqual(ports[0].BaudRate, ports[3].BaudRate);

        Assert.Equal("COM3", ports[1].PortName);
        Assert.Equal("Even", ports[1].Parity);
        Assert.Equal(7, ports[1].DataBits);
        Assert.Equal("Two", ports[1].StopBits);
        Assert.True(ports[0].Enabled);
        Assert.False(ports[2].Enabled);
    }

    [Fact]
    public void Set_FewerThanFour_PadsToFour()
    {
        using var store = NewStore();
        store.Set(new[] { new CatSerialPortConfig(true, "COM1", 9600, "None", 8, "One") });
        var ports = store.Get();
        Assert.Equal(4, ports.Count);
        Assert.True(ports[0].Enabled);
        Assert.False(ports[3].Enabled);
    }

    [Fact]
    public void Set_MoreThanFour_TruncatesToFour()
    {
        using var store = NewStore();
        store.Set(Enumerable.Range(0, 7)
            .Select(i => new CatSerialPortConfig(true, $"COM{i}", 9600, "None", 8, "One")));
        Assert.Equal(4, store.Get().Count);
    }

    [Fact]
    public void Set_InvalidValues_AreSanitised()
    {
        using var store = NewStore();
        store.Set(new[]
        {
            new CatSerialPortConfig(true, "  COM5  ", 12345 /*illegal*/, "Bogus", 5 /*illegal*/, "Three"),
        });
        var p = store.Get()[0];
        Assert.Equal("COM5", p.PortName);          // trimmed
        Assert.Equal(115200, p.BaudRate);          // illegal baud → default
        Assert.Equal("None", p.Parity);            // unknown parity → None
        Assert.Equal(8, p.DataBits);               // illegal data bits → 8
        Assert.Equal("One", p.StopBits);           // unknown stop bits → One
    }

    [Fact]
    public void Set_FiresChanged()
    {
        using var store = NewStore();
        int fired = 0;
        store.Changed += () => fired++;
        store.Set(new[] { new CatSerialPortConfig(true, "COM1") });
        Assert.Equal(1, fired);
    }
}
