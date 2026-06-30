// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

using System.Text;
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

    [Fact]
    public void RoundTrip_PreservesWorkedBeforeAndCountry()
    {
        var batch = new Ft8DecodeBatchDto(0, 1_700_000_000_000, "FT8",
            new[]
            {
                new Ft8DecodeDto(-12f, 0.2f, 1234f, 18, "CQ DL1ABC JO31",
                    WorkedBefore: true, Country: "GER"),
                new Ft8DecodeDto(-5f, 0.0f, 900f, 22, "CQ N0XYZ"), // defaults
            });

        var back = Ft8DecodeFrame.Decode(Ft8DecodeFrame.Encode(batch));

        Assert.True(back.Decodes[0].WorkedBefore);
        Assert.Equal("GER", back.Decodes[0].Country);
        Assert.False(back.Decodes[1].WorkedBefore);
        Assert.Null(back.Decodes[1].Country);
    }

    [Fact]
    public void Encode_EmitsCamelCaseEnrichmentFields()
    {
        var batch = new Ft8DecodeBatchDto(0, 1_700_000_000_000, "FT8",
            new[] { new Ft8DecodeDto(-12f, 0.2f, 1234f, 18, "CQ DL1ABC JO31",
                WorkedBefore: true, Country: "GER") });

        var frame = Ft8DecodeFrame.Encode(batch);
        var json = Encoding.UTF8.GetString(frame, 1, frame.Length - 1);

        Assert.Contains("\"workedBefore\":true", json);
        Assert.Contains("\"country\":\"GER\"", json);
    }

    [Fact]
    public void Decode_OldShapeJsonWithoutEnrichment_DeserializesWithDefaults()
    {
        // A pre-feature server emits decodes with no workedBefore/country keys.
        const string legacyJson =
            "{\"receiver\":0,\"slotStartUnixMs\":1700000000000,\"protocol\":\"FT8\"," +
            "\"decodes\":[{\"snrDb\":-12,\"dtSec\":0.2,\"freqHz\":1234,\"score\":18," +
            "\"text\":\"CQ K1ABC FN42\"}]}";
        var payload = Encoding.UTF8.GetBytes(legacyJson);
        var frame = new byte[1 + payload.Length];
        frame[0] = (byte)MsgType.Ft8Decode;
        payload.CopyTo(frame, 1);

        var back = Ft8DecodeFrame.Decode(frame);

        Assert.Single(back.Decodes);
        Assert.Equal("CQ K1ABC FN42", back.Decodes[0].Text);
        Assert.False(back.Decodes[0].WorkedBefore);
        Assert.Null(back.Decodes[0].Country);
    }
}
