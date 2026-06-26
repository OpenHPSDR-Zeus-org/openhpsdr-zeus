// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zeus.Contracts;
using Zeus.Server;
using Zeus.Server.Cat;

namespace Zeus.Server.Tests.Cat;

// Service-level proof of the FULL serial-CAT chain: CatSerialConfigStore (on
// disk) → CatSerialService opens the configured device → live status reports
// Open → a serial client talks to it through the real handler. This covers the
// wiring the port-level test bypasses (config store, the BackgroundService
// run/reconnect loop, Snapshot status, and clean teardown via StopAsync).
public sealed class CatSerialServiceIntegrationTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-cat-serial-svc-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
        try { if (File.Exists(_dbPath + ".cfg")) File.Delete(_dbPath + ".cfg"); } catch { }
    }

    [SkippableFact]
    public async Task Service_OpensConfiguredPort_AndServesAClient()
    {
        Skip.IfNot(CatSerialTestSupport.PtyHarnessAvailable, "socat pty pair unavailable (POSIX + socat only)");
        using var pair = await SocatPtyPair.CreateAsync(CatSerialTestSupport.ResolveSocat()!);

        var (radio, tx, pipeline, dispose) = CatSerialTestSupport.BuildRadio(_dbPath);
        using var _ = dispose;

        // Persist a config that enables CAT 1 on Zeus's end of the pty pair.
        using var store = new CatSerialConfigStore(NullLogger<CatSerialConfigStore>.Instance, _dbPath + ".cfg");
        store.Set(new[]
        {
            new CatSerialPortConfig(true, pair.DeviceA, 115200, "None", 8, "One"),
            new CatSerialPortConfig(),
            new CatSerialPortConfig(),
            new CatSerialPortConfig(),
        });

        var svc = new CatSerialService(
            NullLogger<CatSerialService>.Instance, NullLoggerFactory.Instance,
            Options.Create(new CatOptions()), store, radio, tx, pipeline);

        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        try
        {
            // Service should open the configured port within a moment.
            await CatSerialPortIntegrationTests.WaitForAsync(() => svc.Snapshot().Ports[0].Open);
            var snap = svc.Snapshot();
            Assert.True(snap.Ports[0].Open, "CAT 1 should be open");
            Assert.Null(snap.Ports[0].Error);
            Assert.False(snap.Ports[1].Open, "CAT 2 (unconfigured) should be closed");

            // A client on the other end gets a valid TS-2000 identity, proving
            // the store→service→port→handler path is fully wired.
            using var client = CatSerialPortIntegrationTests.OpenClient(pair.DeviceB);
            client.Write("ID;");
            Assert.Equal("ID019;", await CatSerialPortIntegrationTests.ReadResponseAsync(client));

            // Setting the frequency over serial drives the real RadioService.
            client.Write("FA00014250000;");
            await CatSerialPortIntegrationTests.WaitForAsync(() => radio.Snapshot().VfoHz == 14_250_000);
            Assert.Equal(14_250_000, radio.Snapshot().VfoHz);

            // Activity counter reflects the two dispatched commands.
            Assert.True(svc.Snapshot().Ports[0].ClientActivity >= 2);
        }
        finally
        {
            await ((IHostedService)svc).StopAsync(CancellationToken.None);
        }

        // After stop, the port is released (a fresh open from the test succeeds).
        using var reopen = CatSerialPortIntegrationTests.OpenClient(pair.DeviceA);
        Assert.True(reopen.IsOpen);
    }

    [SkippableFact]
    public async Task Service_HotReconnects_OnSettingsChange()
    {
        Skip.IfNot(CatSerialTestSupport.PtyHarnessAvailable, "socat pty pair unavailable (POSIX + socat only)");
        using var pair = await SocatPtyPair.CreateAsync(CatSerialTestSupport.ResolveSocat()!);

        var (radio, tx, pipeline, dispose) = CatSerialTestSupport.BuildRadio(_dbPath);
        using var _ = dispose;

        using var store = new CatSerialConfigStore(NullLogger<CatSerialConfigStore>.Instance, _dbPath + ".cfg");
        // Start with everything disabled.
        store.Set(new[] { new CatSerialPortConfig(), new CatSerialPortConfig(), new CatSerialPortConfig(), new CatSerialPortConfig() });

        var svc = new CatSerialService(
            NullLogger<CatSerialService>.Instance, NullLoggerFactory.Instance,
            Options.Create(new CatOptions()), store, radio, tx, pipeline);

        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        try
        {
            Assert.False(svc.Snapshot().Ports[0].Open);

            // Enabling a port via the store fires Changed → the service should
            // re-resolve and open it with NO restart.
            store.Set(new[]
            {
                new CatSerialPortConfig(true, pair.DeviceA, 9600, "None", 8, "One"),
                new CatSerialPortConfig(), new CatSerialPortConfig(), new CatSerialPortConfig(),
            });
            await CatSerialPortIntegrationTests.WaitForAsync(() => svc.Snapshot().Ports[0].Open);
            Assert.True(svc.Snapshot().Ports[0].Open, "port should hot-open after settings change");

            // Disabling it again should close it, also with no restart.
            store.Set(new[] { new CatSerialPortConfig(), new CatSerialPortConfig(), new CatSerialPortConfig(), new CatSerialPortConfig() });
            await CatSerialPortIntegrationTests.WaitForAsync(() => !svc.Snapshot().Ports[0].Open);
            Assert.False(svc.Snapshot().Ports[0].Open, "port should hot-close after settings change");
        }
        finally
        {
            await ((IHostedService)svc).StopAsync(CancellationToken.None);
        }
    }
}
