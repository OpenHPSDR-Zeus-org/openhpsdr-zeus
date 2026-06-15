// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class NativeMicCaptureSanitizerTests
{
    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(float.PositiveInfinity, 0f)]
    [InlineData(float.NegativeInfinity, 0f)]
    [InlineData(1.5f, 1f)]
    [InlineData(-2f, -1f)]
    [InlineData(0.25f, 0.25f)]
    public void SanitizeCapturedSample_ClampsOverrangeAndZerosNonFinite(float sample, float expected)
    {
        Assert.Equal(expected, NativeMicCapture.SanitizeCapturedSample(sample));
    }

    [Fact]
    public void DownmixCapturedFrame_SanitizesChannelsBeforeAveraging()
    {
        var interleaved = new[]
        {
            float.NaN, 0.5f,
            float.PositiveInfinity, 1.5f,
            float.NegativeInfinity, -2f,
        };
        const int channels = 2;
        const float inverseChannels = 0.5f;

        Assert.Equal(0.25f, NativeMicCapture.DownmixCapturedFrame(interleaved, 0, channels, inverseChannels));
        Assert.Equal(0.5f, NativeMicCapture.DownmixCapturedFrame(interleaved, 2, channels, inverseChannels));
        Assert.Equal(-0.5f, NativeMicCapture.DownmixCapturedFrame(interleaved, 4, channels, inverseChannels));
    }
}
