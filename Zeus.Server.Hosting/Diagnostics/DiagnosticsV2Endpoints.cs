// SPDX-License-Identifier: GPL-2.0-or-later
//
// Live Diagnostics API v2 — registry-driven endpoint surface.
//
// One mapping for the whole platform: every registered IDiagnosticsProvider is
// reachable here without per-feature wiring. Snapshots return object so typed
// providers resolve through the source-gen context (fast) and anonymous ones
// fall through to reflection. Self-checks are served from the off-path cache.

namespace Zeus.Server.Diagnostics;

public static class DiagnosticsV2Endpoints
{
    /// <summary>Maps the unified <c>/api/diagnostics/v2</c> surface. Called from MapZeusEndpoints.</summary>
    public static WebApplication MapDiagnosticsV2(this WebApplication app)
    {
        const string b = DiagnosticsProviderRegistry.BaseRoute;

        // Provider index — metadata only, no snapshots taken.
        app.MapGet(b, (DiagnosticsProviderRegistry registry) =>
            Results.Ok(registry.BuildIndex(DateTimeOffset.UtcNow)));

        // Aggregate health (worst-of across providers). Literal route beats the
        // {segment} param in ASP.NET routing, so this never collides below.
        app.MapGet($"{b}/health", (DiagnosticsSelfCheckCache cache) =>
            Results.Ok(cache.BuildHealth()));

        // Single provider snapshot.
        app.MapGet($"{b}/{{segment}}", (string segment, DiagnosticsProviderRegistry registry) =>
            registry.TryGetByRoute(segment, out var provider)
                ? Results.Ok(provider.Snapshot())
                : Results.NotFound(new { error = "unknown-diagnostics-provider", segment }));

        // Cached self-check report for one provider.
        app.MapGet($"{b}/{{segment}}/selfcheck", (string segment, DiagnosticsProviderRegistry registry, DiagnosticsSelfCheckCache cache) =>
            registry.TryGetByRoute(segment, out var provider)
                ? Results.Ok(cache.GetReport(provider, forceRefresh: false))
                : Results.NotFound(new { error = "unknown-diagnostics-provider", segment }));

        // Force a fresh self-check run for one provider (still off the realtime path).
        app.MapPost($"{b}/{{segment}}/selfcheck", (string segment, DiagnosticsProviderRegistry registry, DiagnosticsSelfCheckCache cache) =>
            registry.TryGetByRoute(segment, out var provider)
                ? Results.Ok(cache.GetReport(provider, forceRefresh: true))
                : Results.NotFound(new { error = "unknown-diagnostics-provider", segment }));

        return app;
    }
}
