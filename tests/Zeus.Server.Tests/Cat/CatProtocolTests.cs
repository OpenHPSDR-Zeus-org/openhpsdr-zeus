// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Contracts;
using Zeus.Server.Cat;

namespace Zeus.Server.Tests.Cat;

// Pure-protocol tests for the Kenwood TS-2000 CAT layer. The IF body layout is
// the highest parity risk (Hamlib/WSJT-X silently fail rig-detect on a wrong
// field offset), so it is asserted byte-for-byte against the Thetis
// CATCommands.IF() field widths.
public sealed class CatProtocolTests
{
    [Theory]
    [InlineData(7074000L, "00007074000")]
    [InlineData(0L, "00000000000")]
    [InlineData(146520000L, "00146520000")]
    public void FormatFreq_Is11DigitsZeroPadded(long hz, string expected)
        => Assert.Equal(expected, CatProtocol.FormatFreq(hz));

    [Theory]
    [InlineData(RxMode.LSB, "1")]
    [InlineData(RxMode.USB, "2")]
    [InlineData(RxMode.CWU, "3")]  // Kenwood 3 = CW (normal) = CWU — per Thetis/Hamlib
    [InlineData(RxMode.FM, "4")]
    [InlineData(RxMode.AM, "5")]
    [InlineData(RxMode.DIGL, "6")]
    [InlineData(RxMode.CWL, "7")]  // Kenwood 7 = CW-R (reverse) = CWL
    [InlineData(RxMode.DIGU, "9")]
    public void ModeDigit_ParseMode_RoundTrip(RxMode mode, string digit)
    {
        Assert.Equal(digit, CatProtocol.ModeDigit(mode));
        Assert.Equal(mode, CatProtocol.ParseMode(digit));
    }

    [Theory]
    [InlineData(RxMode.SAM, "5")]   // no Kenwood equivalent → AM
    [InlineData(RxMode.DSB, "2")]   // → USB
    [InlineData(RxMode.FreeDv, "2")] // runs as USB at WDSP
    public void ModeDigit_ZeusOnlyModes_FallBack(RxMode mode, string digit)
        => Assert.Equal(digit, CatProtocol.ModeDigit(mode));

    [Theory]
    [InlineData("8")]
    [InlineData("0")]
    [InlineData("x")]
    public void ParseMode_Unmapped_IsNull(string digit)
        => Assert.Null(CatProtocol.ParseMode(digit));

    [Fact]
    public void BuildIfBody_Is35Chars_WithExactFieldLayout()
    {
        // freq(11) step(4) incr(6) rit(1) xit(1) mem(3) tx(1) mode(1) frft(1) scan(1) split(1) bal(4)
        string body = CatProtocol.BuildIfBody(7074000, RxMode.USB, mox: false, split: false);
        Assert.Equal(35, body.Length);
        Assert.Equal("00007074000", body[0..11]);   // P1 freq
        Assert.Equal("0000", body[11..15]);          // P2 step
        Assert.Equal("+00000", body[15..21]);        // P3 RIT/XIT value
        Assert.Equal('0', body[21]);                 // P4 RIT
        Assert.Equal('0', body[22]);                 // P5 XIT
        Assert.Equal("000", body[23..26]);           // P6 memory bank
        Assert.Equal('0', body[26]);                 // P7 TX/RX (RX)
        Assert.Equal('2', body[27]);                 // P8 mode (USB)
        Assert.Equal('0', body[28]);                 // P9 FR/FT
        Assert.Equal('0', body[29]);                 // P10 scan
        Assert.Equal('0', body[30]);                 // P11 split
        Assert.Equal("0000", body[31..35]);          // P12 balance
    }

    [Fact]
    public void BuildIfBody_MoxAndSplit_SetTheirBits()
    {
        string body = CatProtocol.BuildIfBody(14250000, RxMode.CWU, mox: true, split: true);
        Assert.Equal(35, body.Length);
        Assert.Equal('1', body[26]); // TX
        Assert.Equal('3', body[27]); // CWU → Kenwood 3 (CW normal)
        Assert.Equal('1', body[30]); // split
    }

    [Fact]
    public void Response_WrapsWithTerminator()
    {
        Assert.Equal("FA00007074000;", CatProtocol.Response("FA", "00007074000"));
        Assert.Equal("ID019;", CatProtocol.Response("ID", "019"));
        Assert.Equal("?;", CatProtocol.Error);
    }

    [Fact]
    public void ExtractCommands_BatchedAndSplit()
    {
        var (cmds, rem) = CatProtocol.ExtractCommands("FA;MD;");
        Assert.Equal(new[] { "FA", "MD" }, cmds);
        Assert.Equal("", rem);

        (cmds, rem) = CatProtocol.ExtractCommands("FA00014250000");
        Assert.Empty(cmds);
        Assert.Equal("FA00014250000", rem); // incomplete — held for next read

        (cmds, rem) = CatProtocol.ExtractCommands("ID;FA0001");
        Assert.Equal(new[] { "ID" }, cmds);
        Assert.Equal("FA0001", rem);
    }

    [Theory]
    [InlineData("FA00007074000", "FA", "00007074000")]
    [InlineData("ID", "ID", "")]
    [InlineData("MD2", "MD", "2")]
    public void CommandId_And_Args(string token, string id, string args)
    {
        Assert.Equal(id, CatProtocol.CommandId(token));
        Assert.Equal(args, CatProtocol.Args(token));
    }

    [Theory]
    [InlineData(-200.0, 0)]
    [InlineData(-130.0, 0)]
    [InlineData(-73.0, 12)]
    [InlineData(0.0, 30)]
    [InlineData(100.0, 30)]
    public void SMeter_ClampsToKenwoodRange(double dbm, int expected)
        => Assert.Equal(expected, CatProtocol.SMeter(dbm));

    [Fact]
    public void SMeterField_Is4Digits()
        => Assert.Equal("0012", CatProtocol.SMeterField(-73.0));
}
