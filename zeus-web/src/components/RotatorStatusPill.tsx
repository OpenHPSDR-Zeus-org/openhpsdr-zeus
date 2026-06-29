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

import { useRotatorStore } from '../state/rotator-store';

export function RotatorStatusPill() {
  const status = useRotatorStore((s) => s.status);

  // Backend status is authoritative. The `config` field is only the local
  // form-default mirror from localStorage and is empty on any client that
  // hasn't gone through the settings flow before — reading `enabled` from
  // it would render "off" on a fresh phone even when the backend is happily
  // talking to rotctld.
  const enabled = !!status?.enabled;
  const connected = !!status?.connected;
  const moving = !!status?.moving;
  const currentAz = status?.currentAz;
  const targetAz = status?.targetAz;

  // Pill label mirrors log4ym's feel: off / connecting / NNN° / → NNN°.
  let label: string;
  let pillClass: string;
  if (!enabled) {
    label = 'Rotator: off';
    pillClass = 'bg-neutral-800 text-neutral-400 border border-neutral-600/60';
  } else if (!connected) {
    label = 'Rotator: …';
    pillClass = 'bg-amber-700/30 text-amber-200 border border-amber-500/70';
  } else if (moving && targetAz != null) {
    label = `Rotator: ${formatAz(currentAz)} → ${formatAz(targetAz)}`;
    pillClass = 'bg-cyan-700/40 text-cyan-200 border border-cyan-500/70';
  } else {
    label = `Rotator: ${formatAz(currentAz)}`;
    pillClass = 'bg-emerald-700/50 text-emerald-200 border border-emerald-600/70';
  }

  return (
    <button
      type="button"
      onClick={() => {
        window.location.hash = 'rotator';
      }}
      className={`${pillClass} rounded px-2 py-0.5 text-xs hover:brightness-125`}
      title="Open Rotator settings"
    >
      {connected ? '●' : '○'} {label}
    </button>
  );
}

function formatAz(az: number | null | undefined): string {
  if (az == null || !Number.isFinite(az)) return '—';
  // hamlib can report signed azimuths when the rotator crosses its zero
  // point (e.g. -79° on a rotor that can swing past 0°). For display we
  // want the equivalent 0..359 heading so the compass-style reading is
  // unambiguous (−79° → 281°).
  const normalized = ((az % 360) + 360) % 360;
  return `${normalized.toFixed(0).padStart(3, '0')}°`;
}
