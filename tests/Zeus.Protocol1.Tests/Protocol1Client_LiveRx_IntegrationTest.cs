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

using System.Net;
using Xunit;

namespace Zeus.Protocol1.Tests;

/// <summary>
/// Live HL2 / ANAN regression harness. Skipped unless <c>ZEUS_LIVE_RADIO=ip[:port]</c>
/// is set in the environment. Do NOT auto-connect to the user's radio in CI.
/// </summary>
public class Protocol1Client_LiveRx_IntegrationTest
{
    [SkippableFact]
    public async Task Streams300FramesAt192k_WithLessThan10PercentDropped()
    {
        var endpoint = ResolveEndpointFromEnv();
        Skip.If(endpoint is null,
            "Set ZEUS_LIVE_RADIO=ip[:port] to exercise this harness. Default port 1024.");

        using var client = new Protocol1Client();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await client.ConnectAsync(endpoint, cts.Token).ConfigureAwait(false);
        await client.StartAsync(
            new StreamConfig(HpsdrSampleRate.Rate192k, PreampOn: false, Atten: HpsdrAtten.Zero),
            cts.Token).ConfigureAwait(false);

        int frameCount = 0;
        await foreach (var frame in client.IqFrames.ReadAllAsync(cts.Token).ConfigureAwait(false))
        {
            Assert.Equal(192_000, frame.SampleRateHz);
            Assert.Equal(PacketParser.ComplexSamplesPerPacket, frame.SampleCount);
            if (++frameCount >= 300) break;
        }

        await client.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);

        long dropped = client.DroppedFrames;
        long total = client.TotalFrames;
        Assert.True(total >= 300, $"expected >=300 parsed frames, got {total}");
        Assert.True(dropped * 10 < total, $"dropped {dropped}/{total} > 10%");
    }

    private static IPEndPoint? ResolveEndpointFromEnv()
    {
        var raw = Environment.GetEnvironmentVariable("ZEUS_LIVE_RADIO");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(':', 2);
        if (!IPAddress.TryParse(parts[0], out var ip)) return null;
        int port = 1024;
        if (parts.Length == 2 && int.TryParse(parts[1], out var parsedPort)) port = parsedPort;
        return new IPEndPoint(ip, port);
    }
}
