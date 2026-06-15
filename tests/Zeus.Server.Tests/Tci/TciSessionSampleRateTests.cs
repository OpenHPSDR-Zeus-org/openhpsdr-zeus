// SPDX-License-Identifier: GPL-2.0-or-later

using Zeus.Server.Tci;

namespace Zeus.Server.Tests.Tci;

public class TciSessionSampleRateTests
{
    [Theory]
    [InlineData(1, 48_000)]
    [InlineData(48_000, 48_000)]
    [InlineData(384_000, 384_000)]
    [InlineData(768_000, 768_000)]
    [InlineData(1_536_000, 1_536_000)]
    [InlineData(2_000_000, 1_536_000)]
    public void ClampIqSampleRateRequest_CoversFullProtocol2WidebandLadder(int requested, int expected)
    {
        Assert.Equal(expected, TciSession.ClampIqSampleRateRequest(requested));
    }
}
