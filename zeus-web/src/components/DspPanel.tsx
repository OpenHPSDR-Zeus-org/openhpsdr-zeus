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

import { useCallback, useEffect, useRef } from 'react';
import {
  setNr,
  type NbMode,
  type NrConfigDto,
  type NrMode,
} from '../api/client';
import { getNoiseFloor, getSignalConfidence } from '../dsp/signal-estimator';
import { labelSmartNrProfile, recommendSmartNr, shapeSmartNrRecommendation } from '../dsp/smart-nr';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';
import { useSmartNrStore } from '../state/smart-nr-store';
import { Slider } from './design/Slider';
import { NrSettingsSection, type NrSettingsMode } from './nr/NrSettingsSection';

// Leveler max-gain moved to TxFilterPanel (alongside DRV/TUN/MIC) — it's
// a TX-only stage and lives with the other TX controls now.

// Mirrors NrControls.tsx — cycle order matches Thetis WDSP semantics. NR3
// (RNNR) is intentionally skipped — see issue #79. The four modes are
// mutually exclusive in WDSP so they all ride the single nrMode.
const NR_CYCLE: readonly NrMode[] = ['Off', 'Anr', 'Emnr', 'Sbnr'];
const NR_LABEL: Record<NrMode, string> = {
  Off: 'NR',
  Anr: 'NR',
  Emnr: 'NR2',
  Sbnr: 'NR4',
};

function nrButtonTitle(mode: NrMode): string {
  switch (mode) {
    case 'Off': return 'Noise reduction off (right-click for tunables)';
    case 'Anr': return 'NR1 (ANR, time-domain LMS) — right-click for tunables';
    case 'Emnr': return 'NR2 (EMNR, spectral) — right-click for tunables';
    case 'Sbnr': return 'NR4 (SBNR, libspecbleach) — right-click for tunables';
  }
}

// NR1 / NR2 / NR4 each have a tunables panel. NR4 panel was suppressed
// pre-#162 (libwdsp didn't export SetRXASBNR*); now that Phase 1 binaries
// ship the symbols on linux-x64 + win-x64, the panel is reachable again.
// Mirrors NrControls.tsx.
function settingsModeFor(nrMode: NrMode): NrSettingsMode {
  if (nrMode === 'Anr' || nrMode === 'Emnr' || nrMode === 'Sbnr') return nrMode;
  return 'Emnr';
}

function hasNrSettings(nrMode: NrMode): boolean {
  return nrMode === 'Anr' || nrMode === 'Emnr' || nrMode === 'Sbnr';
}

const NB_CYCLE: readonly NbMode[] = ['Off', 'Nb1', 'Nb2'];
const NB_LABEL: Record<NbMode, string> = {
  Off: 'NB',
  Nb1: 'NB1',
  Nb2: 'NB2',
};

function round1(v: number): number {
  return Math.round(v * 10) / 10;
}

