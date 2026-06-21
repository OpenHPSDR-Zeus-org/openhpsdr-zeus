// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// External-port encoder seam — antenna slice (external-ports plan, issue #804).
//
// This is the FIREWALL: every external-antenna relay bit on the wire —
// RX-antenna select (P1 C3[7:5]) and TX-antenna select (P2 alex0[26:24]) — is
// composed behind one board-branched / protocol-branched encoder, instead of
// scattered bit literals in the emission code.
//
// The encoders delegate to the very same pure helpers the wire path already
// uses (ControlFrame.EncodeRxAntennaC3Bits on P1; Protocol2Client.
// EncodeTxAntennaBits on P2), so the bits they produce are identical to the
// wire path for every supported board — byte-identical by construction. The
// per-board wire-layer clamp (force ANT1 on relay-less boards) lives in the
// shared P1 helper itself (HL2 single-jack case).
//
// Dependency direction: Zeus.Server.Hosting references Zeus.Protocol1 /
// Zeus.Protocol2 (not the reverse), so this seam consults the protocol layer's
// pure helpers via InternalsVisibleTo. The protocol clients never call back
// into this assembly — that would be a cycle.
//
// SCOPE: antenna only. Audio front-end / mic / codec / RX-aux-on-P1 emission
// are intentionally NOT part of this seam (separate slices). RX-aux on P2 rides
// the Protocol2Client.SetAntennas path directly (it is state, not a pure
// per-call encode), so it is not modelled here either.

using Zeus.Contracts;
using Zeus.Protocol1;
using Zeus.Protocol2;

namespace Zeus.Server;

/// <summary>
/// Immutable canonical desired external-antenna state. Protocol-agnostic — holds
/// no wire bits, only the operator-meaningful selections. Antenna slice carries
/// the TX/RX antenna relay picks; RX-aux is threaded through the
/// Protocol2Client.SetAntennas state path, not this per-call encoder.
/// </summary>
public readonly record struct ExternalPortState(
    /// <summary>TX antenna relay select. Honoured only on boards with
    /// <see cref="BoardCapabilities.HasTxAntennaRelays"/> (0x0A / Saturn family);
    /// every other board is ANT1-hardwired on transmit — the encoder gates the
    /// emission so a non-relay board never advertises ANT2/3.</summary>
    HpsdrAntenna TxAnt = HpsdrAntenna.Ant1,
    /// <summary>RX antenna relay select (C3[7:5] on P1). Clamped to ANT1 at the
    /// wire layer on boards without
    /// <see cref="BoardCapabilities.HasRxAntennaRelays"/> (Hermes-Lite 2).</summary>
    HpsdrAntenna RxAnt = HpsdrAntenna.Ant1)
{
    /// <summary>Default state: ANT1 / ANT1 — reproduces today's wire emission
    /// bit-for-bit on every board.</summary>
    public static readonly ExternalPortState Default = new();
}

/// <summary>
/// Per-board / per-protocol external-antenna bit encoder. Mirrors
/// <see cref="IRadioDriveProfile"/>'s seam: a small strategy interface,
/// concrete per-protocol implementations, and an
/// <see cref="ExternalPortEncoders.For(HpsdrBoardKind, OrionMkIIVariant, RadioProtocol?)"/>
/// dispatch. This is the single place external-antenna relay math lives.
/// </summary>
public interface IExternalPortEncoder
{
    /// <summary>Diagnostic label for the encoder strategy.</summary>
    string Label { get; }

    /// <summary>
    /// Encode the Protocol-1 Config-frame RX-antenna relay bits (C3[7:5]) for
    /// the desired <paramref name="state"/>. Returns the byte to OR into C3.
    /// Delegates to the wire path's own pure helper so the bytes match exactly
    /// (including the HL2 wire-layer ANT1 clamp).
    /// </summary>
    byte EncodeP1RxAntennaC3Bits(in ExternalPortState state);

    /// <summary>
    /// Encode the Protocol-2 Alex-word TX-antenna relay bits (alex0[26:24]) for
    /// the desired <paramref name="state"/>. Returns the bits to OR into the
    /// Alex word. Delegates to the wire path's own pure helper.
    /// </summary>
    uint EncodeP2TxAntennaBits(in ExternalPortState state);
}

