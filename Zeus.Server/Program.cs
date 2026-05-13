// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
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
// Wüllen (DL1YCF); and by DeskHPSDR (https://github.com/dl1bz/deskhpsdr),
// maintained by Heiko (DL1BZ). Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using Zeus.Server;

// 5000 is claimed by macOS ControlCenter (AirPlay receiver) by default,
// which replies 403 before Kestrel ever sees the request. 6060 is a
// stable free port across macOS/Linux/Windows for local dev and avoids
// conflicting with the user's Log4YM project (which also binds :5050).
// ZEUS_PORT overrides the default (used by the /run skill's portOffset).
var httpPort = int.TryParse(Environment.GetEnvironmentVariable("ZEUS_PORT"), out var zp) ? zp : 6060;
// PERF_PASS_3_DEBUG: allow disabling HTTPS + LAN bind for a second instance
// on the same box (Brian's main session keeps :6443/40001). Uncommitted.
var perfTest = Environment.GetEnvironmentVariable("ZEUS_PERF_TEST") == "1";

var options = new ZeusHostOptions
{
    HttpPort = httpPort,
    BindAllInterfaces = !perfTest,
    UseHttpsLanCert = !perfTest,
    PrintConsoleBanner = true,
};

return await ZeusHost.RunAsync(args, options);

// Anchor for Microsoft.AspNetCore.Mvc.Testing's WebApplicationFactory<Program>
// — it reflects on this type to discover the host pipeline. Tests that swap
// services (LevelerMaxGainEndpointTests, MicGainEndpointTests) rely on this.
public partial class Program;
