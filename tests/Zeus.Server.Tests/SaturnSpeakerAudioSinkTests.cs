// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Server;

namespace Zeus.Server.Tests;

public sealed class SaturnSpeakerAudioSinkTests
{
    [Theory]
    [InlineData(float.NaN, 0)]
    [InlineData(float.NegativeInfinity, -32768)]
    [InlineData(-2.0f, -32768)]
    [InlineData(-1.0f, -32768)]
    [InlineData(-0.5f, -16384)]
    [InlineData(0.0f, 0)]
    [InlineData(0.5f, 16384)]
    [InlineData(1.0f, 32767)]
    [InlineData(2.0f, 32767)]
    [InlineData(float.PositiveInfinity, 32767)]
    public void FloatToPcm16_ClampsToFullSignedAudioRange(float sample, short expected)
    {
        Assert.Equal(expected, SaturnSpeakerAudioSink.FloatToPcm16(sample));
    }
}
