// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// External-port encoder seam (external-ports plan, Phase 1).
//
// This is the FIREWALL described in the engineering plan §3.1: every
// external-port relay bit on the wire — RX-antenna select, TX-antenna select,
// open-collector masks — is composed behind one board-branched / protocol-
// branched encoder, instead of scattered bit literals in the emission code.
//
// Phase 1 is BYTE-IDENTICAL. The encoders here delegate to the very same pure
// helpers the wire path already uses (ControlFrame.EncodeRxAntennaC3Bits on
// P1; Protocol2Client.EncodeTxAntennaBits / ComputeAlexWord on P2), so the
// bits they produce are identical to today's emission for every supported
// board. No OC / PS / MOX / duplex byte is touched here. The per-board wire-
// layer clamp scaffolding (force ANT1 on relay-less boards) is present but
// inert — it activates in Phase 2 with its own differential golden test.
//
// Dependency direction: Zeus.Server.Hosting references Zeus.Protocol1 /
// Zeus.Protocol2 (not the reverse), so this seam consults the protocol layer's
// pure helpers via InternalsVisibleTo. The protocol clients never call back
// into this assembly — that would be a cycle.

using Zeus.Contracts;
using Zeus.Protocol1;
using Zeus.Protocol2;

namespace Zeus.Server;

/// <summary>
/// Immutable canonical desired external-port state. Protocol-agnostic — holds
/// no wire bits, only the operator-meaningful selections. Phase 1 carries just
/// antenna + OC (the controls whose emission this phase moves behind the
/// encoder); audio front-end and RX-aux inputs join in later phases.
///
/// OC masks mirror the existing <c>CcState.UserOcTxMask</c>/<c>UserOcRxMask</c>
/// (and the P2 <c>_ocTxMask</c>/<c>_ocRxMask</c>) — they are carried here so the
/// canonical state is complete, NOT to introduce a second emission path. Phase 1
/// does not re-route OC emission; the existing OC code is untouched.
/// </summary>
public readonly record struct ExternalPortState(
    /// <summary>TX antenna relay select. Honoured only on boards with
    /// <see cref="BoardCapabilities.HasTxAntennaRelays"/> (0x0A/Saturn family);
    /// every other board is ANT1-hardwired on transmit. Phase 1 leaves the
    /// wire emission at the hardcoded ANT1 — this field is wired through in
    /// Phase 2.</summary>
    HpsdrAntenna TxAnt = HpsdrAntenna.Ant1,
    /// <summary>RX antenna relay select (C3[7:5] on P1; Alex select on P2).
    /// Clamped to ANT1 at the wire layer on boards without
    /// <see cref="BoardCapabilities.HasRxAntennaRelays"/> — scaffolded in
    /// Phase 1, active in Phase 2.</summary>
    HpsdrAntenna RxAnt = HpsdrAntenna.Ant1,
    /// <summary>Open-collector TX mask (7-bit). Carried for completeness;
    /// emission stays on the existing OC path in Phase 1.</summary>
    byte OcTxMask = 0,
    /// <summary>Open-collector RX mask (7-bit). Carried for completeness;
    /// emission stays on the existing OC path in Phase 1.</summary>
    byte OcRxMask = 0,
    // ---- Audio front-end (external-ports plan, Phase 4) --------------------
    // Global per-radio (NOT per-band). Verified byte layouts:
    //   P1 Hermes-class codec (0x12 frame): C2[0]=MicBoost, C2[1]=LineIn.
    //   P1 HL2 (0x14 frame, read-modify-write): C1[4]=BalancedInput (mic_trs),
    //     C1[5]=MicBias, C2[4:0]=LineInGain (PS C2[6] + C4 PGA preserved).
    //   P2 TxSpecific byte 50: bit0=LineIn, bit1=MicBoost, bit4=MicBias,
    //     bit5=BalancedInput(XLR); byte 51=LineInGain.
    // All default off / 0 → byte-identical to today on every board.
    /// <summary>Line-in vs mic select (true = line-in).</summary>
    bool LineIn = false,
    /// <summary>Mic boost (Hermes-class codec / P2).</summary>
    bool MicBoost = false,
    /// <summary>Mic bias enable. DEFAULTS OFF — enabling on a floating
    /// connector can hang PTT; the gate guards it.</summary>
    bool MicBias = false,
    /// <summary>Balanced / TRS input select (HL2 mic_trs / Saturn XLR).</summary>
    bool BalancedInput = false,
    /// <summary>Line-in gain, 0..31 (HL2 0x14 C2[4:0] / P2 byte 51).</summary>
    byte LineInGain = 0)
{
    /// <summary>Default state: ANT1 / ANT1 / no OC / mic input, no boost/bias,
    /// gain 0 — reproduces today's wire emission bit-for-bit on every board.</summary>
    public static readonly ExternalPortState Default = new();
}