export function DspPanel() {
  const nr = useConnectionStore((s) => s.nr);
  const setLocalNr = useConnectionStore((s) => s.setNr);
  const applyState = useConnectionStore((s) => s.applyState);
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const mode = useConnectionStore((s) => s.mode);
  const smartNrMode = useSmartNrStore((s) => s.automationMode);
  const smartNrStatus = useSmartNrStore((s) => s.status);
  const setSmartNrMode = useSmartNrStore((s) => s.setAutomationMode);
  const setSmartNrStatus = useSmartNrStore((s) => s.setStatus);

  const inflightAbort = useRef<AbortController | null>(null);

  useEffect(
    () => () => {
      inflightAbort.current?.abort();
    },
    [],
  );

  const send = useCallback(
    (next: NrConfigDto) => {
      setLocalNr(next);
      inflightAbort.current?.abort();
      const ac = new AbortController();
      inflightAbort.current = ac;
      setNr(next, ac.signal)
        .then((s) => {
          if (!ac.signal.aborted) applyState(s);
        })
        .catch(() => {
          /* next state poll will reconcile */
        });
    },
    [setLocalNr, applyState],
  );

  const cycleNr = useCallback(() => {
    if (!connected) return;
    const idx = NR_CYCLE.indexOf(nr.nrMode);
    const nextIdx = (idx < 0 ? 0 : idx + 1) % NR_CYCLE.length;
    send({ ...nr, nrMode: NR_CYCLE[nextIdx]! });
  }, [nr, send, connected]);

  const cycleNb = useCallback(() => {
    const idx = NB_CYCLE.indexOf(nr.nbMode);
    const nextIdx = (idx < 0 ? 0 : idx + 1) % NB_CYCLE.length;
    send({ ...nr, nbMode: NB_CYCLE[nextIdx]! });
  }, [nr, send]);

  const setNbThreshold = useCallback(
    (v: number) => send({ ...nr, nbThreshold: v }),
    [nr, send],
  );

  const toggleAnf = useCallback(
    () => send({ ...nr, anfEnabled: !nr.anfEnabled }),
    [nr, send],
  );
  const toggleSnb = useCallback(
    () => send({ ...nr, snbEnabled: !nr.snbEnabled }),
    [nr, send],
  );
  const toggleNbp = useCallback(
    () => send({ ...nr, nbpNotchesEnabled: !nr.nbpNotchesEnabled }),
    [nr, send],
  );

  const applySmartNr = useCallback(() => {
    if (!connected) return;
    if (smartNrMode !== 'manual') {
      setSmartNrMode('manual');
      return;
    }

    setSmartNrMode('auto');
    const display = useDisplayStore.getState();
    const rec = recommendSmartNr({
      spectrum: display.panValid ? display.panDb : null,
      floor: getNoiseFloor(),
      confidence: getSignalConfidence(),
      current: nr,
      mode,
    });
    if (!rec) {
      setSmartNrStatus(null);
      return;
    }
    const shaped = shapeSmartNrRecommendation(rec, useSmartNrStore.getState());
    send(shaped);
    setSmartNrStatus({
      atUtc: new Date().toISOString(),
      profile: labelSmartNrProfile(shaped),
      reason: rec.reason,
      maxSnrDb: round1(rec.condition.maxSnrDb),
      occupancyPct: round1(rec.condition.occupancy6 * 100),
      peakCount: rec.condition.peakCount,
      pending: false,
      applied: true,
      nr: shaped,
    });
  }, [connected, smartNrMode, setSmartNrMode, setSmartNrStatus, nr, mode, send]);

  const applySuggestedSmartNr = useCallback(() => {
    if (!connected || !smartNrStatus?.nr) return;
    send(smartNrStatus.nr);
    setSmartNrStatus({
      ...smartNrStatus,
      atUtc: new Date().toISOString(),
      pending: false,
      applied: true,
    });
  }, [connected, smartNrStatus, send, setSmartNrStatus]);

  const nrActive = nr.nrMode !== 'Off';
  const nbActive = nr.nbMode !== 'Off';

  return (
    <div className="dsp-grid">
      <div className="dsp-row">
        <button
          type="button"
          disabled={!connected}
          onClick={cycleNb}
          className={`btn sm ${nbActive ? 'active' : ''}`}
          title={
            nr.nbMode === 'Off'
              ? 'Noise blanker off'
              : nr.nbMode === 'Nb1'
                ? 'NB1 (time-domain blanker, xanbEXT)'
                : 'NB2 (time-domain blanker, xnobEXT)'
          }
        >
          {NB_LABEL[nr.nbMode]}
        </button>
        <Slider
          label="Thresh"
          value={nr.nbThreshold}
          onChange={setNbThreshold}
          disabled={!connected || !nbActive}
        />
      </div>
      <div className="dsp-row">
        <button
          type="button"
          disabled={!connected}
          onClick={applySmartNr}
          aria-pressed={smartNrMode !== 'manual'}
          className={`btn sm ${smartNrMode !== 'manual' ? 'active' : ''}`}
          title={
            smartNrMode === 'manual'
              ? 'SMART - arm automatic panadapter-driven NR and apply the current recommendation'
              : 'SMART active - click to return NR automation to manual'
          }
        >
          SMART
        </button>
        <button
          type="button"
          onClick={cycleNr}
          aria-disabled={!connected}
          className={`btn sm ${nrActive ? 'active' : ''}`}
          title={nrButtonTitle(nr.nrMode)}
        >
          {NR_LABEL[nr.nrMode]}
        </button>
        <button
          type="button"
          disabled={!connected}
          onClick={toggleAnf}
          className={`btn sm ${nr.anfEnabled ? 'active' : ''}`}
          title="ANF — adaptive auto-notch (time domain)"
        >
          ANF
        </button>
        <button
          type="button"
          disabled={!connected}
          onClick={toggleSnb}
          className={`btn sm ${nr.snbEnabled ? 'active' : ''}`}
          title="SNB — spectral noise blanker"
        >
          SNB
        </button>
        <button
          type="button"
          disabled={!connected}
          onClick={toggleNbp}
          className={`btn sm ${nr.nbpNotchesEnabled ? 'active' : ''}`}
          title="NBP — notch-filter auto-notch (RXA)"
        >
          NBP
        </button>
      </div>
      {smartNrMode !== 'manual' && (
        <div className="dsp-smart-status" title={smartNrStatus?.reason ?? 'Smart NR automation is waiting for spectrum data'}>
          <span className="mono">{smartNrMode.toUpperCase()}</span>
          <span>{smartNrStatus?.profile ?? 'WAIT'}</span>
          {smartNrMode === 'suggest' && smartNrStatus?.nr && !smartNrStatus.pending && !smartNrStatus.applied ? (
            <button
              type="button"
              className="btn sm dsp-smart-apply"
              onClick={applySuggestedSmartNr}
              disabled={!connected}
              title="Apply the suggested Smart NR profile"
            >
              APPLY
            </button>
          ) : smartNrStatus && (
            <span className="mono">
              {smartNrStatus.pending ? 'DWELL' : smartNrStatus.applied ? 'APPLIED' : 'READY'}
            </span>
          )}
        </div>
      )}
      {hasNrSettings(nr.nrMode) && (
        <NrSettingsSection mode={settingsModeFor(nr.nrMode)} />
      )}
    </div>
  );
}
