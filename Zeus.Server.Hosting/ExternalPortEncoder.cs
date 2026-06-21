// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// External-port AUDIO encoder seam (external-audio-jacks re-port).
//
// This is the FIREWALL for the TX audio-source feature: every audio-front-end
// bit on the wire — P2 TxSpecific byte-50 mic_control / byte-51 line_in_gain,
// and the P1 0x12-codec mic_boost/mic_linein bits — is composed behind one
// board-branched / protocol-branched encoder instead of scattered bit literals
// in the emission code.
//
// Default-inert / byte-identical: at defaults (Host, no boost/bias, gain 0) the
// encoder produces literal zero on every surface, so the wire bytes are
// unchanged on every board. The PureSignal golden suites stay green.
//
// (The sibling antenna / OC encoder surfaces from the original external-ports
// plan are NOT part of this audio re-port — they land separately. This seam
// carries audio only.)

using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Immutable canonical desired TX-audio-source state. Protocol-agnostic — holds
/// no wire bits, only the operator-meaningful selection. Global per-radio (NOT
/// per-band). The wire bytes are PURE FUNCTIONS of <see cref="Source"/> (plus
/// that source's params).
///
/// Source == <see cref="TxAudioSource.Host"/> → every audio surface is literal
/// zero, byte-identical to today; the encoder must NOT fall through to read the
/// params under Host.
/// </summary>
public readonly record struct ExternalPortState(
    /// <summary>The single mutually-exclusive TX-audio source.</summary>
    TxAudioSource Source = TxAudioSource.Host,
    /// <summary>Mic boost — parameter of <see cref="TxAudioSource.RadioMic"/>
    /// (and optionally XLR). Ignored under Host / RadioLineIn.</summary>
    bool MicBoost = false,
    /// <summary>Mic bias enable — parameter of <see cref="TxAudioSource.RadioMic"/>
    /// (and XLR). DEFAULTS OFF — enabling on a floating connector can hang PTT;
    /// the gate guards it. Ignored under Host / RadioLineIn.</summary>
    bool MicBias = false,
    /// <summary>Line-in gain, 0..31 — parameter of
    /// <see cref="TxAudioSource.RadioLineIn"/>. Ignored under every other
    /// source.</summary>
    byte LineInGain = 0)
{
    /// <summary>Default state: Host audio, no boost/bias, gain 0 — reproduces
    /// today's wire emission bit-for-bit on every board.</summary>
    public static readonly ExternalPortState Default = new();
}

/// <summary>
/// Per-board / per-protocol TX-audio bit encoder. Mirrors
/// <see cref="IRadioDriveProfile"/>'s seam: a small strategy interface,
/// concrete per-protocol implementations, and an
/// <see cref="ExternalPortEncoders.For(HpsdrBoardKind, OrionMkIIVariant, RadioProtocol)"/>
/// dispatch. This is the single place external-port audio bit math lives.
/// </summary>
public interface IExternalPortEncoder
{
    /// <summary>Diagnostic label for the encoder strategy.</summary>
    string Label { get; }

    /// <summary>
    /// Encode the Protocol-2 TxSpecific (port 1026) byte-50 mic_control flags as
    /// a PURE FUNCTION of <see cref="ExternalPortState.Source"/>. Only emitted on
    /// codec boards; a board without <see cref="BoardCapabilities.HasOnboardCodec"/>
    /// returns 0 so its TxSpecific tail stays byte-identical to today. Bit layout
    /// (Thetis network.c:1226-1233): bit0=line_in, bit1=mic_boost, bit4=mic_bias,
    /// bit5=balanced/XLR. <see cref="TxAudioSource.Host"/> returns LITERAL 0 with
    /// NO fallthrough that reads the params, so a persisted non-zero
    /// MicBoost/MicBias can never leak onto the wire after a revert to Host.
    /// Pairs with <see cref="EncodeP2LineInGainByte"/> (byte 51).
    /// </summary>
    byte EncodeP2MicControlByte(in ExternalPortState state);

    /// <summary>
    /// Encode the Protocol-2 TxSpecific byte-51 line_in_gain (0..31). Non-zero
    /// ONLY for <see cref="TxAudioSource.RadioLineIn"/>; every other source
    /// (including Host) returns 0 so the radio's last-remembered gain is cleared
    /// the moment line-in is deselected. Pure function of Source.
    /// </summary>
    byte EncodeP2LineInGainByte(in ExternalPortState state);

    /// <summary>
    /// Resolve the Protocol-1 codec (0x12 DriveFilter frame) audio bits as a
    /// PURE FUNCTION of <see cref="ExternalPortState.Source"/>. C2[0]=mic_boost,
    /// C2[1]=mic_linein. <see cref="TxAudioSource.RadioMic"/> drives mic_boost
    /// from the param; every other source (Host included) returns
    /// <c>(false,false)</c> — no param leak. RadioLineIn is intentionally
    /// unreachable on pure-P1 codec boards in v1 (no P1 radio-mic RX path), so
    /// mic_linein stays clear. HL2 is Host-only and never calls this.
    /// </summary>
    (bool MicBoost, bool MicLineIn) EncodeP1CodecAudioBits(in ExternalPortState state);
}