/// <summary>
/// Per-board / per-protocol external-port bit encoder. Mirrors
/// <see cref="IRadioDriveProfile"/>'s seam: a small strategy interface,
/// concrete per-protocol implementations, and an
/// <see cref="ExternalPortEncoders.For(HpsdrBoardKind, OrionMkIIVariant, RadioProtocol)"/>
/// dispatch. This is the single place external-port relay math lives.
/// </summary>
public interface IExternalPortEncoder
{
    /// <summary>Diagnostic label for the encoder strategy.</summary>
    string Label { get; }

    /// <summary>
    /// Encode the Protocol-1 Config-frame RX-antenna relay bits (C3[7:5]) for
    /// the desired <paramref name="state"/>. Returns the byte to OR into C3.
    /// Delegates to the wire path's own pure helper so the bytes match exactly.
    /// </summary>
    byte EncodeP1RxAntennaC3Bits(in ExternalPortState state);

    /// <summary>
    /// Encode the Protocol-2 Alex-word TX-antenna relay bits (alex0[26:24]) for
    /// the desired <paramref name="state"/>. Returns the bits to OR into the
    /// Alex word. Delegates to the wire path's own pure helper.
    /// </summary>
    uint EncodeP2TxAntennaBits(in ExternalPortState state);

    /// <summary>
    /// Encode the Protocol-2 TxSpecific (port 1026) byte-50 mic_control flags
    /// for the desired audio front-end state (external-ports plan, Phase 4).
    /// Only emitted on codec boards; a board without
    /// <see cref="BoardCapabilities.HasOnboardCodec"/> returns 0 so its
    /// TxSpecific tail stays byte-identical to today. Bit layout (Thetis
    /// network.c:1226-1233): bit0=line_in, bit1=mic_boost, bit4=mic_bias,
    /// bit5=balanced/XLR. Pairs with byte 51 = <c>LineInGain</c>.
    /// </summary>
    byte EncodeP2MicControlByte(in ExternalPortState state);
}

/// <summary>
/// Protocol-1 encoder for codec / Alex boards that DO have switchable RX
/// antenna relays (Hermes-class, ANAN-100D/200D, ANAN-G2E). Emits C3[7:5]
/// straight from the desired RX antenna. These boards are ANT1-hardwired on
/// transmit (no P2 Alex word), so the P2 TX-antenna path returns ANT1.
/// </summary>
public sealed class Protocol1PortEncoder : IExternalPortEncoder
{
    /// <summary>Whether the target board has RX-antenna relays. False clamps
    /// the RX-antenna selection to ANT1 at the wire layer (the HL2 case).
    /// Phase 1 keeps this informational only (clamp inert); Phase 2 enforces
    /// it.</summary>
    private readonly bool _hasRxAntennaRelays;
    private readonly HpsdrBoardKind _board;

    public Protocol1PortEncoder(HpsdrBoardKind board, bool hasRxAntennaRelays)
    {
        _board = board;
        _hasRxAntennaRelays = hasRxAntennaRelays;
    }

    public string Label => $"P1({_board}, rxRelays={_hasRxAntennaRelays})";

