// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server.FrontPanel;

namespace Zeus.Server.Tests;

// Persistence for the G2 / G2-Ultra front-panel bridge settings (enable +
// serial device + baud override) surfaced in the Radio Settings card.
public sealed class G2PanelSettingsStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-g2panel-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private G2PanelSettingsStore NewStore() =>
        new(NullLogger<G2PanelSettingsStore>.Instance, _dbPath);

    [Fact]
    public void Get_FreshInstall_DefaultsEnabledTrueNoOverride()
    {
        using var store = NewStore();
        var s = store.Get();
        Assert.True(s.Enabled);          // auto-detect behaviour preserved
        Assert.Null(s.DevicePath);       // no override → auto-detect
        Assert.Equal(0, s.Baud);         // 0 = auto
    }

    [Fact]
    public void SetThenGet_RoundTrips()
    {
        using var store = NewStore();
        store.Set(enabled: false, devicePath: "COM5", baud: 9600);
        var s = store.Get();
        Assert.False(s.Enabled);
        Assert.Equal("COM5", s.DevicePath);
        Assert.Equal(9600, s.Baud);
    }

    [Fact]
    public void Set_NormalizesBlankPathAndNonPositiveBaud()
    {
        using var store = NewStore();
        store.Set(enabled: true, devicePath: "   ", baud: -1);
        var s = store.Get();
        Assert.Null(s.DevicePath);  // whitespace → auto-detect
        Assert.Equal(0, s.Baud);    // non-positive → auto
    }

    [Fact]
    public void Set_PersistsAcrossReopen()
    {
        using (var store = NewStore())
            store.Set(enabled: true, devicePath: "/dev/ttyACM0", baud: 115200);
        using var reopened = NewStore();
        var s = reopened.Get();
        Assert.Equal("/dev/ttyACM0", s.DevicePath);
        Assert.Equal(115200, s.Baud);
    }

    [Fact]
    public void Set_FiresChanged()
    {
        using var store = NewStore();
        int fired = 0;
        store.Changed += () => fired++;
        store.Set(enabled: false, devicePath: null, baud: 0);
        Assert.Equal(1, fired);
    }
}
