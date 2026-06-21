// SPDX-License-Identifier: GPL-2.0-or-later
//
// Live Diagnostics API v2 — the provider registry.
//
// Built ONCE at startup from every IDiagnosticsProvider registered in DI.
// Validates id/route-segment uniqueness and non-emptiness in the constructor so
// a misconfigured provider fails fast at boot rather than at first request.
// Lookups go through a FrozenDictionary for O(1) access with no per-request
// reflection or LINQ-over-DI — the request hot path stays allocation-light.

using System.Collections.Frozen;
using Zeus.Contracts;

namespace Zeus.Server.Diagnostics;

public sealed class DiagnosticsProviderRegistry
{
    /// <summary>Base route the v2 surface is mounted at.</summary>
    public const string BaseRoute = "/api/diagnostics/v2";

    private const int IndexSchemaVersion = 1;

    private readonly FrozenDictionary<string, IDiagnosticsProvider> _byId;
    private readonly FrozenDictionary<string, IDiagnosticsProvider> _byRoute;

    public DiagnosticsProviderRegistry(IEnumerable<IDiagnosticsProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var ordered = providers
            .OrderBy(static p => p.Id, StringComparer.Ordinal)
            .ToArray();

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ordered)
        {
            if (string.IsNullOrWhiteSpace(p.Id))
                throw new InvalidOperationException(
                    $"Diagnostics provider {p.GetType().FullName} has an empty Id.");
            if (string.IsNullOrWhiteSpace(p.RouteSegment))
                throw new InvalidOperationException(
                    $"Diagnostics provider '{p.Id}' has an empty RouteSegment.");
            if (!seenIds.Add(p.Id))
                throw new InvalidOperationException(
                    $"Duplicate diagnostics provider Id '{p.Id}'. Provider ids must be unique.");
            if (!seenRoutes.Add(p.RouteSegment))
                throw new InvalidOperationException(
                    $"Duplicate diagnostics provider RouteSegment '{p.RouteSegment}' (id '{p.Id}'). Route segments must be unique.");
        }

        All = ordered;
        _byId = ordered.ToFrozenDictionary(static p => p.Id, StringComparer.Ordinal);
        _byRoute = ordered.ToFrozenDictionary(static p => p.RouteSegment, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every registered provider, ordered by id. Stable for the process lifetime.</summary>
    public IReadOnlyList<IDiagnosticsProvider> All { get; }

    /// <summary>O(1) lookup by provider id (stable across renames; used by tests + self-check cache).</summary>
    public bool TryGet(string id, out IDiagnosticsProvider provider)
    {
        if (!string.IsNullOrEmpty(id))
            return _byId.TryGetValue(id, out provider!);
        provider = null!;
        return false;
    }

    /// <summary>O(1) lookup by URL route segment (what the v2 endpoints bind to).</summary>
    public bool TryGetByRoute(string routeSegment, out IDiagnosticsProvider provider)
    {
        if (!string.IsNullOrEmpty(routeSegment))
            return _byRoute.TryGetValue(routeSegment, out provider!);
        provider = null!;
        return false;
    }

    /// <summary>
    /// Builds the provider index (metadata only — cheap, no snapshots taken).
    /// <paramref name="generatedUtc"/> is injected so callers control the clock
    /// (and so it stays test-deterministic where needed).
    /// </summary>
    public DiagnosticsIndexDto BuildIndex(DateTimeOffset generatedUtc)
    {
        var infos = new DiagnosticsProviderInfoDto[All.Count];
        for (var i = 0; i < All.Count; i++)
        {
            var p = All[i];
            infos[i] = new DiagnosticsProviderInfoDto(
                SchemaVersion: 1,
                Id: p.Id,
                RouteSegment: p.RouteSegment,
                Category: p.Category,
                Description: p.Description,
                ProviderSchemaVersion: p.SchemaVersion,
                SnapshotUrl: $"{BaseRoute}/{p.RouteSegment}",
                SelfCheckUrl: $"{BaseRoute}/{p.RouteSegment}/selfcheck",
                SelfCheckCount: p.SelfChecks.Count);
        }

        return new DiagnosticsIndexDto(
            SchemaVersion: IndexSchemaVersion,
            GeneratedUtc: generatedUtc,
            ProviderCount: infos.Length,
            Providers: infos);
    }
}
