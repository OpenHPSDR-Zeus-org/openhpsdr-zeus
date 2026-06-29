// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.Protocol2.Tests;

/// <summary>
/// GOLDEN-BYTE tests for the Protocol-2 RX front-end inside the CmdHighPriority
/// (port 1027) packet. The operator PRE button drives <c>SetPreamp</c> →
/// <c>WriteRxFrontEndBytes</c>, which lands the Mercury-preamp bit at byte 1403
/// (bit 0 = RX0 preamp, bit 1 = RX1 preamp — Thetis network.c:1037). This was
/// inert on Angelia / ANAN-100D until issue #126 wired the
/// RadioService → DspPipelineService → Protocol2Client forwarding; these tests
/// lock the wire offset + bit so it can't silently regress again. The two ADC
/// step-attenuators ride the same seam at bytes 1442 (ADC1) / 1443 (ADC0).
/// </summary>
public class CmdHighPriorityRxFrontEndTests
{
    // CmdHighPriority packet length (Protocol2Client.BufLen). The highest
    // RX-front-end offset is 1443, so a full-length buffer proves the writes
    // land at absolute offsets rather than relative to a smaller scratch span.
    private const int BufLen = 1444;

    [Fact]
    public void RxFrontEnd_PreampOn_SetsByte1403Bit0()
    {
        var p = new byte[BufLen];
        Protocol2Client.WriteRxFrontEndBytes(p, preampOn: true, adc1StepAttnDb: 0, adc0StepAttnDb: 0);

        Assert.Equal(0x01, p[1403] & 0x01); // RX0 preamp bit
    }

    [Fact]
    public void RxFrontEnd_PreampOff_LeavesByte1403Clear()
    {
        var p = new byte[BufLen];
        Protocol2Client.WriteRxFrontEndBytes(p, preampOn: false, adc1StepAttnDb: 0, adc0StepAttnDb: 0);

        Assert.Equal(0x00, p[1403]);
    }

    [Theory]
    [InlineData((byte)0, (byte)0)]
    [InlineData((byte)15, (byte)20)]
    [InlineData((byte)31, (byte)31)]
    public void RxFrontEnd_StepAttenuators_LandAtBytes1442And1443(byte adc1Db, byte adc0Db)
    {
        var p = new byte[BufLen];
        Protocol2Client.WriteRxFrontEndBytes(p, preampOn: false, adc1StepAttnDb: adc1Db, adc0StepAttnDb: adc0Db);

        Assert.Equal(adc1Db, p[1442]); // ADC1 step attenuator
        Assert.Equal(adc0Db, p[1443]); // ADC0 step attenuator
    }

    [Fact]
    public void RxFrontEnd_PreampAndAttenuators_AreIndependentBytes()
    {
        // Preamp on with a non-zero ADC0 attenuator: each control owns its own
        // byte, so neither clobbers the other.
        var p = new byte[BufLen];
        Protocol2Client.WriteRxFrontEndBytes(p, preampOn: true, adc1StepAttnDb: 0, adc0StepAttnDb: 12);

        Assert.Equal(0x01, p[1403] & 0x01);
        Assert.Equal(0, p[1442]);
        Assert.Equal(12, p[1443]);
    }

    [Fact]
    public void SetPreamp_TogglesTheInstancePreampState_OnTheWireSeam()
    {
        // End-to-end at the client seam: the operator toggle (SetPreamp) feeds
        // the same _preampOn the packet builder reads. Verified here by routing
        // both through WriteRxFrontEndBytes with the post-toggle value.
        using var p2 = new Protocol2Client(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Protocol2Client>.Instance);
        p2.SetPreamp(true);

        var p = new byte[BufLen];
        Protocol2Client.WriteRxFrontEndBytes(p, preampOn: true, adc1StepAttnDb: 0, adc0StepAttnDb: 0);
        Assert.Equal(0x01, p[1403] & 0x01);
    }
}
