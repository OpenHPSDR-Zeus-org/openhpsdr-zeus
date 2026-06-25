// SPDX-License-Identifier: GPL-2.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;
using Zeus.Server.Cat;

namespace Zeus.Server.Tests.Cat;

// Dispatch-level tests for the Tier-1 CAT command set, driving the real
// RadioService / TxService through CatCommandHandler with a captured send
// callback. Focus is on correctness AND the safety contract: CAT keys ONLY on
// an explicit TX;, owns its MOX via MoxSource.Cat, and never auto-keys.
public sealed class CatCommandHandlerTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-cat-handler-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    private (CatCommandHandler H, RadioService Radio, TxService Tx, List<string> Out) Build(
        CatOptions? options = null, double latestDbm = -73.0)
    {
        var lf = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(lf, dspStore, paStore);
        radio.MarkProtocol2Connected("127.0.0.1:1024", 48_000);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), lf);
        var tx = new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        var output = new List<string>();
        var handler = new CatCommandHandler(radio, tx, options ?? new CatOptions(), () => latestDbm, output.Add);
        return (handler, radio, tx, output);
    }

    [Fact]
    public void Id_RespondsTs2000()
    {
        var (h, _, _, o) = Build();
        h.Dispatch("ID");
        Assert.Equal(new[] { "ID019;" }, o);
    }

    [Fact]
    public void Ps_RespondsPowerOn()
    {
        var (h, _, _, o) = Build();
        h.Dispatch("PS");
        Assert.Equal(new[] { "PS1;" }, o);
    }

    [Fact]
    public void Fa_GetThenSet()
    {
        var (h, radio, _, o) = Build();
        radio.SetVfo(7_074_000);
        h.Dispatch("FA");
        Assert.Equal(new[] { "FA00007074000;" }, o);

        h.Dispatch("FA00014250000");
        Assert.Equal(14_250_000, radio.Snapshot().VfoHz);
    }

    [Fact]
    public void Md_GetThenSet()
    {
        var (h, radio, _, o) = Build();
        radio.SetMode(RxMode.USB);
        h.Dispatch("MD");
        Assert.Equal(new[] { "MD2;" }, o);

        h.Dispatch("MD3"); // Kenwood 3 = CW (normal) = CWU
        Assert.Equal(RxMode.CWU, radio.Snapshot().Mode);
    }

    [Fact]
    public void If_Is38CharFramedResponse()
    {
        var (h, radio, _, o) = Build();
        radio.SetVfo(7_074_000);
        h.Dispatch("IF");
        Assert.Single(o);
        Assert.StartsWith("IF", o[0]);
        Assert.EndsWith(";", o[0]);
        Assert.Equal(38, o[0].Length); // "IF" + 35 body + ";"
    }

    [Fact]
    public void Tx_Keys_With_CatSource_Then_Rx_Unkeys()
    {
        var (h, _, tx, _) = Build();
        h.Dispatch("TX");
        Assert.True(tx.IsMoxOn);
        Assert.Equal(MoxSource.Cat, tx.MoxOwner);

        h.Dispatch("RX");
        Assert.False(tx.IsMoxOn);
    }

    [Fact]
    public void EnablingAutoInfo_DoesNotKey_NoAutoKeyContract()
    {
        var (h, _, tx, _) = Build();
        Assert.False(h.AutoInfoEnabled);

        h.Dispatch("AI2");
        Assert.True(h.AutoInfoEnabled);
        // Safety: enabling Auto-Information must never key the transmitter.
        Assert.False(tx.IsMoxOn);
        Assert.Null(tx.MoxOwner);
    }

    [Fact]
    public void Ai_Query_ReportsLevel_And_SeedsStateOnEnable()
    {
        var (h, _, _, o) = Build();
        h.Dispatch("AI");
        Assert.Equal(new[] { "AI0;" }, o);

        o.Clear();
        h.Dispatch("AI1"); // enable → seeds an IF frame (SendInitialStateOnConnect default true)
        Assert.Contains(o, s => s.StartsWith("IF"));

        o.Clear();
        h.Dispatch("AI");
        Assert.Equal(new[] { "AI1;" }, o);
    }

    [Fact]
    public void Pc_GetThenSet_AndPowerLimitClamp()
    {
        var (h, radio, _, o) = Build();
        h.Dispatch("PC050");
        Assert.Equal(50, radio.Snapshot().DrivePct);

        o.Clear();
        h.Dispatch("PC");
        Assert.Equal(new[] { "PC050;" }, o);

        // LimitPowerLevels clamps to 50%.
        var (h2, radio2, _, _) = Build(new CatOptions { LimitPowerLevels = true });
        h2.Dispatch("PC080");
        Assert.Equal(50, radio2.Snapshot().DrivePct);
    }

    [Fact]
    public void Sm_ReportsScaledMeter()
    {
        var (h, _, _, o) = Build(latestDbm: -73.0);
        h.Dispatch("SM");
        Assert.Equal(new[] { "SM00012;" }, o); // -73 dBm → 12
    }

    [Fact]
    public void UnknownCommand_ReturnsKenwoodError()
    {
        var (h, _, _, o) = Build();
        h.Dispatch("ZZXYZ");
        Assert.Equal(new[] { "?;" }, o);
    }
}
