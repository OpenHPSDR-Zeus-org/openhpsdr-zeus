// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class TxMonitorPreviewTransmitTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-txmon-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    private (RadioService radio, TxService tx) BuildRadioAndTx(bool connected = true)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(loggerFactory, dspStore, paStore);
        if (connected) radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), loggerFactory);
        var tx = new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        return (radio, tx);
    }

    [Fact]
    public void TrySetMox_On_ClearsTxMonitorPreview()
    {
        var (radio, tx) = BuildRadioAndTx();
        radio.SetTxMonitor(new TxMonitorSetRequest(true));

        bool ok = tx.TrySetMox(true, out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.True(tx.IsMoxOn);
        Assert.False(radio.Snapshot().TxMonitorEnabled);
    }

    [Fact]
    public void TrySetTun_On_ClearsTxMonitorPreview()
    {
        var (radio, tx) = BuildRadioAndTx();
        radio.SetTxMonitor(new TxMonitorSetRequest(true));

        bool ok = tx.TrySetTun(true, out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.True(tx.IsTunOn);
        Assert.False(radio.Snapshot().TxMonitorEnabled);
    }

    [Fact]
    public void TrySetTwoTone_On_ClearsTxMonitorPreview()
    {
        var (radio, tx) = BuildRadioAndTx();
        radio.SetTxMonitor(new TxMonitorSetRequest(true));

        bool ok = tx.TrySetTwoTone(new TwoToneSetRequest(true), out var err);

        Assert.True(ok);
        Assert.Null(err);
        Assert.True(tx.IsTwoToneOn);
        Assert.False(radio.Snapshot().TxMonitorEnabled);
    }

    [Fact]
    public void TrySetMox_Rejected_DoesNotClearTxMonitorPreview()
    {
        var (radio, tx) = BuildRadioAndTx(connected: false);
        radio.SetTxMonitor(new TxMonitorSetRequest(true));

        bool ok = tx.TrySetMox(true, out var err);

        Assert.False(ok);
        Assert.Equal("not connected", err);
        Assert.False(tx.IsMoxOn);
        Assert.True(radio.Snapshot().TxMonitorEnabled);
    }
}
