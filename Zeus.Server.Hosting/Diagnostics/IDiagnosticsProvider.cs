// SPDX-License-Identifier: GPL-2.0-or-later
//
// Live Diagnostics API v2 — the provider contract.
//
// A diagnostics provider is the single seam a feature implements to join the
// unified diagnostics surface. Implement it, register it in DI as
// IDiagnosticsProvider, and the feature automatically gets:
//   * a pull endpoint  (GET /api/diagnostics/v2/{id})
//   * a self-check endpoint (GET/POST /api/diagnostics/v2/{id}/selfcheck)
//   * inclusion in the aggregate index + health + live push frame
//   * autonomous test coverage via the conformance harness + source generator
//
// Snapshot() returns object (not a generic DTO) on purpose: several existing
// diagnostics surfaces (e.g. HardwareDiagnosticsService) return anonymous
// types that must serialise verbatim for wire compatibility. Typed providers
// register their DTO with DiagnosticsJsonContext for source-gen serialisation;
// untyped (anonymous) snapshots fall through to the reflection resolver. Both
// paths are camelCase + string-enum identical on the wire.

namespace Zeus.Server.Diagnostics;

/// <summary>Severity attached to a declared self-check (how bad a failure is).</summary>
public enum DiagnosticsSeverity
{
    Info,
    Warn,
    Error,
}

/// <summary>Outcome of running a single self-check probe.</summary>
public enum SelfCheckOutcome
{
    Pass,
    Warn,
    Fail,
}

/// <summary>
/// Result of running one <see cref="DiagnosticsSelfCheck"/>. Cheap, allocation-light
/// record returned by the probe delegate. <paramref name="RanUtc"/> is stamped by
/// the runner so callers can reason about staleness.
/// </summary>
public sealed record SelfCheckResult(
    SelfCheckOutcome Outcome,
    string Detail,
    DateTimeOffset RanUtc);

/// <summary>
/// A declared probe: pure metadata (<paramref name="Id"/>, <paramref name="Description"/>,
/// <paramref name="Severity"/>) plus a delegate-as-data (<paramref name="Run"/>) that the
/// off-path runner invokes. The delegate must be cheap and read snapshots only — it runs
/// on a background timer, never on the realtime/request thread, and must not touch the DSP
/// or TX hot path.
/// </summary>
public sealed record DiagnosticsSelfCheck(
    string Id,
    string Description,
    DiagnosticsSeverity Severity,
    Func<CancellationToken, SelfCheckResult> Run);

/// <summary>
/// The one interface a feature implements to expose live diagnostics. Implementations
/// MUST be cheap, read-only snapshot producers — no DSP work, no blocking I/O on the
/// request path. Register as <c>IDiagnosticsProvider</c> in DI; the
/// <see cref="DiagnosticsProviderRegistry"/> picks every registration up once at startup.
/// </summary>
public interface IDiagnosticsProvider
{
    /// <summary>Stable, unique, kebab/dotted id, e.g. <c>"dsp.live"</c>. Used in URLs and tests.</summary>
    string Id { get; }

    /// <summary>Unique URL-safe path segment, e.g. <c>"dsp-live"</c>. Distinct from <see cref="Id"/>
    /// so the wire route can stay clean while the id stays stable across renames.</summary>
    string RouteSegment { get; }

    /// <summary>Coarse grouping for the index, e.g. <c>"dsp"</c>, <c>"hardware"</c>, <c>"frontend"</c>.</summary>
    string Category { get; }

    /// <summary>Schema version of the snapshot DTO this provider returns.</summary>
    int SchemaVersion { get; }

    /// <summary>Human-readable one-liner describing what this provider reports.</summary>
    string Description { get; }

    /// <summary>
    /// Produces the current snapshot. Returns <see cref="object"/> so providers can keep
    /// returning their existing (sometimes anonymous) DTOs verbatim. Must be allocation-light
    /// and free of realtime/DSP work — read cached state only.
    /// </summary>
    object Snapshot();

    /// <summary>The probes this provider declares. May be empty. Run off the request path by
    /// <see cref="DiagnosticsSelfCheckCache"/>.</summary>
    IReadOnlyList<DiagnosticsSelfCheck> SelfChecks { get; }
}
