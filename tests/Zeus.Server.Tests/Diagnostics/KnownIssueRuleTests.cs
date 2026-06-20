// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using Xunit;
using Zeus.Server.Diagnostics;

namespace Zeus.Server.Tests;

public sealed class KnownIssueRuleTests
{
    private static DiagnosticContext Ctx(
        string? symptomId, IReadOnlyList<string> recentLog) =>
        new()
        {
            Services = EmptyServiceProvider.Instance,
            SymptomId = symptomId,
            FreeText = null,
            RecentLog = recentLog,
        };

    private static DiagnosticSection Section(
        string id, params (string Key, string Value)[] items)
    {
        var kv = new List<DiagnosticKeyValue>();
        foreach (var (k, v) in items)
            kv.Add(new DiagnosticKeyValue(k, v));
        return new DiagnosticSection(id, id, kv);
    }

    // ---- PsNotArmedRule ----

    [Fact]
    public void PsNotArmed_FiresOnGateSkipMarker()
    {
        var rule = new PsNotArmedRule();
        var log = new[] { "info: psAutoAttn.gate skip=PsEnabled-off" };
        var finding = rule.Evaluate(Ctx("ps-not-working", log), []);

        Assert.NotNull(finding);
        Assert.Equal(DiagnosticSeverity.Likely, finding!.Severity);
        Assert.False(string.IsNullOrWhiteSpace(finding.DocRef));
    }

    [Fact]
    public void PsNotArmed_FiresWhenProbeReportsPsEnabledFalse()
    {
        var rule = new PsNotArmedRule();
        // Real probe key is "ps.enabled".
        var sections = new[] { Section("tx-ps", ("ps.enabled", "false")) };
        var finding = rule.Evaluate(Ctx("ps-not-working", []), sections);

        Assert.NotNull(finding);
        Assert.Equal(DiagnosticSeverity.Likely, finding!.Severity);
    }

    [Fact]
    public void PsNotArmed_DoesNotFireWhenArmed()
    {
        var rule = new PsNotArmedRule();
        var sections = new[] { Section("tx-ps", ("ps.enabled", "true")) };
        var finding = rule.Evaluate(Ctx("ps-not-working", ["info: nothing relevant"]), sections);

        Assert.Null(finding);
    }

    // ---- IqWriteGateRule ----

    [Fact]
    public void IqWriteGate_FiresOnHermesWithDrivePeakZero()
    {
        var rule = new IqWriteGateRule();
        var sections = new[] { Section("board", ("board.kind", "HermesII")) };
        var log = new[] { "p1.tx.rate pkts=120 drv=240 peak=0" };
        var finding = rule.Evaluate(Ctx("no-tx-power", log), sections);

        Assert.NotNull(finding);
        Assert.Equal(DiagnosticSeverity.Likely, finding!.Severity);
        Assert.False(string.IsNullOrWhiteSpace(finding.DocRef));
    }

    [Fact]
    public void IqWriteGate_DoesNotFireWhenPeakNonZero()
    {
        var rule = new IqWriteGateRule();
        var sections = new[] { Section("board", ("board.kind", "HermesII")) };
        var log = new[] { "p1.tx.rate pkts=120 drv=240 peak=18000" };
        Assert.Null(rule.Evaluate(Ctx("no-tx-power", log), sections));
    }

    [Fact]
    public void IqWriteGate_DoesNotFireWhenDriveZero()
    {
        var rule = new IqWriteGateRule();
        var sections = new[] { Section("board", ("board.kind", "HermesII")) };
        var log = new[] { "p1.tx.rate pkts=120 drv=0 peak=0" };
        Assert.Null(rule.Evaluate(Ctx("no-tx-power", log), sections));
    }

    [Fact]
    public void IqWriteGate_DoesNotFireOnHermesLite2()
    {
        // HL2 is excluded from the Hermes-class match; its low-power story is the
        // percentage drive model, not the IQ-write gate.
        var rule = new IqWriteGateRule();
        var sections = new[] { Section("board", ("board.kind", "HermesLite2")) };
        var log = new[] { "p1.tx.rate pkts=120 drv=240 peak=0" };
        Assert.Null(rule.Evaluate(Ctx("no-tx-power", log), sections));
    }

    // ---- Hl2DriveModelRule ----

    [Fact]
    public void Hl2DriveModel_FiresOnHl2()
    {
        var rule = new Hl2DriveModelRule();
        var sections = new[] { Section("board", ("board.kind", "HermesLite2")) };
        var finding = rule.Evaluate(Ctx("no-tx-power", []), sections);

        Assert.NotNull(finding);
        Assert.Equal("docs/lessons/hl2-drive-model.md", finding!.DocRef);
    }

    [Fact]
    public void Hl2DriveModel_DoesNotFireOnOtherBoard()
    {
        var rule = new Hl2DriveModelRule();
        var sections = new[] { Section("board", ("board.kind", "OrionMkII")) };
        Assert.Null(rule.Evaluate(Ctx("no-tx-power", []), sections));
    }

    // ---- RxAuxBypassRule ----

    [Fact]
    public void RxAuxBypass_AlwaysProvidesAdvisoryForReceiveSymptoms()
    {
        var rule = new RxAuxBypassRule();
        var finding = rule.Evaluate(Ctx("rx-no-audio", []), []);

        Assert.NotNull(finding);
        Assert.Equal(DiagnosticSeverity.Warning, finding!.Severity);
        Assert.Contains("None", finding.Detail);
    }

