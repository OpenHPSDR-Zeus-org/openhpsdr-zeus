// SPDX-License-Identifier: GPL-2.0-or-later
//
// Tests for the diagnostics v2 push path (MsgType 0x36). The hosted
// DiagnosticsFramePublisher itself is stripped under IsolatedPrefsFactory
// (which removes IHostedService), so these exercise the encode + aggregate
// pieces directly: the cache builds health, the publisher encodes the frame,
// and the hub exposes its read-only snapshot.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Zeus.Contracts;
using Zeus.Server.Diagnostics;

namespace Zeus.Server.Tests;

public sealed class DiagnosticsPushTests : IClassFixture<DiagnosticsPushTests.Factory>
{
    private readonly Factory _factory;
    public DiagnosticsPushTests(Factory factory) => _factory = factory;

    public sealed class Factory : IsolatedPrefsFactory { }

    [Fact]
    public void Cache_BuildHealth_AggregatesProviders()
    {
        var registry = _factory.Services.GetRequiredService<DiagnosticsProviderRegistry>();
        var cache = _factory.Services.GetRequiredService<DiagnosticsSelfCheckCache>();

        var health = cache.BuildHealth();
        Assert.Equal(registry.All.Count, health.ProviderCount);
        Assert.Equal(registry.All.Count, health.Providers.Length);
        Assert.Contains(health.Overall, new[] { "pass", "warn", "fail" });
        Assert.Equal(health.ProviderCount, health.PassCount + health.WarnCount + health.FailCount);
    }

    [Fact]
    public void EncodeFrame_PrependsTypeByte_AndIsValidJson()
    {
        var cache = _factory.Services.GetRequiredService<DiagnosticsSelfCheckCache>();
        var health = cache.BuildHealth();

        var frame = DiagnosticsFramePublisher.EncodeFrame(health);

        Assert.True(frame.Length > 1);
        Assert.Equal((byte)MsgType.DiagnosticsHealth, frame[0]);

        using var doc = JsonDocument.Parse(frame.AsMemory(1));
        Assert.Contains(doc.RootElement.GetProperty("overall").GetString(), new[] { "pass", "warn", "fail" });
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        // camelCase from the source-gen context.
        Assert.True(doc.RootElement.TryGetProperty("providers", out _));
    }

    [Fact]
    public void Publisher_IsRegistered_AsSingleton()
    {
        // AddSingleton + AddHostedService(sp => GetRequiredService) means the
        // singleton survives even though IsolatedPrefsFactory strips hosted
        // services — so the publisher is resolvable for direct testing.
        var publisher = _factory.Services.GetService<DiagnosticsFramePublisher>();
        Assert.NotNull(publisher);
    }

    [Fact]
    public void Hub_DiagnosticsSnapshot_HasExpectedShape()
    {
        var hub = _factory.Services.GetRequiredService<StreamingHub>();
        var json = JsonSerializer.Serialize(hub.DiagnosticsSnapshot());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(0, root.GetProperty("connectedClients").GetInt32());
        Assert.True(root.TryGetProperty("drops", out var drops));
        Assert.True(drops.TryGetProperty("audio", out _));
    }
}
