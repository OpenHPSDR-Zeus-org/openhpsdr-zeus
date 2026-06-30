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

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Zeus.Protocol2.Tests")]
// The virtual-radio emulator (Zeus.VirtualRadio) mirrors the Protocol-2 wire
// format from the radio's side. It consumes Protocol2Client internals — the
// canonical paired-feedback de-interleave (DecodePsPairForTest), the CmdRx
// composer (ComposeCmdRxBuffer), and the single-ADC time-mux flag
// (Hermes10ePsTimeMuxOnAir / G2ePsTimeMuxOnAir) — so the emulator and the
// client share ONE definition of every byte offset and a wire-format change
// breaks the build or a round-trip test rather than drifting silently. The
// Tests assembly gets the same access so the socketless round-trip tests can
// feed the emulator's frames through the real client decode path.
[assembly: InternalsVisibleTo("Zeus.VirtualRadio")]
[assembly: InternalsVisibleTo("Zeus.VirtualRadio.Tests")]
// The external-port encoder seam (Zeus.Server.Hosting.IExternalPortEncoder)
// delegates to Protocol2Client's pure antenna-bit helpers so the firewall and
// the wire path share one copy of the math — byte-identical by construction.
[assembly: InternalsVisibleTo("Zeus.Server.Hosting")]
// HardwarePttEndToEndTests drives the hi-priority status seam
// (RaiseHiPriStatusForTest) to integration-test the live P2 PTT-IN → MOX wiring
// across the RadioService / ExternalPttService seams without a UDP socket.
[assembly: InternalsVisibleTo("Zeus.Server.Tests")]
