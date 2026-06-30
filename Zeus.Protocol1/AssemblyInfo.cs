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

[assembly: InternalsVisibleTo("Zeus.Protocol1.Tests")]
// The external-port encoder seam (Zeus.Server.Hosting.IExternalPortEncoder)
// delegates to ControlFrame's pure antenna-bit helpers so the firewall and the
// wire path share one copy of the math — byte-identical by construction.
[assembly: InternalsVisibleTo("Zeus.Server.Hosting")]
// The virtual-radio emulator is the MIRROR side of these wire codecs: it
// decodes EP2 (ControlFrame) and encodes EP6 (PacketParser). It consumes the
// framing CONSTANTS / byte offsets / CcRegister + CcState shapes directly so
// the emulator and the real parsers can never drift — any wire-format change
// breaks the emulator's compile or its round-trip tests. See the Virtual HPSDR
// Radio plan (Phase 0/1).
[assembly: InternalsVisibleTo("Zeus.VirtualRadio")]
// The emulator's anti-drift round-trip tests live in Zeus.VirtualRadio.Tests and
// must call the REAL parsers (PacketParser.TryParsePacket) and encoders
// (ControlFrame.BuildDataPacket / WriteCcBytes) directly to prove the emulator's
// EP6 encode / EP2 decode invert them byte-for-byte. Without this the round-trip
// guarantee can only be self-consistent, not validated against Zeus's own wire
// code. INTEGRATOR: added by the wire-codec implementer; confirm placement.
[assembly: InternalsVisibleTo("Zeus.VirtualRadio.Tests")]
