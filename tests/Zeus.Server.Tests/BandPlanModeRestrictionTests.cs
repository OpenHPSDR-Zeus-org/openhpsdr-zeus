// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.

using Xunit;
using Zeus.Contracts;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class BandPlanModeRestrictionTests
{
    // CwAndDigital was added for the IARU R1/R2 30m WARC allocation where
    // CW and narrowband digital share the band but phone is prohibited (#337).
    [Theory]
    [InlineData(RxMode.CWU, true)]
    [InlineData(RxMode.CWL, true)]
    [InlineData(RxMode.DIGU, true)]
    [InlineData(RxMode.DIGL, true)]
    [InlineData(RxMode.USB, false)]
    [InlineData(RxMode.LSB, false)]
    [InlineData(RxMode.AM, false)]
    [InlineData(RxMode.SAM, false)]
    [InlineData(RxMode.DSB, false)]
    [InlineData(RxMode.FM, false)]
    public void CwAndDigital_permits_cw_and_digital_blocks_phone(RxMode mode, bool expected)
    {
        Assert.Equal(expected, BandPlanService.ModeMatchesRestriction(mode, ModeRestriction.CwAndDigital));
    }

    // Sanity-check the pre-existing restrictions haven't regressed.
    [Theory]
    [InlineData(ModeRestriction.Any, RxMode.USB, true)]
    [InlineData(ModeRestriction.CwOnly, RxMode.CWU, true)]
    [InlineData(ModeRestriction.CwOnly, RxMode.DIGU, false)]
    [InlineData(ModeRestriction.PhoneOnly, RxMode.USB, true)]
    [InlineData(ModeRestriction.PhoneOnly, RxMode.CWU, false)]
    [InlineData(ModeRestriction.DigitalOnly, RxMode.DIGL, true)]
    [InlineData(ModeRestriction.DigitalOnly, RxMode.CWU, false)]
    public void Existing_restrictions_unchanged(ModeRestriction restriction, RxMode mode, bool expected)
    {
        Assert.Equal(expected, BandPlanService.ModeMatchesRestriction(mode, restriction));
    }
}
