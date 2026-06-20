// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Zeus.Server.Diagnostics;

/// <summary>
/// Assembles a "Report a problem" diagnostic: snapshots the recent log, runs the
/// probe recipe for the chosen symptom, evaluates the known-issue rules, and
/// renders both the structured report and a paste-ready Markdown + prefilled
/// GitHub-issue URL.
///
/// Registered as a singleton. Probes and rules are injected via
/// <c>IEnumerable&lt;&gt;</c> so this type never references a concrete probe or
/// rule. The whole path is read-only — it must never mutate radio/DSP/PS state.
/// </summary>
public sealed class DiagnosticReportBuilder
{
    /// <summary>Bumped when the wire shape of <see cref="DiagnosticReport"/> changes.</summary>
    public const string SchemaVersion = "1";

    private readonly IReadOnlyDictionary<string, IDiagnosticProbe> _probesById;
    private readonly IReadOnlyList<IKnownIssueRule> _rules;
    private readonly DiagnosticLogBuffer _logBuffer;
    private readonly SymptomRegistry _symptoms;
    private readonly IServiceProvider _services;
    private readonly ILogger<DiagnosticReportBuilder> _log;

    /// <summary>Light cap on free-text length (user prose — passed through, not log-redacted).</summary>
    private const int FreeTextMaxChars = 2000;

    public DiagnosticReportBuilder(
        IEnumerable<IDiagnosticProbe> probes,
        IEnumerable<IKnownIssueRule> rules,
        DiagnosticLogBuffer logBuffer,
        SymptomRegistry symptoms,
        IServiceProvider services,
        ILogger<DiagnosticReportBuilder> log)
    {
        // First-registration-wins on duplicate ids; keeps lookup O(1) per recipe id.
        var byId = new Dictionary<string, IDiagnosticProbe>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in probes)
            byId.TryAdd(p.Id, p);
        _probesById = byId;

        _rules = rules.ToList();
        _logBuffer = logBuffer;
        _symptoms = symptoms;
        _services = services;
        _log = log;
    }

    /// <summary>The selectable symptoms for the picker.</summary>
    public IReadOnlyList<Symptom> Symptoms() => _symptoms.All;

    /// <summary>Run the probe recipe + rules for a request and render the result.</summary>
    public DiagnosticReportResult Build(DiagnosticRequest req)
    {
        var symptom = ResolveSymptom(req.SymptomId);
        var freeText = TrimFreeText(req.FreeText);

        // 1. Snapshot the recent log (already redacted by the ring-buffer logger).
        IReadOnlyList<string> recentLog;
        try
        {
            recentLog = _logBuffer.Snapshot(DiagnosticLogBuffer.ReportTailLines);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "diagnostics.report log snapshot failed");
            recentLog = [];
        }

        var ctx = new DiagnosticContext
        {
            Services = _services,
            SymptomId = req.SymptomId,
            FreeText = freeText,
            RecentLog = recentLog,
        };

        // 2. Run the recipe probes, in the recipe's stable order. Guard each one.
        var sections = new List<DiagnosticSection>();
        foreach (var probeId in _symptoms.ProbeIdsFor(req.SymptomId))
        {
            if (!_probesById.TryGetValue(probeId, out var probe))
            {
                _log.LogWarning(
                    "diagnostics.report recipe references unregistered probe {ProbeId}", probeId);
                continue;
            }

            try
            {
                sections.Add(probe.Collect(ctx));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "diagnostics.report probe {ProbeId} threw", probeId);
                sections.Add(new DiagnosticSection(
                    probeId,
                    probeId + " (collection failed)",
                    [new DiagnosticKeyValue(
                        "error", "This probe failed to collect; the rest of the report is unaffected.")]));
            }
        }

        // 3. Evaluate the rules relevant to this symptom; collect non-null findings.
        var findings = new List<DiagnosticFinding>();
        foreach (var rule in _rules)
        {
            bool relevant = rule.Symptoms.Count == 0 ||
                            (req.SymptomId is not null && rule.Symptoms.Contains(req.SymptomId));
            if (!relevant) continue;

            try
            {
                var finding = rule.Evaluate(ctx, sections);
                if (finding is not null)
                    findings.Add(finding);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "diagnostics.report rule {RuleId} threw", rule.Id);
            }
        }

        // Order: most urgent first, then title for stability.
        findings.Sort((a, b) =>
        {
            int s = b.Severity.CompareTo(a.Severity);
            return s != 0 ? s : string.CompareOrdinal(a.Title, b.Title);
        });

        var report = new DiagnosticReport(
            SchemaVersion: SchemaVersion,
            GeneratedUtc: DateTimeOffset.UtcNow,
            SymptomId: req.SymptomId,
            SymptomLabel: symptom?.Label,
            FreeText: freeText,
            Sections: sections,
            Findings: findings,
            RecentLog: recentLog);

        // 4. Render Markdown + GitHub URL.
        string markdown = MarkdownReportRenderer.Render(report);
        string githubUrl = MarkdownReportRenderer.BuildGithubIssueUrl(report, markdown);

        return new DiagnosticReportResult(report, markdown, githubUrl);
    }

    private Symptom? ResolveSymptom(string? symptomId)
    {
        if (string.IsNullOrEmpty(symptomId)) return null;
        foreach (var s in _symptoms.All)
            if (string.Equals(s.Id, symptomId, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    private static string? TrimFreeText(string? freeText)
    {
        if (string.IsNullOrWhiteSpace(freeText)) return null;
        var trimmed = freeText.Trim();
        if (trimmed.Length > FreeTextMaxChars)
            trimmed = trimmed[..FreeTextMaxChars];
        return trimmed;
    }
}
