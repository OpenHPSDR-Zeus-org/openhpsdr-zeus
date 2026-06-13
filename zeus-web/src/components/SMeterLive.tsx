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
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { SMeter } from './SMeter';
import { useTxStore } from '../state/tx-store';

// Replaces SMeterDemo's animated harness with real tx-store telemetry. The
// SMeter component itself is unchanged (discriminated-union presentation
// component from PR #1). TX mode renders forward watts; RX mode renders the
// live rxDbm value pushed from DspPipelineService's 5 Hz RxMeterFrame.
//
// SWR and mic dBfs are surfaced alongside the meter only while MOX is on —
// they're TX-only telemetry and would be misleading under RX.
//
// `hideChips` lets a host (mobile shell) suppress the in-body chip row and
// surface the same telemetry in its own chrome (e.g. the S-Meter section
// header). Without that escape, the chips appear/disappear with TX state and
// shift everything below the meter down on key — see MobileApp.tsx.

export function SMeterLive({ hideChips = false }: { hideChips?: boolean } = {}) {
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const fwdWatts = useTxStore((s) => s.fwdWatts);
  const swr = useTxStore((s) => s.swr);
  const micDbfs = useTxStore((s) => s.micDbfs);
  const rxDbm = useTxStore((s) => s.rxDbm);
  const transmitting = moxOn || tunOn;

  const swrColor = swr >= 3 ? 'var(--tx)' : swr >= 2 ? 'var(--power)' : 'var(--fg-0)';

  return (
    <div style={{ padding: 10, display: 'flex', flexDirection: 'column', gap: 8 }}>
      <div>
        {transmitting ? (
          <SMeter mode="tx" watts={fwdWatts} maxWatts={100} />
        ) : (
          <SMeter mode="rx" dbm={rxDbm} />
        )}
      </div>
      {transmitting && !hideChips && (
        <div style={{ display: 'flex', gap: 10, justifyContent: 'flex-end' }}>
          <span className="chip mono">
            <span className="k">SWR</span>
            <span className="v" style={{ color: swrColor }}>
              {swr.toFixed(2)}
            </span>
          </span>
          <span className="chip mono">
            <span className="k">MIC</span>
            <span className="v">{micDbfs.toFixed(0)} dBfs</span>
          </span>
        </div>
      )}
    </div>
  );
}
