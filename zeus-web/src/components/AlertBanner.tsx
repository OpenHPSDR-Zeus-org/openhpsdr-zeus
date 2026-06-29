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

import { useTxStore } from '../state/tx-store';

// PRD FR-6: server-generated amber banner shown until the operator dismisses.
// Single-hue amber per the project color convention — no rose/red even for a
// protection event. The trip itself is already visible (MOX drops, meters
// zero); the banner's job is to explain why.
export function AlertBanner() {
  const alert = useTxStore((s) => s.alert);
  const setAlert = useTxStore((s) => s.setAlert);
  // Always render a container so the parent grid keeps a fixed number of
  // child rows. Returning null collapsed the 1fr panadapter track because
  // the 8-row template outran the 7 DOM children, leaving the panadapter
  // container pinned to an `auto` row with 0 intrinsic height.
  // Always render something so the parent grid keeps a stable row count —
  // returning null collapses the grid track below (see App.tsx grid-template).
  if (alert == null) return <div className="alert-banner" aria-hidden style={{ height: 0 }} />;
  return (
    <div
      className="alert-banner"
      role="alert"
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 10,
        padding: '6px 10px',
        margin: '0 6px',
        background: 'linear-gradient(180deg, rgba(255,160,40,0.18), rgba(180,84,16,0.18))',
        border: '1px solid rgba(255,160,40,0.45)',
        borderRadius: 'var(--r-md)',
        color: 'var(--power)',
        fontSize: 12,
      }}
    >
      <span className="label-xs" style={{ color: 'var(--power)' }}>
        ALERT
      </span>
      <span className="mono" style={{ flex: 1, color: 'var(--fg-0)' }}>
        {alert.message}
      </span>
      {alert.action && (
        <button type="button" onClick={alert.action.onClick} className="btn sm">
          {alert.action.label}
        </button>
      )}
      <button type="button" onClick={() => setAlert(null)} className="btn sm">
        Dismiss
      </button>
    </div>
  );
}
