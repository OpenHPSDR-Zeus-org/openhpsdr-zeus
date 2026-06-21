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
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Verbose RX squelch editor for the DSP settings tab — the full counterpart to
// the inline SQL control (SquelchSlider). Same store + endpoint (setSquelch), so
// edits stay in sync. The squelch is mode-aware: the server routes run +
// threshold to the WDSP stage for the current RX mode (SSB/CW → voice SSQL,
// AM/SAM → AMSQ, FM → FMSQ). This panel surfaces which stage is active and what
// the 0..100 level maps to, so the single control reads as Thetis-verbose.

import { useCallback, useEffect, useRef } from 'react';
import { setSquelch, type RxMode, type SquelchConfigDto } from '../api/client';
import { useConnectionStore } from '../state/connection-store';

type Stage = { name: string; detail: string };

function stageForMode(mode: RxMode): Stage {
  if (mode === 'AM' || mode === 'SAM')
    return { name: 'AM (AMSQ)', detail: 'fixed level → carrier threshold' };
  if (mode === 'FM')
    return { name: 'FM (FMSQ)', detail: 'level → noise-gate threshold' };
  return { name: 'Voice (SSQL)', detail: 'fixed level → syllabic threshold' };
}

export function SquelchSettingsSection() {
  const squelch = useConnectionStore((s) => s.squelch);
  const mode = useConnectionStore((s) => s.mode);
  const setLocalSquelch = useConnectionStore((s) => s.setSquelch);
  const applyState = useConnectionStore((s) => s.applyState);
  const connected = useConnectionStore((s) => s.status === 'Connected');

  const abort = useRef<AbortController | null>(null);
  useEffect(() => () => abort.current?.abort(), []);

  const send = useCallback(
    (next: SquelchConfigDto) => {
      setLocalSquelch(next);
      abort.current?.abort();
      const ac = new AbortController();
      abort.current = ac;
      setSquelch(next, ac.signal)
        .then((s) => !ac.signal.aborted && applyState(s))
        .catch(() => {});
    },
    [setLocalSquelch, applyState],
  );

  const stage = stageForMode(mode);
  const levelDisabled = !connected || squelch.adaptive;
  const fixedSensitivityDisabled = !connected || squelch.adaptive;

  return (
    <div className="dsp-cfg">
      <div className="dsp-cfg-row">
        <span className="dsp-cfg-label">Squelch</span>
        <button
          type="button"
          disabled={!connected}
          aria-pressed={squelch.enabled}
          onClick={() => connected && send({ ...squelch, enabled: !squelch.enabled })}
          className={`btn sm ${squelch.enabled ? 'active' : ''}`}
          title="Toggle RX squelch (mode-aware)"
        >
          {squelch.enabled ? 'ON' : 'OFF'}
        </button>
      </div>

      <label className="dsp-cfg-row">
        <span className="dsp-cfg-label">
          Level
          <span className="dsp-cfg-hint">
            {squelch.adaptive ? ' automatic' : ' higher = tighter'}
          </span>
        </span>
        <input
          type="range"
          min={0}
          max={100}
          step={1}
          value={squelch.level}
          disabled={levelDisabled}
          title={squelch.adaptive ? 'DYN mode tracks the noise floor automatically' : 'Fixed squelch threshold'}
          onChange={(e) => send({ ...squelch, level: Number(e.currentTarget.value) })}
          style={{
            flex: 1,
            cursor: levelDisabled ? 'not-allowed' : 'pointer',
            accentColor: levelDisabled ? 'var(--fg-3)' : 'var(--accent)',
            opacity: levelDisabled ? 0.55 : 1,
          }}
        />
        <span
          className="dsp-cfg-unit mono"
          style={{ color: squelch.adaptive ? 'var(--fg-3)' : undefined }}
        >
          {squelch.adaptive ? 'AUTO' : squelch.level}
        </span>
      </label>

      <label className="dsp-cfg-row">
        <span className="dsp-cfg-label">
          Sensitivity
          <span className="dsp-cfg-hint"> fixed SQL</span>
        </span>
        <input
          type="range"
          min={0}
          max={100}
          step={1}
          value={squelch.fixedSensitivity}
          disabled={fixedSensitivityDisabled}
          title={squelch.adaptive ? 'DYN mode tracks the noise floor automatically' : 'Fixed SQL sensitivity'}
          onChange={(e) => send({ ...squelch, fixedSensitivity: Number(e.currentTarget.value) })}
          style={{
            flex: 1,
            cursor: fixedSensitivityDisabled ? 'not-allowed' : 'pointer',
            accentColor: fixedSensitivityDisabled ? 'var(--fg-3)' : 'var(--accent)',
            opacity: fixedSensitivityDisabled ? 0.55 : 1,
          }}
        />
        <span
          className="dsp-cfg-unit mono"
          style={{ color: fixedSensitivityDisabled ? 'var(--fg-3)' : undefined }}
        >
          {squelch.fixedSensitivity}
        </span>
      </label>

      <div className="dsp-cfg-row">
        <span className="dsp-cfg-label">Mode</span>
        <button
          type="button"
          disabled={!connected}
          aria-pressed={squelch.adaptive}
          onClick={() => connected && send({ ...squelch, adaptive: !squelch.adaptive })}
          className={`btn sm ${squelch.adaptive ? 'active' : ''}`}
          title="Switch between adaptive noise-floor squelch and fixed WDSP squelch"
        >
          {squelch.adaptive ? 'DYN' : 'FIX'}
        </button>
      </div>

      {/* Which WDSP stage the current mode routes to — makes the single
          mode-aware control self-explanatory. */}
      <div className="dsp-cfg-row">
        <span className="dsp-cfg-label">Active</span>
        <span className="dsp-cfg-hint" style={{ flex: 1 }}>
          {squelch.adaptive ? 'Adaptive floor gate' : stage.name} —{' '}
          {squelch.adaptive ? 'opens above the measured band noise' : stage.detail}
        </span>
      </div>
    </div>
  );
}
