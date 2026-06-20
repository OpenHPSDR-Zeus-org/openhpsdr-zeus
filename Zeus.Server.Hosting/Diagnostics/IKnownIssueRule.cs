// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server.Diagnostics;

/// <summary>
/// A machine-checkable encoding of one piece of the team's hard-won
/// "symptom → root cause" knowledge (seeded from docs/rca/ and docs/lessons/).
/// Each rule inspects the collected probe sections + recent log + context and,
/// when its pattern matches, emits a <see cref="DiagnosticFinding"/> that turns
/// a raw log dump into "here's what's probably wrong, and here's the doc".
///
/// Rules are registered in DI and discovered via
/// <c>IEnumerable&lt;IKnownIssueRule&gt;</c>. The builder only evaluates rules
/// whose <see cref="Symptoms"/> include the active symptom (or that declare no
/// symptoms, meaning "always relevant"). Rules are read-only like probes.
/// </summary>
public interface IKnownIssueRule
{
    /// <summary>Stable id (e.g. "ps-not-armed", "iq-write-gate", "rx-aux-bypass").</summary>
    string Id { get; }

    /// <summary>
    /// Symptom ids this rule is relevant to. Empty = evaluate for every symptom.
    /// </summary>
    IReadOnlyCollection<string> Symptoms { get; }

    /// <summary>
    /// Inspect the collected sections + context. Return a finding when the rule's
    /// pattern matches, or <c>null</c> when it does not apply. Must not throw.
    /// </summary>
    DiagnosticFinding? Evaluate(DiagnosticContext ctx, IReadOnlyList<DiagnosticSection> sections);
}
