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

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Server;
using Xunit;

namespace Zeus.Server.Tests;

/// <summary>
/// PRD FR-6 TX timeout: a single MOX or TUN transmission may not exceed 120 s.
/// Drives the <see cref="TxMetersService.EvaluateTimeoutTrip(DateTime)"/> seam
/// with synthetic timestamps so the tests don't wait 2 minutes of wall-clock.
/// </summary>
public class TxTimeoutTests : IDisposable
{
    // Per-fixture temp DBs — see ZoomValidationTests for the rationale.
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"zeus-prefs-txtimeout-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (File.Exists(_dbPath + ".pa")) File.Delete(_dbPath + ".pa"); } catch { }
    }

    private TxMetersService BuildService(out TxService tx)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        var radio = new RadioService(loggerFactory, dspStore, paStore);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), loggerFactory);
        tx = new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        return new TxMetersService(hub, radio, tx, pipeline, new NullLogger<TxMetersService>());
    }

    [Fact]
    public void Mox_KeyedFor119s_DoesNotTrip()
    {
        var svc = BuildService(out var tx);
        var t0 = new DateTime(2026, 4, 18, 12, 0, 0, DateTimeKind.Utc);
        tx.SetMoxStartedAtForTest(t0);

        Assert.Null(svc.EvaluateTimeoutTrip(t0.AddSeconds(119)));
    }

    [Fact]
    public void Mox_KeyedFor121s_Trips()
    {
        var svc = BuildService(out var tx);
        var t0 = new DateTime(2026, 4, 18, 12, 0, 0, DateTimeKind.Utc);
        tx.SetMoxStartedAtForTest(t0);

        var reason = svc.EvaluateTimeoutTrip(t0.AddSeconds(121));
        Assert.NotNull(reason);
        Assert.Contains("MOX", reason);
    }

    [Fact]
    public void Mox_Exactly120s_Trips()
    {
        var svc = BuildService(out var tx);
        var t0 = new DateTime(2026, 4, 18, 12, 0, 0, DateTimeKind.Utc);
        tx.SetMoxStartedAtForTest(t0);

        var reason = svc.EvaluateTimeoutTrip(t0.AddSeconds(120));
        Assert.NotNull(reason);
    }

    [Fact]
    public void Mox_ReKeyedAt100s_ResetsTheWindow()
    {
        var svc = BuildService(out var tx);
        var t0 = new DateTime(2026, 4, 18, 12, 0, 0, DateTimeKind.Utc);
        tx.SetMoxStartedAtForTest(t0);

        // At t0+100s operator unkeys — TrySetMox(false) clears the timestamp.
        var unkeyAt = t0.AddSeconds(100);
        tx.SetMoxStartedAtForTest(null);
        Assert.Null(svc.EvaluateTimeoutTrip(unkeyAt));

        // Operator re-keys at t0+100s. A full 120 s window runs from the NEW
        // key-down, so no trip at (new start + 119 s).
        var newStart = unkeyAt;
        tx.SetMoxStartedAtForTest(newStart);
        Assert.Null(svc.EvaluateTimeoutTrip(newStart.AddSeconds(119)));
        Assert.NotNull(svc.EvaluateTimeoutTrip(newStart.AddSeconds(120)));
    }

    [Fact]
    public void Tun_KeyedFor120s_Trips()
    {
        var svc = BuildService(out var tx);
        var t0 = new DateTime(2026, 4, 18, 12, 0, 0, DateTimeKind.Utc);
        tx.SetTunStartedAtForTest(t0);

        var reason = svc.EvaluateTimeoutTrip(t0.AddSeconds(120));
        Assert.NotNull(reason);
        Assert.Contains("TUN", reason);
    }

    [Fact]
    public void Tun_KeyedFor60s_DoesNotTrip()
    {
        var svc = BuildService(out var tx);
        var t0 = new DateTime(2026, 4, 18, 12, 0, 0, DateTimeKind.Utc);
        tx.SetTunStartedAtForTest(t0);

        Assert.Null(svc.EvaluateTimeoutTrip(t0.AddSeconds(60)));
    }

    [Fact]
    public void Neither_Keyed_NoTrip()
    {
        var svc = BuildService(out _);
        Assert.Null(svc.EvaluateTimeoutTrip(DateTime.UtcNow));
    }

    [Fact]
    public void TryTripForAlert_ClearsKeyedAtTimestamps()
    {
        var svc = BuildService(out var tx);
        var t0 = new DateTime(2026, 4, 18, 12, 0, 0, DateTimeKind.Utc);
        tx.SetMoxStartedAtForTest(t0);

        // Pre-condition: a timeout WOULD fire.
        Assert.NotNull(svc.EvaluateTimeoutTrip(t0.AddSeconds(121)));

        // TryTripForAlert on a non-keyed service is a no-op (no MOX/TUN to
        // drop), but even in that path we want the keyed-at timestamps cleared
        // so a stale _moxStartedAt can't keep re-firing the timeout check.
        tx.TryTripForAlert(AlertKind.TxTimeout, "probe");
        Assert.Null(tx.MoxStartedAt);
        Assert.Null(svc.EvaluateTimeoutTrip(t0.AddSeconds(121)));
    }

    // --- Issue #1270: operator-configurable timeout + pre-warning ---

    [Fact]
    public void OperatorSet_60s_TripsAt60s_NotAt120s()
    {
        var svc = BuildServiceWithRadio(out var tx, out var radio);
        radio.SetTxTimeoutSec(60);
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        tx.SetMoxStartedAtForTest(t0);

        Assert.Null(svc.EvaluateTimeoutTrip(t0.AddSeconds(59)));
        var reason = svc.EvaluateTimeoutTrip(t0.AddSeconds(60));
        Assert.NotNull(reason);
        Assert.Contains("60 s", reason);
    }

    [Fact]
    public void OperatorSet_300s_DoesNotTripAt120s()
    {
        var svc = BuildServiceWithRadio(out var tx, out var radio);
        radio.SetTxTimeoutSec(300);
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        tx.SetMoxStartedAtForTest(t0);

        Assert.Null(svc.EvaluateTimeoutTrip(t0.AddSeconds(120)));
        Assert.NotNull(svc.EvaluateTimeoutTrip(t0.AddSeconds(300)));
    }

    [Fact]
    public void SetTxTimeoutSec_BelowMin_ClampsToMin()
    {
        BuildServiceWithRadio(out _, out var radio);
        var applied = radio.SetTxTimeoutSec(5);
        Assert.Equal(RadioService.MinTxTimeoutSec, applied.TxTimeoutSec);
    }

    [Fact]
    public void SetTxTimeoutSec_AboveMax_ClampsToMax()
    {
        BuildServiceWithRadio(out _, out var radio);
        var applied = radio.SetTxTimeoutSec(9999);
        Assert.Equal(RadioService.MaxTxTimeoutSec, applied.TxTimeoutSec);
    }

    [Fact]
    public void EvaluateTimeoutWarning_FiresInLastLeadWindow_OncePerTransmission()
    {
        var svc = BuildServiceWithRadio(out var tx, out _);
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        tx.SetMoxStartedAtForTest(t0);

        // Default 120 s timeout, 30 s warning lead → no warning at 89 s.
        Assert.Null(svc.EvaluateTimeoutWarning(t0.AddSeconds(89)));

        // 90 s in → within the lead window, warning fires once with the
        // remaining seconds and the MOX label.
        var first = svc.EvaluateTimeoutWarning(t0.AddSeconds(90));
        Assert.NotNull(first);
        Assert.Contains("MOX", first);
        Assert.Contains("remaining", first);

        // Same keyed edge → dedup: second call does NOT re-fire.
        Assert.Null(svc.EvaluateTimeoutWarning(t0.AddSeconds(100)));
    }

    [Fact]
    public void EvaluateTimeoutWarning_ResetsOnNewKeyedEdge()
    {
        var svc = BuildServiceWithRadio(out var tx, out _);
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        tx.SetMoxStartedAtForTest(t0);
        Assert.NotNull(svc.EvaluateTimeoutWarning(t0.AddSeconds(95)));

        // Operator un-keys, then re-keys — a fresh keyed-at timestamp must
        // clear the dedup so the next transmission gets its own warning.
        tx.SetMoxStartedAtForTest(null);
        Assert.Null(svc.EvaluateTimeoutWarning(t0.AddSeconds(96)));
        var t1 = t0.AddSeconds(200);
        tx.SetMoxStartedAtForTest(t1);
        Assert.NotNull(svc.EvaluateTimeoutWarning(t1.AddSeconds(95)));
    }

    [Fact]
    public void EvaluateTimeoutWarning_ShortTimeout_StillLeavesMinLead()
    {
        var svc = BuildServiceWithRadio(out var tx, out var radio);
        // Minimum timeout — the warning must not consume the entire keyed
        // window; it should still fire only inside a bounded lead.
        radio.SetTxTimeoutSec(RadioService.MinTxTimeoutSec);
        var t0 = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        tx.SetMoxStartedAtForTest(t0);

        // Right at key-down there must be no warning yet (the lead is clamped
        // to timeout − 5 s so we don't spam the client on rising edge).
        Assert.Null(svc.EvaluateTimeoutWarning(t0));
        // Well inside the lead window a warning does fire.
        Assert.NotNull(svc.EvaluateTimeoutWarning(t0.AddSeconds(RadioService.MinTxTimeoutSec - 1)));
    }

    private TxMetersService BuildServiceWithRadio(out TxService tx, out RadioService radio)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var dspStore = new DspSettingsStore(NullLogger<DspSettingsStore>.Instance, _dbPath);
        var paStore = new PaSettingsStore(NullLogger<PaSettingsStore>.Instance, _dbPath + ".pa");
        radio = new RadioService(loggerFactory, dspStore, paStore);
        var hub = new StreamingHub(new NullLogger<StreamingHub>());
        var pipeline = new DspPipelineService(radio, hub, Array.Empty<IRxAudioSink>(), loggerFactory);
        tx = new TxService(radio, pipeline, hub, NullBandPlanService.Instance, new NullLogger<TxService>());
        return new TxMetersService(hub, radio, tx, pipeline, new NullLogger<TxMetersService>());
    }
}
