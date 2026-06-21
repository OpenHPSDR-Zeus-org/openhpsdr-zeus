// SPDX-License-Identifier: GPL-2.0-or-later
//
// Layer-3 of the diagnostics test framework: a reflection/registry conformance
// harness. It enumerates EVERY registered IDiagnosticsProvider from the live DI
// container and asserts each one conforms to the v2 contract. The point is
// AUTOMATIC coverage: a new feature that registers a provider is tested here
// with no new test file. (Layer 2, the source generator, additionally emits a
// per-provider NAMED test so each shows up individually in the runner.)

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Zeus.Server.Diagnostics;

namespace Zeus.Server.Tests;

public sealed class DiagnosticsConformanceTests : IClassFixture<DiagnosticsConformanceTests.Factory>
{
    private readonly Factory _factory;
    public DiagnosticsConformanceTests(Factory factory) => _factory = factory;

    public sealed class Factory : IsolatedPrefsFactory { }

    private const string Base = "/api/diagnostics/v2";

    [Fact]
    public void Registry_HasProviders_WithUniqueIdsAndRoutes()
    {
        var registry = _factory.Services.GetRequiredService<DiagnosticsProviderRegistry>();
        Assert.NotEmpty(registry.All);

        var ids = registry.All.Select(p => p.Id).ToArray();
        var routes = registry.All.Select(p => p.RouteSegment).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(routes.Length, routes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(registry.All, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Id));
            Assert.False(string.IsNullOrWhiteSpace(p.RouteSegment));
            Assert.False(string.IsNullOrWhiteSpace(p.Category));
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
        });
    }

    [Fact]
    public async Task Index_ListsEveryProvider()
    {
        var registry = _factory.Services.GetRequiredService<DiagnosticsProviderRegistry>();
        using var client = _factory.CreateClient();

        using var doc = await GetJson(client, Base);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(registry.All.Count, root.GetProperty("providerCount").GetInt32());

        var listed = root.GetProperty("providers").EnumerateArray()
            .Select(p => p.GetProperty("id").GetString())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var p in registry.All)
            Assert.Contains(p.Id, listed);
    }

    // The keystone: every provider's snapshot + self-check endpoints must conform.
    // Looping (not MemberData) keeps it to a single shared host; failures are
    // collected with provider context so one bad provider names itself.
    [Fact]
    public async Task EveryProvider_Snapshot_And_SelfCheck_Conform()
    {
        var registry = _factory.Services.GetRequiredService<DiagnosticsProviderRegistry>();
        using var client = _factory.CreateClient();
        var failures = new List<string>();

        foreach (var provider in registry.All)
        {
            var snapUrl = $"{Base}/{provider.RouteSegment}";
            try
            {
                using var snap = await GetJson(client, snapUrl);
                if (!snap.RootElement.TryGetProperty("schemaVersion", out _))
                    failures.Add($"{provider.Id}: snapshot missing camelCase 'schemaVersion' ({snapUrl})");
            }
            catch (Exception ex)
            {
                failures.Add($"{provider.Id}: snapshot GET failed: {ex.Message}");
            }

            try
            {
                using var report = await GetJson(client, $"{snapUrl}/selfcheck");
                var r = report.RootElement;
                if (r.GetProperty("providerId").GetString() != provider.Id)
                    failures.Add($"{provider.Id}: self-check report providerId mismatch");
                var worst = r.GetProperty("worst").GetString();
                if (worst is not ("pass" or "warn" or "fail"))
                    failures.Add($"{provider.Id}: invalid worst outcome '{worst}'");
                foreach (var check in r.GetProperty("checks").EnumerateArray())
                {
                    var outcome = check.GetProperty("outcome").GetString();
                    if (outcome is not ("pass" or "warn" or "fail"))
                        failures.Add($"{provider.Id}: invalid check outcome '{outcome}'");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{provider.Id}: self-check GET failed: {ex.Message}");
            }

            // POST forces a fresh run; must also succeed (off-path, never 500).
            var post = await client.PostAsync($"{snapUrl}/selfcheck", new StringContent("", Encoding.UTF8));
            if (post.StatusCode != HttpStatusCode.OK)
                failures.Add($"{provider.Id}: POST selfcheck returned {(int)post.StatusCode}");
        }

        Assert.True(failures.Count == 0,
            "Diagnostics provider conformance failures:\n  " + string.Join("\n  ", failures));
    }

    [Fact]
    public async Task Health_AggregatesAllProviders()
    {
        var registry = _factory.Services.GetRequiredService<DiagnosticsProviderRegistry>();
        using var client = _factory.CreateClient();

        using var doc = await GetJson(client, $"{Base}/health");
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Contains(root.GetProperty("overall").GetString(), new[] { "pass", "warn", "fail" });
        Assert.Equal(registry.All.Count, root.GetProperty("providerCount").GetInt32());
        Assert.Equal(registry.All.Count, root.GetProperty("providers").GetArrayLength());

        int pass = root.GetProperty("passCount").GetInt32();
        int warn = root.GetProperty("warnCount").GetInt32();
        int fail = root.GetProperty("failCount").GetInt32();
        Assert.Equal(registry.All.Count, pass + warn + fail);
    }

    [Fact]
    public async Task UnknownProvider_Returns404()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync($"{Base}/this-provider-does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // Verifies the source-gen JSON resolver chain didn't break the existing
    // reflection path: a legacy enum-bearing payload still serialises as a
    // string, and a v2 typed DTO is camelCase.
    [Fact]
    public async Task ResolverChain_PreservesStringEnums_AndCamelCase()
    {
        using var client = _factory.CreateClient();

        using var state = await GetJson(client, "/api/state");
        // Mode is an enum on StateDto; the global JsonStringEnumConverter must
        // still render it as a string (e.g. "USB"), not a number.
        var mode = state.RootElement.GetProperty("mode");
        Assert.Equal(JsonValueKind.String, mode.ValueKind);

        using var live = await GetJson(client, $"{Base}/dsp-live");
        // Envelope is camelCase first-letter-lowercase.
        Assert.True(live.RootElement.TryGetProperty("schemaVersion", out _));
        Assert.False(live.RootElement.TryGetProperty("SchemaVersion", out _));
        // The nested typed DTO (source-gen context) is also camelCase.
        var snap = live.RootElement.GetProperty("snapshot");
        Assert.True(snap.TryGetProperty("schemaVersion", out _));
        Assert.False(snap.TryGetProperty("SchemaVersion", out _));
    }

    // Backward-compat: the v2 route and the legacy route are backed by the same
    // builder, so structural fields match.
    [Fact]
    public async Task V2DspLive_MatchesLegacyRoute()
    {
        using var client = _factory.CreateClient();
        using var v2 = await GetJson(client, $"{Base}/dsp-live");
        using var legacy = await GetJson(client, "/api/dsp/live-diagnostics");

        // The v2 route wraps the same payload in an envelope; the bare legacy
        // payload must match what's under `snapshot`.
        var v2Snap = v2.RootElement.GetProperty("snapshot");
        Assert.Equal(
            legacy.RootElement.GetProperty("schemaVersion").GetInt32(),
            v2Snap.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            legacy.RootElement.GetProperty("status").GetString(),
            v2Snap.GetProperty("status").GetString());
    }

    [Fact]
    public async Task V2TxDiagnostics_MatchesLegacyRoute()
    {
        using var client = _factory.CreateClient();
        using var v2 = await GetJson(client, $"{Base}/tx");
        using var legacy = await GetJson(client, "/api/tx/diag");

        var v2Snap = v2.RootElement.GetProperty("snapshot");
        Assert.Equal(
            legacy.RootElement.GetProperty("iqSourceIsRing").GetBoolean(),
            v2Snap.GetProperty("iqSourceIsRing").GetBoolean());
        Assert.Equal(
            legacy.RootElement.GetProperty("audioPath").GetProperty("status").GetString(),
            v2Snap.GetProperty("audioPath").GetProperty("status").GetString());
        Assert.Equal(
            legacy.RootElement.GetProperty("egress").GetProperty("healthStatus").GetString(),
            v2Snap.GetProperty("egress").GetProperty("healthStatus").GetString());
    }

    private static async Task<JsonDocument> GetJson(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
    }
}
