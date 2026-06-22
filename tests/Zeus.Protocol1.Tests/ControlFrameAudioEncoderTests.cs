// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

namespace Zeus.Protocol1.Tests;

/// <summary>
/// GOLDEN-BYTE tests for the Protocol-1 audio front-end (external-ports plan,
/// Phase 4). Two verified surfaces:
///
///   (a) Hermes-class codec boards — DriveFilter (0x12) frame:
///         C2[0] = mic_boost, C2[1] = mic_linein
///       (ramdor Thetis networkproto1.c:581; piHPSDR old_protocol.c:2154-2156).
///
///   (b) Hermes-Lite 2 — 0x0a / wire-0x14 frame, READ-MODIFY-WRITE:
///         C1[4] = mic_trs, C1[5] = mic_bias, C2[4:0] = line_in_gain
///       (ramdor Thetis networkproto1.c:597-599 case 11; same in mi0bot).
///       The SAME frame carries C2[6] = puresignal_run and the C4 PGA /
///       step-attenuator byte — those MUST survive any audio write.
///
/// The CRUX safety assertion: setting an HL2 audio field must NOT disturb
/// puresignal_run (C2[6]) or the C4 step-attenuator byte. PureSignal lives next
/// to audio on this frame; a clobber here would silently break PS.
/// </summary>
public class ControlFrameAudioEncoderTests
{
    private static ControlFrame.CcState Hermes() => new(
        VfoAHz: 14_200_000,
        Rate: HpsdrSampleRate.Rate48k,
        PreampOn: false,
        Atten: HpsdrAtten.Zero,
        RxAntenna: HpsdrAntenna.Ant1,
        Mox: false,
        EnableHl2BandVolts: false,
        Board: HpsdrBoardKind.Hermes,
        DriveLevel: 0x80);

    private static ControlFrame.CcState Hl2(bool ps = false) => new(
        VfoAHz: 14_200_000,
        Rate: HpsdrSampleRate.Rate48k,
        PreampOn: false,
        Atten: HpsdrAtten.Zero,
        RxAntenna: HpsdrAntenna.Ant1,
        Mox: false,
        EnableHl2BandVolts: false,
        Board: HpsdrBoardKind.HermesLite2,
        PsEnabled: ps);

    private static byte[] Frame(ControlFrame.CcRegister reg, in ControlFrame.CcState s)
    {
        Span<byte> cc = stackalloc byte[5];
        ControlFrame.WriteCcBytes(cc, reg, s);
        return cc.ToArray();
    }

    // ---- ATU auto-tune-start bit (C2[4]) on the 0x12 DriveFilter frame ------
    // Register 0x09 bit [20] ("Tune request") per the HL2 protocol doc; the
    // Apollo/Alex auto-tune bit on Hermes-class boards.

    [Fact]
    public void DriveFilter_AtuTune_SetsC2Bit4_Hermes()
    {
        var cc = Frame(ControlFrame.CcRegister.DriveFilter, Hermes() with { AtuTune = true });
        Assert.Equal(0x12, cc[0]);
        Assert.Equal(0x80, cc[1]);          // drive byte unchanged
        Assert.Equal(0x10, cc[2] & 0x10);   // C2[4] asserted
    }

    [Fact]
    public void DriveFilter_AtuTune_SetsC2Bit4_Hl2()
    {
        // HL2 carries PA-enable on C2[3] only while MOX; ATU bit is independent.
        var cc = Frame(ControlFrame.CcRegister.DriveFilter, Hl2() with { AtuTune = true });
        Assert.Equal(0x10, cc[2] & 0x10);   // C2[4] asserted
        Assert.Equal(0x00, cc[2] & 0x08);   // PA-enable not set (no MOX)
    }

    [Fact]
    public void DriveFilter_AtuTuneOff_IsByteIdentical()
    {
        // Default (AtuTune=false) must leave the 0x12 tail untouched.
        var cc = Frame(ControlFrame.CcRegister.DriveFilter, Hermes());
        Assert.Equal(0x00, cc[2] & 0x10);
    }

    // ---- (a) Hermes-class codec — 0x12 frame mic_boost / mic_linein --------

    [Fact]
    public void DriveFilter_Hermes_DefaultAudio_IsByteIdentical()
    {
        // No audio fields set → C2/C3/C4 all zero (the historical 0x12 tail).
        var cc = Frame(ControlFrame.CcRegister.DriveFilter, Hermes());
        Assert.Equal(0x12, cc[0]);
        Assert.Equal(0x80, cc[1]); // drive byte
        Assert.Equal(0x00, cc[2]);
        Assert.Equal(0x00, cc[3]);
        Assert.Equal(0x00, cc[4]);
    }

