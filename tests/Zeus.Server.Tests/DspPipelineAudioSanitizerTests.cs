// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class DspPipelineAudioSanitizerTests
{
    private static float Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0.0;
        for (int i = 0; i < samples.Length; i++)
            sum += samples[i] * samples[i];
        return (float)Math.Sqrt(sum / samples.Length);
    }

    [Fact]
    public void SanitizeAudioBuffer_ClampsOverrangeAndZerosNonFiniteSamples()
    {
        float[] samples =
        {
            float.NaN,
            float.PositiveInfinity,
            float.NegativeInfinity,
            1.25f,
            -1.5f,
            0.125f,
        };

        DspPipelineService.SanitizeAudioBuffer(samples);

        Assert.Equal(0f, samples[0]);
        Assert.Equal(0f, samples[1]);
        Assert.Equal(0f, samples[2]);
        Assert.Equal(1f, samples[3]);
        Assert.Equal(-1f, samples[4]);
        Assert.Equal(0.125f, samples[5]);
    }

    [Fact]
    public void SanitizeDisplayBuffer_ReplacesOnlyNonFiniteBins()
    {
        float[] bins =
        {
            -140.5f,
            float.NaN,
            -73.25f,
            float.PositiveInfinity,
            12.5f,
            float.NegativeInfinity,
        };

        DspPipelineService.SanitizeDisplayBuffer(bins);

        Assert.Equal(-140.5f, bins[0]);
        Assert.Equal(-200f, bins[1]);
        Assert.Equal(-73.25f, bins[2]);
        Assert.Equal(-200f, bins[3]);
        Assert.Equal(12.5f, bins[4]);
        Assert.Equal(-200f, bins[5]);
    }

    [Fact]
    public void ApplyRxAudioLeveler_LiftsWeakAudioTowardSpeechLevel()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];

        for (int i = 0; i < 12; i++)
        {
            Array.Fill(block, 0.01f);
            DspPipelineService.ApplyRxAudioLeveler(block, ref state);
        }

        Assert.InRange(Rms(block), 0.10f, 0.15f);
        Assert.True(state.GainDb > 20.0);
    }

    [Fact]
    public void ApplyRxAudioLeveler_DoesNotLiftBelowGateSilence()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];
        Array.Fill(block, 0.00001f);

        DspPipelineService.ApplyRxAudioLeveler(block, ref state);

        Assert.InRange(Rms(block), 0.000009f, 0.000011f);
        Assert.Equal(0.0, state.GainDb);
    }

    [Fact]
    public void ApplyRxAudioLeveler_StrongSignalAfterWeakSignalDoesNotBlast()
    {
        var state = new DspPipelineService.RxAudioLevelerState();
        float[] block = new float[1024];

        for (int i = 0; i < 18; i++)
        {
            Array.Fill(block, 0.01f);
            DspPipelineService.ApplyRxAudioLeveler(block, ref state);
        }

        Assert.True(state.GainDb > 20.0);

        Array.Fill(block, 0.9f);
        DspPipelineService.ApplyRxAudioLeveler(block, ref state);

        Assert.InRange(Rms(block), 0.12f, 0.14f);
        Assert.InRange(state.GainDb, -18.0, -16.0);
    }
}
