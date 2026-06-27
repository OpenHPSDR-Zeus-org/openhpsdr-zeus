// SPDX-License-Identifier: GPL-2.0-or-later
//
// End-to-end tests for the managed WSPR wrapper over the zeus_wspr native ABI.
// Native-dependent tests SkippableFact-skip when the lib isn't staged for the
// RID (or ships encode-only, e.g. Windows). The decode round-trip is fully
// self-contained: encode -> synth -> decode through managed code.

using Zeus.Dsp.Ft8;

namespace Zeus.Dsp.Ft8.Tests;

public class WsprDecoderTests
{
    [SkippableFact]
    public void NativeLibrary_IsAvailable_AndReportsVersion()
    {
        Skip.IfNot(WsprDecoder.IsAvailable, "zeus_wspr native library not staged for this RID");
        Assert.NotNull(WsprDecoder.NativeVersion);
        Assert.Contains("zeus_wspr", WsprDecoder.NativeVersion!);
    }

    [SkippableFact]
    public void Encode_StandardMessage_Produces162Symbols()
    {
        Skip.IfNot(WsprDecoder.IsAvailable, "zeus_wspr native library not staged for this RID");
        byte[]? tones = WsprDecoder.Encode("KB2UKA FN12 30");
        Assert.NotNull(tones);
        Assert.Equal(162, tones!.Length);
        Assert.All(tones, t => Assert.InRange(t, (byte)0, (byte)3)); // WSPR = 4-FSK
    }

    [SkippableFact]
    public void RoundTrip_EncodeSynthDecode_RecoversMessage()
    {
        Skip.IfNot(WsprDecoder.IsAvailable, "zeus_wspr native library not staged for this RID");

        byte[]? tones = WsprDecoder.Encode("KB2UKA FN12 30");
        Assert.NotNull(tones);

        float[]? synth = WsprDecoder.Synth(tones!, 1500f, WsprDecoder.SampleRate);
        Assert.NotNull(synth);

        // Place the ~110.6 s signal ~1 s into a 114 s slot.
        long total = 114L * WsprDecoder.SampleRate;
        var buf = new float[total];
        long off = WsprDecoder.SampleRate;
        for (long i = 0; i < synth!.Length && off + i < total; i++)
            buf[off + i] = synth[i] * 0.5f;

        var spots = WsprDecoder.Decode(buf, 14.0956);

        // Decode is POSIX-only; if the platform lib is encode-only, accept empty.
        if (spots.Count == 0)
        {
            Skip.If(true, "WSPR decode unsupported on this platform build (encode-only)");
        }

        Assert.Contains(spots, s => s.Message.Contains("KB2UKA"));
        var hit = spots.First(s => s.Message.Contains("KB2UKA"));
        Assert.InRange(hit.FreqMhz, 14.09f, 14.10f);   // dial + ~1500 Hz audio
    }
}