    [Theory]
    [InlineData(false, false, 0x00)]
    [InlineData(true,  false, 0x01)] // mic_boost → C2[0]
    [InlineData(false, true,  0x02)] // mic_linein → C2[1]
    [InlineData(true,  true,  0x03)] // both
    public void DriveFilter_Hermes_MicBits_LandInC2(bool boost, bool lineIn, byte expectedC2)
    {
        var cc = Frame(ControlFrame.CcRegister.DriveFilter,
            Hermes() with { MicBoost = boost, MicLineIn = lineIn });
        Assert.Equal(expectedC2, cc[2]);
        // Drive byte untouched.
        Assert.Equal(0x80, cc[1]);
    }

    [Fact]
    public void DriveFilter_Hl2_DoesNotEmitCodecMicBits()
    {
        // HL2 has no stream codec — mic_boost/linein must NOT bleed onto the
        // 0x12 frame (they ride the 0x14 frame instead). C2 stays at the HL2
        // baseline (PA-enable only when MOX; here RX so 0).
        var cc = Frame(ControlFrame.CcRegister.DriveFilter,
            Hl2() with { MicBoost = true, MicLineIn = true });
        Assert.Equal(0x00, cc[2]);
    }

    [Fact]
    public void DriveFilter_Hl2_PaEnableBit_StillSetOnMox()
    {
        // Regression guard: the HL2 PA-enable C2[3] path must keep working with
        // the new codec-board branch added alongside it.
        var cc = Frame(ControlFrame.CcRegister.DriveFilter, Hl2() with { Mox = true });
        Assert.Equal(0x08, cc[2] & 0x08);
    }

    // ---- (b) HL2 0x14 frame — mic_trs / mic_bias / line_in_gain ------------

    [Fact]
    public void Attenuator_Hl2_DefaultAudio_IsByteIdentical()
    {
        // No audio set: C1=0, C2=0 (no PS), C3=0, C4 = 0x40 | (60-0) = 0x7C.
        // This is exactly today's WriteAttenuatorPayload output.
        var cc = Frame(ControlFrame.CcRegister.Attenuator, Hl2());
        Assert.Equal(0x14, cc[0]);
        Assert.Equal(0x00, cc[1]);
        Assert.Equal(0x00, cc[2]);
        Assert.Equal(0x00, cc[3]);
        Assert.Equal(0x7C, cc[4]); // 0x40 | 60
    }

