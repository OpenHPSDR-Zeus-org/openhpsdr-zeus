// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;

namespace Zeus.Protocol2.Tests;

public class RfFilterMatrixTests
{
    [Fact]
    public void Runtime_DefaultDisabled_Is_ByteIdentical()
    {
        var rf = new RfFilterRuntimeSettings(
            CustomMatrixEnabled: false,
            RxBypassAll: false,
            RxBypassOnTx: false,
            RxBypassOnPureSignal: false,
            Anan7000RxFilters: Array.Empty<RfFilterRangeDto>(),
            ClassicAlexRxFilters: Array.Empty<RfFilterRangeDto>(),
            TxFilters: Array.Empty<RfFilterRangeDto>());

        uint legacy = Protocol2Client.ComputeAlexWord(
            rxFreqHz: 7_100_000, txFreqHz: 14_200_000, txAnt: 1,
            board: HpsdrBoardKind.OrionMkII);
        uint withRuntime = Protocol2Client.ComputeAlexWord(
            rxFreqHz: 7_100_000, txFreqHz: 14_200_000, txAnt: 1,
            board: HpsdrBoardKind.OrionMkII,
            rfFilters: rf);

        Assert.Equal(legacy, withRuntime);
    }

    [Fact]
    public void Custom_Anan7000_RxRange_Selects_Configured_Bpf()
    {
        var rf = Runtime(
            ananRx: new[] { new RfFilterRangeDto("80_60", "80 / 60 m", 0, 65_000_000) });

        uint word = Protocol2Client.ComputeAlexWord(
            rxFreqHz: 7_100_000, txFreqHz: 7_100_000, txAnt: 1,
            board: HpsdrBoardKind.OrionMkII,
            rfFilters: rf);

        Assert.Equal(Protocol2Client.BpfBitsAnan7000(3_500_000), word & 0xFFFFu);
    }

    [Fact]
    public void RxBypassOnTx_Forces_Rx_Bpf_Bypass_While_Keyed()
    {
        var rf = new RfFilterRuntimeSettings(
            CustomMatrixEnabled: false,
            RxBypassAll: false,
            RxBypassOnTx: true,
            RxBypassOnPureSignal: false,
            Anan7000RxFilters: Array.Empty<RfFilterRangeDto>(),
            ClassicAlexRxFilters: Array.Empty<RfFilterRangeDto>(),
            TxFilters: Array.Empty<RfFilterRangeDto>());

        uint idle = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 14_200_000,
            moxOn: false,
            psEnabled: false,
            psExternal: false,
            rfFilters: rf);
        uint keyed = Protocol2Client.ComposeAlex0ForTest(
            rxFreqHz: 14_200_000,
            moxOn: true,
            psEnabled: false,
            psExternal: false,
            rfFilters: rf);

        Assert.Equal(Protocol2Client.BpfBitsAnan7000(14_200_000), idle & 0xFFFFu);
        Assert.Equal(Protocol2Client.BpfBitsAnan7000(1_000_000), keyed & 0xFFFFu);
    }

    [Fact]
    public void Custom_TxRange_Selects_Configured_Lpf()
    {
        var rf = Runtime(
            tx: new[] { new RfFilterRangeDto("80", "80 m LPF", 0, 65_000_000) });

        uint word = Protocol2Client.ComputeAlexWord(
            rxFreqHz: 14_200_000, txFreqHz: 14_200_000, txAnt: 1,
            board: HpsdrBoardKind.OrionMkII,
            rfFilters: rf);
        uint expected = Protocol2Client.ComputeAlexWord(
            rxFreqHz: 14_200_000, txFreqHz: 3_500_000, txAnt: 1,
            board: HpsdrBoardKind.OrionMkII);

        Assert.Equal(expected, word);
    }

    private static RfFilterRuntimeSettings Runtime(
        IReadOnlyList<RfFilterRangeDto>? ananRx = null,
        IReadOnlyList<RfFilterRangeDto>? classicRx = null,
        IReadOnlyList<RfFilterRangeDto>? tx = null) => new(
            CustomMatrixEnabled: true,
            RxBypassAll: false,
            RxBypassOnTx: false,
            RxBypassOnPureSignal: false,
            Anan7000RxFilters: ananRx ?? Array.Empty<RfFilterRangeDto>(),
            ClassicAlexRxFilters: classicRx ?? Array.Empty<RfFilterRangeDto>(),
            TxFilters: tx ?? Array.Empty<RfFilterRangeDto>());
}
