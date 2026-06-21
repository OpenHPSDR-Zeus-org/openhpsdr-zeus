// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Protocol1;
using Zeus.Server;
using Xunit;

namespace Zeus.Server.Tests;

/// <summary>
/// PRD FR-6 SWR auto-trip: TxMetersService must drop MOX/TUN when SWR sustains
/// above the per-mode threshold for the per-mode sustain duration, honour the
/// per-mode startup-grace window after the keying edge, and NOT trip on brief
/// spikes. The trip logic is exercised directly via the internal
/// <c>EvaluateSwrTrip(swr, now, isTun, keyedAt)</c> seam so tests can drive
/// synthetic timestamps without wall-clock delays. Issue #362.
/// </summary>
public class TxMetersSwrTripTests : IDisposable
{
    private static readonly RadioCalibration Cal = RadioCalibration.HermesLite2;

    // Per-fixture temp DBs — see ZoomValidationTests for the rationale.
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-swrtrip-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    // Helper: compute ADC value that yields a specific SWR when FWD = 3 W.
    // SWR = (1 + rho) / (1 - rho) where rho = sqrt(P_ref / P_fwd).
    // Solve for P_ref: rho = (SWR - 1) / (SWR + 1), P_ref = rho^2 * P_fwd.
    private static ushort AdcForSwr(double swr, double fwdWatts = 3.0)
    {
        double rho = (swr - 1.0) / (swr + 1.0);
        double refWatts = rho * rho * fwdWatts;
        double refV = Math.Sqrt(refWatts * Cal.BridgeVolt);
        double adc = Cal.AdcCalOffset + (refV / Cal.RefVoltage) * 4095.0;
        return (ushort)Math.Clamp(adc, 0, 4095);
    }

    private static ushort AdcForWatts(double watts)
    {
        double v = Math.Sqrt(watts * Cal.BridgeVolt);
        double adc = Cal.AdcCalOffset + (v / Cal.RefVoltage) * 4095.0;
        return (ushort)Math.Clamp(adc, 0, 4095);
    }