    [Theory]
    [InlineData(false, false, 0x00)]
    [InlineData(true,  false, 0x10)] // mic_trs (balanced) → C1[4]
    [InlineData(false, true,  0x20)] // mic_bias → C1[5]
    [InlineData(true,  true,  0x30)] // both
    public void Attenuator_Hl2_MicTrsBias_LandInC1(bool trs, bool bias, byte expectedC1)
    {
        var cc = Frame(ControlFrame.CcRegister.Attenuator,
            Hl2() with { MicTrs = trs, MicBias = bias });
        Assert.Equal(expectedC1, cc[1]); // C1 = cc[1]
        // mic_ptt (C1[6]) is a PTT-IN concern, never written here.
        Assert.Equal(0x00, cc[1] & (1 << 6));
    }

    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(1, 0x01)]
    [InlineData(31, 0x1F)]
    [InlineData(63, 0x1F)] // clamps to the 5-bit field
    public void Attenuator_Hl2_LineInGain_LandsInC2LowFiveBits(int gain, byte expectedLow)
    {
        var cc = Frame(ControlFrame.CcRegister.Attenuator,
            Hl2() with { LineInGain = (byte)gain });
        Assert.Equal(expectedLow, (byte)(cc[2] & 0x1F));
        // No PS → C2[6] stays clear; line_in_gain must not bleed into it.
        Assert.Equal(0x00, cc[2] & (1 << 6));
    }

    // ---- (c) ANAN-10E (HermesII) line-in — issue #667 ----------------------

    private static ControlFrame.CcState Hermes2() =>
        Hermes() with { Board = HpsdrBoardKind.HermesII };

    [Fact]
    public void DriveFilter_Anan10E_LineInSelect_LandsInC2Bit1()
    {
        // 10E RadioLineIn → mic_linein select on the 0x12 frame C2[1].
        var cc = Frame(ControlFrame.CcRegister.DriveFilter, Hermes2() with { MicLineIn = true });
        Assert.Equal(0x12, cc[0]);
        Assert.Equal(0x02, cc[2] & 0x02);
    }

    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(17, 0x11)]
    [InlineData(31, 0x1F)]
    [InlineData(63, 0x1F)] // clamps to the 5-bit field
    public void Attenuator_Anan10E_LineInGain_LandsInC2LowFiveBits(int gain, byte expectedLow)
    {
        // 10E line-in gain rides the 0x14 frame C2[4:0], same layout as HL2.
        var cc = Frame(ControlFrame.CcRegister.Attenuator, Hermes2() with { LineInGain = (byte)gain });
        Assert.Equal(0x14, cc[0]);
        Assert.Equal(expectedLow, (byte)(cc[2] & 0x1F));
        // The 10E does NOT carry puresignal_run here — C2[6] stays clear.
        Assert.Equal(0x00, cc[2] & (1 << 6));
    }

    [Fact]
    public void Attenuator_NonAnan10E_LineInGain_IsNotEmitted()
    {
        // Board-gating: a pure Hermes (non-HL2, non-10E) with a stale LineInGain
        // must NOT emit it — C2 stays zero, byte-identical to today. Proves the
        // 0x14 gain change is confined to the 10E and never moves another board.
        var cc = Frame(ControlFrame.CcRegister.Attenuator, Hermes() with { LineInGain = 0x1F });
        Assert.Equal(0x00, cc[2]);
    }

    // ---- CRUX: audio writes must NOT disturb PureSignal or step-atten ------

    [Fact]
    public void Attenuator_Hl2_AudioSet_PreservesPureSignalRun()
    {
        // PS armed + ALL audio fields set. puresignal_run (C2[6]) MUST survive
        // — this is the load-bearing safety property of the read-modify-write.
        var s = Hl2(ps: true) with
        {
            MicTrs = true,
            MicBias = true,
            LineInGain = 0x15, // 10101
        };
        var cc = Frame(ControlFrame.CcRegister.Attenuator, s);

        // PS bit survives.
        Assert.Equal(1 << 6, cc[2] & (1 << 6));
        // line_in_gain occupies the low 5 bits alongside it.
        Assert.Equal(0x15, cc[2] & 0x1F);
        // Full C2 = line_in_gain | PS bit = 0x15 | 0x40 = 0x55.
        Assert.Equal(0x55, cc[2]);
        // mic_trs + mic_bias in C1.
        Assert.Equal(0x30, cc[1]);
    }

    [Fact]
    public void Attenuator_Hl2_AudioSet_PreservesStepAttenuatorByte()
    {
        // C4 step-attenuator byte (PGA select 0x40 | 60-Db) must be untouched
        // by an audio write. 20 dB atten → 0x40 | (60-20) = 0x68.
        var s = Hl2(ps: true) with
        {
            Atten = new HpsdrAtten(20),
            MicTrs = true,
            MicBias = true,
            LineInGain = 0x1F,
        };
        var cc = Frame(ControlFrame.CcRegister.Attenuator, s);
        Assert.Equal(0x68, cc[4]);
    }

    [Fact]
    public void Attenuator_Hl2_AudioSet_PreservesPsTxStepAtten_DuringMox()
    {
        // The HL2 PS auto-attenuate path (C4 = (31 - txAttn) | 0x40 during MOX)
        // must also survive an audio write. txAttn = 5 → 0x40 | (31-5) = 0x5A.
        var s = Hl2(ps: true) with
        {
            Mox = true,
            Hl2TxAttnDb = 5,
            MicTrs = true,
            LineInGain = 0x10,
        };
        var cc = Frame(ControlFrame.CcRegister.Attenuator, s);
        Assert.Equal(0x5A, cc[4]);
        // PS + audio still intact in C2.
        Assert.Equal(1 << 6, cc[2] & (1 << 6));
        Assert.Equal(0x10, cc[2] & 0x1F);
    }

    [Fact]
    public void Attenuator_NonHl2_AudioFields_DoNotTouchTheFrame()
    {
        // A Hermes board with the 0x14-frame audio fields set must NOT emit
        // mic_trs/bias/line_in_gain there — that frame's mic surface is HL2-
        // only. (Hermes mic bits ride the 0x12 frame instead.)
        var s = Hermes() with { MicTrs = true, MicBias = true, LineInGain = 0x1F };
        var cc = Frame(ControlFrame.CcRegister.Attenuator, s);
        Assert.Equal(0x00, cc[1]); // C1 — no mic bits
        Assert.Equal(0x00, cc[2] & 0x1F); // C2 — no line_in_gain
    }
}
