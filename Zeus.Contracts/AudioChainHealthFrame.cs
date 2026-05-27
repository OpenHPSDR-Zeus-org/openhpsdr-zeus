// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Zeus.Contracts;

/// <summary>
/// Audio Chain Monitor stage identifier. The factory widget renders nine
/// tiles in pipeline order; each verdict on the wire keys back to one of
/// these. Stable wire value — renaming or renumbering is a wire break.
/// </summary>
public enum AudioChainStageId : byte
{
    Mic = 0,
    Eq = 1,
    Leveler = 2,
    Cfc = 3,
    Comp = 4,
    Alc = 5,
    Out = 6,
    Wire = 7,
    Pa = 8,
}

/// <summary>
/// Four-state severity per ADR-0002 / CONTEXT.md. Wire values frozen.
/// Immediate-action ("stop tx now") is a flag on Error, not a fifth tier.
/// </summary>
public enum AudioChainSeverity : byte
{
    Ok = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

[Flags]
public enum AudioChainVerdictFlags : byte
{
    None = 0,
    ImmediateAction = 1 << 0,
    HasApply = 1 << 1,
}

/// <summary>
/// One stage's verdict at one tick. The factory widget joins this against
/// the raw meter readings (TxMetersV2 / PaTemp / RxMetersV2) in the
/// frontend by <see cref="StageId"/>.
/// </summary>
public readonly record struct AudioChainVerdict(
    AudioChainStageId StageId,
    AudioChainSeverity Severity,
    AudioChainVerdictFlags Flags,
    string Message,
    string ApplyLabel)
{
    public static AudioChainVerdict Ok(AudioChainStageId id) =>
        new(id, AudioChainSeverity.Ok, AudioChainVerdictFlags.None, string.Empty, string.Empty);

    public static AudioChainVerdict Info(AudioChainStageId id, string message) =>
        new(id, AudioChainSeverity.Info, AudioChainVerdictFlags.None, message, string.Empty);
}

/// <summary>
/// AudioChainHealth wire frame (MsgType 0x32). Broadcast at ~2 Hz —
/// always-on, even outside MOX. Carries verdicts only; raw stage numbers
/// stay on their native frames (TxMetersV2 0x16, PaTemp 0x17, RxMetersV2
/// 0x19). Per ADR-0002. Variable length:
///
/// <code>
/// [type:1=0x32]
/// [mode:u8]                 // RxMode of the operator's current channel
/// [verdictCount:u8]         // typically 9, the nine factory-widget tiles
/// for each verdict:
///   [stageId:u8]            // AudioChainStageId
///   [severity:u8]           // AudioChainSeverity
///   [flags:u8]              // AudioChainVerdictFlags
///   [messageLen:u8]         // 0..255 UTF-8 bytes
///   [applyLabelLen:u8]      // 0..255 UTF-8 bytes
///   [message:utf8 messageLen]
///   [applyLabel:utf8 applyLabelLen]
/// </code>
///
/// Apply target values are NOT on the wire — the service keeps the
/// current target per StageId in-process and the apply endpoint
/// (<c>POST /api/audio-chain/apply { stageId }</c>) consults it. The
/// frontend only needs the human-readable label ("Apply · 22 → 28 dB")
/// to render the button.
/// </summary>
public readonly record struct AudioChainHealthFrame(
    RxMode Mode,
    IReadOnlyList<AudioChainVerdict> Verdicts)
{
    public const int HeaderByteLength = 3;
    public const int PerVerdictHeaderBytes = 5;
    public const int MaxVerdicts = 32;
    public const int MaxMessageBytes = 255;
    public const int MaxApplyLabelBytes = 255;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var verdicts = Verdicts ?? Array.Empty<AudioChainVerdict>();
        if (verdicts.Count > MaxVerdicts)
            throw new ArgumentException(
                $"AudioChainHealthFrame supports up to {MaxVerdicts} verdicts; got {verdicts.Count}",
                nameof(Verdicts));

        // Pre-encode every message/applyLabel so we can size the buffer in one
        // GetSpan and avoid a second pass over the verdicts.
        Span<int> messageByteCounts = verdicts.Count <= 32
            ? stackalloc int[verdicts.Count]
            : new int[verdicts.Count];
        Span<int> applyByteCounts = verdicts.Count <= 32
            ? stackalloc int[verdicts.Count]
            : new int[verdicts.Count];
        byte[][] messageBytes = new byte[verdicts.Count][];
        byte[][] applyBytes = new byte[verdicts.Count][];

        int payloadBytes = HeaderByteLength + verdicts.Count * PerVerdictHeaderBytes;
        for (int i = 0; i < verdicts.Count; i++)
        {
            var v = verdicts[i];
            var msg = Encoding.UTF8.GetBytes(v.Message ?? string.Empty);
            var apl = Encoding.UTF8.GetBytes(v.ApplyLabel ?? string.Empty);
            messageByteCounts[i] = Math.Min(msg.Length, MaxMessageBytes);
            applyByteCounts[i] = Math.Min(apl.Length, MaxApplyLabelBytes);
            messageBytes[i] = msg;
            applyBytes[i] = apl;
            payloadBytes += messageByteCounts[i] + applyByteCounts[i];
        }

        var span = writer.GetSpan(payloadBytes);
        span[0] = (byte)MsgType.AudioChainHealth;
        span[1] = (byte)Mode;
        span[2] = (byte)verdicts.Count;
        int offset = HeaderByteLength;
        for (int i = 0; i < verdicts.Count; i++)
        {
            var v = verdicts[i];
            span[offset + 0] = (byte)v.StageId;
            span[offset + 1] = (byte)v.Severity;
            span[offset + 2] = (byte)v.Flags;
            span[offset + 3] = (byte)messageByteCounts[i];
            span[offset + 4] = (byte)applyByteCounts[i];
            offset += PerVerdictHeaderBytes;
            if (messageByteCounts[i] > 0)
            {
                messageBytes[i].AsSpan(0, messageByteCounts[i]).CopyTo(span.Slice(offset, messageByteCounts[i]));
                offset += messageByteCounts[i];
            }
            if (applyByteCounts[i] > 0)
            {
                applyBytes[i].AsSpan(0, applyByteCounts[i]).CopyTo(span.Slice(offset, applyByteCounts[i]));
                offset += applyByteCounts[i];
            }
        }
        writer.Advance(payloadBytes);
    }

