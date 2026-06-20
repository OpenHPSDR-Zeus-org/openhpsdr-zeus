// SPDX-License-Identifier: GPL-2.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zeus.Server.Diagnostics;

namespace Zeus.Server.Tests;

/// <summary>
/// Every diagnostic probe must be side-effect free and resilient to missing
/// services: with an empty service provider, <c>Collect</c> must return a
/// well-formed section (correct id, non-null items) and never throw. These
/// tests exercise exactly that contract — no radio, DSP, or audio service is
/// registered, so each probe should degrade gracefully rather than fault.
/// </summary>
public sealed class ProbeTests
{
    private static DiagnosticContext EmptyContext() => new()
    {
        Services = new ServiceCollection().BuildServiceProvider(),
        RecentLog = new List<string>(),
    };

    public static IEnumerable<object[]> Probes() => new[]
    {
        new object[] { new EnvironmentProbe(), "environment" },
        new object[] { new ConnectionProbe(), "connection" },
        new object[] { new BoardProbe(), "board" },
        new object[] { new DspAudioProbe(), "dsp-audio" },
        new object[] { new TxPsProbe(), "tx-ps" },
    };

    [Theory]
    [MemberData(nameof(Probes))]
    public void Collect_ReturnsExpectedSection_WithoutThrowing(IDiagnosticProbe probe, string expectedId)
    {
        var ctx = EmptyContext();

        // The probe's advertised id must match the recipe-facing contract.
        Assert.Equal(expectedId, probe.Id);

        // Must not throw out even when every backend service is absent.
        var thrown = Record.Exception(() => probe.Collect(ctx));
        Assert.Null(thrown);
    }

    [Theory]
    [MemberData(nameof(Probes))]
    public void Collect_SectionHasMatchingIdAndNonNullItems(IDiagnosticProbe probe, string expectedId)
    {
        var ctx = EmptyContext();

        DiagnosticSection result = probe.Collect(ctx);

        Assert.NotNull(result);
        Assert.Equal(expectedId, result.Id);
        Assert.False(string.IsNullOrWhiteSpace(result.Title));
        Assert.NotNull(result.Items);
        // A graceful probe always emits at least one item (real data, an
        // "unavailable" marker, or an "error" marker) — never an empty void.
        Assert.NotEmpty(result.Items);
        foreach (var item in result.Items)
        {
            Assert.NotNull(item.Key);
            Assert.NotNull(item.Value);
        }
    }

    [Fact]
    public void EnvironmentProbe_ReportsHostFactsWithoutServices()
    {
        // The environment probe needs no services and should always surface
        // the runtime/version facts regardless of DI contents.
        var section = new EnvironmentProbe().Collect(EmptyContext());

        Assert.Contains(section.Items, kv => kv.Key == "zeus.version");
        Assert.Contains(section.Items, kv => kv.Key == "dotnet.version");
        Assert.Contains(section.Items, kv => kv.Key == "os.architecture");
    }

    [Fact]
    public void ServiceDependentProbes_ReportUnavailable_WhenServicesMissing()
    {
        var ctx = EmptyContext();

        foreach (IDiagnosticProbe probe in new IDiagnosticProbe[]
                 {
                     new ConnectionProbe(),
                     new BoardProbe(),
                     new DspAudioProbe(),
                     new TxPsProbe(),
                 })
        {
            var section = probe.Collect(ctx);
            // With no RadioService/DspPipelineService/NativeAudioSink registered,
            // each probe should mark itself unavailable rather than fabricate data.
            Assert.Contains(section.Items, kv => kv.Value == "unavailable" || kv.Key == "status");
        }
    }
}
