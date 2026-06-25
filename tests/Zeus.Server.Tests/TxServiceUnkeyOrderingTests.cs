// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Server;

namespace Zeus.Server.Tests;

// Issue #870: the TX→RX transition (radio TX LED, external PTT line, amplifier
// T/R sequencing — all driven by the wire MOX bit) was being stretched on every
// unkey because the WDSP TX-chain teardown ran BEFORE the wire MOX drop.
// WdspDspEngine.SetMox(false) damps TXA with dmp=1 and WDSP's SetChannelState
// spins a Sleep(1) loop up to its 100 ms timeout (native/wdsp/channel.c:271-276)
// because Zeus stops feeding fexchange2 the instant MOX drops, so the down-slew
// never completes. Sitting that ~100 ms+ block in front of the wire drop delayed
// the radio's T/R on BOTH the on-screen MOX button and hardware PTT, on every
// WDSP board. The fix drops the wire bit first, then tears the chain down.
//
// These tests pin the ordering through the universal keying chokepoint:
//   • MOX-OFF → wire MOX edge (RadioService.MoxChanged) must fire BEFORE the
//     pipeline teardown (DspPipelineService.SetMox(false)).
//   • MOX-ON  → unchanged: the pipeline (TXA up / RXA mute) must run BEFORE the
//     wire key, so the first TX frame never carries stale RX-side IQ.
public sealed class TxServiceUnkeyOrderingTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-tx-unkey-order-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    private (TxService Tx, List<string> Order) BuildConnectedTx()
    {
        var order = new List<string>();
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(loggerFactory, dspStore, paStore);
        radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000);
        // RadioService.SetMox raises MoxChanged after it flips the wire bit, so
        // this records the exact moment the radio leaves/enters TX.
        radio.MoxChanged += on => order.Add($"wire:{on}");
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new RecordingPipeline(order, radio, hub, loggerFactory);
        var tx = new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        return (tx, order);
    }

    [Fact]
    public void TrySetMox_Off_DropsWireBitBeforeWdspTeardown()
    {
        var (tx, order) = BuildConnectedTx();
        Assert.True(tx.TrySetMox(true, out var onErr), onErr);
        order.Clear();

        Assert.True(tx.TrySetMox(false, out var offErr), offErr);

        // Wire MOX must drop first so the radio's T/R, external PTT line and amp
        // sequencing release immediately; the ~100 ms WDSP teardown comes after.
        Assert.Equal(new[] { "wire:False", "dsp:False" }, order);
    }

    [Fact]
    public void TrySetMox_On_RaisesWdspChainBeforeWireKey()
    {
        var (tx, order) = BuildConnectedTx();

        Assert.True(tx.TrySetMox(true, out var onErr), onErr);

        // On the key-down edge the order is unchanged: the WDSP TX chain comes up
        // before the wire MOX bit, so the first TX frame can't carry stale RX IQ.
        Assert.Equal(new[] { "dsp:True", "wire:True" }, order);
    }

    private sealed class RecordingPipeline(
        List<string> order,
        RadioService radio,
        StreamingHub hub,
        ILoggerFactory logs) : DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), logs)
    {
        public override void SetMox(bool on) => order.Add($"dsp:{on}");
    }
}
