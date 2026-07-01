// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Buffers.Binary;
using Zeus.Contracts;
using Zeus.Protocol2;

namespace Zeus.VirtualRadio.P2;

/// <summary>
/// Decodes inbound Protocol-2 host command packets — the inverse of
/// <c>Zeus.Protocol2.Protocol2Client</c>'s <c>SendCmdGeneral</c> /
/// <c>SendCmdRx</c> / <c>SendCmdTx</c> / <c>SendCmdHighPriority</c> composers.
/// The client sends each command kind to a distinct UDP port, so the engine
/// classifies a datagram by the port it arrived on and this decoder reads the
/// fields the emulator needs to drive RX / telemetry / PureSignal feedback:
/// MOX, drive byte, the byte-1363 <c>Mux</c> PS-arm, and the byte-59 TX
/// attenuator. Pure and socketless.
/// </summary>
internal sealed class P2CmdDecoder
{
    private static readonly IReadOnlyList<DecodedHostCommand> Empty = Array.Empty<DecodedHostCommand>();

    // User-RX DDC config block offset for the rest-mode rate. The client lays
    // DDC config blocks at 17 + ddc*6 with the BE u16 rate at +1
    // (Protocol2Client.ComposeCmdRxBuffer); the user RX sits at RxBaseDdc(board).
    private readonly int _userRxRateOffset;

    /// <param name="board">The board the emulator impersonates — selects the
    /// user-RX DDC slot for rest-mode rate decode (HermesII RX is DDC0).</param>
    public P2CmdDecoder(HpsdrBoardKind board = HpsdrBoardKind.HermesII)
    {
        int rxDdc = Protocol2Client.RxBaseDdc(board);
        _userRxRateOffset = 17 + rxDdc * 6 + 1;
    }

    /// <summary>
    /// Decode one inbound command packet that arrived on <paramref name="destPort"/>
    /// (the well-known radio port the host targeted), mutating
    /// <paramref name="state"/> in place and returning the decoded event(s).
    /// Returns an empty list for unrecognised packets (discovery probes are
    /// handled by the engine before this is called).
    /// </summary>
    public IReadOnlyList<DecodedHostCommand> Decode(int destPort, ReadOnlySpan<byte> packet, HostCommandState state)
    {
        switch (destPort)
        {
            case P2Wire.CmdGeneralPort:
                return DecodeGeneral(packet, state);
            case P2Wire.CmdRxPort:
                return DecodeRx(packet, state);
            case P2Wire.CmdTxPort:
                return DecodeTx(packet, state);
            case P2Wire.CmdHighPriorityPort:
                return DecodeHighPriority(packet, state);
            default:
                return Empty;
        }
    }

    private static IReadOnlyList<DecodedHostCommand> DecodeGeneral(ReadOnlySpan<byte> p, HostCommandState state)
    {
        // CmdGeneral is a 60-byte packet with byte 4 = 0x00 (a discovery probe
        // carries 0x02 there and never reaches this decoder). It carries the
        // radio-side port map and PA-enable; we only need it to mark that the
        // host has begun the connect handshake, which flips the RX stream on.
        if (p.Length < P2Wire.CmdSmallLength || p[P2Wire.GeneralCmdByte] != P2Wire.GeneralConnect)
            return Empty;
        state.Running = true;
        return One("CmdGeneral", "connect handshake — radio configured, RX stream armed");
    }

