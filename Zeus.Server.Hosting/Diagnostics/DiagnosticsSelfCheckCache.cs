// SPDX-License-Identifier: GPL-2.0-or-later
//
// Live Diagnostics API v2 — self-check runner + cache.
//
// Runs each provider's declared probes and caches the report with a short TTL.
// Every probe delegate is wrapped in try/catch + a wall-clock guard so a single
// throwing or slow probe degrades to a Fail result (with detail) instead of
// faulting the endpoint or stalling the publisher. Probes read cached snapshots
// only — they never run on the DSP/realtime path. The background publisher warms
// this cache on its timer; pull endpoints return the warm copy (or recompute the
// cheap snapshot reads under a per-provider lock if the TTL has lapsed).

using System.Diagnostics;
using Zeus.Contracts;

namespace Zeus.Server.Diagnostics;

public sealed class DiagnosticsSelfCheckCache
{
    private const int ReportSchemaVersion = 1;
    private const int HealthSchemaVersion = 1;

    // A single slow probe should not pin the runner. Self-checks are meant to be
    // snapshot reads; anything beyond this is treated as a Fail.
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(2);

    private readonly DiagnosticsProviderRegistry _registry;
    private readonly ILogger<DiagnosticsSelfCheckCache> _log;
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, Entry> _entries;

    public DiagnosticsSelfCheckCache(
        DiagnosticsProviderRegistry registry,
        ILogger<DiagnosticsSelfCheckCache> log)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _ttl = TimeSpan.FromSeconds(3);
        _entries = registry.All.ToDictionary(
            static p => p.Id,
            static p => new Entry(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns the self-check report for one provider. Serves the cached copy when
    /// fresh; otherwise re-runs the (cheap, snapshot-only) probes under a per-provider
    /// lock so concurrent callers don't stampede. <paramref name="forceRefresh"/> bypasses
    /// the TTL (POST .../selfcheck).
    /// </summary>
    public ProviderSelfCheckReportDto GetReport(string providerId, bool forceRefresh = false)
    {
        if (!_registry.TryGet(providerId, out var provider))
            throw new KeyNotFoundException($"Unknown diagnostics provider '{providerId}'.");
        return GetReport(provider, forceRefresh);
    }

    /// <summary>Report for a provider instance (avoids a second registry lookup on the hot path).</summary>
    public ProviderSelfCheckReportDto GetReport(IDiagnosticsProvider provider, bool forceRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var entry = GetEntry(provider.Id);

        lock (entry.Gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (!forceRefresh
                && entry.Cached is { } cached
                && now - entry.GeneratedUtc < _ttl)
            {
                return cached;
            }

            var report = Run(provider, now);
            entry.Cached = report;
            entry.GeneratedUtc = now;
            return report;
        }
    }

    /// <summary>
    /// Aggregate health across every provider — the payload pushed over the hub and
    /// returned by <c>GET /api/diagnostics/v2/health</c>. Reuses warm per-provider
    /// reports where possible.
    /// </summary>
    public DiagnosticsHealthDto BuildHealth(bool forceRefresh = false)
    {
        var providers = _registry.All;
        var reports = new ProviderSelfCheckReportDto[providers.Count];
        int pass = 0, warn = 0, fail = 0;

        for (var i = 0; i < providers.Count; i++)
        {
            var report = GetReport(providers[i], forceRefresh);
            reports[i] = report;
            switch (report.Worst)
            {
                case "fail": fail++; break;
                case "warn": warn++; break;
                default: pass++; break;
            }
        }

        var overall = fail > 0 ? "fail" : warn > 0 ? "warn" : "pass";
        return new DiagnosticsHealthDto(
            SchemaVersion: HealthSchemaVersion,
            GeneratedUtc: DateTimeOffset.UtcNow,
            Overall: overall,
            ProviderCount: reports.Length,
            PassCount: pass,
            WarnCount: warn,
            FailCount: fail,
            Providers: reports);
    }

    private Entry GetEntry(string id)
    {
        // _entries is built once from the frozen registry and never mutated, so
        // reads are safe without locking the dictionary itself.
        if (_entries.TryGetValue(id, out var entry))
            return entry;
        throw new KeyNotFoundException($"Unknown diagnostics provider '{id}'.");
    }

    private ProviderSelfCheckReportDto Run(IDiagnosticsProvider provider, DateTimeOffset generatedUtc)
    {
        var checks = provider.SelfChecks;
        var results = new SelfCheckResultDto[checks.Count];
        var worst = SelfCheckOutcome.Pass;

        for (var i = 0; i < checks.Count; i++)
        {
            var check = checks[i];
            var sw = Stopwatch.StartNew();
            SelfCheckOutcome outcome;
            string detail;
            try
            {
                using var cts = new CancellationTokenSource(ProbeBudget);
                var result = check.Run(cts.Token);
                outcome = result.Outcome;
                detail = result.Detail ?? string.Empty;
            }
            catch (Exception ex)
            {
                // A throwing probe is a failed probe — never a 500. Surface the reason.
                outcome = SelfCheckOutcome.Fail;
                detail = $"probe threw: {ex.GetType().Name}: {ex.Message}";
                _log.LogWarning(ex, "diagnostics self-check '{Provider}/{Check}' threw", provider.Id, check.Id);
            }
            sw.Stop();

            if (outcome > worst) worst = outcome;
            results[i] = new SelfCheckResultDto(
                SchemaVersion: 1,
                Id: check.Id,
                Description: check.Description,
                Severity: SeverityText(check.Severity),
                Outcome: OutcomeText(outcome),
                Detail: detail,
                RanUtc: generatedUtc,
                DurationMs: sw.Elapsed.TotalMilliseconds);
        }

        return new ProviderSelfCheckReportDto(
            SchemaVersion: ReportSchemaVersion,
            ProviderId: provider.Id,
            Worst: OutcomeText(worst),
            GeneratedUtc: generatedUtc,
            Checks: results);
    }

    private static string OutcomeText(SelfCheckOutcome o) => o switch
    {
        SelfCheckOutcome.Fail => "fail",
        SelfCheckOutcome.Warn => "warn",
        _ => "pass",
    };

    private static string SeverityText(DiagnosticsSeverity s) => s switch
    {
        DiagnosticsSeverity.Error => "error",
        DiagnosticsSeverity.Warn => "warn",
        _ => "info",
    };

    private sealed class Entry
    {
        public readonly object Gate = new();
        public ProviderSelfCheckReportDto? Cached;
        public DateTimeOffset GeneratedUtc;
    }
}
