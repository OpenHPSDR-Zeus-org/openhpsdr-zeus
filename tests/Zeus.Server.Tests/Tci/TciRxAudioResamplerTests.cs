// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Zeus.Server;
using Zeus.Server.Tci;

namespace Zeus.Server.Tests.Tci;

public class TciRxAudioResamplerTests
{
    [Fact]
    public void Convert48kTo24k_PreservesSpeechBandTone()
    {
        var output = Convert(GenerateSine(48_000, 1_000, seconds: 0.20), 24_000);
        Assert.True(output.Length >= 4_700 && output.Length <= 4_900, $"length={output.Length}");

        var steady = output.AsSpan(600, output.Length - 1_200);
        Assert.InRange(Rms(steady), 0.55, 0.75);
        Assert.InRange(EstimateFrequency(steady, 24_000), 980, 1_020);
    }

    [Fact]
    public void Convert48kTo8k_AttenuatesAboveTargetNyquist()
    {
        var output = Convert(GenerateSine(48_000, 6_000, seconds: 0.30), 8_000);
        var steady = output.AsSpan(300, output.Length - 600);

        Assert.True(Rms(steady) < 0.08, $"RMS was {Rms(steady):F4}");
    }

    [Fact]
    public void BuildTciFrame_UsesRequestedAudioSampleRateAfterDownsample()
    {
        var output = Convert(GenerateSine(48_000, 1_000, seconds: 0.05), 24_000);

        var frame = TciStreamPayload.BuildAudioFromFloats(receiver: 0, sampleRate: 24_000, output);

        Assert.Equal(24_000u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal((uint)TciStreamType.RxAudioStream, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(24)));
        Assert.Equal((uint)(output.Length * 2), BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(20)));
    }

    private static float[] Convert(float[] input, int outputRate)
    {
        var resampler = new TciRxAudioResampler();
        var output = new List<float>();
        for (int pos = 0; pos < input.Length;)
        {
            int n = Math.Min(1024, input.Length - pos);
            output.AddRange(resampler.Convert(input.AsSpan(pos, n), 48_000, outputRate));
            pos += n;
        }
        output.AddRange(resampler.Convert(new float[WindowedSincKernel.Taps * 2], 48_000, outputRate));
        return output.ToArray();
    }

    private static float[] GenerateSine(int sampleRate, double hz, double seconds)
    {
        int count = (int)Math.Round(sampleRate * seconds);
        var samples = new float[count];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)Math.Sin(2.0 * Math.PI * hz * i / sampleRate);
        return samples;
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
            sum += samples[i] * samples[i];
        return Math.Sqrt(sum / samples.Length);
    }

    private static double EstimateFrequency(ReadOnlySpan<float> samples, int sampleRate)
    {
        int crossings = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] <= 0f && samples[i] > 0f)
                crossings++;
        }
        return crossings / (samples.Length / (double)sampleRate);
    }
}
