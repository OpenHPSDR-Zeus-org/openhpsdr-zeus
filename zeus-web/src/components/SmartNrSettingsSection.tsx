// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { useCallback, useEffect, useRef } from 'react';
import { setNr } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useSmartNrStore, type SmartNrAutomationMode } from '../state/smart-nr-store';
import { Slider } from './design/Slider';

const MODES: Array<{ id: SmartNrAutomationMode; label: string }> = [
  { id: 'manual', label: 'Manual' },
  { id: 'suggest', label: 'Suggest' },
  { id: 'auto', label: 'Auto' },
];

export function SmartNrSettingsSection() {
  const state = useSmartNrStore();
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const setLocalNr = useConnectionStore((s) => s.setNr);
  const applyState = useConnectionStore((s) => s.applyState);
  const {
    automationMode,
    aggressiveness,
    autoBlankerEnabled,
    autoNotchEnabled,
    maxBlankerThreshold,
    dwellSamples,
    status,
    setAutomationMode,
    setSettings,
    setStatus,
    resetSettings,
  } = state;
  const inflightAbort = useRef<AbortController | null>(null);

  useEffect(
    () => () => {
      inflightAbort.current?.abort();
    },
    [],
  );

  const applySuggestedSmartNr = useCallback(() => {
    if (!connected || !status?.nr) return;
    const nr = status.nr;
    setLocalNr(nr);
    inflightAbort.current?.abort();
    const ac = new AbortController();
    inflightAbort.current = ac;
    setNr(nr, ac.signal)
      .then((s) => {
        if (!ac.signal.aborted) applyState(s);
      })
      .catch(() => {
        /* next state poll will reconcile */
      });
    setStatus({
      ...status,
      atUtc: new Date().toISOString(),
      pending: false,
      applied: true,
    });
  }, [connected, status, setLocalNr, applyState, setStatus]);

  return (
    <div className="smart-nr-settings">
      <div className="sig-intel-top">
        <div className="sig-profile-row" role="group" aria-label="Smart NR automation mode">
          {MODES.map((m) => (
            <button
              key={m.id}
              type="button"
              className="sig-profile-btn"
              aria-pressed={automationMode === m.id}
              onClick={() => setAutomationMode(m.id)}
            >
              {m.label}
            </button>
          ))}
        </div>
        <div className="sig-status mono">
          {automationMode === 'manual' ? 'MANUAL' : status ? `${automationMode.toUpperCase()} ${status.profile}` : automationMode.toUpperCase()}
        </div>
      </div>

      <div className="sig-toggle-row">
        <label className="sig-switch">
          <input
            type="checkbox"
            checked={autoBlankerEnabled}
            onChange={(e) => setSettings({ autoBlankerEnabled: e.currentTarget.checked })}
          />
          <span className="sig-switch-track" aria-hidden="true" />
          <span>Blanker</span>
        </label>
        <label className="sig-switch">
          <input
            type="checkbox"
            checked={autoNotchEnabled}
            onChange={(e) => setSettings({ autoNotchEnabled: e.currentTarget.checked })}
          />
          <span className="sig-switch-track" aria-hidden="true" />
          <span>Notch Helpers</span>
        </label>
        <button type="button" className="sig-reset-btn" onClick={resetSettings}>
          Reset
        </button>
      </div>

      <div className="sig-grid">
        <Slider
          label="Aggression"
          value={aggressiveness}
          min={0}
          max={100}
          onChange={(v) => setSettings({ aggressiveness: Math.round(v) })}
          formatValue={(v) => `${Math.round(v)}%`}
        />
        <Slider
          label="Max NB Threshold"
          value={maxBlankerThreshold}
          min={8}
          max={30}
          onChange={(v) => setSettings({ maxBlankerThreshold: Math.round(v) })}
          formatValue={(v) => Math.round(v).toString()}
        />
        <Slider
          label="Dwell"
          value={dwellSamples}
          min={3}
          max={8}
          onChange={(v) => setSettings({ dwellSamples: Math.round(v) })}
          formatValue={(v) => `${Math.round(v)} frames`}
        />
      </div>

      {status && automationMode !== 'manual' && (
        <div className="smart-nr-status">
          <span className="mono">{status.profile}</span>
          <span>{status.reason}</span>
          {status.capabilityLimited && status.capabilityRecommendation && (
            <span className="smart-nr-rx-advice optimize">
              <span className="mono">DSP CAP</span>
              <span>{status.capabilityRecommendation}</span>
            </span>
          )}
          <span className="mono">
            SNR {status.maxSnrDb.toFixed(1)} dB · OCC {status.occupancyPct.toFixed(1)}% · PK {status.peakCount}
          </span>
          <span className="mono">
            COH {status.coherentOccupancyPct.toFixed(1)}% · CPK {status.coherentPeakCount} · IMP {status.impulsivePct.toFixed(1)}%
          </span>
          {status.rxChainLabel && status.rxChainRecommendation && (
            <span
              className={`smart-nr-rx-advice ${status.rxChainTone === 'protect' ? 'protect' : status.rxChainTone === 'optimize' ? 'optimize' : ''}`}
            >
              <span className="mono">RX {status.heldByRxChain ? 'HOLD' : status.rxChainScore ?? '--'}</span>
              <span>{status.rxChainLabel}: {status.rxChainRecommendation}</span>
            </span>
          )}
          {automationMode === 'suggest' && status.nr && !status.pending && !status.applied && (
            <button
              type="button"
              className="btn sm smart-nr-apply"
              onClick={applySuggestedSmartNr}
              disabled={!connected}
              title="Apply the suggested Smart NR profile"
            >
              Apply
            </button>
          )}
        </div>
      )}
    </div>
  );
}
