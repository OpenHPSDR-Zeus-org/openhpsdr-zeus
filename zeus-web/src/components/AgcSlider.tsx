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

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  setAgc,
  setAgcTop,
  setAutoAgc,
  type AgcConfigDto,
  type AgcMode,
} from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useLiveSlider } from '../hooks/useLiveSlider';

// AGC top (max gain) in dB. 80 is the Thetis AGC_MEDIUM default; the WDSP
// docs call this the upper gain limit before compression kicks in.
// 0-120 mirrors the range Thetis exposes on its AGC-T slider.
const MIN = 0;
const MAX = 120;

// AGC mode dropdown order matches Thetis (enums.cs:152-162).
const AGC_MODES: readonly AgcMode[] = ['Fixed', 'Long', 'Slow', 'Med', 'Fast', 'Custom'];

// Custom/Fixed param fallbacks shown when a field is null (Thetis radio.cs §4.3),
// mirroring the engine's Custom defaults (hang/decay 250, slope 0, thresh 0).
const CUSTOM_DEFAULTS = {
  slope: 0,
  decayMs: 250,
  hangMs: 250,
  hangThreshold: 0,
  fixedGainDb: 20,
} as const;

// One labelled number input for the Custom/Fixed popover. Token classes only.
function ParamRow(props: {
  label: string;
  value: number;
  min: number;
  max: number;
  unit?: string;
  disabled: boolean;
  onCommit: (v: number) => void;
}) {
  const { label, value, min, max, unit, disabled, onCommit } = props;
  return (
    <label className="agc-param-row">
      <span className="label-xs" style={{ minWidth: 78 }}>
        {label}
      </span>
      <input
        type="number"
        className="mono"
        min={min}
        max={max}
        step={1}
        value={value}
        disabled={disabled}
        onChange={(e) => {
          const v = Number(e.currentTarget.value);
          if (Number.isFinite(v)) onCommit(Math.max(min, Math.min(max, v)));
        }}
        style={{
          width: 66,
          background: 'var(--bg-0)',
          color: 'var(--fg-0)',
          border: '1px solid var(--line)',
          borderRadius: 3,
          padding: '2px 6px',
          fontSize: 12,
        }}
      />
      {unit != null && (
        <span className="label-xs" style={{ color: 'var(--fg-2)' }}>
          {unit}
        </span>
      )}
    </label>
  );
}