    public byte EncodeP1RxAntennaC3Bits(in ExternalPortState state)
    {
        // The pure helper ControlFrame.EncodeRxAntennaC3Bits is the single
        // source of the C3[7:5] math and is also called inline on the wire
        // path. It applies the wire-layer relay-presence clamp itself (forcing
        // ANT1 on relay-less boards), so a relay-capable P1 board emits the raw
        // selection and HL2 is clamped — same bytes the inline path produces.
        // _hasRxAntennaRelays stays available for the encoder's own gating.
        _ = _hasRxAntennaRelays;
        return ControlFrame.EncodeRxAntennaC3Bits(state.RxAnt, _board);
    }

    public uint EncodeP2TxAntennaBits(in ExternalPortState state)
        // P1 boards do not emit a P2 Alex word; ANT1-hardwired on transmit.
        => Protocol2Client.EncodeTxAntennaBits(txAnt: 1);

    public byte EncodeP2MicControlByte(in ExternalPortState state)
        // P1 codec boards carry mic_boost/mic_linein on the 0x12 frame
        // (ControlFrame), not the P2 TxSpecific byte 50. No P2 byte here.
        => 0;
}

/// <summary>
/// Protocol-2 encoder for the 0x0A / Saturn family (G2 / 7000DLE / 8000DLE /
/// G2-1K / OrionMkII original / ANVELINA-PRO3 / Red Pitaya). These boards have
/// switchable TX antenna relays (alex0[26:24]) and RX antenna relays.
/// </summary>
public sealed class Protocol2PortEncoder : IExternalPortEncoder
{
    private readonly HpsdrBoardKind _board;
    private readonly OrionMkIIVariant _variant;
    private readonly bool _hasTxAntennaRelays;
    private readonly bool _hasOnboardCodec;

    public Protocol2PortEncoder(HpsdrBoardKind board, OrionMkIIVariant variant, bool hasTxAntennaRelays, bool hasOnboardCodec)
    {
        _board = board;
        _variant = variant;
        _hasTxAntennaRelays = hasTxAntennaRelays;
        _hasOnboardCodec = hasOnboardCodec;
    }

    public string Label => $"P2({_board}/{_variant}, txRelays={_hasTxAntennaRelays})";

    public byte EncodeP1RxAntennaC3Bits(in ExternalPortState state)
        // P2 boards do not use the P1 Config frame; the P2 RX-antenna select
        // rides the Alex word (Phase 5). Phase 1 returns no C3 bits.
        => 0;

    public uint EncodeP2TxAntennaBits(in ExternalPortState state)
    {
        // Gate the alex0[26:24] TX-antenna emission on the variant's relay
        // population: a board/variant without TX-antenna relays stays on ANT1
        // regardless of the requested selection, so it can never advertise
        // ANT2/3. Relay-capable variants thread the real per-band TxAnt through
        // the shared pure helper (1-based wire selector).
        int wire = _hasTxAntennaRelays ? (int)state.TxAnt + 1 : 1;
        return Protocol2Client.EncodeTxAntennaBits(wire);
    }

    public byte EncodeP2MicControlByte(in ExternalPortState state)
    {
        // Gate on codec presence: a board without the stream codec gets the
        // zero byte, so its TxSpecific tail is byte-identical to today. The
        // shared helper is the single source of the byte-50 bit math (the same
        // constants Protocol2Client.SetAudioFrontEnd composes).
        if (!_hasOnboardCodec) return 0;
        return ExternalPortAudio.P2MicControlByte(
            lineIn: state.LineIn,
            micBoost: state.MicBoost,
            micBias: state.MicBias,
            balanced: state.BalancedInput);
    }
}

/// <summary>
/// Shared pure helpers for the audio front-end bit math (external-ports plan,
/// Phase 4). The single source of truth for the P2 TxSpecific byte-50
/// mic_control flag layout (Thetis network.c:1226-1233), so the encoder seam
/// and any test compute the same byte. The P1 audio bits (0x12 mic_boost/linein
/// and 0x14 mic_trs/mic_bias/line_in_gain) live in ControlFrame, which owns the
/// per-board gating for the Protocol-1 frames.
/// </summary>
internal static class ExternalPortAudio
{
    // TxSpecific byte-50 mic_control flags (Thetis network.c:1226-1233).
    private const byte LineInBit   = 0x01; // bit0
    private const byte MicBoostBit = 0x02; // bit1
    private const byte MicBiasBit  = 0x10; // bit4 (enable Orion mic bias)
    private const byte XlrBit       = 0x20; // bit5 (balanced/XLR input — Saturn)