    private IReadOnlyList<DecodedHostCommand> DecodeRx(ReadOnlySpan<byte> p, HostCommandState state)
    {
        if (p.Length != P2Wire.BufLen)
            return Empty;

        state.NumAdc = p[P2Wire.RxNumAdcByte];
        byte ddcEnable = p[P2Wire.RxDdcEnableByte];

        // Byte 1363 (the Mux register on the 10E): bit 1 (0x02) arms the
        // single-ADC PS time-mux for this TX burst. The gateware keys feedback
        // off exactly this bit (Hermes.v:684-687), so the emulator does too.
        bool psArmed = (p[P2Wire.RxMuxByte] & P2Wire.RxMuxPsBit) != 0;
        state.PsArmedBurst = psArmed;

        // Negotiated IQ rate: the burst descriptor pins DDC0 at 192k (offset 18);
        // at rest read the user-RX DDC config block (RxBaseDdc-dependent offset).
        int rateOffset = psArmed ? P2Wire.RxDdc0SampleRateOffset : _userRxRateOffset;
        int rateKhz = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(rateOffset, 2));
        if (rateKhz > 0) state.SampleRateKhz = rateKhz;

        return One("CmdRx",
            $"numAdc={state.NumAdc} ddcEnable=0x{ddcEnable:X2} mux1363=0x{p[P2Wire.RxMuxByte]:X2} " +
            $"psBurst={(psArmed ? 1 : 0)} rate={state.SampleRateKhz}k");
    }

    private static IReadOnlyList<DecodedHostCommand> DecodeTx(ReadOnlySpan<byte> p, HostCommandState state)
    {
        if (p.Length < P2Wire.CmdSmallLength)
            return Empty;

        state.MicBoost = (p[P2Wire.TxMicControlByte] & 0x02) != 0;
        state.MicLineIn = (p[P2Wire.TxMicControlByte] & 0x01) != 0;
        // Byte 59 = the TX-time ADC attenuator (Angelia_atten_Tx0). On a
        // single-ADC PS time-mux this is the protective seed; surface it for the
        // safety assertion.
        state.TxStepAttnDb = p[P2Wire.TxStepAttnByte];

        return One("CmdTx",
            $"micCtrl=0x{p[P2Wire.TxMicControlByte]:X2} lineInGain={p[P2Wire.TxLineInGainByte]} " +
            $"byte59={state.TxStepAttnDb}");
    }

    private static IReadOnlyList<DecodedHostCommand> DecodeHighPriority(ReadOnlySpan<byte> p, HostCommandState state)
    {
        if (p.Length != P2Wire.BufLen)
            return Empty;

        byte runMox = p[P2Wire.HpRunMoxByte];
        state.Running |= (runMox & P2Wire.HpRunBit) != 0;
        state.Mox = (runMox & P2Wire.HpMoxBit) != 0;
        state.Ptt = state.Mox;
        state.DriveByte = p[P2Wire.HpDriveByte];

        // Decode the DDC0 RX NCO + TX-DUC phase words back to Hz for
        // observability (the host writes phase = Hz * HzToPhase).
        uint rxPhase = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(P2Wire.HpDdc0PhaseOffset, 4));
        uint txPhase = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(P2Wire.HpTxDucPhaseOffset, 4));
        state.RxFreqHz = (long)Math.Round(rxPhase / P2Wire.HzToPhase);
        state.TxFreqHz = (long)Math.Round(txPhase / P2Wire.HzToPhase);

        // alex0 bit 11 = external PS feedback tap relay (RX 1 Out). On the
        // single-ADC G2E this is the ONLY path the one ADC can see the coupler, so
        // the emulator keys the coupler CONTENT off it (frames still flow on the
        // byte-1363 arm, but the coupler is silent until the tap is routed).
        uint alex0 = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(P2Wire.HpAlex0Offset, 4));
        state.PsCouplerRouted = (alex0 & P2Wire.AlexBypassBit) != 0;

        return One("CmdHighPriority",
            $"run={(state.Running ? 1 : 0)} mox={(state.Mox ? 1 : 0)} drive={state.DriveByte} " +
            $"rxFreq={state.RxFreqHz}Hz txFreq={state.TxFreqHz}Hz psTap={(state.PsCouplerRouted ? 1 : 0)}");
    }

    private static IReadOnlyList<DecodedHostCommand> One(string kind, string summary) =>
        new[] { new DecodedHostCommand(DateTimeOffset.UtcNow, ProtocolVersion.P2, kind, summary) };
}
