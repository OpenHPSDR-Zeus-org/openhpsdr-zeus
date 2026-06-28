// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

using Zeus.Contracts;
using Xunit;

namespace Zeus.Contracts.Tests;

public class Ft8DecodeFrameTests
{
    [Fact]
    public void Encode_PrefixesTheFt8DecodeTypeByte()
    {
        var batch = new Ft8DecodeBatchDto(0, 1_700_000_000_000, "FT8",
            new[] { new Ft8DecodeDto(-12f, 0.2f, 1234f, 18, "CQ KB2UKA FN12") });
        byte[] frame = Ft8DecodeFrame.Encode(batch);
        Assert.Equal((byte)MsgType.Ft8Decode, frame[0]);
        Assert.Equal(0x38, frame[0]);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var batch = new Ft8DecodeBatchDto(1, 1_700_000_000_123, "FT4",
            new[]
            {
                new Ft8DecodeDto(-21.5f, -0.3f, 627f, 7, "<...> RY8CAA"),
                new Ft8DecodeDto(3f, 0.1f, 1500.5f, 36, "GJ0KYZ RK9AX MO05"),
            });

        var back = Ft8DecodeFrame.Decode(Ft8DecodeFrame.Encode(batch));

        Assert.Equal(1, back.Receiver);
        Assert.Equal(1_700_000_000_123, back.SlotStartUnixMs);
        Assert.Equal("FT4", back.Protocol);
        Assert.Equal(2, back.Decodes.Count);
        Assert.Equal("GJ0KYZ RK9AX MO05", back.Decodes[1].Text);
        Assert.Equal(1500.5f, back.Decodes[1].FreqHz);
        Assert.Equal(36, back.Decodes[1].Score);
        Assert.Equal(-21.5f, back.Decodes[0].SnrDb);
    }

    [Fact]
    public void Decode_WrongTypeByte_Throws()
    {
        var bad = new byte[] { 0x01, 0x02, 0x03 };
        Assert.Throws<InvalidDataException>(() => Ft8DecodeFrame.Decode(bad));
    }
}