    // ---- RxaAudioSilenceRule ----

    [Fact]
    public void RxaAudioSilence_FiresOnMinus400Sentinel()
    {
        var rule = new RxaAudioSilenceRule();
        var sections = new[] { Section("dsp-audio", ("rx.dbm", "-400")) };
        var finding = rule.Evaluate(Ctx("rx-no-audio", []), sections);

        Assert.NotNull(finding);
        Assert.Equal("docs/rca/2026-04-17-rxa-audio-silence.md", finding!.DocRef);
    }

    [Fact]
    public void RxaAudioSilence_DoesNotFireOnHealthyMeter()
    {
        var rule = new RxaAudioSilenceRule();
        var sections = new[] { Section("dsp-audio", ("rx.dbm", "-92.5")) };
        Assert.Null(rule.Evaluate(Ctx("rx-no-audio", ["info: all good"]), sections));
    }

    // ---- DisconnectionRule ----

    [Fact]
    public void Disconnection_FiresLikelyOnRxTimeout()
    {
        var rule = new DisconnectionRule();
        var log = new[] { "warn: RX: 10 consecutive socket timeouts — radio gone" };
        var finding = rule.Evaluate(Ctx("wont-connect", log), []);

        Assert.NotNull(finding);
        Assert.Equal(DiagnosticSeverity.Likely, finding!.Severity);
        Assert.Equal("docs/lessons/disconnection-troubleshooting.md", finding.DocRef);
    }

    [Fact]
    public void Disconnection_FallsBackToWarningWithoutTimeout()
    {
        var rule = new DisconnectionRule();
        var finding = rule.Evaluate(Ctx("wont-connect", ["info: nothing"]), []);

        Assert.NotNull(finding);
        Assert.Equal(DiagnosticSeverity.Warning, finding!.Severity);
    }

    // ---- PsStartupArmedRule ----

    [Fact]
    public void PsStartupArmed_HasNoSymptomFilter()
    {
        var rule = new PsStartupArmedRule();
        Assert.Empty(rule.Symptoms);
    }

    [Fact]
    public void PsStartupArmed_FiresCriticalWhenArmedAtStartup()
    {
        var rule = new PsStartupArmedRule();
        var sections = new[] { Section("tx-ps", ("ps.armedAtStartup", "true")) };
        var finding = rule.Evaluate(Ctx("other", []), sections);

        Assert.NotNull(finding);
        Assert.Equal(DiagnosticSeverity.Critical, finding!.Severity);
    }

    [Fact]
    public void PsStartupArmed_SilentInNormalCase()
    {
        var rule = new PsStartupArmedRule();
        var sections = new[] { Section("tx-ps", ("ps.enabled", "false")) };
        Assert.Null(rule.Evaluate(Ctx("other", []), sections));
    }

    [Fact]
    public void AllRules_DoNotThrowOnEmptyInput()
    {
        IKnownIssueRule[] rules =
        [
            new PsNotArmedRule(), new IqWriteGateRule(), new RxAuxBypassRule(),
            new Hl2DriveModelRule(), new RxaAudioSilenceRule(), new DisconnectionRule(),
            new PsStartupArmedRule(), new AudioUnderrunRule(),
        ];
        foreach (var rule in rules)
        {
            var ex = Record.Exception(() => rule.Evaluate(Ctx(null, []), []));
            Assert.Null(ex);
        }
    }

    // ---- AudioUnderrunRule ----

    [Fact]
    public void AudioUnderrun_FiresLikely_OnLargeUnderrunCount()
    {
        var rule = new AudioUnderrunRule();
        var sections = new[]
        {
            Section("dsp-audio",
                ("audio.underrunSamplesTotal", "167520"),
                ("audio.sampleRateHz", "48000")),
        };
        var finding = rule.Evaluate(Ctx("rx-audio-quality", []), sections);

        Assert.NotNull(finding);
        Assert.Equal(DiagnosticSeverity.Likely, finding!.Severity);
        Assert.Contains("167,520", finding.Detail); // formatted count surfaced
    }

    [Fact]
    public void AudioUnderrun_Silent_WhenUnderrunsAreTrivial()
    {
        var rule = new AudioUnderrunRule();
        var sections = new[]
        {
            Section("dsp-audio",
                ("audio.underrunSamplesTotal", "128"),
                ("audio.sampleRateHz", "48000")),
        };
        Assert.Null(rule.Evaluate(Ctx("rx-audio-quality", []), sections));
    }

    [Fact]
    public void AudioUnderrun_Silent_WhenNoDspAudioSection()
    {
        var rule = new AudioUnderrunRule();
        Assert.Null(rule.Evaluate(Ctx("rx-audio-quality", []), []));
    }

    // ---- RxAuxBypassRule symptom scope (must not fire on crackle) ----

    [Fact]
    public void RxAuxBypass_ScopedToNoAudioOnly_NotCrackle()
    {
        var rule = new RxAuxBypassRule();
        Assert.Contains("rx-no-audio", rule.Symptoms);
        Assert.DoesNotContain("rx-audio-quality", rule.Symptoms);
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
