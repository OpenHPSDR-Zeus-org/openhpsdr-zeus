// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;

namespace Zeus.Server.Tests;

public sealed class HamClockPushTests
{
    // ---- URL builder -----------------------------------------------------

    [Fact]
    public void BuildSetDxUrl_UsesNamedGridParamAndClassicPort()
    {
        // Classic HamClock REST: set_newdx?grid=<grid> (named param, NOT a bare
        // arg) on the REST port (default 8080) — verified June 2026.
        Assert.Equal(
            "http://192.168.1.50:8080/set_newdx?grid=FN31",
            HamClockService.BuildSetDxUrl("192.168.1.50", 8080, "FN31"));
    }

    [Fact]
    public void BuildSetDxUrl_EscapesGrid()
    {
        var url = HamClockService.BuildSetDxUrl("host", 8080, "FN31aa");
        Assert.Equal("http://host:8080/set_newdx?grid=FN31aa", url);
    }

    // ---- Grid validation -------------------------------------------------

    [Theory]
    [InlineData("FN31", "FN31")]
    [InlineData("fn31", "FN31")]            // field upper-cased
    [InlineData("FN31aa", "FN31aa")]
    [InlineData("FN31AA", "FN31aa")]        // subsquare lower-cased
    [InlineData(" FN31 ", "FN31")]          // trimmed
    public void TryNormalizeGrid_AcceptsValid(string input, string expected)
    {
        Assert.True(HamClockService.TryNormalizeGrid(input, out var g));
        Assert.Equal(expected, g);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("F")]
    [InlineData("FN3")]                     // 3 chars
    [InlineData("FN311")]                   // 5 chars
    [InlineData("ZN31")]                    // field out of A-R
    [InlineData("FNAB")]                    // square not digits
    [InlineData("FN31zz")]                  // subsquare out of a-x
    public void TryNormalizeGrid_RejectsInvalid(string? input)
    {
        Assert.False(HamClockService.TryNormalizeGrid(input, out var g));
        Assert.Equal("", g);
    }

    // ---- PushDxAsync gating (no network) ---------------------------------

    [Fact]
    public async Task PushDxAsync_InvalidGrid_NoOp()
    {
        var hc = new HamClockService(NullLogger<HamClockService>.Instance);
        Assert.False(await hc.PushDxAsync("nope", "external", "127.0.0.1", 8080));
    }

    [Fact]
    public async Task PushDxAsync_BundledTarget_NoOp()
    {
        // The bundled OpenHamClock sidecar has no set-DX endpoint; bundled must be
        // a logged no-op (returns false) even with a valid grid.
        var hc = new HamClockService(NullLogger<HamClockService>.Instance);
        Assert.False(await hc.PushDxAsync("FN31", "bundled", "127.0.0.1", 8080));
    }

    [Fact]
    public async Task PushDxAsync_ExternalWithoutHost_NoOp()
    {
        var hc = new HamClockService(NullLogger<HamClockService>.Instance);
        Assert.False(await hc.PushDxAsync("FN31", "external", "", 8080));
    }

    // ---- Push config store ----------------------------------------------

    [Fact]
    public void PushConfigStore_DefaultsToOff()
    {
        using var tmp = new TempDb();
        using var store = new HamClockPushConfigStore(NullLogger<HamClockPushConfigStore>.Instance, tmp.Path);
        Assert.Null(store.Get());
    }

    [Fact]
    public void PushConfigStore_RoundTrips()
    {
        using var tmp = new TempDb();
        using (var store = new HamClockPushConfigStore(NullLogger<HamClockPushConfigStore>.Instance, tmp.Path))
            store.Set(new HamClockPushConfig(true, "on-active-QSO", "external", "10.0.0.5", 8080));
        using (var store = new HamClockPushConfigStore(NullLogger<HamClockPushConfigStore>.Instance, tmp.Path))
        {
            var c = store.Get();
            Assert.NotNull(c);
            Assert.True(c!.Enabled);
            Assert.Equal("on-active-QSO", c.Trigger);
            Assert.Equal("10.0.0.5", c.ExternalHost);
            Assert.Equal(8080, c.ExternalPort);
        }
    }

    [Fact]
    public void Management_NormalizesTriggerTargetAndPort()
    {
        using var tmp = new TempDb();
        using var store = new HamClockPushConfigStore(NullLogger<HamClockPushConfigStore>.Instance, tmp.Path);
        var mgmt = new HamClockPushManagementService(NullLogger<HamClockPushManagementService>.Instance, store);

        var c = mgmt.SetConfig(new HamClockPushConfig(true, "garbage", "weird", "  host  ", 0));
        Assert.Equal("on-click", c.Trigger);   // unknown trigger -> default
        Assert.Equal("external", c.Target);    // unknown target -> external
        Assert.Equal("host", c.ExternalHost);  // trimmed
        Assert.Equal(8080, c.ExternalPort);    // invalid port -> default
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
