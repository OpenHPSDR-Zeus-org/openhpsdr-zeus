// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server.Diagnostics;

/// <summary>
/// Read-only context handed to every <see cref="IDiagnosticProbe"/> and
/// <see cref="IKnownIssueRule"/> while a report is built. Probes resolve the
/// services they need from <see cref="Services"/> (e.g. <c>RadioService</c>,
/// the capabilities table). NOTHING in the diagnostic path may mutate radio
/// state — this is strictly read-only (global diagnostics rule), and the
/// PureSignal probe in particular may read <c>PsEnabled</c>/HwPeak but must
/// never arm or change PS state (burn-zone).
/// </summary>
public sealed class DiagnosticContext
{
    /// <summary>DI root for probes to resolve backend services.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>The symptom the operator picked, if any.</summary>
    public string? SymptomId { get; init; }

    /// <summary>The operator's free-text description, if any (already redacted).</summary>
    public string? FreeText { get; init; }

    /// <summary>
    /// The last lines of the main log (redacted), newest last. The report ships
    /// the last <see cref="DiagnosticLogBuffer.ReportTailLines"/> (100) of these.
    /// </summary>
    public required IReadOnlyList<string> RecentLog { get; init; }
}
