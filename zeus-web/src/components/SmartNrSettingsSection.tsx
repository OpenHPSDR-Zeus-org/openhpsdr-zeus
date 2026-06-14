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

import { useSmartNrStore, type SmartNrAutomationMode } from '../state/smart-nr-store';
import { Slider } from './design/Slider';

const MODES: Array<{ id: SmartNrAutomationMode; label: string }> = [
  { id: 'manual', label: 'Manual' },
  { id: 'suggest', label: 'Suggest' },
  { id: 'auto', label: 'Auto' },
];

export function SmartNrSettingsSection() {
  const state = useSmartNrStore();
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
    resetSettings,
  } = state;

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
          min={1}
          max={8}
          onChange={(v) => setSettings({ dwellSamples: Math.round(v) })}
          formatValue={(v) => `${Math.round(v)} frames`}
        />
      </div>

      {status && automationMode !== 'manual' && (
        <div className="smart-nr-status">
          <span className="mono">{status.profile}</span>
          <span>{status.reason}</span>
          <span className="mono">
            SNR {status.maxSnrDb.toFixed(1)} dB · OCC {status.occupancyPct.toFixed(1)}% · PK {status.peakCount}
          </span>
        </div>
      )}
    </div>
  );
}
