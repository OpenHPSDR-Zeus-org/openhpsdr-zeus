// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Ft8SynthTests — the pure (always-run) tests assert the waveform shape with no
// native dependency: exact length, phase continuity (no key clicks), RMS, the
// start/end ramp, and a single-tone spectral check. The round-trip tests
// (Encode → Synth → Decode) SkippableFact-skip when the zeus_ft8 native lib
// isn't staged for the current RID, mirroring WsprDecoderTests.

using Zeus.Dsp.Ft8;

namespace Zeus.Dsp.Ft8.Tests;

public class Ft8SynthTests
{
    // ---- pure, always-run -----------------------------------------------------

    [Theory]
    [InlineData(48000, 79 * 7680)]   // FT8 @ 48 kHz (nsps = 7680) — matches the brief
    [InlineData(12000, 79 * 1920)]   // FT8 @ 12 kHz (nsps = 1920)
    public void Ft8_Output_HasExactLength(int sampleRate, int expected)
    {
        var tones = new byte[79];          // all tone 0 — pure carrier
        var audio = Ft8Synth.Synth(tones, Ft8Protocol.Ft8, 1500f, sampleRate);
        Assert.Equal(expected, audio.Length);
    }

    [Theory]
    [InlineData(48000, 105 * 2304)]  // FT4 @ 48 kHz (nsps = 2304)
    [InlineData(12000, 105 * 576)]   // FT4 @ 12 kHz (nsps = 576)
    public void Ft4_Output_HasExactLength(int sampleRate, int expected)
    {
        var tones = new byte[105];
        var audio = Ft8Synth.Synth(tones, Ft8Protocol.Ft4, 1500f, sampleRate);
        Assert.Equal(expected, audio.Length);
    }

