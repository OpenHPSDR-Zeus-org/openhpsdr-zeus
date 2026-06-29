// SPDX-License-Identifier: GPL-2.0-or-later
//
// Live Diagnostics API v2 — wire DTOs for the unified diagnostics surface.
//
// These are the aggregate / framework-level shapes (index, self-check report,
// health). Per-provider snapshot DTOs (DspLiveDiagnosticsDto, etc.) keep living
// in Dtos.cs next to their domain. Severity/outcome are carried as strings here
// (not the server-side enums) so Zeus.Contracts stays free of the provider
// abstraction and older clients tolerate new values. SchemaVersion is the first
// field on every DTO per the repo convention.

namespace Zeus.Contracts;

/// <summary>Index entry describing one registered diagnostics provider.</summary>
public sealed record DiagnosticsProviderInfoDto(
    int SchemaVersion,
    string Id,
    string RouteSegment,
    string Category,
    string Description,
    int ProviderSchemaVersion,
    string SnapshotUrl,
    string SelfCheckUrl,
    int SelfCheckCount);

/// <summary>The catalogue of every provider on the v2 surface.</summary>
public sealed record DiagnosticsIndexDto(
    int SchemaVersion,
    DateTimeOffset GeneratedUtc,
    int ProviderCount,
    DiagnosticsProviderInfoDto[] Providers);

/// <summary>Result of a single self-check probe, flattened for the wire.</summary>
public sealed record SelfCheckResultDto(
    int SchemaVersion,
    string Id,
    string Description,
    string Severity,   // info | warn | error
    string Outcome,    // pass | warn | fail
    string Detail,
    DateTimeOffset RanUtc,
    double DurationMs);

/// <summary>All self-check results for one provider plus the worst outcome across them.</summary>
public sealed record ProviderSelfCheckReportDto(
    int SchemaVersion,
    string ProviderId,
    string Worst,      // pass | warn | fail
    DateTimeOffset GeneratedUtc,
    SelfCheckResultDto[] Checks);

/// <summary>Aggregate health across all providers — the payload pushed over the hub at low rate.</summary>
public sealed record DiagnosticsHealthDto(
    int SchemaVersion,
    DateTimeOffset GeneratedUtc,
    string Overall,    // pass | warn | fail
    int ProviderCount,
    int PassCount,
    int WarnCount,
    int FailCount,
    ProviderSelfCheckReportDto[] Providers);
