// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers;
using System.Text;

namespace Zeus.Contracts;

/// <summary>
/// Receive-side Audio Suite chain order broadcast. Carries the operator's
/// current RX insert chain order as a comma-separated list of plugin IDs.
///
/// Payload: <c>[type:1][csvUtf8…]</c>. This intentionally mirrors
/// <see cref="AudioChainOrderFrame"/> while using a distinct message type so
/// clients never confuse TX and RX rack state.
/// </summary>
public readonly record struct RxAudioChainOrderFrame(IReadOnlyList<string> PluginIds)
{
    public const int MaxByteLength = AudioChainOrderFrame.MaxByteLength;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var csv = string.Join(",", PluginIds);
        var msgBytes = Encoding.UTF8.GetBytes(csv);
        int totalLen = 1 + msgBytes.Length;
        if (totalLen > MaxByteLength)
            throw new InvalidOperationException(
                $"RxAudioChainOrderFrame too long: {totalLen} bytes (max {MaxByteLength}). " +
                $"Plugin IDs combined are longer than the contract's per-frame cap.");

        var span = writer.GetSpan(totalLen);
        span[0] = (byte)MsgType.RxAudioChainOrder;
        msgBytes.CopyTo(span.Slice(1));
        writer.Advance(totalLen);
    }

    public static RxAudioChainOrderFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 1)
            throw new InvalidDataException(
                $"RxAudioChainOrderFrame requires >=1 byte, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.RxAudioChainOrder)
            throw new InvalidDataException(
                $"expected RxAudioChainOrder (0x{(byte)MsgType.RxAudioChainOrder:X2}), got 0x{bytes[0]:X2}");

        var csv = Encoding.UTF8.GetString(bytes.Slice(1));
        var ids = csv.Length == 0
            ? Array.Empty<string>()
            : csv.Split(',');
        return new RxAudioChainOrderFrame(ids);
    }
}
