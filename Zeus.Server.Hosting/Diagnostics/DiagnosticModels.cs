// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server.Diagnostics;

/// <summary>
/// Self-diagnostic report contracts. The "Report a problem" footer button
/// asks the backend to assemble one of these: the operator declares a symptom
/// (and optional free text), the backend runs the matching <see cref="IDiagnosticProbe"/>
/// recipe + <see cref="IKnownIssueRule"/> set, and returns a redacted,
/// paste-ready report plus a prefilled GitHub-issue URL.
///
/// Everything here is wire DTO shape — keep it serialisation-stable.
/// </summary>

/// <summary>Finding severity, ordered least → most urgent for sorting.</summary>
public enum DiagnosticSeverity
{
    Info = 0,
    Warning = 1,
    /// <summary>A known-issue rule's best guess at the operator's root cause.</summary>
    Likely = 2,
    Critical = 3,
}

/// <summary>A single collected key/value (already redacted).</summary>
public sealed record DiagnosticKeyValue(string Key, string Value);

/// <summary>One probe's contribution: a titled group of key/values.</summary>
public sealed record DiagnosticSection(
    string Id,
    string Title,
    IReadOnlyList<DiagnosticKeyValue> Items);

/// <summary>
/// A rule's conclusion: a human-readable likely-cause or warning, optionally
/// pointing at a docs/ lesson or RCA the operator (or maintainer) can read.
/// </summary>
public sealed record DiagnosticFinding(
    string Title,
    string Detail,
    DiagnosticSeverity Severity,
    string? DocRef = null);

/// <summary>A selectable symptom shown in the report modal's picker.</summary>
public sealed record Symptom(string Id, string Label, string Group);

/// <summary>Request body for <c>POST /api/diagnostics/report</c>.</summary>
public sealed record DiagnosticRequest(string? SymptomId, string? FreeText);

/// <summary>The assembled report (the JSON half of the result).</summary>
public sealed record DiagnosticReport(
    string SchemaVersion,
    DateTimeOffset GeneratedUtc,
    string? SymptomId,
    string? SymptomLabel,
    string? FreeText,
    IReadOnlyList<DiagnosticSection> Sections,
    IReadOnlyList<DiagnosticFinding> Findings,
    IReadOnlyList<string> RecentLog);

/// <summary>
/// What <c>POST /api/diagnostics/report</c> returns: the structured report,
/// a paste-ready Markdown rendering, and a prefilled GitHub "new issue" URL
/// (body may be trimmed to stay under GitHub's URL length ceiling — the full
/// detail always lives in <see cref="Markdown"/>, which the UI copies).
/// </summary>
public sealed record DiagnosticReportResult(
    DiagnosticReport Report,
    string Markdown,
    string GithubIssueUrl);
