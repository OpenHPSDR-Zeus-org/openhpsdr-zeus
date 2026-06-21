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
// Zeus is an independent reimplementation in .NET — not a fork. Its WDSP
// integration was informed by studying the Thetis project (the authoritative
// OpenHPSDR reference).
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// Verbose AGC editor for the DSP settings tab — the full counterpart to the
// inline AGC dropdown on the control strip (AgcSlider). Both drive the same
// store + endpoints (setAgc / setAgcTop / setAutoAgc), so edits here and on the
// toolbar stay in sync via the optimistic-send + applyState reconcile. Mirrors
// Thetis's Setup ▸ DSP ▸ AGC/ALC layout: every parameter visible and labelled,
// the canned/custom-locked fields disabled until CUSTOM/FIXED is selected.

import { useCallback, useEffect, useRef } from 'react';
import {
  setAgc,
  setAgcTop,
  setAutoAgc,
  type AgcConfigDto,
  type AgcMode,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';

const AGC_MODES: readonly AgcMode[] = ['Fixed', 'Long', 'Slow', 'Med', 'Fast', 'Custom'];

// Custom/Fixed fallbacks shown when a field is null (Thetis radio.cs §4.3),
// matching the engine's Custom defaults (hang/decay 250, slope 0, thresh 0).
const CUSTOM_DEFAULTS = {
  slope: 0,
  decayMs: 250,
  hangMs: 250,
  hangThreshold: 0,
  fixedGainDb: 20,
} as const;

function Row(props: {
  label: string;
  hint?: string;
  value: number;
  min: number;
  max: number;
  unit?: string;
  disabled: boolean;
  onCommit: (v: number) => void;
}) {
  const { label, hint, value, min, max, unit, disabled, onCommit } = props;
  return (
    <label className="dsp-cfg-row">
      <span className="dsp-cfg-label">
        {label}
        {hint != null && <span className="dsp-cfg-hint"> {hint}</span>}
      </span>
      <input
        type="number"
        className="mono dsp-cfg-num"
        min={min}
        max={max}
        step={1}
        value={value}
        disabled={disabled}
        onChange={(e) => {
          const v = Number(e.currentTarget.value);
          if (Number.isFinite(v)) onCommit(Math.max(min, Math.min(max, v)));
        }}
      />
      {unit != null && <span className="dsp-cfg-unit">{unit}</span>}
    </label>
  );
}

export function AgcSettingsSection() {
  const agc = useConnectionStore((s) => s.agc);
  const agcTopDb = useConnectionStore((s) => s.agcTopDb);
  const offsetDb = useConnectionStore((s) => s.agcOffsetDb);
  const autoEnabled = useConnectionStore((s) => s.autoAgcEnabled);
  const setLocalAgc = useConnectionStore((s) => s.setAgc);
  const applyState = useConnectionStore((s) => s.applyState);
  const connected = useConnectionStore((s) => s.status === 'Connected');

  const abort = useRef<AbortController | null>(null);
  useEffect(() => () => abort.current?.abort(), []);

  const fresh = () => {
    abort.current?.abort();
    const ac = new AbortController();
    abort.current = ac;
    return ac;
  };

  const sendAgc = useCallback(
    (next: AgcConfigDto) => {
      setLocalAgc(next);
      const ac = fresh();
      setAgc(next, ac.signal)
        .then((s) => !ac.signal.aborted && applyState(s))
        .catch(() => {});
    },
    [setLocalAgc, applyState],
  );

  const sendTop = useCallback(
    (top: number) => {
      const ac = fresh();
      setAgcTop(top, ac.signal)
        .then((s) => !ac.signal.aborted && applyState(s))
        .catch(() => {});
    },
    [applyState],
  );

  const toggleAuto = useCallback(() => {
    if (!connected) return;
    const ac = fresh();
    setAutoAgc(!autoEnabled, ac.signal)
      .then((s) => !ac.signal.aborted && applyState(s))
      .catch(() => {});
  }, [autoEnabled, connected, applyState]);

  const isCustom = agc.mode === 'Custom';
  const isFixed = agc.mode === 'Fixed';
  const effectiveTop = Math.round(Math.max(0, Math.min(120, agcTopDb + offsetDb)));
  const topDisabled = !connected || autoEnabled;

  return (
    <div className="dsp-cfg">
      {/* Mode selector — all six, mirrors the inline dropdown. */}
      <div className="dsp-cfg-row">
        <span className="dsp-cfg-label">Mode</span>
        <div className="dsp-cfg-btns">
          {AGC_MODES.map((m) => (
            <button
              key={m}
              type="button"
              disabled={!connected}
              onClick={() => m !== agc.mode && sendAgc({ ...agc, mode: m })}
              className={`btn sm ${agc.mode === m ? 'active' : ''}`}
              title={`AGC ${m}`}
            >
              {m}
            </button>
          ))}
        </div>
      </div>

      {/* Auto-AGC (noise-floor tracking) — disables the manual max-gain edit. */}
      <div className="dsp-cfg-row">
        <span className="dsp-cfg-label">
          Auto
          <span className="dsp-cfg-hint"> noise-floor track</span>
        </span>
        <button
          type="button"
          disabled={!connected}
          aria-pressed={autoEnabled}
          onClick={toggleAuto}
          className={`btn sm ${autoEnabled ? 'active' : ''}`}
          title="Auto-AGC tracks the panadapter noise floor"
        >
          {autoEnabled ? 'ON' : 'OFF'}
        </button>
      </div>

      {/* Max gain (AGC-T top). Disabled while Auto is driving it; the readout
          shows the effective value (baseline + auto offset). */}
      <label className="dsp-cfg-row">
        <span className="dsp-cfg-label">
          Max Gain
          {autoEnabled && <span className="dsp-cfg-hint"> eff {effectiveTop} dB</span>}
        </span>
        <input
          type="range"
          min={0}
          max={120}
          step={1}
          value={agcTopDb}
          disabled={topDisabled}
          title={autoEnabled ? 'Auto-AGC is controlling max gain' : 'AGC-T max gain'}
          onChange={(e) => sendTop(Number(e.currentTarget.value))}
          style={{
            flex: 1,
            cursor: topDisabled ? 'not-allowed' : 'pointer',
            accentColor: topDisabled ? 'var(--fg-3)' : 'var(--accent)',
            opacity: topDisabled ? 0.55 : 1,
          }}
        />
        <span
          className="dsp-cfg-unit mono"
          style={{ color: autoEnabled ? 'var(--fg-3)' : undefined }}
        >
          {agcTopDb} dB
        </span>
      </label>

      {/* Custom-mode tunables — always shown (Thetis-verbose), disabled unless CUSTOM. */}
      <Row
        label="Slope"
        value={agc.slope ?? CUSTOM_DEFAULTS.slope}
        min={0}
        max={20}
        disabled={!connected || !isCustom}
        onCommit={(v) => sendAgc({ ...agc, slope: v })}
      />
      <Row
        label="Decay"
        value={agc.decayMs ?? CUSTOM_DEFAULTS.decayMs}
        min={1}
        max={5000}
        unit="ms"
        disabled={!connected || !isCustom}
        onCommit={(v) => sendAgc({ ...agc, decayMs: v })}
      />
      <Row
        label="Hang"
        value={agc.hangMs ?? CUSTOM_DEFAULTS.hangMs}
        min={10}
        max={5000}
        unit="ms"
        disabled={!connected || !isCustom}
        onCommit={(v) => sendAgc({ ...agc, hangMs: v })}
      />
      <Row
        label="Hang Thresh"
        value={agc.hangThreshold ?? CUSTOM_DEFAULTS.hangThreshold}
        min={0}
        max={100}
        unit="%"
        disabled={!connected || !isCustom}
        onCommit={(v) => sendAgc({ ...agc, hangThreshold: v })}
      />

      {/* Fixed-mode gain — disabled unless FIXED. */}
      <Row
        label="Fixed Gain"
        value={agc.fixedGainDb ?? CUSTOM_DEFAULTS.fixedGainDb}
        min={-20}
        max={120}
        unit="dB"
        disabled={!connected || !isFixed}
        onCommit={(v) => sendAgc({ ...agc, fixedGainDb: v })}
      />
    </div>
  );
}
