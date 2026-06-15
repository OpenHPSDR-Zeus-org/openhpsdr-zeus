// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Server;

namespace Zeus.Server.Tests;

public class TxMicBlockResamplerTests
{
    [Fact]
    public void Accept48k_ReblocksWithoutChangingSamples()
    {
        var blocks = new List<float[]>();
        var resampler = new TxMicBlockResampler(block => blocks.Add(block.ToArray()));
        var first = Enumerable.Range(0, 480).Select(i => i / 1000f).ToArray();
        var second = Enumerable.Range(480, 480).Select(i => i / 1000f).ToArray();

        var r1 = resampler.Accept(first, 48_000);
        Assert.Empty(blocks);
        var r2 = resampler.Accept(second, 48_000);

        Assert.Equal(480, r1.OutputSamplesGenerated);
        Assert.Equal(0, r1.OutputSamplesEmitted);
        Assert.Equal(480, r2.OutputSamplesGenerated);
        Assert.Equal(TxMicBlockResampler.OutputBlockSamples, r2.OutputSamplesEmitted);
        Assert.Single(blocks);
        var expected = first.Concat(second).ToArray();
        Assert.Equal(expected, blocks[0]);
    }

    [Fact]
    public void FlushZeroPadded_EmitsFinalPartialBlock()
    {
        var blocks = new List<float[]>();
        var resampler = new TxMicBlockResampler(block => blocks.Add(block.ToArray()));
        resampler.Accept(new[] { 0.25f, -0.25f }, 48_000);

        resampler.FlushZeroPadded();

        Assert.Single(blocks);
        Assert.Equal(0.25f, blocks[0][0]);
        Assert.Equal(-0.25f, blocks[0][1]);
        Assert.All(blocks[0].Skip(2), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Resample44k1To48k_PreservesSpeechBandToneAndDuration()
    {
        var output = Convert(GenerateSine(44_100, 1_000, seconds: 0.10), 44_100, chunk: 441);

        Assert.Equal(4_800, output.Length);
        var steady = output.AsSpan(960, output.Length - 1_920);
        Assert.InRange(Rms(steady), 0.55, 0.75);
        Assert.InRange(EstimateFrequency(steady), 980, 1_020);
    }

    [Fact]
    public void Downsample96kTo48k_AntiAliasesAboveOutputNyquist()
    {
        var output = Convert(GenerateSine(96_000, 30_000, seconds: 0.20), 96_000, chunk: 1_920);

        var steady = output.AsSpan(960, output.Length - 1_920);
        Assert.True(Rms(steady) < 0.08, $"RMS was {Rms(steady):F4}");
    }

    [Fact]
    public void Upsample24kTo48k_ProducesExpectedDuration()
    {
        var output = Convert(GenerateSine(24_000, 1_200, seconds: 0.20), 24_000, chunk: 480);

        Assert.Equal(9_600, output.Length);
        var steady = output.AsSpan(960, output.Length - 1_920);
        Assert.InRange(EstimateFrequency(steady), 1_175, 1_225);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7_999)]
    [InlineData(768_001)]
    public void Accept_RejectsUnsupportedSampleRates(int sampleRate)
    {
        var resampler = new TxMicBlockResampler(_ => { });
        Assert.Throws<ArgumentOutOfRangeException>(() => resampler.Accept(new[] { 0f }, sampleRate));
    }

    private static float[] Convert(float[] input, int sampleRate, int chunk)
    {
        var blocks = new List<float[]>();
        var resampler = new TxMicBlockResampler(block => blocks.Add(block.ToArray()));
        for (int pos = 0; pos < input.Length;)
        {
            int n = Math.Min(chunk, input.Length - pos);
            resampler.Accept(input.AsSpan(pos, n), sampleRate);
            pos += n;
        }
        resampler.FlushZeroPadded();
        return blocks.SelectMany(static b => b).ToArray();
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

    private static double EstimateFrequency(ReadOnlySpan<float> samples)
    {
        int crossings = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] <= 0f && samples[i] > 0f)
                crossings++;
        }
        return crossings / (samples.Length / (double)TxMicBlockResampler.OutputSampleRate);
    }
}