    public static byte P2MicControlByte(bool lineIn, bool micBoost, bool micBias, bool balanced)
    {
        byte b = 0;
        if (lineIn)   b |= LineInBit;
        if (micBoost) b |= MicBoostBit;
        if (micBias)  b |= MicBiasBit;
        if (balanced) b |= XlrBit;
        return b;
    }
}

/// <summary>
/// Hermes-Lite 2 encoder. HL2 has a single antenna jack: C3[5] does NOT drive
/// an ANT1/2/3 relay, it forwards to the N2ADR antenna pad. The RX-antenna
/// selection MUST therefore be clamped to ANT1 at the wire layer
/// (<see cref="BoardCapabilities.HasRxAntennaRelays"/> is false for HL2).
///
/// Phase 1 is BYTE-IDENTICAL: today's wire path emits HL2's raw C3[7:5] value
/// unconditionally, so the clamp is scaffolded here but inert — it activates
/// in Phase 2 with a differential golden test (raw ANT3 today → ANT1 after).
/// HL2 has no P2 Alex word and is ANT1-hardwired on transmit.
/// </summary>
public sealed class HermesLite2PortEncoder : IExternalPortEncoder
{
    public string Label => "HL2(singleJack, rxClamp=Phase2)";

    public byte EncodeP1RxAntennaC3Bits(in ExternalPortState state)
    {
        // HL2 has no RX-antenna relay (C3[5] drives the N2ADR antenna pad). The
        // shared pure helper applies the wire-layer clamp for HL2, so whatever
        // RxAnt the operator persisted, the emitted bits are ANT1 — the N2ADR
        // pad never flips off a stale per-band ANT2/3.
        return ControlFrame.EncodeRxAntennaC3Bits(state.RxAnt, HpsdrBoardKind.HermesLite2);
    }

    public uint EncodeP2TxAntennaBits(in ExternalPortState state)
        => Protocol2Client.EncodeTxAntennaBits(txAnt: 1);

    public byte EncodeP2MicControlByte(in ExternalPortState state)
        // HL2 is Protocol 1 with no stream codec — its mic front-end rides the
        // 0x14 frame (ControlFrame read-modify-write), not the P2 TxSpecific.
        => 0;
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
    /// Resolve the external-port encoder for a connected board. The protocol is
    /// derived from the board kind when not given explicitly: the 0x0A
    /// OrionMkII family is Protocol 2, everything else Protocol 1.
    /// </summary>
    public static IExternalPortEncoder For(
        HpsdrBoardKind board,
        OrionMkIIVariant variant = OrionMkIIVariant.G2,
        RadioProtocol? protocol = null)
    {
        var caps = BoardCapabilitiesTable.For(board, variant);
        var p = protocol ?? DefaultProtocolFor(board);

        // HL2 first — it is the single-jack special case (single P1 board with
        // no RX-antenna relay), so its dedicated encoder owns the clamp.
        if (board == HpsdrBoardKind.HermesLite2)
            return new HermesLite2PortEncoder();

        return p == RadioProtocol.Protocol2
            ? new Protocol2PortEncoder(board, variant, caps.HasTxAntennaRelays, caps.HasOnboardCodec)
            : new Protocol1PortEncoder(board, caps.HasRxAntennaRelays);
    }

    /// <summary>The transport Zeus uses for a given board kind.</summary>
    public static RadioProtocol DefaultProtocolFor(HpsdrBoardKind board) => board switch
    {
        HpsdrBoardKind.OrionMkII => RadioProtocol.Protocol2,
        _                        => RadioProtocol.Protocol1,
    };
}
