// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using Zeus.Contracts;
using Zeus.Server.AudioChainHealth;
using Xunit;

namespace Zeus.Server.Tests.AudioChainHealth;

public class RuleEvaluatorTests
{
    private static readonly DateTime T0 = new(2026, 5, 27, 12, 0, 0, DateTimeKind.Utc);

    private static AudioChainReadings ReadingsWithMicAv(float micAv) => new(
        MicPk: micAv + 12, MicAv: micAv,
        EqPk: 0, EqAv: 0,
        LvlrPk: 0, LvlrAv: 0, LvlrGr: 0,
        CfcPk: 0, CfcAv: 0, CfcGr: 0,
        CompPk: 0, CompAv: 0,
        AlcPk: 0, AlcAv: 0, AlcGr: 0,
        OutPk: 0, OutAv: 0,
        DrivePct: 50, DriveByte: 128,
        FwdWatts: 0, RefWatts: 0, Swr: 1f,
        MicGainDb: 0, LevelerMaxGainDb: 8, CfcPreCompDb: 0);

    private static AudioChainReadings ReadingsWithSwr(float swr) => new(
        MicPk: -24, MicAv: -24,
        EqPk: 0, EqAv: 0,
        LvlrPk: 0, LvlrAv: 0, LvlrGr: 0,
        CfcPk: 0, CfcAv: 0, CfcGr: 0,
        CompPk: 0, CompAv: 0,
        AlcPk: 0, AlcAv: 0, AlcGr: 0,
        OutPk: 0, OutAv: 0,
        DrivePct: 50, DriveByte: 128,
        FwdWatts: 50, RefWatts: 0, Swr: swr,
        MicGainDb: 0, LevelerMaxGainDb: 8, CfcPreCompDb: 0);

    private static RuleContext SsbCtx => new(RxMode.USB, HpsdrBoardKind.HermesLite2, Mox: true);

    // "Mic too low" rule with explicit hysteresis: enter -30, exit -28
    // (so the rule trips below -30 and clears above -28, a 2 dB band).
    private static AudioChainRule MicLowRule(TimeSpan? window = null) => new()
    {
        Name = "mic.low",
        Stage = AudioChainStageId.Mic,
        Severity = AudioChainSeverity.Warn,
        AppliesIn = _ => true,
        Read = r => r.MicAv,
        EnterThreshold = -30,
        ExitThreshold = -28,
        Direction = TripDirection.Below,
        Message = (_, _) => "mic low",
        SustainedWindow = window,
    };

    // "SWR runaway" — error+immediate, fires above 3.0, clears under 2.5.
    private static AudioChainRule SwrRunawayRule() => new()
    {
        Name = "pa.swr.runaway",
        Stage = AudioChainStageId.Pa,
        Severity = AudioChainSeverity.Error,
        ImmediateAction = true,
        AppliesIn = _ => true,
        Read = r => r.Swr,
        EnterThreshold = 3.0,
        ExitThreshold = 2.5,
        Direction = TripDirection.Above,
        Message = (_, _) => "stop tx swr",
    };

    [Fact]
    public void Below_Enter_DoesNotFire_BeforeSustainedWindow()
    {
        var ev = new RuleEvaluator();
        var rule = MicLowRule();
        var rules = new[] { rule };

        // Drop below the enter threshold at T0.
        var fired = ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0);
        Assert.Empty(fired);