    [Fact]
    public void WrongToneCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => Ft8Synth.Synth(new byte[10], Ft8Protocol.Ft8));
        Assert.Throws<ArgumentException>(() => Ft8Synth.Synth(new byte[79], Ft8Protocol.Ft4));
    }

    [Fact]
    public void PhaseIsContinuous_NoKeyClicks()
    {
        // A constant-envelope GFSK waveform changes slowly sample-to-sample: the
        // max step is bounded by 2*pi*fmax/fs * amplitude. The highest tone is
        // baseFreq + 7*6.25 ≈ 1544 Hz at 48 kHz → ~0.101 per sample at A=0.5.
        var tones = RampTones(79, 8);
        var audio = Ft8Synth.Synth(tones, Ft8Protocol.Ft8, 1500f, 48000, 0.5f);
        float maxStep = 0f;
        for (int i = 1; i < audio.Length; i++)
            maxStep = MathF.Max(maxStep, MathF.Abs(audio[i] - audio[i - 1]));
        Assert.True(maxStep < 0.15f, $"max sample step {maxStep} suggests a discontinuity/click");
    }

    [Fact]
    public void Rms_IsApproximatelyAmplitudeOverRootTwo()
    {
        var tones = RampTones(79, 8);
        const float amp = 0.5f;
        var audio = Ft8Synth.Synth(tones, Ft8Protocol.Ft8, 1500f, 48000, amp);
        double sumSq = 0;
        foreach (var s in audio) sumSq += (double)s * s;
        double rms = Math.Sqrt(sumSq / audio.Length);
        double expected = amp / Math.Sqrt(2.0);
        Assert.True(Math.Abs(rms - expected) < 0.02, $"RMS {rms:F4} vs expected {expected:F4}");
    }

    [Fact]
    public void StartAndEnd_AreRampedToZero()
    {
        var tones = RampTones(79, 8);
        var audio = Ft8Synth.Synth(tones, Ft8Protocol.Ft8, 1500f, 48000, 0.5f);
        // The raised-cosine envelope is exactly 0 at the very first/last sample.
        Assert.True(MathF.Abs(audio[0]) < 1e-6f);
        Assert.True(MathF.Abs(audio[^1]) < 1e-6f);
    }

    [Fact]
    public void SingleTone_ConcentratesEnergyAtExpectedFrequency()
    {
        // All tone 4 ⇒ a steady tone at baseFreq + 4*6.25 = 1525 Hz. Goertzel
        // power there must dominate an off-tone bin.
        const int fs = 48000;
        const float baseHz = 1500f;
        var tones = new byte[79];
        for (int i = 0; i < tones.Length; i++) tones[i] = 4;
        var audio = Ft8Synth.Synth(tones, Ft8Protocol.Ft8, baseHz, fs, 0.5f);

        // Use the steady middle (skip the ramp + first/last symbol shaping).
        int start = audio.Length / 4, len = audio.Length / 2;
        double onTone = GoertzelPower(audio, start, len, 1525.0, fs);
        double offTone = GoertzelPower(audio, start, len, 1000.0, fs);
        Assert.True(onTone > offTone * 50, $"on-tone {onTone:E2} not dominant over off-tone {offTone:E2}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(7)]
    public void SteadyTone_LandsEnergyInTheExpectedFskBin(int tone)
    {
        // Each FT8 tone index n maps to baseFreq + n*6.25 Hz. Synthesizing a steady
        // tone must land its energy in THAT bin (not just "some single bin"), so a
        // tone→frequency mapping regression is caught even when native is absent.
        const int fs = 48000;
        const float baseHz = 1500f;
        var tones = new byte[79];
        for (int i = 0; i < tones.Length; i++) tones[i] = (byte)tone;
        var audio = Ft8Synth.Synth(tones, Ft8Protocol.Ft8, baseHz, fs, 0.5f);

        double expectedHz = baseHz + tone * 6.25;
        int start = audio.Length / 4, len = audio.Length / 2;
        double onTone = GoertzelPower(audio, start, len, expectedHz, fs);
        double neighbor = GoertzelPower(audio, start, len, expectedHz + 6.25, fs);
        double offTone = GoertzelPower(audio, start, len, 1000.0, fs);
        Assert.True(onTone > neighbor * 5, $"tone {tone}: on {onTone:E2} vs neighbor {neighbor:E2}");
        Assert.True(onTone > offTone * 50, $"tone {tone}: on {onTone:E2} vs off {offTone:E2}");
    }

    // ---- round-trip (native-gated) -------------------------------------------

    [SkippableFact]
    public void RoundTrip_Ft8_EncodeSynthDecode_RecoversMessage()
    {
        Skip.IfNot(Ft8Decoder.IsAvailable, "zeus_ft8 native library not staged for this RID");
        AssertRoundTrip("CQ KB2UKA FN12", Ft8Protocol.Ft8, 15.0);
    }

    [SkippableFact]
    public void RoundTrip_Ft4_EncodeSynthDecode_RecoversMessage()
    {
        Skip.IfNot(Ft8Decoder.IsAvailable, "zeus_ft8 native library not staged for this RID");
        AssertRoundTrip("CQ KB2UKA FN12", Ft8Protocol.Ft4, 7.5);
    }

    private static void AssertRoundTrip(string message, Ft8Protocol proto, double slotSeconds)
    {
        byte[]? tones = Ft8Decoder.Encode(message, proto);
        Assert.NotNull(tones);

        int fs = Ft8Decoder.SampleRate;                       // 12 kHz
        float[] wave = Ft8Synth.Synth(tones!, proto, 1500f, fs);

        // Place the waveform 0.5 s into a full UTC slot, the rest silence. FT4's
        // sync is far more DT-sensitive than FT8 (tolerance ~±1 s vs ±2.5 s), so
        // keep the offset modest; both protocols decode cleanly at 0.5 s.
        int slot = (int)(slotSeconds * fs);
        int lead = fs / 2;                                     // 0.5 s lead
        var buf = new float[slot];
        Array.Copy(wave, 0, buf, lead, Math.Min(wave.Length, slot - lead));

        using var decoder = new Ft8Decoder();
        var decodes = decoder.Decode(buf, proto, passes: 1);
        Assert.Contains(decodes, d => d.Text.Trim() == message);
    }

    // ---- helpers -------------------------------------------------------------

    private static byte[] RampTones(int n, int modulo)
    {
        var t = new byte[n];
        for (int i = 0; i < n; i++) t[i] = (byte)(i % modulo);
        return t;
    }

    private static double GoertzelPower(float[] x, int start, int len, double freq, int fs)
    {
        double w = 2.0 * Math.PI * freq / fs;
        double cw = Math.Cos(w), coeff = 2.0 * cw;
        double s0 = 0, s1 = 0, s2 = 0;
        for (int i = 0; i < len; i++)
        {
            s0 = x[start + i] + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return s1 * s1 + s2 * s2 - coeff * s1 * s2;
    }
}
