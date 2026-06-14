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

    // ---- HL2 user GPIO (external-ports plan, Phase 5) — 0x14 frame C3[3:0] ----
    // Verified Thetis-mi0bot networkproto1.c:774 `C3 = user_dig_out & 0x0F`.
    // C3 is cc[3] of the WriteCcBytes output (C0=cc[0], C1=cc[1], ... C4=cc[4]).

    [Fact]
    public void Attenuator_Hl2_DefaultGpio_C3IsZero_ByteIdentical()
    {
        // Default UserDigOut=0 → C3 stays 0, byte-identical to today.
        var cc = Frame(ControlFrame.CcRegister.Attenuator, Hl2());
        Assert.Equal(0x00, cc[3]);
    }

    [Theory]
    [InlineData(0x0, 0x0)]
    [InlineData(0x1, 0x1)]
    [InlineData(0x5, 0x5)]
    [InlineData(0xF, 0xF)]
    [InlineData(0xFF, 0x0F)] // high bits masked off — only the low nibble lands
    public void Attenuator_Hl2_UserGpio_LandsInC3LowNibble(byte mask, byte expectedC3)
    {
        var cc = Frame(ControlFrame.CcRegister.Attenuator, Hl2() with { UserDigOut = mask });
        Assert.Equal(expectedC3, cc[3]);
        // GPIO never touches the high C3 bits.
        Assert.Equal(0x00, cc[3] & 0xF0);
    }

    [Fact]
    public void Attenuator_Hl2_UserGpio_PreservesPsAndAudioAndStepAtten()
    {
        // CRUX: GPIO write must NOT disturb puresignal_run (C2[6]), the audio
        // bits, or the C4 step-attenuator. GPIO is on C3 (cc[3]); none of those
        // co-tenants live in C3, so the read-modify-write keeps them intact.
        var s = Hl2(ps: true) with
        {
            Atten = new HpsdrAtten(20),
            MicTrs = true,
            MicBias = true,
            LineInGain = 0x15,
            UserDigOut = 0x0A,
        };
        var cc = Frame(ControlFrame.CcRegister.Attenuator, s);
        Assert.Equal(0x0A, cc[3]);            // GPIO in C3
        Assert.Equal(1 << 6, cc[2] & (1 << 6)); // puresignal_run survives
        Assert.Equal(0x15, cc[2] & 0x1F);     // line_in_gain survives
        Assert.Equal(0x30, cc[1]);            // mic_trs + mic_bias survive
        Assert.Equal(0x68, cc[4]);            // C4 step-att = 0x40|(60-20)
    }

    [Fact]
    public void Attenuator_NonHl2_UserGpio_DoesNotTouchC3()
    {
        // A non-HL2 board with UserDigOut set must NOT emit it — the GPIO surface
        // is HL2-only. C3 on the 0x14 frame stays 0 for Hermes.
        var cc = Frame(ControlFrame.CcRegister.Attenuator, Hermes() with { UserDigOut = 0x0F });
        Assert.Equal(0x00, cc[3]);
    }
}
