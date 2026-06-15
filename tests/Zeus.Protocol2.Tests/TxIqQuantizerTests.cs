// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Protocol2.Tests;

public class TxIqQuantizerTests
{
    [Theory]
    [InlineData(1.0f, 8_388_607)]
    [InlineData(2.5f, 8_388_607)]
    [InlineData(-1.0f, -8_388_607)]
    [InlineData(-2.5f, -8_388_607)]
    [InlineData(0.5f, 4_194_304)]
    public void Int24Clamp_ClampsToSigned24BitDomain(float sample, int expected)
    {
        Assert.Equal(expected, Protocol2Client.Int24Clamp(sample));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Int24Clamp_ZerosNonFiniteSamples(float sample)
    {
        Assert.Equal(0, Protocol2Client.Int24Clamp(sample));
    }
}