/// <summary>
/// Protocol-1 encoder for codec / Alex boards (Hermes-class, ANAN-100D/200D,
/// ANAN-G2E). Carries mic_boost / mic_linein on the 0x12 codec frame; no P2
/// TxSpecific bytes.
/// </summary>
public sealed class Protocol1PortEncoder : IExternalPortEncoder
{
    private readonly HpsdrBoardKind _board;

    public Protocol1PortEncoder(HpsdrBoardKind board) => _board = board;

    public string Label => $"P1({_board})";

    public byte EncodeP2MicControlByte(in ExternalPortState state)
        // P1 codec boards carry mic_boost/mic_linein on the 0x12 frame, not the
        // P2 TxSpecific byte 50.
        => 0;

    public byte EncodeP2LineInGainByte(in ExternalPortState state)
        // P1 boards have no P2 TxSpecific byte 51.
        => 0;

    public (bool MicBoost, bool MicLineIn) EncodeP1CodecAudioBits(in ExternalPortState state)
        // Pure function of Source: RadioMic → mic_boost; Host / everything else
        // → all clear (no param leak).
        => ExternalPortAudio.P1CodecAudioBits(state.Source, state.MicBoost);
}

/// <summary>
/// Protocol-2 encoder for the 0x0A / Saturn family (G2 / 7000DLE / 8000DLE /
/// G2-1K / OrionMkII original / ANVELINA-PRO3 / Red Pitaya). Audio rides the
/// TxSpecific byte 50 (mic_control) / byte 51 (line_in_gain).
/// </summary>
public sealed class Protocol2PortEncoder : IExternalPortEncoder
{
    private readonly HpsdrBoardKind _board;
    private readonly OrionMkIIVariant _variant;
    private readonly bool _hasOnboardCodec;

    public Protocol2PortEncoder(HpsdrBoardKind board, OrionMkIIVariant variant, bool hasOnboardCodec)
    {
        _board = board;
        _variant = variant;
        _hasOnboardCodec = hasOnboardCodec;
    }

    public string Label => $"P2({_board}/{_variant})";

    public byte EncodeP2MicControlByte(in ExternalPortState state)
    {
        // Gate on codec presence: a board without the stream codec gets the
        // zero byte, so its TxSpecific tail is byte-identical to today. The
        // shared helper is the single source of the byte-50 bit math, computed
        // PURELY from the source — Host returns literal 0 with no param read.
        if (!_hasOnboardCodec) return 0;
        return ExternalPortAudio.P2MicControlByte(state.Source, state.MicBoost, state.MicBias);
    }

    public byte EncodeP2LineInGainByte(in ExternalPortState state)
    {
        // byte 51 is non-zero only for RadioLineIn; suppressed (0) for every
        // other source so a deselected line-in jack drops its remembered gain.
        if (!_hasOnboardCodec) return 0;
        return ExternalPortAudio.P2LineInGainByte(state.Source, state.LineInGain);
    }

    public (bool MicBoost, bool MicLineIn) EncodeP1CodecAudioBits(in ExternalPortState state)
        // P2 boards do not use the P1 0x12 codec frame.
        => (false, false);
}

/// <summary>
/// Shared PURE audio-source bit math. Single source of truth for the P2
/// TxSpecific byte-50 mic_control / byte-51 line_in_gain layout (Thetis
/// network.c:1226-1233) and the P1 0x12 codec bits, computed straight from
/// <see cref="TxAudioSource"/> so the encoder seam and any test agree.
///
/// CRUCIAL PURITY INVARIANT: <see cref="TxAudioSource.Host"/> returns literal 0
/// on every surface WITHOUT reading MicBoost/MicBias/LineInGain — a persisted
/// non-zero param must never leak onto the wire after a revert to Host. The
/// switch's Host arm short-circuits before any param is consulted.
/// </summary>
internal static class ExternalPortAudio
{
    // TxSpecific byte-50 mic_control flags (Thetis network.c:1226-1233).
    private const byte LineInBit   = 0x01; // bit0 — line-in select
    private const byte MicBoostBit = 0x02; // bit1 — mic boost
    private const byte MicBiasBit  = 0x10; // bit4 — enable Orion mic bias
    private const byte XlrBit      = 0x20; // bit5 — balanced/XLR input (Saturn)