export function AgcSlider() {
  const userAgc = useConnectionStore((s) => s.agcTopDb);
  const offsetDb = useConnectionStore((s) => s.agcOffsetDb);
  const autoEnabled = useConnectionStore((s) => s.autoAgcEnabled);
  const connected = useConnectionStore((s) => s.status === 'Connected');
  const applyState = useConnectionStore((s) => s.applyState);
  const agc = useConnectionStore((s) => s.agc);
  const setLocalAgc = useConnectionStore((s) => s.setAgc);

  // Local drag state overrides the store while the user is actively moving
  // the slider so echoed state updates don't yank the thumb back.
  const [dragValue, setDragValue] = useState<number | null>(null);
  // Slider thumb edits the user baseline (agcTopDb); the displayed number shows
  // the effective AGC on the DSP so the user can watch the auto ramp.
  const sliderValue = dragValue ?? userAgc;
  const effective = Math.round(Math.max(MIN, Math.min(MAX, sliderValue + offsetDb)));

  const autoAbort = useRef<AbortController | null>(null);
  const agcAbort = useRef<AbortController | null>(null);

  // Popover holding Custom (slope/decay/hang/thresh) or Fixed (fixed gain)
  // tunables, anchored under the mode dropdown so the toolbar stays compact.
  const [paramsOpen, setParamsOpen] = useState(false);
  const popRef = useRef<HTMLDivElement | null>(null);

  // Stream during drag (rAF coalesced), flush on release. The hook owns
  // abort-on-supersede so a fast drag doesn't queue stale POSTs.
  const liveSlider = useLiveSlider<number>({
    send: useCallback(
      (v: number, signal: AbortSignal) =>
        setAgcTop(v, signal)
          .then((next) => {
            if (!signal.aborted) applyState(next);
          })
          .catch(() => {
            /* next poll will reconcile; don't noisily log on abort */
          }),
      [applyState],
    ),
  });

  const toggleAuto = useCallback(() => {
    if (!connected) return;
    autoAbort.current?.abort();
    const ac = new AbortController();
    autoAbort.current = ac;
    setAutoAgc(!autoEnabled, ac.signal)
      .then((next) => {
        if (!ac.signal.aborted) applyState(next);
      })
      .catch(() => {
        /* state subscription will reconcile on next broadcast */
      });
  }, [autoEnabled, connected, applyState]);

  const sendAgc = useCallback(
    (next: AgcConfigDto) => {
      setLocalAgc(next);
      agcAbort.current?.abort();
      const ac = new AbortController();
      agcAbort.current = ac;
      setAgc(next, ac.signal)
        .then((s) => {
          if (!ac.signal.aborted) applyState(s);
        })
        .catch(() => {
          /* next state poll will reconcile */
        });
    },
    [setLocalAgc, applyState],
  );

  useEffect(
    () => () => {
      autoAbort.current?.abort();
      agcAbort.current?.abort();
    },
    [],
  );

  // Close the params popover on outside click / Escape.
  useEffect(() => {
    if (!paramsOpen) return;
    const onDown = (e: MouseEvent) => {
      if (popRef.current && !popRef.current.contains(e.target as Node)) {
        setParamsOpen(false);
      }
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setParamsOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [paramsOpen]);

  const isCustom = agc.mode === 'Custom';
  const isFixed = agc.mode === 'Fixed';
  const hasParams = isCustom || isFixed;

  return (
    <div className="agc-control">
      <label className="knob-group" style={{ minWidth: 0 }}>
        <button
          type="button"
          onClick={toggleAuto}
          disabled={!connected}
          aria-pressed={autoEnabled}
          aria-label={autoEnabled ? 'Auto AGC on' : 'Auto AGC off'}
          title={
            autoEnabled
              ? 'Auto-AGC ON (click to disable)'
              : 'Auto-AGC OFF (click to enable)'
          }
          className={`btn sm ${autoEnabled ? 'active' : ''}`}
          style={{ whiteSpace: 'nowrap' }}
        >
          AGC-T
        </button>
        <input
          type="range"
          min={MIN}
          max={MAX}
          step={1}
          value={sliderValue}
          disabled={!connected || autoEnabled}
          onChange={(e) => {
            const v = Number(e.currentTarget.value);
            setDragValue(v);
            liveSlider.push(v);
          }}
          onMouseUp={() => {
            liveSlider.flush();
            setDragValue(null);
          }}
          onTouchEnd={() => {
            liveSlider.flush();
            setDragValue(null);
          }}
          onKeyUp={() => {
            liveSlider.flush();
            setDragValue(null);
          }}
          style={{ flex: 1, cursor: 'pointer', accentColor: 'var(--accent)' }}
        />
        <span
          className="mono"
          style={{
            flex: '0 0 auto',
            width: 44,
            textAlign: 'right',
            color: 'var(--fg-1)',
            fontSize: 11,
            whiteSpace: 'nowrap',
          }}
        >
          {effective} dB
        </span>
      </label>

      <div className="agc-mode-row" ref={popRef}>
        <select
          className="agc-mode-select"
          value={agc.mode}
          disabled={!connected}
          aria-label="AGC mode"
          title="AGC mode"
          onChange={(e) => {
            const mode = e.currentTarget.value as AgcMode;
            if (mode !== agc.mode) sendAgc({ ...agc, mode });
          }}
        >
          {AGC_MODES.map((mode) => (
            <option key={mode} value={mode}>
              {mode}
            </option>
          ))}
        </select>
        {hasParams && (
          <button
            type="button"
            className={`btn sm ${paramsOpen ? 'active' : ''}`}
            disabled={!connected}
            aria-expanded={paramsOpen}
            aria-label="AGC parameters"
            title={isCustom ? 'Custom AGC parameters' : 'Fixed gain'}
            onClick={() => setParamsOpen((o) => !o)}
          >
            ⋯
          </button>
        )}

        {paramsOpen && hasParams && (
          <div className="agc-params-pop" role="group" aria-label="AGC parameters">
            {isCustom && (
              <>
                <ParamRow
                  label="Slope"
                  value={agc.slope ?? CUSTOM_DEFAULTS.slope}
                  min={0}
                  max={20}
                  disabled={!connected}
                  onCommit={(v) => sendAgc({ ...agc, slope: v })}
                />
                <ParamRow
                  label="Decay"
                  value={agc.decayMs ?? CUSTOM_DEFAULTS.decayMs}
                  min={1}
                  max={5000}
                  unit="ms"
                  disabled={!connected}
                  onCommit={(v) => sendAgc({ ...agc, decayMs: v })}
                />
                <ParamRow
                  label="Hang"
                  value={agc.hangMs ?? CUSTOM_DEFAULTS.hangMs}
                  min={10}
                  max={5000}
                  unit="ms"
                  disabled={!connected}
                  onCommit={(v) => sendAgc({ ...agc, hangMs: v })}
                />
                <ParamRow
                  label="Hang Thresh"
                  value={agc.hangThreshold ?? CUSTOM_DEFAULTS.hangThreshold}
                  min={0}
                  max={100}
                  unit="%"
                  disabled={!connected}
                  onCommit={(v) => sendAgc({ ...agc, hangThreshold: v })}
                />
              </>
            )}
            {isFixed && (
              <ParamRow
                label="Fixed Gain"
                value={agc.fixedGainDb ?? CUSTOM_DEFAULTS.fixedGainDb}
                min={-20}
                max={120}
                unit="dB"
                disabled={!connected}
                onCommit={(v) => sendAgc({ ...agc, fixedGainDb: v })}
              />
            )}
          </div>
        )}
      </div>
    </div>
  );
}