    public static AudioChainHealthFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderByteLength)
            throw new InvalidDataException(
                $"AudioChainHealthFrame requires ≥{HeaderByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.AudioChainHealth)
            throw new InvalidDataException(
                $"expected AudioChainHealth (0x{(byte)MsgType.AudioChainHealth:X2}), got 0x{bytes[0]:X2}");
        var mode = (RxMode)bytes[1];
        int count = bytes[2];
        if (count > MaxVerdicts)
            throw new InvalidDataException(
                $"AudioChainHealthFrame verdictCount {count} exceeds max {MaxVerdicts}");
        var verdicts = new AudioChainVerdict[count];
        int offset = HeaderByteLength;
        for (int i = 0; i < count; i++)
        {
            if (offset + PerVerdictHeaderBytes > bytes.Length)
                throw new InvalidDataException(
                    $"AudioChainHealthFrame truncated at verdict {i} header");
            var stageId = (AudioChainStageId)bytes[offset + 0];
            var severity = (AudioChainSeverity)bytes[offset + 1];
            var flags = (AudioChainVerdictFlags)bytes[offset + 2];
            int msgLen = bytes[offset + 3];
            int aplLen = bytes[offset + 4];
            offset += PerVerdictHeaderBytes;
            if (offset + msgLen + aplLen > bytes.Length)
                throw new InvalidDataException(
                    $"AudioChainHealthFrame truncated at verdict {i} payload");
            string message = msgLen == 0
                ? string.Empty
                : Encoding.UTF8.GetString(bytes.Slice(offset, msgLen));
            offset += msgLen;
            string applyLabel = aplLen == 0
                ? string.Empty
                : Encoding.UTF8.GetString(bytes.Slice(offset, aplLen));
            offset += aplLen;
            verdicts[i] = new AudioChainVerdict(stageId, severity, flags, message, applyLabel);
        }
        return new AudioChainHealthFrame(mode, verdicts);
    }
}
