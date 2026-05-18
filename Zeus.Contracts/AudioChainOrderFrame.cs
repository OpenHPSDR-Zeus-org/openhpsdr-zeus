// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers;
using System.Text;

namespace Zeus.Contracts;

/// <summary>
/// Audio plugin chain order broadcast. Carries the operator's
/// current chain order as a comma-separated list of plugin IDs
/// (head first = first in chain, processes mic first; tail =
/// last in chain, output goes downstream to WDSP TXA).
///
/// Broadcast by <c>ChainOrderService</c> whenever the order
/// changes — either because the operator drag-dropped a tile in
/// the Audio Suite window, or because a plugin was installed /
/// uninstalled. Other connected clients (LAN-share phone,
/// second browser) update their tile strip on receipt without
/// polling.
///
/// Payload: <c>[type:1][csvUtf8…]</c>. Capped at
/// <see cref="MaxByteLength"/> bytes so clients can stack-allocate
/// a decode buffer. The cap is conservative for the v1 chain
/// (8 plugins × ~50 chars + commas ≈ 410 bytes) — third-party
/// plugins with very long IDs that overflow are dropped on the
/// server side with a log warning, not throw.
/// </summary>
public readonly record struct AudioChainOrderFrame(IReadOnlyList<string> PluginIds)
{
    public const int MaxByteLength = 1024;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var csv = string.Join(",", PluginIds);
        var msgBytes = Encoding.UTF8.GetBytes(csv);
        int totalLen = 1 + msgBytes.Length;
        if (totalLen > MaxByteLength)
            throw new InvalidOperationException(
                $"AudioChainOrderFrame too long: {totalLen} bytes (max {MaxByteLength}). " +
                $"Plugin IDs combined are longer than the contract's per-frame cap.");

        var span = writer.GetSpan(totalLen);
        span[0] = (byte)MsgType.AudioChainOrder;
        msgBytes.CopyTo(span.Slice(1));
        writer.Advance(totalLen);
    }

    public static AudioChainOrderFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 1)
            throw new InvalidDataException(
                $"AudioChainOrderFrame requires ≥1 byte, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.AudioChainOrder)
            throw new InvalidDataException(
                $"expected AudioChainOrder (0x{(byte)MsgType.AudioChainOrder:X2}), got 0x{bytes[0]:X2}");

        var csv = Encoding.UTF8.GetString(bytes.Slice(1));
        // Empty payload encodes "empty chain" — chain order with no
        // plugins, e.g. after the last plugin is uninstalled.
        var ids = csv.Length == 0
            ? Array.Empty<string>()
            : csv.Split(',');
        return new AudioChainOrderFrame(ids);
    }
}
