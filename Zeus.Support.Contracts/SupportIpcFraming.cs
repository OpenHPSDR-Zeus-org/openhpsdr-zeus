// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Text.Json;

namespace Zeus.Support.Contracts;

/// <summary>
/// Length-prefixed JSON framing for the support IPC channel, transport-agnostic
/// so it works over any duplex <see cref="Stream"/> (named pipe today, a loopback
/// socket if that ever changes) and is unit-testable without a live pipe.
///
/// Wire shape per message: a 4-byte little-endian unsigned length, then exactly
/// that many bytes of UTF-8 JSON (a <see cref="SupportIpcMessage"/> serialised via
/// <see cref="SupportIpcJsonContext"/>). A length of 0 or one exceeding
/// <see cref="SupportIpc.MaxFrameBytes"/> is a framing error: the channel is
/// desynchronised or the peer is hostile, so the caller must drop the connection
/// rather than trust the stream.
/// </summary>
public static class SupportIpcFraming
{
    /// <summary>Serialise and write one framed message. Concurrent writers must serialise externally.</summary>
    public static async Task WriteAsync(Stream stream, SupportIpcMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(message);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            message, typeof(SupportIpcMessage), SupportIpcJsonContext.Default);

        if (payload.Length == 0 || payload.Length > SupportIpc.MaxFrameBytes)
            throw new InvalidOperationException(
                $"Support IPC frame of {payload.Length} bytes is outside the 1..{SupportIpc.MaxFrameBytes} range.");

        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read one framed message. Returns null on a clean end-of-stream (peer closed
    /// the pipe between frames). Throws <see cref="InvalidDataException"/> on a
    /// truncated or out-of-range frame so the caller tears the channel down.
    /// </summary>
    public static async Task<SupportIpcMessage?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[4];
        if (!await TryReadExactAsync(stream, header, ct).ConfigureAwait(false))
            return null; // clean EOF at a frame boundary

        uint len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (len == 0 || len > SupportIpc.MaxFrameBytes)
            throw new InvalidDataException(
                $"Support IPC frame length {len} is outside the 1..{SupportIpc.MaxFrameBytes} range.");

        byte[] payload = new byte[len];
        if (!await TryReadExactAsync(stream, payload, ct).ConfigureAwait(false))
            throw new InvalidDataException("Support IPC stream ended mid-frame (truncated payload).");

        var message = JsonSerializer.Deserialize(
            payload, typeof(SupportIpcMessage), SupportIpcJsonContext.Default) as SupportIpcMessage;
        return message ?? throw new InvalidDataException("Support IPC frame did not deserialise to a known message.");
    }

    // Fill the whole buffer or fail. Returns false only when the stream ends
    // before any byte of the buffer is read (clean EOF); a partial read followed
    // by EOF is a truncation and surfaces to the caller as such.
    private static async Task<bool> TryReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0) return false;       // EOF exactly at a frame boundary
                throw new InvalidDataException("Support IPC stream ended mid-frame.");
            }
            offset += read;
        }
        return true;
    }
}