        // Still below at T0 + 1 s — not yet 3 s sustained.
        fired = ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0.AddSeconds(1));
        Assert.Empty(fired);

        // 3 s sustained: fires.
        fired = ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0.AddSeconds(3));
        Assert.Single(fired);
        Assert.Equal("mic.low", fired[0].Rule.Name);
    }

    [Fact]
    public void Below_RecoversToHysteresisBand_StaysTripping()
    {
        var ev = new RuleEvaluator();
        var rule = MicLowRule();
        var rules = new[] { rule };

        // Trip at T0 by going below enter (-30).
        ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0);

        // Reading rises into the hysteresis band (between exit -28 and
        // enter -30) — still raw-tripping by spec.
        ev.Evaluate(rules, ReadingsWithMicAv(-29), SsbCtx, T0.AddSeconds(1));

        // Wait out the sustained window; still tripping continuously.
        var fired = ev.Evaluate(rules, ReadingsWithMicAv(-29), SsbCtx, T0.AddSeconds(3.1));
        Assert.Single(fired);
    }

    [Fact]
    public void Below_RisesAboveExitThreshold_VerdictStaysSticky()
    {
        // Sticky-fired semantics: once the warning has fired, the
        // operator gets to read it at human speed instead of the
        // verdict vanishing the moment the reading recovers. The
        // cached message keeps replaying until the rule's scope
        // changes (see RuleOutOfScope_ScrubsLatchedState).
        var ev = new RuleEvaluator();
        var rule = MicLowRule();
        var rules = new[] { rule };

        // Trip-and-fire at T0+3.
        ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0);
        ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0.AddSeconds(3));

        // Climb above exit threshold (-28). Verdict stays sticky.
        var fired = ev.Evaluate(rules, ReadingsWithMicAv(-20), SsbCtx, T0.AddSeconds(3.5));
        Assert.Single(fired);

        // Several more ticks with healthy mic — verdict still sticky.
        fired = ev.Evaluate(rules, ReadingsWithMicAv(-20), SsbCtx, T0.AddSeconds(10));
        Assert.Single(fired);

        // Re-trip with a different reading — cache should update on
        // the next raw-tripping tick (cache replays only while
        // recovered).
        fired = ev.Evaluate(rules, ReadingsWithMicAv(-50), SsbCtx, T0.AddSeconds(11));
        Assert.Single(fired);
    }

    [Fact]
    public void Below_FlutterAtBoundary_DoesNotFire()
    {
        // Hysteresis prevents flutter. The reading alternates between
        // -29.5 (inside hysteresis band, would NOT enter on its own but
        // also wouldn't exit if already tripping) and -28.5 (above
        // exit — clears any tripping). Since we never breach the enter
        // threshold (-30), the rule must never trip raw, and therefore
        // never fire.
        var ev = new RuleEvaluator();
        var rule = MicLowRule();
        var rules = new[] { rule };

        for (int i = 0; i < 20; i++)
        {
            float micAv = (i % 2 == 0) ? -29.5f : -28.5f;
            var fired = ev.Evaluate(rules, ReadingsWithMicAv(micAv), SsbCtx, T0.AddSeconds(i * 0.5));
            Assert.Empty(fired);
        }
    }

    [Fact]
    public void Above_ErrorRule_UsesFiveSecondDefault()
    {
        var ev = new RuleEvaluator();
        var rule = SwrRunawayRule();
        var rules = new[] { rule };

        // SWR jumps to 4.0.
        var fired = ev.Evaluate(rules, ReadingsWithSwr(4f), SsbCtx, T0);
        Assert.Empty(fired);

        // At T0 + 4.9 s — just under the 5 s error default — still not firing.
        fired = ev.Evaluate(rules, ReadingsWithSwr(4f), SsbCtx, T0.AddSeconds(4.9));
        Assert.Empty(fired);

        // At T0 + 5 s — fires.
        fired = ev.Evaluate(rules, ReadingsWithSwr(4f), SsbCtx, T0.AddSeconds(5));
        Assert.Single(fired);
        Assert.True(fired[0].Rule.ImmediateAction);
    }

    [Fact]
    public void RuleOutOfScope_ScrubsLatchedState()
    {
        var ev = new RuleEvaluator();
        // Rule applies only in USB.
        AudioChainRule rule = MicLowRule() with
        {
            Name = "mic.low.ssb-only",
            AppliesIn = c => c.Mode == RxMode.USB,
        };
        var rules = new[] { rule };

        // Trip and fire in USB.
        ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0);
        var fired = ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0.AddSeconds(3.1));
        Assert.Single(fired);

        // Operator switches to CW — rule is out of scope, MUST clear
        // the fired bit AND the sustained-violation timer.
        var cwCtx = SsbCtx with { Mode = RxMode.CWU };
        fired = ev.Evaluate(rules, ReadingsWithMicAv(-40), cwCtx, T0.AddSeconds(3.2));
        Assert.Empty(fired);

        // Back to USB, mic still low. The window must start FRESH —
        // not resume from where it left off in CW.
        fired = ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0.AddSeconds(3.3));
        Assert.Empty(fired);

        // Need a fresh 3 s after the USB return before re-firing.
        fired = ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0.AddSeconds(6.4));
        Assert.Single(fired);
    }

    [Fact]
    public void Reset_ForgetsAllState()
    {
        var ev = new RuleEvaluator();
        var rules = new[] { MicLowRule() };

        ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0);
        ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0.AddSeconds(3.1));
        // After firing, reset, then immediately evaluate. The next 3 s
        // window starts now — the prior fire must not survive.
        ev.Reset();
        var fired = ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0.AddSeconds(3.2));
        Assert.Empty(fired);
        fired = ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0.AddSeconds(6.3));
        Assert.Single(fired);
    }

    [Fact]
    public void CustomSustainedWindow_OverridesDefault()
    {
        var ev = new RuleEvaluator();
        var rule = MicLowRule(window: TimeSpan.FromSeconds(1));
        var rules = new[] { rule };

        ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0);

        // Default would be 3 s warn — but the rule overrides to 1 s.
        var fired = ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0.AddSeconds(1));
        Assert.Single(fired);
    }

    [Fact]
    public void FiredRule_CarriesMessageAndApplyAction()
    {
        var ev = new RuleEvaluator();
        var rule = new AudioChainRule
        {
            Name = "mic.low.with.apply",
            Stage = AudioChainStageId.Mic,
            Severity = AudioChainSeverity.Warn,
            AppliesIn = _ => true,
            Read = r => r.MicAv,
            EnterThreshold = -30,
            ExitThreshold = -28,
            Direction = TripDirection.Below,
            Message = (r, _) => $"Mic at {r.MicAv:F0} dBFS — raise to −24",
            ApplyLabel = (_, _) => "Apply · 22 → 28 dB",
            Apply = (_, _) => new AudioChainApplyAction("tx.mic-gain-db", 28),
        };
        var rules = new[] { rule };

        ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0);
        var fired = ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0.AddSeconds(3.1));

        Assert.Single(fired);
        Assert.Equal("Mic at -40 dBFS — raise to −24", fired[0].Message);
        Assert.Equal("Apply · 22 → 28 dB", fired[0].ApplyLabel);
        Assert.NotNull(fired[0].Apply);
        Assert.Equal("tx.mic-gain-db", fired[0].Apply!.Value.Kind);
        Assert.Equal(28, fired[0].Apply!.Value.Value);
    }

    [Fact]
    public void Sticky_RecoveredVerdict_ReplaysCachedMessage()
    {
        // While raw-tripping: message uses live readings. After
        // recovery (still sticky-fired): message stays frozen at the
        // last raw-tripping snapshot so it doesn't read "Mic at -22"
        // while showing the "Mic too low" verdict.
        var ev = new RuleEvaluator();
        var rule = new AudioChainRule
        {
            Name = "mic.low.live",
            Stage = AudioChainStageId.Mic,
            Severity = AudioChainSeverity.Warn,
            AppliesIn = _ => true,
            Read = r => r.MicAv,
            EnterThreshold = -30,
            ExitThreshold = -28,
            Direction = TripDirection.Below,
            Message = (r, _) => $"Mic at {r.MicAv:F0} dBFS",
        };
        var rules = new[] { rule };

        // Trip-and-fire at MicAv = -40.
        ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0);
        var fired = ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0.AddSeconds(3.1));
        Assert.Equal("Mic at -40 dBFS", fired[0].Message);

        // Reading climbs into the hysteresis band (-29). Still raw-tripping,
        // so message recomputes to the new live reading.
        fired = ev.Evaluate(rules, ReadingsWithMicAv(-29), SsbCtx, T0.AddSeconds(3.2));
        Assert.Equal("Mic at -29 dBFS", fired[0].Message);

        // Reading recovers above the exit threshold (-20). Sticky-fired,
        // raw-tripping false — message MUST replay the most recent
        // raw-tripping snapshot, not the now-healthy reading.
        fired = ev.Evaluate(rules, ReadingsWithMicAv(-20), SsbCtx, T0.AddSeconds(3.3));
        Assert.Single(fired);
        Assert.Equal("Mic at -29 dBFS", fired[0].Message);

        // Several more ticks at the healthy level — cached message
        // continues to replay.
        fired = ev.Evaluate(rules, ReadingsWithMicAv(-15), SsbCtx, T0.AddSeconds(10));
        Assert.Single(fired);
        Assert.Equal("Mic at -29 dBFS", fired[0].Message);

        // Re-trip with a worse reading (-50). Back in raw-tripping,
        // message + cache refresh.
        fired = ev.Evaluate(rules, ReadingsWithMicAv(-50), SsbCtx, T0.AddSeconds(11));
        Assert.Equal("Mic at -50 dBFS", fired[0].Message);

        // Recover again — replays the new (-50) cache, not the old (-29).
        fired = ev.Evaluate(rules, ReadingsWithMicAv(-20), SsbCtx, T0.AddSeconds(12));
        Assert.Equal("Mic at -50 dBFS", fired[0].Message);
    }

    [Fact]
    public void Sticky_ClearsOnScopeChange()
    {
        // The applicability scope changing is the only way a sticky-
        // fired verdict goes away on the server side (operator-side
        // dismiss is frontend-only and never visible to the evaluator).
        var ev = new RuleEvaluator();
        var rule = MicLowRule() with
        {
            Name = "mic.low.ssb-only",
            AppliesIn = c => c.Mode == RxMode.USB,
        };
        var rules = new[] { rule };

        // Trip-and-fire.
        ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0);
        var fired = ev.Evaluate(rules, ReadingsWithMicAv(-40), SsbCtx, T0.AddSeconds(3.1));
        Assert.Single(fired);

        // Recovery — still sticky.
        fired = ev.Evaluate(rules, ReadingsWithMicAv(-15), SsbCtx, T0.AddSeconds(4));
        Assert.Single(fired);

        // Operator switches to CW — scope flips, sticky verdict clears.
        var cwCtx = SsbCtx with { Mode = RxMode.CWU };
        fired = ev.Evaluate(rules, ReadingsWithMicAv(-40), cwCtx, T0.AddSeconds(5));
        Assert.Empty(fired);

        // Back to USB with healthy mic — no stale verdict resurrected,
        // even though the cache existed in the same evaluator instance.
        fired = ev.Evaluate(rules, ReadingsWithMicAv(-20), SsbCtx, T0.AddSeconds(6));
        Assert.Empty(fired);
    }
}