    /// <summary>P2 byte-50 mic_control as a pure function of the source.</summary>
    public static byte P2MicControlByte(TxAudioSource source, bool micBoost, bool micBias) => source switch
    {
        // Host: literal zero, no param read. The analog jacks are suppressed.
        TxAudioSource.Host => 0,
        // RadioMic: boost (bit1) + bias (bit4) from the params, mic jack implied
        // (no line-in / XLR bit).
        TxAudioSource.RadioMic =>
            (byte)((micBoost ? MicBoostBit : 0) | (micBias ? MicBiasBit : 0)),
        // RadioLineIn: line-in select (bit0); gain rides byte 51. Mic params do
        // not apply.
        TxAudioSource.RadioLineIn => LineInBit,
        // RadioBalancedXlr: XLR select (bit5) | optional boost/bias (the XLR
        // front-end still honours the Orion mic-bias / boost stage on G2).
        TxAudioSource.RadioBalancedXlr =>
            (byte)(XlrBit | (micBoost ? MicBoostBit : 0) | (micBias ? MicBiasBit : 0)),
        _ => 0,
    };

    /// <summary>P2 byte-51 line_in_gain — non-zero ONLY for RadioLineIn.</summary>
    public static byte P2LineInGainByte(TxAudioSource source, byte lineInGain) =>
        source == TxAudioSource.RadioLineIn ? (byte)(lineInGain & 0x1F) : (byte)0;

    /// <summary>P1 0x12 codec bits (mic_boost C2[0], mic_linein C2[1]) as a pure
    /// function of the source. RadioMic drives mic_boost; everything else,
    /// including Host, returns all-clear — no param leak.</summary>
    public static (bool MicBoost, bool MicLineIn) P1CodecAudioBits(TxAudioSource source, bool micBoost) => source switch
    {
        TxAudioSource.RadioMic => (micBoost, false),
        // RadioLineIn is unreachable on pure-P1 codec boards in v1 (no P1
        // radio-mic RX path); Host/XLR are not P1-codec sources. All clear.
        _ => (false, false),
    };
}

/// <summary>
/// Hermes-Lite 2 encoder. HL2 has no stream codec and no P2 TxSpecific frame;
/// its mic front-end rides the 0x14 frame and is handled in
/// <c>ControlFrame</c> via the read-modify-write that preserves the PureSignal
/// bit + C4 PGA. This encoder emits no codec audio bits — the HL2 0x14 mic
/// fields are driven directly through <c>IProtocol1Client.SetAudioFrontEnd</c>.
/// </summary>
public sealed class HermesLite2PortEncoder : IExternalPortEncoder
{
    public string Label => "HL2(0x14 mic front-end)";

    public byte EncodeP2MicControlByte(in ExternalPortState state)
        // HL2 has no stream codec and no P2 TxSpecific frame at all.
        => 0;

    public byte EncodeP2LineInGainByte(in ExternalPortState state)
        // HL2 has no P2 TxSpecific byte 51.
        => 0;

    public (bool MicBoost, bool MicLineIn) EncodeP1CodecAudioBits(in ExternalPortState state)
        // HL2 is not a stream-codec board — its 0x12 codec audio bits stay
        // clear. The HL2 mic front-end (0x14) is encoded in ControlFrame.
        => (false, false);
}

/// <summary>
/// Which transport a connected board uses, for encoder dispatch. The 0x0A
/// family runs Protocol 2 in Zeus; every other supported board runs Protocol 1.
/// </summary>
public enum RadioProtocol
{
    Protocol1,
    Protocol2,
}

/// <summary>
/// Per-board / per-protocol dispatch for <see cref="IExternalPortEncoder"/>.
/// Mirrors <see cref="RadioDriveProfiles.For"/>. Every <see cref="HpsdrBoardKind"/>
/// maps to a non-null encoder (the board-coverage test asserts this).
/// </summary>
public static class ExternalPortEncoders
{
    /// <summary>
    /// Resolve the external-port audio encoder for a connected board. The
    /// protocol is derived from the board kind when not given explicitly: the
    /// 0x0A OrionMkII family is Protocol 2, everything else Protocol 1.
    /// </summary>
    public static IExternalPortEncoder For(
        HpsdrBoardKind board,
        OrionMkIIVariant variant = OrionMkIIVariant.G2,
        RadioProtocol? protocol = null)
    {
        var caps = BoardCapabilitiesTable.For(board, variant);
        var p = protocol ?? DefaultProtocolFor(board);

        // HL2 first — it is the no-codec special case (mic front-end rides the
        // 0x14 frame, handled in ControlFrame).
        if (board == HpsdrBoardKind.HermesLite2)
            return new HermesLite2PortEncoder();

        return p == RadioProtocol.Protocol2
            ? new Protocol2PortEncoder(board, variant, caps.HasOnboardCodec)
            : new Protocol1PortEncoder(board);
    }

    /// <summary>The transport Zeus uses for a given board kind.</summary>
    public static RadioProtocol DefaultProtocolFor(HpsdrBoardKind board) => board switch
    {
        HpsdrBoardKind.OrionMkII => RadioProtocol.Protocol2,
        _                        => RadioProtocol.Protocol1,
    };
}
