// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeus.Server.Diagnostics;

namespace Zeus.Server.Tests;

public sealed class DiagnosticReportBuilderTests
{
    private sealed class FakeProbe : IDiagnosticProbe
    {
        public FakeProbe(string id, params (string, string)[] items)
        {
            Id = id;
            Items = items.Select(i => new DiagnosticKeyValue(i.Item1, i.Item2)).ToList();
        }

        public string Id { get; }
        public IReadOnlyList<DiagnosticKeyValue> Items { get; }
        public bool WasRun { get; private set; }

        public DiagnosticSection Collect(DiagnosticContext ctx)
        {
            WasRun = true;
            return new DiagnosticSection(Id, "Section " + Id, Items);
        }
    }

    private sealed class FakeRule : IKnownIssueRule
    {
        private readonly DiagnosticFinding? _finding;

        public FakeRule(string id, IReadOnlyCollection<string> symptoms, DiagnosticFinding? finding)
        {
            Id = id;
            Symptoms = symptoms;
            _finding = finding;
        }

        public string Id { get; }
        public IReadOnlyCollection<string> Symptoms { get; }
        public DiagnosticFinding? Evaluate(
            DiagnosticContext ctx, IReadOnlyList<DiagnosticSection> sections) => _finding;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static DiagnosticLogBuffer LogBufferWith(int lines)
    {
        var buf = new DiagnosticLogBuffer();
        for (int i = 0; i < lines; i++)
            buf.Add("log-line-" + i.ToString(CultureInfo.InvariantCulture));
        return buf;
    }

    private static DiagnosticReportBuilder NewBuilder(
        IEnumerable<IDiagnosticProbe> probes,
        IEnumerable<IKnownIssueRule> rules,
        DiagnosticLogBuffer logBuffer) =>
        new(
            probes,
            rules,
            logBuffer,
            new SymptomRegistry(),
            new EmptyServiceProvider(),
            NullLogger<DiagnosticReportBuilder>.Instance);

    [Fact]
    public void Symptoms_ExposesAllEight()
    {
        var builder = NewBuilder([], [], new DiagnosticLogBuffer());
        Assert.Equal(8, builder.Symptoms().Count);
    }

    [Fact]
    public void Build_RunsOnlyRecipeProbes_ForSymptom()
    {
        var env = new FakeProbe("environment", ("OS", "macOS"));
        var conn = new FakeProbe("connection");
        var board = new FakeProbe("board", ("Board", "OrionMkII"));
        var dsp = new FakeProbe("dsp-audio");
        var txps = new FakeProbe("tx-ps", ("PsEnabled", "false"));

        var builder = NewBuilder([env, conn, board, dsp, txps], [], LogBufferWith(150));

        // wont-connect recipe = environment, connection, board (no dsp-audio, no tx-ps).
        var result = builder.Build(new DiagnosticRequest("wont-connect", null));

        Assert.True(env.WasRun);
        Assert.True(conn.WasRun);
        Assert.True(board.WasRun);
        Assert.False(dsp.WasRun);
        Assert.False(txps.WasRun);

        var ids = result.Report.Sections.Select(s => s.Id).ToArray();
        Assert.Equal(new[] { "environment", "connection", "board" }, ids);
    }

    [Fact]
    public void Build_ProbeThatThrows_EmitsFailureSectionAndContinues()
    {
        var board = new FakeProbe("board", ("Board", "OrionMkII"));
        var builder = NewBuilder(
            [new ThrowingProbe("environment"), new FakeProbe("connection"), board],
            [], new DiagnosticLogBuffer());

        var result = builder.Build(new DiagnosticRequest("wont-connect", null));

        var envSection = result.Report.Sections.Single(s => s.Id == "environment");
        Assert.Contains(envSection.Items, i => i.Key == "error");
        // The other probes still ran.
        Assert.Contains(result.Report.Sections, s => s.Id == "board");
    }

    [Fact]
    public void Build_OrdersFindingsBySeverityDescThenTitle()
    {
        var warn = new FakeRule("w", ["rx-no-audio"],
            new DiagnosticFinding("Zeta warning", "d", DiagnosticSeverity.Warning));
        var likely = new FakeRule("l", ["rx-no-audio"],
            new DiagnosticFinding("Alpha likely", "d", DiagnosticSeverity.Likely));
        var critical = new FakeRule("c", ["rx-no-audio"],
            new DiagnosticFinding("Beta critical", "d", DiagnosticSeverity.Critical));

        var builder = NewBuilder(
            [new FakeProbe("environment"), new FakeProbe("connection"),
             new FakeProbe("board"), new FakeProbe("dsp-audio")],
            [warn, likely, critical], new DiagnosticLogBuffer());

        var result = builder.Build(new DiagnosticRequest("rx-no-audio", null));

        var titles = result.Report.Findings.Select(f => f.Title).ToArray();
        Assert.Equal(new[] { "Beta critical", "Alpha likely", "Zeta warning" }, titles);
    }

    [Fact]
    public void Build_OnlyEvaluatesRulesMatchingSymptomOrEmpty()
    {
        var matching = new FakeRule("m", ["rx-no-audio"],
            new DiagnosticFinding("Matching", "d", DiagnosticSeverity.Likely));
        var nonMatching = new FakeRule("n", ["no-tx-power"],
            new DiagnosticFinding("Should not appear", "d", DiagnosticSeverity.Likely));
        var always = new FakeRule("a", [],
            new DiagnosticFinding("Always", "d", DiagnosticSeverity.Info));

        var builder = NewBuilder(
            [new FakeProbe("environment"), new FakeProbe("connection"),
             new FakeProbe("board"), new FakeProbe("dsp-audio")],
            [matching, nonMatching, always], new DiagnosticLogBuffer());

        var result = builder.Build(new DiagnosticRequest("rx-no-audio", null));
        var titles = result.Report.Findings.Select(f => f.Title).ToArray();

        Assert.Contains("Matching", titles);
        Assert.Contains("Always", titles);
        Assert.DoesNotContain("Should not appear", titles);
    }

