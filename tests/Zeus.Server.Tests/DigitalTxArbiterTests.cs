// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// DigitalTxArbiterTests — the single-owner interlock guarantee: FT8/FT4 and WSPR
// share MoxSource.Ft8, so only ONE may be armed at a time. Arming one MUST
// force-disarm the other (otherwise a slot-end MOX drop on the shared source
// truncates the sibling's live transmission, and both feed audio into TXA).

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Dsp.Ft8;
using Zeus.Server;

namespace Zeus.Server.Tests;

public class DigitalTxArbiterTests
{
    private static bool NoKey(bool on, out string? error) { error = null; return true; }

    private static Ft8TxService NewFt8(DigitalTxArbiter arb)
    {
        var svc = new Ft8TxService(
            NoKey, _ => { }, _ => { }, (_, _, _) => new float[960],
            () => DateTime.UtcNow, static (_, _) => Task.CompletedTask, NullLogger.Instance);
        svc.SetArbiter(arb);
        return svc;
    }

    private static WsprTxService NewWspr(DigitalTxArbiter arb)
    {
        var svc = new WsprTxService(
            NoKey, _ => { }, _ => { }, (_, _) => new float[960],
            () => DateTime.UtcNow, static (_, _) => Task.CompletedTask, () => 0.0, NullLogger.Instance);
        svc.SetArbiter(arb);
        return svc;
    }

    [Fact]
    public void ArmingWspr_ForceDisarmsFt8()
    {
        var arb = new DigitalTxArbiter();
        var ft8 = NewFt8(arb);
        var wspr = NewWspr(arb);

        ft8.SetArmed(true);
        Assert.True(ft8.Armed);

        wspr.SetSettings("KB2UKA", "FN12", 30, 1500, 1.0);
        wspr.SetArmed(true);

        Assert.True(wspr.Armed);
        Assert.False(ft8.Armed);   // FT8 force-disarmed by the arbiter
    }

    [Fact]
    public void ArmingFt8_ForceDisarmsWspr()
    {
        var arb = new DigitalTxArbiter();
        var ft8 = NewFt8(arb);
        var wspr = NewWspr(arb);

        wspr.SetSettings("KB2UKA", "FN12", 30, 1500, 1.0);
        wspr.SetArmed(true);
        Assert.True(wspr.Armed);

        ft8.SetArmed(true);

        Assert.True(ft8.Armed);
        Assert.False(wspr.Armed);  // WSPR force-disarmed by the arbiter
    }
}
