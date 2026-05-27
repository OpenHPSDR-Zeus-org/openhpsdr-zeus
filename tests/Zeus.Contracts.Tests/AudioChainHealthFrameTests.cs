// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using System.Buffers;
using Zeus.Contracts;
using Xunit;

namespace Zeus.Contracts.Tests;

public class AudioChainHealthFrameTests
{
    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var frame = new AudioChainHealthFrame(
            Mode: RxMode.USB,
            Verdicts: new[]
            {
                AudioChainVerdict.Ok(AudioChainStageId.Mic),
                new AudioChainVerdict(
                    AudioChainStageId.Cfc,
                    AudioChainSeverity.Warn,
                    AudioChainVerdictFlags.HasApply,
                    "CFC pumping — gr 11 dB",
                    "Apply · drive 0 → −3"),
                new AudioChainVerdict(
                    AudioChainStageId.Pa,
                    AudioChainSeverity.Error,
                    AudioChainVerdictFlags.ImmediateAction,
                    "STOP TX — SWR 4.8:1, reflected 38W. Unkey now.",
                    string.Empty),
                AudioChainVerdict.Info(AudioChainStageId.Leveler, "bypassed in FM"),
            });

        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);

        var decoded = AudioChainHealthFrame.Deserialize(writer.WrittenSpan);

        Assert.Equal(RxMode.USB, decoded.Mode);
        Assert.Equal(4, decoded.Verdicts.Count);
        Assert.Equal(AudioChainStageId.Mic, decoded.Verdicts[0].StageId);
        Assert.Equal(AudioChainSeverity.Ok, decoded.Verdicts[0].Severity);
        Assert.Equal(AudioChainStageId.Cfc, decoded.Verdicts[1].StageId);
        Assert.Equal("CFC pumping — gr 11 dB", decoded.Verdicts[1].Message);
        Assert.Equal("Apply · drive 0 → −3", decoded.Verdicts[1].ApplyLabel);
        Assert.True(decoded.Verdicts[1].Flags.HasFlag(AudioChainVerdictFlags.HasApply));
        Assert.True(decoded.Verdicts[2].Flags.HasFlag(AudioChainVerdictFlags.ImmediateAction));
        Assert.Equal(AudioChainSeverity.Info, decoded.Verdicts[3].Severity);
    }

    [Fact]
    public void Serialize_MsgTypePrefixIs0x32()
    {
        var frame = new AudioChainHealthFrame(RxMode.USB, Array.Empty<AudioChainVerdict>());
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        Assert.Equal(0x32, writer.WrittenSpan[0]);
        Assert.Equal((byte)MsgType.AudioChainHealth, writer.WrittenSpan[0]);
    }

    [Fact]
    public void EmptyVerdictList_RoundTrips()
    {
        var frame = new AudioChainHealthFrame(RxMode.LSB, Array.Empty<AudioChainVerdict>());
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);

        Assert.Equal(AudioChainHealthFrame.HeaderByteLength, writer.WrittenCount);

        var decoded = AudioChainHealthFrame.Deserialize(writer.WrittenSpan);
        Assert.Equal(RxMode.LSB, decoded.Mode);
        Assert.Empty(decoded.Verdicts);
    }

    [Fact]
    public void Deserialize_RejectsWrongMsgType()
    {
        var bogus = new byte[] { (byte)MsgType.PaTemp, 0, 0 };
        Assert.Throws<InvalidDataException>(() => AudioChainHealthFrame.Deserialize(bogus));
    }

    [Fact]
    public void Deserialize_RejectsTruncatedHeader()
    {
        var buf = new byte[] { (byte)MsgType.AudioChainHealth, 0 };
        Assert.Throws<InvalidDataException>(() => AudioChainHealthFrame.Deserialize(buf));
    }

    [Fact]
    public void Deserialize_RejectsTruncatedVerdictPayload()
    {
        // Claim 1 verdict with msgLen=5 but only provide 2 bytes after the
        // per-verdict header.
        var buf = new byte[]
        {
            (byte)MsgType.AudioChainHealth,
            (byte)RxMode.USB,
            1,                       // verdictCount
            (byte)AudioChainStageId.Mic,
            (byte)AudioChainSeverity.Warn,
            (byte)AudioChainVerdictFlags.None,
            5,                       // messageLen — but only 2 follow
            0,                       // applyLabelLen
            (byte)'h', (byte)'i',
        };
        Assert.Throws<InvalidDataException>(() => AudioChainHealthFrame.Deserialize(buf));
    }

    [Fact]
    public void Serialize_ThrowsOnTooManyVerdicts()
    {
        var verdicts = new AudioChainVerdict[AudioChainHealthFrame.MaxVerdicts + 1];
        for (int i = 0; i < verdicts.Length; i++)
            verdicts[i] = AudioChainVerdict.Ok(AudioChainStageId.Mic);
        var frame = new AudioChainHealthFrame(RxMode.USB, verdicts);
        var writer = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(() => frame.Serialize(writer));
    }

    [Fact]
    public void Utf8Message_RoundTrips()
    {
        // Make sure dB minus signs and other non-ASCII glyphs survive.
        var frame = new AudioChainHealthFrame(
            RxMode.USB,
            new[]
            {
                new AudioChainVerdict(
                    AudioChainStageId.Alc,
                    AudioChainSeverity.Error,
                    AudioChainVerdictFlags.HasApply,
                    "Clipping — ALC hitting +2 dBFS · drive ≪",
                    "Apply · drive 95 → 78"),
            });
        var writer = new ArrayBufferWriter<byte>();
        frame.Serialize(writer);
        var decoded = AudioChainHealthFrame.Deserialize(writer.WrittenSpan);
        Assert.Equal("Clipping — ALC hitting +2 dBFS · drive ≪", decoded.Verdicts[0].Message);
        Assert.Equal("Apply · drive 95 → 78", decoded.Verdicts[0].ApplyLabel);
    }
}
