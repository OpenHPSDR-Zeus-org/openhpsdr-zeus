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

// Manual smoke tool: run on a LAN with a real HPSDR Protocol-1 radio to
// confirm discovery works end-to-end. NOT executed in CI.
//
// Usage: dotnet run --project tools/discovery-probe [-- <timeout-seconds>]

using Microsoft.Extensions.Logging.Abstractions;
using Zeus.Contracts;
using Zeus.Protocol1.Discovery;

var timeout = TimeSpan.FromSeconds(args.Length > 0 && double.TryParse(args[0], out var s) ? s : 3.0);
var discovery = new RadioDiscoveryService(NullLogger<RadioDiscoveryService>.Instance);

Console.WriteLine($"Broadcasting discovery; listening for {timeout.TotalSeconds:F1}s...");
var found = await discovery.DiscoverAsync(timeout, CancellationToken.None);

if (found.Count == 0)
{
    Console.WriteLine("No radios responded.");
    return 1;
}

foreach (var r in found)
{
    Console.WriteLine($"{r.Ip,-16} {r.Mac}  {r.Board,-13} fw={r.FirmwareString} busy={r.Details.Busy}");
    if (r.Details.FixedIpEnabled)
    {
        Console.WriteLine($"    HL2 fixed-IP={r.Details.FixedIpAddress} dhcpOverride={r.Details.FixedIpOverridesDhcp}");
    }
    if (r.Details.MacAddressModified)
    {
        Console.WriteLine("    HL2 MAC modified");
    }
}

return 0;