    [Fact]
    public void Build_MarkdownContainsSectionsFindingsAndLastHundredLog()
    {
        var board = new FakeProbe("board", ("Board", "OrionMkII"));
        var rule = new FakeRule("r", ["wont-connect"],
            new DiagnosticFinding(
                "Network looks flaky", "Try wired Ethernet.",
                DiagnosticSeverity.Likely, "docs/lessons/disconnection-troubleshooting.md"));

        var builder = NewBuilder(
            [new FakeProbe("environment", ("OS", "macOS")),
             new FakeProbe("connection"), board],
            [rule], LogBufferWith(150));

        var result = builder.Build(new DiagnosticRequest("wont-connect", "It keeps dropping."));
        var md = result.Markdown;

        // Symptom + free text.
        Assert.Contains("Radio won't connect or keeps dropping", md);
        Assert.Contains("It keeps dropping.", md);
        // Section values.
        Assert.Contains("Board", md);
        Assert.Contains("OrionMkII", md);
        // Finding + doc ref.
        Assert.Contains("Network looks flaky", md);
        Assert.Contains("docs/lessons/disconnection-troubleshooting.md", md);
        // Report ships exactly the last 100 lines.
        Assert.Equal(100, result.Report.RecentLog.Count);
        Assert.Contains("log-line-149", md);  // newest
        Assert.Contains("log-line-50", md);   // 100th-from-newest
        Assert.DoesNotContain("log-line-49", md);  // older than the tail
    }

    [Fact]
    public void Build_GithubUrl_TargetsRepoAndEncodesTitle()
    {
        var builder = NewBuilder(
            [new FakeProbe("environment"), new FakeProbe("connection"), new FakeProbe("board")],
            [], new DiagnosticLogBuffer());

        var result = builder.Build(new DiagnosticRequest("wont-connect", null));

        Assert.StartsWith(
            "https://github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus/issues/new",
            result.GithubIssueUrl);
        // Title is "[Report] Radio won't connect or keeps dropping", URL-encoded.
        Assert.Contains("title=", result.GithubIssueUrl);
        Assert.Contains(Uri.EscapeDataString("[Report] Radio won't connect or keeps dropping"),
            result.GithubIssueUrl);
        Assert.Contains("labels=bug", result.GithubIssueUrl);
    }

    [Fact]
    public void Build_NullSymptom_UsesProblemReportTitle()
    {
        var builder = NewBuilder(
            [new FakeProbe("environment"), new FakeProbe("connection"),
             new FakeProbe("board"), new FakeProbe("dsp-audio"), new FakeProbe("tx-ps")],
            [], new DiagnosticLogBuffer());

        var result = builder.Build(new DiagnosticRequest(null, null));

        Assert.Contains("title=" + Uri.EscapeDataString("Problem report"), result.GithubIssueUrl);
        // null/unknown runs all five probes.
        Assert.Equal(5, result.Report.Sections.Count);
    }

    [Fact]
    public void Build_LongLog_ProducesShorterUrlThanFullMarkdown()
    {
        // A log full of long lines blows past the URL ceiling; the URL should drop
        // the log block (and so be much shorter than the full markdown the UI copies).
        var buf = new DiagnosticLogBuffer();
        for (int i = 0; i < 100; i++)
            buf.Add(new string('x', 400) + i.ToString(CultureInfo.InvariantCulture));

        var builder = NewBuilder(
            [new FakeProbe("environment"), new FakeProbe("connection"), new FakeProbe("board")],
            [], buf);

        var result = builder.Build(new DiagnosticRequest("wont-connect", null));

        // Full markdown keeps the whole log.
        Assert.Contains("xxxx", result.Markdown);
        Assert.True(result.Markdown.Length > 30_000,
            "full markdown should carry the big log");

        // URL stays under the ceiling and notes the omission.
        Assert.True(result.GithubIssueUrl.Length <= 7000 + 256,
            $"URL should be trimmed, was {result.GithubIssueUrl.Length}");
        Assert.True(result.GithubIssueUrl.Length < result.Markdown.Length,
            "trimmed URL should be shorter than full markdown");
        Assert.Contains(
            Uri.EscapeDataString("Log omitted from this link"),
            result.GithubIssueUrl);
    }

    [Fact]
    public void Build_FreeText_IsLengthCapped()
    {
        var builder = NewBuilder(
            [new FakeProbe("environment"), new FakeProbe("connection"), new FakeProbe("board")],
            [], new DiagnosticLogBuffer());

        var huge = new string('a', 5000);
        var result = builder.Build(new DiagnosticRequest("wont-connect", huge));

        Assert.NotNull(result.Report.FreeText);
        Assert.True(result.Report.FreeText!.Length <= 2000);
    }

    private sealed class ThrowingProbe : IDiagnosticProbe
    {
        public ThrowingProbe(string id) => Id = id;
        public string Id { get; }
        public DiagnosticSection Collect(DiagnosticContext ctx) =>
            throw new InvalidOperationException("boom");
    }
}
