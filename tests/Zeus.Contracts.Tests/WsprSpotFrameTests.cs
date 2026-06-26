// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;
using Xunit;

namespace Zeus.Contracts.Tests;

public class WsprSpotFrameTests
{
    [Fact]
    public void Encode_PrefixesTheWsprSpotTypeByte()
    {
        var batch = new WsprSpotBatchDto(0, 1_700_000_000_000, 14.0956,
            new[] { new WsprSpotDto(-24f, 0.4f, 14.097120f, 0, "KB2UKA FN12 37") });
        byte[] frame = WsprSpotFrame.Encode(batch);
        Assert.Equal((byte)MsgType.WsprSpot, frame[0]);
        Assert.Equal(0x39, frame[0]);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var batch = new WsprSpotBatchDto(0, 1_700_000_000_456, 7.0386,
            new[]
            {
                new WsprSpotDto(-28f, 1.2f, 7.040123f, -1, "G0XYZ IO91 23"),
                new WsprSpotDto(-7f, -0.5f, 7.040088f, 2, "VK7JJ QE37 30"),
            });

        var back = WsprSpotFrame.Decode(WsprSpotFrame.Encode(batch));

        Assert.Equal(0, back.Receiver);
        Assert.Equal(1_700_000_000_456, back.SlotStartUnixMs);
        Assert.Equal(7.0386, back.DialFreqMhz);
        Assert.Equal(2, back.Spots.Count);
        Assert.Equal("VK7JJ QE37 30", back.Spots[1].Message);
        Assert.Equal(7.040088f, back.Spots[1].FreqMhz);
        Assert.Equal(-1, back.Spots[0].DriftHz);
        Assert.Equal(-28f, back.Spots[0].SnrDb);
    }

    [Fact]
    public void Decode_WrongTypeByte_Throws()
    {
        var bad = new byte[] { 0x38, 0x02, 0x03 };
        Assert.Throws<InvalidDataException>(() => WsprSpotFrame.Decode(bad));
    }
}
