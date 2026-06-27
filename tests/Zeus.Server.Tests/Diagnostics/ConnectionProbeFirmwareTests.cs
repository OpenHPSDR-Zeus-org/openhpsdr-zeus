// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
//
// The "Report a problem" connection snapshot must record the firmware /
// gateware version captured at connect. An ANAN-10E running its Protocol-2
// beta (issue #1053) needs its exact firmware pinned for triage — the value
// used to be a hard-coded "not exposed by RadioService" placeholder. These
// tests drive RadioService through a P2 connect and assert ConnectionProbe
// surfaces the captured firmware string, and degrades sanely when it is absent
// (forced connect) or disconnected.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;
using Zeus.Server.Diagnostics;

namespace Zeus.Server.Tests;

public sealed class ConnectionProbeFirmwareTests : IDisposable
{
    private readonly string _dbPath;

    public ConnectionProbeFirmwareTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"zeus-prefs-fwprobe-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", ".pa", ".dsp" })
        {
            try { if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix); } catch { }
        }
    }

    private RadioService NewRadio() => new(
        NullLoggerFactory.Instance,
        new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath + ".dsp"),
        new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa"));

    // Run the ConnectionProbe against a service provider holding the given
    // RadioService and return the reported firmware.version value.
    private static string FirmwareValue(RadioService radio)
    {
        var services = new ServiceCollection();
        services.AddSingleton(radio);
        var ctx = new DiagnosticContext
        {
            Services = services.BuildServiceProvider(),
            RecentLog = new List<string>(),
        };
        var section = new ConnectionProbe().Collect(ctx);
        return section.Items.First(i => i.Key == "firmware.version").Value;
    }

    [Fact]
    public void Connected_P2_SurfacesCapturedFirmwareString()
    {
        using var radio = NewRadio();
        radio.MarkProtocol2Connected(
            "127.0.0.1:1024", 192_000, client: null,
            boardKind: HpsdrBoardKind.HermesII, firmware: "10.3");

        Assert.Equal("10.3", radio.ConnectedFirmware);
        Assert.Equal("10.3", FirmwareValue(radio));
    }

    [Fact]
    public void Connected_WithoutDiscoveredFirmware_ReportsUnknown()
    {
        // A forced/reclaim connect skips the discovery probe, so no firmware is
        // captured — the snapshot must say "unknown", not leak a stale value.
        using var radio = NewRadio();
        radio.MarkProtocol2Connected(
            "127.0.0.1:1024", 192_000, client: null,
            boardKind: HpsdrBoardKind.HermesII, firmware: null);

        Assert.Null(radio.ConnectedFirmware);
        Assert.Equal("unknown", FirmwareValue(radio));
    }

    [Fact]
    public void Disconnected_ReportsNotApplicable()
    {
        using var radio = NewRadio();
        Assert.Equal("n/a (disconnected)", FirmwareValue(radio));
    }

    [Fact]
    public void Disconnect_ClearsCapturedFirmware()
    {
        using var radio = NewRadio();
        radio.MarkProtocol2Connected(
            "127.0.0.1:1024", 192_000, client: null,
            boardKind: HpsdrBoardKind.HermesII, firmware: "10.3");
        Assert.Equal("10.3", radio.ConnectedFirmware);

        radio.MarkProtocol2Disconnected();

        Assert.Null(radio.ConnectedFirmware);
        Assert.Equal("n/a (disconnected)", FirmwareValue(radio));
    }
}
