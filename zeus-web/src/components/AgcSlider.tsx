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
import { createPortal } from 'react-dom';
import {
  disengageAgcThreshold,
  setAgc,
  setAgcThreshold,
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

// AGC threshold ("knee") in dBm. The operator sets this just above the noise
// floor; signals above it drive the AGC. Range mirrors a usable HF signal
// window. KNEE_FALLBACK is the slider thumb position shown while the knee is
// unset (null) — a sensible mid-low spot so the thumb isn't pinned to a rail.
const KNEE_MIN = -140;
const KNEE_MAX = 0;
const KNEE_FALLBACK = -120;

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
  const agcThresholdDbm = useConnectionStore((s) => s.agcThresholdDbm);

  // Local drag state overrides the store while the user is actively moving
  // the slider so echoed state updates don't yank the thumb back.
  const [dragValue, setDragValue] = useState<number | null>(null);
  // Slider thumb edits the user baseline (agcTopDb); the displayed number shows
  // the effective AGC on the DSP so the user can watch the auto ramp.
  const sliderValue = dragValue ?? userAgc;
  const effective = Math.round(Math.max(MIN, Math.min(MAX, sliderValue + offsetDb)));
  const sliderDisabled = !connected || autoEnabled;

  // Knee (AGC threshold) local drag override, mirroring the AGC-T pattern so
  // an echoed StateDto doesn't yank the thumb while the operator is dragging.
  const [kneeDragValue, setKneeDragValue] = useState<number | null>(null);
  // Thumb position: live drag value, else the operator's set knee, else the
  // fallback. The numeric readout shows "—" until the knee has actually been set.
  const kneeSliderValue = kneeDragValue ?? agcThresholdDbm ?? KNEE_FALLBACK;
  const kneeSet = kneeDragValue != null || agcThresholdDbm != null;
  // Unlike AGC-T, the knee is independent of auto-AGC — only gated on connect.
  const kneeDisabled = !connected;

  const autoAbort = useRef<AbortController | null>(null);
  const agcAbort = useRef<AbortController | null>(null);
  const kneeAbort = useRef<AbortController | null>(null);

  // Popover holding Custom (slope/decay/hang/thresh) or Fixed (fixed gain)
  // tunables, anchored under the mode dropdown so the toolbar stays compact.
  const [paramsOpen, setParamsOpen] = useState(false);
  const [modeMenuOpen, setModeMenuOpen] = useState(false);
  const [modeMenuPos, setModeMenuPos] = useState<{ top: number; left: number; width: number } | null>(null);
  const popRef = useRef<HTMLDivElement | null>(null);
  const modeButtonRef = useRef<HTMLButtonElement | null>(null);
  const modeMenuRef = useRef<HTMLDivElement | null>(null);

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

  // Knee streams /api/agc/threshold the same way AGC-T streams /api/agcGain.
  const kneeLiveSlider = useLiveSlider<number>({
    send: useCallback(
      (v: number, signal: AbortSignal) =>
        setAgcThreshold(v, signal)
          .then((next) => {
            if (!signal.aborted) applyState(next);
          })
          .catch(() => {
            /* next poll will reconcile; don't noisily log on abort */
          }),
      [applyState],
    ),
  });

  // The "Knee" label is a toggle: when engaged (a threshold is set) it
  // disengages → server restores WDSP's default knee; when disengaged it
  // engages at the current thumb position. Dragging the slider always engages.
  const toggleKnee = useCallback(() => {
    if (!connected) return;
    kneeAbort.current?.abort();
    const ac = new AbortController();
    kneeAbort.current = ac;
    const req = kneeSet
      ? disengageAgcThreshold(ac.signal)
      : setAgcThreshold(kneeSliderValue, ac.signal);
    req
      .then((next) => {
        if (!ac.signal.aborted) applyState(next);
      })
      .catch(() => {
        /* next poll reconciles */
      });
  }, [connected, kneeSet, kneeSliderValue, applyState]);

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
      kneeAbort.current?.abort();
    },
    [],
  );

  // Close popovers on outside click / Escape.
  useEffect(() => {
    if (!paramsOpen && !modeMenuOpen) return;
    const onDown = (e: MouseEvent) => {
      const target = e.target as Node;
      const insideTrigger = popRef.current?.contains(target);
      const insideModeMenu = modeMenuRef.current?.contains(target);
      if (!insideTrigger && !insideModeMenu) {
        setParamsOpen(false);
        setModeMenuOpen(false);
      }
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setParamsOpen(false);
        setModeMenuOpen(false);
      }
    };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [paramsOpen, modeMenuOpen]);

  const toggleModeMenu = useCallback(() => {
    if (!connected) return;
    const r = modeButtonRef.current?.getBoundingClientRect();
    if (r) {
      setModeMenuPos({
        top: Math.round(r.bottom + 4),
        left: Math.round(r.left),
        width: Math.round(Math.max(92, r.width)),
      });
    }
    setModeMenuOpen((open) => !open);
  }, [connected]);

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
          disabled={sliderDisabled}
          title={autoEnabled ? 'Auto-AGC is controlling AGC-T' : 'AGC-T top gain'}
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
          style={{
            flex: 1,
            cursor: sliderDisabled ? 'not-allowed' : 'pointer',
            accentColor: sliderDisabled ? 'var(--fg-3)' : 'var(--accent)',
            opacity: sliderDisabled ? 0.55 : 1,
          }}
        />
        <span
          className="mono"
          style={{
            flex: '0 0 auto',
            width: 44,
            textAlign: 'right',
            color: autoEnabled ? 'var(--fg-3)' : 'var(--fg-1)',
            fontSize: 11,
            whiteSpace: 'nowrap',
          }}
        >
          {effective} dB
        </span>
      </label>

      <label className="knob-group" style={{ minWidth: 0 }}>
        <button
          type="button"
          onClick={toggleKnee}
          disabled={!connected}
          aria-pressed={kneeSet}
          aria-label={kneeSet ? 'AGC knee engaged (click to disengage)' : 'AGC knee off (click to engage)'}
          title={
            kneeSet
              ? 'AGC knee ENGAGED — click to disengage (restore the default AGC threshold)'
              : 'AGC knee off — click to engage, or drag the slider. Set it just above the noise floor for smooth, signal-relative AGC.'
          }
          className={`btn sm ${kneeSet ? 'active' : ''}`}
          style={{ whiteSpace: 'nowrap' }}
        >
          Knee
        </button>
        <input
          type="range"
          min={KNEE_MIN}
          max={KNEE_MAX}
          step={1}
          value={kneeSliderValue}
          disabled={kneeDisabled}
          aria-label="AGC threshold (knee)"
          title="AGC threshold (knee) — set just above the noise floor for smooth, signal-relative AGC"
          onChange={(e) => {
            const v = Number(e.currentTarget.value);
            setKneeDragValue(v);
            kneeLiveSlider.push(v);
          }}
          onMouseUp={() => {
            kneeLiveSlider.flush();
            setKneeDragValue(null);
          }}
          onTouchEnd={() => {
            kneeLiveSlider.flush();
            setKneeDragValue(null);
          }}
          onKeyUp={() => {
            kneeLiveSlider.flush();
            setKneeDragValue(null);
          }}
          style={{
            flex: 1,
            cursor: kneeDisabled ? 'not-allowed' : 'pointer',
            // Lit accent only while engaged; muted when disengaged so the
            // off-state reads at a glance (mirrors the lit "Knee" button).
            accentColor: kneeDisabled || !kneeSet ? 'var(--fg-3)' : 'var(--accent)',
            opacity: kneeDisabled ? 0.55 : kneeSet ? 1 : 0.7,
          }}
        />
        <span
          className="mono"
          style={{
            flex: '0 0 auto',
            width: 60,
            textAlign: 'right',
            color: kneeDisabled ? 'var(--fg-3)' : 'var(--fg-1)',
            fontSize: 11,
            whiteSpace: 'nowrap',
          }}
        >
          {kneeSet ? `${Math.round(kneeSliderValue)} dBm` : '—'}
        </span>
      </label>

      <div className="agc-mode-row" ref={popRef}>
        <button
          ref={modeButtonRef}
          type="button"
          className={`btn sm agc-mode-button ${modeMenuOpen ? 'active' : ''}`}
          disabled={!connected}
          aria-haspopup="listbox"
          aria-expanded={modeMenuOpen}
          aria-label="AGC mode"
          title="AGC mode"
          onClick={toggleModeMenu}
        >
          <span>{agc.mode}</span>
          <span className="agc-mode-caret" aria-hidden>v</span>
        </button>
        {modeMenuOpen && modeMenuPos && createPortal(
          <div
            ref={modeMenuRef}
            className="agc-mode-menu"
            role="listbox"
            aria-label="AGC mode"
            style={{
              top: modeMenuPos.top,
              left: modeMenuPos.left,
              minWidth: modeMenuPos.width,
            }}
          >
            {AGC_MODES.map((mode) => (
              <button
                key={mode}
                type="button"
                role="option"
                aria-selected={agc.mode === mode}
                className={`agc-mode-option ${agc.mode === mode ? 'active' : ''}`}
                onClick={() => {
                  if (mode !== agc.mode) sendAgc({ ...agc, mode });
                  setModeMenuOpen(false);
                }}
              >
                {mode}
              </button>
            ))}
          </div>,
          document.body,
        )}
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
