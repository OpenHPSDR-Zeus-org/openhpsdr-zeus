// SPDX-License-Identifier: GPL-2.0-or-later
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Buffers;

namespace Zeus.Contracts;

/// <summary>
/// Audio Suite master-bypass broadcast. Carries a single boolean — the
/// operator's master-bypass state for the whole plugin chain.
///
/// <para>Broadcast by <c>AudioChainMasterBypassService</c> on every
/// operator toggle so all connected clients (LAN-share phone, second
/// browser) stay in sync without polling.</para>
///
/// <para>Master bypass disengages the WHOLE plugin chain (NoiseGate,
/// EQ, Comp, Exciter, Bass, Reverb). Per-plugin bypass states are
/// untouched — when the operator flips master back off, each plugin
/// resumes in the state it was last left in. CFC is downstream in
/// WDSP and unaffected.</para>
///
/// <para>Payload: <c>[type:1][bypassed:u8]</c> — 2 bytes total.</para>
/// </summary>
public readonly record struct AudioMasterBypassFrame(bool Bypassed)
{
    public const int ByteLength = 2;

    public void Serialize(IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(ByteLength);
        span[0] = (byte)MsgType.AudioMasterBypass;
        span[1] = Bypassed ? (byte)1 : (byte)0;
        writer.Advance(ByteLength);
    }

    public static AudioMasterBypassFrame Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ByteLength)
            throw new InvalidDataException(
                $"AudioMasterBypassFrame requires {ByteLength} bytes, got {bytes.Length}");
        if (bytes[0] != (byte)MsgType.AudioMasterBypass)
            throw new InvalidDataException(
                $"expected AudioMasterBypass (0x{(byte)MsgType.AudioMasterBypass:X2}), got 0x{bytes[0]:X2}");
        return new AudioMasterBypassFrame(Bypassed: bytes[1] != 0);
    }
}
