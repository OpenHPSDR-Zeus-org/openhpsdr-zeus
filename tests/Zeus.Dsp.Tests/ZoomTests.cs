// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Text.Json;
using System.Text.Json.Serialization;
using Zeus.Contracts;
using Zeus.Dsp;
using Zeus.Dsp.Wdsp;
using Xunit;

namespace Zeus.Dsp.Tests;

[Collection("Wdsp")]
public class ZoomTests
{
    private static bool WdspAvailable()
    {
        try { return WdspNativeLoader.TryProbe(); }
        catch { return false; }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(33)]
    [InlineData(100)]
    public void Synthetic_SetZoom_RejectsOutOfRange(int level)
    {
        using var eng = new SyntheticDspEngine();
        int id = eng.OpenChannel(192_000, 256);
        Assert.Throws<ArgumentException>(() => eng.SetZoom(id, level));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void Synthetic_SetZoom_AcceptsAllowedLevels(int level)
    {
        using var eng = new SyntheticDspEngine();
        int id = eng.OpenChannel(192_000, 256);
        eng.SetZoom(id, level);
    }

    // Lifecycle test: walk a representative set of levels (powers of two
    // plus a few odd integers and the boundary value) to prove SetAnalyzer
    // reconfig doesn't crash / leak between reconfigs and that the analyzer
    // continues producing pixels after each switch. The analyzer lock
    // serializes reconfig against Spectrum0 and GetPixels, so an in-flight
    // frame-drain never races a SetAnalyzer.
    [SkippableFact]
    public void Wdsp_ZoomLifecycle_WalkAllLevels_DoesNotCrash()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");
        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(192_000, 1024);
        try
        {
            engine.SetZoom(channel, 2);
            engine.SetZoom(channel, 3);
            engine.SetZoom(channel, 4);
            engine.SetZoom(channel, 7);
            engine.SetZoom(channel, 8);
            engine.SetZoom(channel, 16);
            engine.SetZoom(channel, 32);
            engine.SetZoom(channel, 1);

            // Pixel drain should still succeed after the walk — proves the
            // analyzer is in a usable state post-reconfig, not just quietly
            // broken with GetPixels returning stale flags forever.
            var iq = new double[2 * 32 * 1024];
            for (int n = 0; n < iq.Length / 2; n++)
            {
                double phase = 2.0 * Math.PI * 2_000.0 * n / 192_000;
                iq[2 * n] = 0.2 * Math.Cos(phase);
                iq[2 * n + 1] = 0.2 * Math.Sin(phase);
            }
            engine.FeedIq(channel, iq);

            var pan = new float[1024];
            bool got = false;
            for (int i = 0; i < 50 && !got; i++)
            {
                Thread.Sleep(20);
                got = engine.TryGetDisplayPixels(channel, DisplayPixout.Panadapter, pan);
            }
            Assert.True(got, "analyzer stopped producing pixels after zoom lifecycle");
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    [SkippableFact]
    public void Wdsp_SetZoom_RejectsOutOfRange()
    {
        Skip.IfNot(WdspAvailable(), "libwdsp not available");
        using var engine = new WdspDspEngine();
        int channel = engine.OpenChannel(192_000, 1024);
        try
        {
            Assert.Throws<ArgumentException>(() => engine.SetZoom(channel, 0));
            Assert.Throws<ArgumentException>(() => engine.SetZoom(channel, -1));
            Assert.Throws<ArgumentException>(() => engine.SetZoom(channel, 33));
        }
        finally
        {
            engine.CloseChannel(channel);
        }
    }

    [Fact]
    public void ZoomSetRequest_JsonRoundTrip_PreservesLevel()
    {
        var opts = new JsonSerializerOptions();
        opts.Converters.Add(new JsonStringEnumConverter());

        var req = new ZoomSetRequest(4);
        string json = JsonSerializer.Serialize(req, opts);
        var back = JsonSerializer.Deserialize<ZoomSetRequest>(json, opts);

        Assert.NotNull(back);
        Assert.Equal(req, back);
        Assert.Contains("\"Level\":4", json);
    }

    [Fact]
    public void StateDto_DefaultZoomLevel_IsOne()
    {
        var state = new StateDto(
            Status: ConnectionStatus.Disconnected,
            Endpoint: null,
            VfoHz: 14_200_000,
            Mode: RxMode.USB,
            FilterLowHz: 150,
            FilterHighHz: 2850,
            SampleRate: 192_000);

        Assert.Equal(1, state.ZoomLevel);
    }

    [Fact]
    public void StateDto_JsonRoundTrip_PreservesZoomLevel()
    {
        var opts = new JsonSerializerOptions();
        opts.Converters.Add(new JsonStringEnumConverter());

        var state = new StateDto(
            Status: ConnectionStatus.Connected,
            Endpoint: "192.168.1.100:1024",
            VfoHz: 14_200_000,
            Mode: RxMode.USB,
            FilterLowHz: 150,
            FilterHighHz: 2850,
            SampleRate: 192_000,
            ZoomLevel: 4);

        string json = JsonSerializer.Serialize(state, opts);
        var back = JsonSerializer.Deserialize<StateDto>(json, opts);

        Assert.NotNull(back);
        Assert.Equal(4, back!.ZoomLevel);
    }
}