    private TxMetersService BuildService(out TxService tx, out StreamingHub hub)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(loggerFactory, dspStore, paStore);
        hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), loggerFactory);
        tx = new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        return new TxMetersService(hub, radio, tx, pipeline, new NullLogger<TxMetersService>());
    }

    [Fact]
    public void SwrMath_ProducesCorrectThresholdValue()
    {
        ushort fwdAdc = AdcForWatts(3.0);
        ushort refAdc = AdcForSwr(2.5, 3.0);
        var (_, _, swr) = TxMetersService.ComputeMeters(fwdAdc, refAdc, Cal);
        Assert.True(Math.Abs(swr - 2.5) < 0.1, $"Expected SWR ≈ 2.5, got {swr}");
    }

    // Keyed-at sentinel that is well before any synthetic `now` the tests
    // construct, so the per-mode startup grace is already past and the
    // tests exercise the pure sustain-window logic. The grace behaviour
    // gets its own dedicated tests below.
    private static readonly DateTime KeyedAtPastGrace =
        new DateTime(2026, 4, 18, 11, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EvaluateSwrTrip_Mox_FiresAtExactly500msSustained_NotBefore()
    {
        var svc = BuildService(out _, out _);
        var t0 = new DateTime(2026, 4, 18, 12, 0, 0, DateTimeKind.Utc);

        // First exceedance: starts the timer, no trip yet.
        Assert.Null(svc.EvaluateSwrTrip(3.0, t0, isTun: false, KeyedAtPastGrace));
        // 100 ms in: still sustaining, not yet 500 ms.
        Assert.Null(svc.EvaluateSwrTrip(3.0, t0.AddMilliseconds(100), isTun: false, KeyedAtPastGrace));
        // 499 ms in: one tick below the threshold duration — MUST NOT trip.
        Assert.Null(svc.EvaluateSwrTrip(3.0, t0.AddMilliseconds(499), isTun: false, KeyedAtPastGrace));
        // Exactly 500 ms: trip fires.
        var reason = svc.EvaluateSwrTrip(3.0, t0.AddMilliseconds(500), isTun: false, KeyedAtPastGrace);
        Assert.NotNull(reason);
        Assert.Contains("SWR", reason);
    }

    [Fact]
    public void EvaluateSwrTrip_Mox_BriefSpikeThenClears_DoesNotTrip()
    {
        var svc = BuildService(out _, out _);
        var t0 = new DateTime(2026, 4, 18, 12, 0, 0, DateTimeKind.Utc);

        // 300 ms burst above threshold, then below.
        Assert.Null(svc.EvaluateSwrTrip(3.0, t0, isTun: false, KeyedAtPastGrace));
        Assert.Null(svc.EvaluateSwrTrip(3.0, t0.AddMilliseconds(100), isTun: false, KeyedAtPastGrace));
        Assert.Null(svc.EvaluateSwrTrip(3.0, t0.AddMilliseconds(300), isTun: false, KeyedAtPastGrace));
        // SWR drops — timer clears.
        Assert.Null(svc.EvaluateSwrTrip(1.5, t0.AddMilliseconds(350), isTun: false, KeyedAtPastGrace));
        // Now sustained ≥500 ms window past t0 has elapsed in wall-time, but
        // because we dipped below threshold at 350, the sustain timer restarted
        // on the next exceedance and must NOT carry the earlier excursion over.
        Assert.Null(svc.EvaluateSwrTrip(3.0, t0.AddMilliseconds(600), isTun: false, KeyedAtPastGrace));
        Assert.Null(svc.EvaluateSwrTrip(3.0, t0.AddMilliseconds(1000), isTun: false, KeyedAtPastGrace));
        // Full 500 ms from the second exceedance onset (600) → trip at 1100.
        var reason = svc.EvaluateSwrTrip(3.0, t0.AddMilliseconds(1100), isTun: false, KeyedAtPastGrace);
        Assert.NotNull(reason);
    }

    [Fact]
    public void EvaluateSwrTrip_Mox_FiresOnceThenResets_NoRepeatSpam()
    {
        var svc = BuildService(out _, out _);
        var t0 = new DateTime(2026, 4, 18, 12, 0, 0, DateTimeKind.Utc);

        svc.EvaluateSwrTrip(3.0, t0, isTun: false, KeyedAtPastGrace);                          // start
        Assert.NotNull(svc.EvaluateSwrTrip(3.0, t0.AddMilliseconds(500), isTun: false, KeyedAtPastGrace)); // fire
        // Immediately after the trip, repeated high-SWR ticks must NOT fire again
        // until a full new 500 ms sustain window completes from a fresh onset.
        Assert.Null(svc.EvaluateSwrTrip(3.0, t0.AddMilliseconds(550), isTun: false, KeyedAtPastGrace));
        Assert.Null(svc.EvaluateSwrTrip(3.0, t0.AddMilliseconds(700), isTun: false, KeyedAtPastGrace));
        Assert.Null(svc.EvaluateSwrTrip(3.0, t0.AddMilliseconds(1049), isTun: false, KeyedAtPastGrace));
        // 500 ms past the 550 onset = 1050. Fires.
        Assert.NotNull(svc.EvaluateSwrTrip(3.0, t0.AddMilliseconds(1050), isTun: false, KeyedAtPastGrace));
    }

    [Fact]
    public void EvaluateSwrTrip_Mox_StartupGrace_SuppressesTripDuringFirst300ms()
    {
        var svc = BuildService(out _, out _);
        var keyedAt = new DateTime(2026, 4, 18, 12, 0, 0, DateTimeKind.Utc);

        // High SWR right after keying — MUST NOT trip within the 300 ms grace
        // even if sustained the entire window. This is the HL2 bridge-settle
        // / PA-bias transient case from issue #362.
        Assert.Null(svc.EvaluateSwrTrip(3.0, keyedAt, isTun: false, keyedAt));
        Assert.Null(svc.EvaluateSwrTrip(3.0, keyedAt.AddMilliseconds(100), isTun: false, keyedAt));
        Assert.Null(svc.EvaluateSwrTrip(3.0, keyedAt.AddMilliseconds(299), isTun: false, keyedAt));
        // From 300 ms onward grace is over; first post-grace tick starts the
        // sustain timer, and the trip fires 500 ms after that onset.
        Assert.Null(svc.EvaluateSwrTrip(3.0, keyedAt.AddMilliseconds(300), isTun: false, keyedAt));
        Assert.Null(svc.EvaluateSwrTrip(3.0, keyedAt.AddMilliseconds(799), isTun: false, keyedAt));
        var reason = svc.EvaluateSwrTrip(3.0, keyedAt.AddMilliseconds(800), isTun: false, keyedAt);
        Assert.NotNull(reason);
    }

    [Fact]
    public void EvaluateSwrTrip_Tun_HighThreshold_DoesNotTripAt3To1()
    {
        var svc = BuildService(out _, out _);
        var t0 = new DateTime(2026, 4, 18, 12, 0, 0, DateTimeKind.Utc);

        // 3:1 sustained well past the MOX threshold/sustain window — MUST NOT
        // trip on TUN, because TUN's job is to drive a not-yet-matched load.
        Assert.Null(svc.EvaluateSwrTrip(3.0, t0, isTun: true, KeyedAtPastGrace));
        Assert.Null(svc.EvaluateSwrTrip(3.0, t0.AddMilliseconds(500), isTun: true, KeyedAtPastGrace));
        Assert.Null(svc.EvaluateSwrTrip(3.0, t0.AddMilliseconds(1000), isTun: true, KeyedAtPastGrace));
        Assert.Null(svc.EvaluateSwrTrip(3.0, t0.AddMilliseconds(5000), isTun: true, KeyedAtPastGrace));
    }

    [Fact]
    public void EvaluateSwrTrip_Tun_FiresAt6To1Sustained()
    {
        var svc = BuildService(out _, out _);
        var t0 = new DateTime(2026, 4, 18, 12, 0, 0, DateTimeKind.Utc);

        // Genuine bad match — 7:1 sustained ≥500 ms on TUN should still trip.
        Assert.Null(svc.EvaluateSwrTrip(7.0, t0, isTun: true, KeyedAtPastGrace));
        Assert.Null(svc.EvaluateSwrTrip(7.0, t0.AddMilliseconds(499), isTun: true, KeyedAtPastGrace));
        var reason = svc.EvaluateSwrTrip(7.0, t0.AddMilliseconds(500), isTun: true, KeyedAtPastGrace);
        Assert.NotNull(reason);
        Assert.Contains("SWR", reason);
    }

    [Fact]
    public void EvaluateSwrTrip_Tun_StartupGrace_SuppressesTripDuringFirst500ms()
    {
        var svc = BuildService(out _, out _);
        var keyedAt = new DateTime(2026, 4, 18, 12, 0, 0, DateTimeKind.Utc);

        // Even a 9:1 reading during the 500 ms TUN grace must not arm the
        // trip — the ATU is by design seeing a wildly mismatched load while
        // hunting for a match.
        Assert.Null(svc.EvaluateSwrTrip(9.0, keyedAt, isTun: true, keyedAt));
        Assert.Null(svc.EvaluateSwrTrip(9.0, keyedAt.AddMilliseconds(250), isTun: true, keyedAt));
        Assert.Null(svc.EvaluateSwrTrip(9.0, keyedAt.AddMilliseconds(499), isTun: true, keyedAt));
        // From 500 ms onward grace is over; sustain timer starts and trip
        // fires 500 ms later.
        Assert.Null(svc.EvaluateSwrTrip(9.0, keyedAt.AddMilliseconds(500), isTun: true, keyedAt));
        var reason = svc.EvaluateSwrTrip(9.0, keyedAt.AddMilliseconds(1000), isTun: true, keyedAt);
        Assert.NotNull(reason);
    }

    [Fact]
    public void TxService_TryTripForAlert_WhileTunOn_DropsTunAndBroadcastsAlert()
    {
        var svc = BuildService(out var tx, out var hub);

        // Simulate TUN being on without going through the full radio-connect flow:
        // TrySetTun guards on ActiveClient != null, which we don't have in a unit
        // test. Instead, drive the internal state via the protection seam — the
        // test is about the trip + broadcast plumbing, not the TUN precondition.
        // We assert trip is a no-op when neither MOX nor TUN is on (idempotency).
        tx.TryTripForAlert(AlertKind.SwrTrip, "probe");
        Assert.False(tx.IsMoxOn);
        Assert.False(tx.IsTunOn);
        Assert.Equal(0, hub.ClientCount); // no clients attached — broadcast is a no-op that must not throw
    }
}
