// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers;
using Zeus.Contracts;

namespace Zeus.Contracts.Tests;

public class AudioChainOrderFrameTests
{
    [Fact]
    public void RoundTrip_PreservesOrderAndIds()
    {
        var input = new AudioChainOrderFrame(new[]
        {
            "com.openhpsdr.zeus.samples.gate",
            "com.openhpsdr.zeus.samples.eq",
            "com.openhpsdr.zeus.samples.compressor",
        });

        var writer = new ArrayBufferWriter<byte>(AudioChainOrderFrame.MaxByteLength);
        input.Serialize(writer);

        var decoded = AudioChainOrderFrame.Deserialize(writer.WrittenSpan);
        Assert.Equal(input.PluginIds.Count, decoded.PluginIds.Count);
        for (int i = 0; i < input.PluginIds.Count; i++)
            Assert.Equal(input.PluginIds[i], decoded.PluginIds[i]);
    }

    [Fact]
    public void Empty_Chain_RoundTrips_Cleanly()
    {
        // "No plugins installed" — an empty chain order is valid
        // and must decode back to an empty list, not a list with
        // one empty string.
        var input = new AudioChainOrderFrame(Array.Empty<string>());

        var writer = new ArrayBufferWriter<byte>(AudioChainOrderFrame.MaxByteLength);
        input.Serialize(writer);

        var decoded = AudioChainOrderFrame.Deserialize(writer.WrittenSpan);
        Assert.Empty(decoded.PluginIds);
    }

    [Fact]
    public void Serialize_StartsWithMsgTypeByte()
    {
        var frame = new AudioChainOrderFrame(new[] { "x" });
        var writer = new ArrayBufferWriter<byte>(8);
        frame.Serialize(writer);

        // The frame multiplexer in ws-client.ts peeks byte 0 to
        // dispatch — verify our serialiser puts MsgType there.
        Assert.Equal((byte)MsgType.AudioChainOrder, writer.WrittenSpan[0]);
    }

    [Fact]
    public void Deserialize_WrongTypeByte_Throws()
    {
        // Heap array (not stackalloc) — can't capture a ref struct
        // (Span<byte>) inside a lambda passed to Assert.Throws.
        var bad = new byte[] { 0xFF, (byte)'a', (byte)'b' };
        Assert.Throws<InvalidDataException>(() => AudioChainOrderFrame.Deserialize(bad));
    }

    [Fact]
    public void Serialize_OverMaxLength_Throws_NotSilent()
    {
        // Concoct an absurdly long ID to overflow the cap.
        var longId = new string('x', AudioChainOrderFrame.MaxByteLength);
        var frame = new AudioChainOrderFrame(new[] { longId });
        var writer = new ArrayBufferWriter<byte>(AudioChainOrderFrame.MaxByteLength * 2);

        Assert.Throws<InvalidOperationException>(() => frame.Serialize(writer));
    }
}