/// <summary>
/// Protocol-1 encoder for boards that DO have switchable RX antenna relays
/// (Hermes-class, ANAN-100D/200D, ANAN-G2E). Emits C3[7:5] straight from the
/// desired RX antenna. These boards are ANT1-hardwired on transmit (no P2 Alex
/// word), so the P2 TX-antenna path returns ANT1.
/// </summary>
public sealed class Protocol1PortEncoder : IExternalPortEncoder
{
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
        // The shared pure helper applies the wire-layer relay-presence clamp
        // itself (forcing ANT1 on relay-less boards), so a relay-capable P1 board
        // emits the raw selection and HL2 is clamped — same bytes the inline wire
        // path produces. _hasRxAntennaRelays is retained for the encoder's own
        // gating / diagnostics.
        _ = _hasRxAntennaRelays;
        return ControlFrame.EncodeRxAntennaC3Bits(state.RxAnt, _board);
    }

    public uint EncodeP2TxAntennaBits(in ExternalPortState state)
        // P1 boards do not emit a P2 Alex word; ANT1-hardwired on transmit.
        => Protocol2Client.EncodeTxAntennaBits(txAnt: 1);
}

/// <summary>
/// Protocol-2 encoder for the 0x0A / Saturn family (G2 / 7000DLE / 8000DLE /
/// G2-1K / OrionMkII original / ANVELINA-PRO3 / Red Pitaya). These boards have
/// switchable TX antenna relays (alex0[26:24]). The P1 C3 path is unused.
/// </summary>
public sealed class Protocol2PortEncoder : IExternalPortEncoder
{
    private readonly HpsdrBoardKind _board;
    private readonly OrionMkIIVariant _variant;
    private readonly bool _hasTxAntennaRelays;

    public Protocol2PortEncoder(HpsdrBoardKind board, OrionMkIIVariant variant, bool hasTxAntennaRelays)
    {
        _board = board;
        _variant = variant;
        _hasTxAntennaRelays = hasTxAntennaRelays;
    }

    public string Label => $"P2({_board}/{_variant}, txRelays={_hasTxAntennaRelays})";

    public byte EncodeP1RxAntennaC3Bits(in ExternalPortState state)
        // P2 boards do not use the P1 Config frame; the RX-antenna select rides
        // the Alex word on the SetAntennas state path.
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
}

/// <summary>
/// Hermes-Lite 2 encoder. HL2 has a single antenna jack: C3[5] does NOT drive
/// an ANT1/2/3 relay, it forwards to the N2ADR antenna pad. The RX-antenna
/// selection is therefore clamped to ANT1 at the wire layer
/// (<see cref="BoardCapabilities.HasRxAntennaRelays"/> is false for HL2). HL2
/// has no P2 Alex word and is ANT1-hardwired on transmit.
/// </summary>
public sealed class HermesLite2PortEncoder : IExternalPortEncoder
{
    public string Label => "HL2(singleJack, rxClampToAnt1)";

    public byte EncodeP1RxAntennaC3Bits(in ExternalPortState state)
        // The shared pure helper applies the HL2 clamp, so whatever RxAnt the
        // operator persisted, the emitted bits are ANT1 — the N2ADR pad never
        // flips off a stale per-band ANT2/3.
        => ControlFrame.EncodeRxAntennaC3Bits(state.RxAnt, HpsdrBoardKind.HermesLite2);

    public uint EncodeP2TxAntennaBits(in ExternalPortState state)
        => Protocol2Client.EncodeTxAntennaBits(txAnt: 1);
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
    /// Resolve the external-antenna encoder for a connected board. The protocol
    /// is derived from the board kind when not given explicitly: the 0x0A
    /// OrionMkII family is Protocol 2, everything else Protocol 1.
    /// </summary>
    public static IExternalPortEncoder For(
        HpsdrBoardKind board,
        OrionMkIIVariant variant = OrionMkIIVariant.G2,
        RadioProtocol? protocol = null)
    {
        var caps = BoardCapabilitiesTable.For(board, variant);
        var p = protocol ?? DefaultProtocolFor(board);

        // HL2 first — it is the single-jack special case (the one P1 board with
        // no RX-antenna relay), so its dedicated encoder owns the clamp.
        if (board == HpsdrBoardKind.HermesLite2)
            return new HermesLite2PortEncoder();

        return p == RadioProtocol.Protocol2
            ? new Protocol2PortEncoder(board, variant, caps.HasTxAntennaRelays)
            : new Protocol1PortEncoder(board, caps.HasRxAntennaRelays);
    }

    /// <summary>The transport Zeus uses for a given board kind.</summary>
    public static RadioProtocol DefaultProtocolFor(HpsdrBoardKind board) => board switch
    {
        HpsdrBoardKind.OrionMkII => RadioProtocol.Protocol2,
        _                        => RadioProtocol.Protocol1,
    };
}
