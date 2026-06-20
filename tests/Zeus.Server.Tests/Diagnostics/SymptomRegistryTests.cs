// SPDX-License-Identifier: GPL-2.0-or-later
using System.Linq;
using Xunit;
using Zeus.Server.Diagnostics;

namespace Zeus.Server.Tests;

public sealed class SymptomRegistryTests
{
    private readonly SymptomRegistry _reg = new();

    [Fact]
    public void All_HasEightSymptoms_WithExpectedIds()
    {
        var ids = _reg.All.Select(s => s.Id).ToArray();
        Assert.Equal(8, ids.Length);
        Assert.Equal(
            new[]
            {
                "wont-connect", "no-tx-power", "ps-not-working", "tx-audio",
                "rx-no-audio", "rx-audio-quality", "ui-display", "other",
            },
            ids);
    }

    [Fact]
    public void All_EverySymptom_HasLabelAndGroup()
    {
        foreach (var s in _reg.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Label));
            Assert.False(string.IsNullOrWhiteSpace(s.Group));
        }
    }

    [Theory]
    [InlineData("wont-connect", "environment", "connection", "board")]
    [InlineData("no-tx-power", "environment", "connection", "board", "tx-ps")]
    [InlineData("ps-not-working", "environment", "connection", "board", "tx-ps")]
    [InlineData("tx-audio", "environment", "connection", "board", "dsp-audio", "tx-ps")]
    [InlineData("rx-no-audio", "environment", "connection", "board", "dsp-audio")]
    [InlineData("rx-audio-quality", "environment", "connection", "board", "dsp-audio")]
    [InlineData("ui-display", "environment", "connection", "board", "dsp-audio")]
    public void ProbeIdsFor_KnownSymptom_ReturnsExpectedRecipe(
        string symptomId, params string[] expected)
    {
        var probes = _reg.ProbeIdsFor(symptomId);
        Assert.Equal(expected, probes.ToArray());
    }

    [Fact]
    public void ProbeIdsFor_AlwaysIncludesBaseProbes()
    {
        foreach (var s in _reg.All)
        {
            var probes = _reg.ProbeIdsFor(s.Id);
            Assert.Contains("environment", probes);
            Assert.Contains("connection", probes);
            Assert.Contains("board", probes);
        }
    }

    [Theory]
    [InlineData("other")]
    [InlineData(null)]
    [InlineData("totally-unknown-symptom")]
    [InlineData("")]
    public void ProbeIdsFor_OtherNullOrUnknown_ReturnsAllFiveProbes(string? symptomId)
    {
        var probes = _reg.ProbeIdsFor(symptomId);
        Assert.Equal(
            new[] { "environment", "connection", "board", "dsp-audio", "tx-ps" },
            probes.ToArray());
    }
}
