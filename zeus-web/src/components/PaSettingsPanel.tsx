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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

import { useEffect, useRef, useState } from 'react';
import { HF_BANDS, usePaStore } from '../state/pa-store';
import { useRadioStore } from '../state/radio-store';
import { useTxStore } from '../state/tx-store';
import { BOARD_LABELS } from '../api/radio';
import { setTxTimeout } from '../api/client';
import type { PaBandSettings } from '../api/pa';
import anvelinaLogo from '../assets/anvelina-logo.png';

// TX timeout clamp — matches RadioService.MinTxTimeoutSec / MaxTxTimeoutSec.
// Issue #1270.
const TX_TIMEOUT_MIN_S = 30;
const TX_TIMEOUT_MAX_S = 600;

const OC_PINS = [1, 2, 3, 4, 5, 6, 7] as const;

// Anvelina-PRO3 DX OC outputs (issue #407 / EU2AV
// Open_Collector_Anvelina_DX). UI pin numbers 8..11 sit inline after the
// standard 1..7 — the user explicitly asked for sequential labels and the
// Claude-Design handoff (PA Settings.html, 2026-05-20) uses the same
// numbering. The underlying wire bits are still USEROUT7..USEROUT10 per
// EU2AV's spec; pin tooltips spell out the mapping so operators consulting
// the datasheet aren't surprised. bitOffset 8 ⇒ UI pin 8 -> mask bit 0
// -> wire byte-1397 bit 1 (USEROUT7), etc.
const ANVELINA_DX_PINS = [8, 9, 10, 11] as const;
const ANVELINA_DX_BIT_OFFSET = 8;
const ANVELINA_DX_PIN_LABELS: Record<number, string> = {
  8: 'DX OUT 8 — USEROUT7 (byte 1397 bit 1)',
  9: 'DX OUT 9 — USEROUT8 (byte 1397 bit 2)',
  10: 'DX OUT 10 — USEROUT9 (byte 1397 bit 3)',
  11: 'DX OUT 11 — USEROUT10 (byte 1397 bit 4)',
};

// Developer-only escape hatch. Set this localStorage key to '1' from
// the browser console to surface the Anvelina ext columns on a non-
// Anvelina radio for bench-testing the wire path:
//
//   localStorage.setItem('zeus.pa.showAnvelinaExtForTesting', '1');
//   location.reload();
//
// There is no operator-facing UI to flip this — Anvelina-PRO3 users
// always see the ext columns (capability flag drives it), and every
// other operator never sees them. The flag exists so the maintainer
// can exercise byte 1397 end-to-end before on-radio verification
// against an actual Anvelina-PRO3 lands; delete the constant and its
// hydration once #407 is fully verified.
const ANVELINA_EXT_TESTING_KEY = 'zeus.pa.showAnvelinaExtForTesting';

// Persisted active tab for the Per Band section (design v2). Falls
// back to 'tx' on first load and on any unrecognised value.
const PERBAND_TAB_KEY = 'zeus.pa.activePerBandTab';
type PerBandTab = 'tx' | 'rx' | 'auto';

// N2ADR LPF band-range hints — surfaced under each LPF pin number in
// the AUTO tab so operators can see at a glance which filter bank the
// firmware switches in for the current band. Matches the mi0bot
// openhpsdr-thetis HL2 LPF mapping (N2adrBands.cs on the backend).
const LPF_RANGES: Record<number, string> = {
  1: '160m',
  2: '80m',
  3: '60–40m',
  4: '30–20m',
  5: '17–15m',
  6: '12–10m',
  7: '6m',
};

const COPY_ICON = (
  <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.6" aria-hidden="true">
    <rect x="2.5" y="5.5" width="8" height="8" rx="1.5" />
    <path d="M5.5 5.5 L5.5 3.5 A1 1 0 0 1 6.5 2.5 L12.5 2.5 A1 1 0 0 1 13.5 3.5 L13.5 10.5" />
  </svg>
);

// HL2 uses a percentage-based PA model (mi0bot openhpsdr-thetis) — the
// PaGainDb DTO field is interpreted as output % 0..100 rather than dB
// forward gain. Backend HermesLite2DriveProfile enforces this; frontend
// relabels the input and widens the clamp so the operator can actually
// type 100. See docs/lessons/hl2-drive-model.md.
const HL2_BOARD_ID = 'HermesLite2';

// Physical sanity bounds — guards against typos like "100" (intended as a
// percentage) landing in the dB field on non-HL2 radios, which collapses
// the drive byte to 0.
const PA_GAIN_MIN_DB  = 0;
const PA_GAIN_MAX_DB  = 70;    // G2-class radios top out ~51 dB; 70 leaves headroom
const PA_GAIN_MAX_PCT = 100;   // HL2: value is an output percentage
const PA_MAX_W_MIN    = 0;
const PA_MAX_W_MAX    = 1500;  // Covers Shared Apex / 1 kW + amps

const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v));

// One unified pill bar replacing the previous 7-checkbox grid. The
// container holds N tappable pins; clicking flips the bit, and dragging
// across multiple pins paints them to the first-clicked target value
// (so changing a whole row is one drag, not N clicks). Read-only mode is
// used for the Auto N2ADR column and disables the click + drag handlers.
// `disabled` is the soft-grey state for the Anvelina DX OUT columns when
// the connected radio doesn't support them — same visual cue as readOnly
// but with a tooltip explaining why and the click handlers stripped.
// `pins` lets the caller drive a 4-pin Anvelina DX bar (7..10) instead of
// the default 7-pin OC bar; `bitOffset` maps the displayed pin number to
// the underlying mask bit (DX bar maps pin 7 -> bit 0, pin 8 -> bit 1, …).
function PillBar({
  label,
  mask,
  onChange,
  readOnly = false,
  disabled = false,
  disabledTitle,
  pins = OC_PINS,
  bitOffset = 1,
  size = 'sm',
  ext = false,
  pinTitles,
}: {
  label: string;
  mask: number;
  onChange?: (next: number) => void;
  readOnly?: boolean;
  disabled?: boolean;
  disabledTitle?: string;
  pins?: readonly number[];
  bitOffset?: number;
  // 'lg' applies the bigger 30px chip variant used by the prominent
  // OC TX / OC RX rows; default 'sm' keeps the dense AUTO N2ADR sizing.
  size?: 'sm' | 'lg';
  // Anvelina extension styling — green on-state (vs. standard --accent
  // blue) so the platform-exclusive pins read as visually distinct from
  // the standard 1..7. Layered on top of `disabled` for the soft-grey
  // state when the connected radio doesn't expose the extension.
  ext?: boolean;
  // Per-pin tooltip override — used by the Anvelina ext bar to spell out
  // the UI-pin → USEROUT mapping for operators cross-referencing the
  // EU2AV spec. Falls back to the generic `${label} pin ${n}` title.
  pinTitles?: Record<number, string>;
}) {
  const [paintTo, setPaintTo] = useState<0 | 1 | null>(null);
  useEffect(() => {
    if (paintTo === null) return;
    const up = () => setPaintTo(null);
    window.addEventListener('mouseup', up);
    return () => window.removeEventListener('mouseup', up);
  }, [paintTo]);

  const inert = readOnly || disabled;
  const setBit = (bit: number, on: 0 | 1) => {
    if (!onChange) return;
    const b = 1 << (bit - bitOffset);
    onChange(on ? mask | b : mask & ~b);
  };

  return (
    <span
      className={
        'pa-pill-bar' +
        (size === 'lg' ? ' lg' : '') +
        (readOnly ? ' ro' : '') +
        (disabled ? ' ro' : '')
      }
      title={disabled ? disabledTitle : undefined}
    >
      {pins.map((bit) => {
        const active = (mask & (1 << (bit - bitOffset))) !== 0;
        const baseTitle = pinTitles?.[bit] ?? `${label} pin ${bit}`;
        const title = disabled
          ? disabledTitle ?? `${baseTitle} — unsupported on this radio`
          : readOnly
            ? `${baseTitle} — ${active ? 'firmware-driven' : 'not driven'}`
            : baseTitle;
        return (
          <span
            key={bit}
            role={inert ? undefined : 'button'}
            aria-pressed={inert ? undefined : active}
            aria-disabled={disabled || undefined}
            title={title}
            className={
              'pa-pill' +
              (ext ? ' ext' : '') +
              (active ? ' on' : '') +
              (inert ? ' ro' : '')
            }
            onMouseDown={
              inert
                ? undefined
                : (e) => {
                    e.preventDefault();
                    const next: 0 | 1 = active ? 0 : 1;
                    setPaintTo(next);
                    setBit(bit, next);
                  }
            }
            onMouseEnter={
              inert
                ? undefined
                : () => {
                    if (paintTo === null) return;
                    if (active === (paintTo === 1)) return;
                    setBit(bit, paintTo);
                  }
            }
          >
            {bit}
          </span>
        );
      })}
    </span>
  );
}

// Drag-to-set horizontal slider replacing the per-band number input. On
// HL2 the value is an output percentage (0..100); on Hermes / ANAN /
// Orion / G2 it's PA forward gain in dB (0..70). Click anywhere on the
// track to jump, drag for fine control. Step quantises to 0.1 to match
// the previous numeric-input precision.
function PaSlider({
  value,
  min,
  max,
  step,
  unit,
  onChange,
}: {
  value: number;
  min: number;
  max: number;
  step: number;
  unit: string;
  onChange: (next: number) => void;
}) {
  const trackRef = useRef<HTMLDivElement>(null);
  const quantise = (v: number) => {
    const clamped = Math.max(min, Math.min(max, v));
    return Math.round(clamped / step) * step;
  };

  const startDrag = (e: React.MouseEvent) => {
    e.preventDefault();
    const rect = trackRef.current?.getBoundingClientRect();
    if (!rect) return;
    const upd = (clientX: number) => {
      const pct = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
      onChange(quantise(min + pct * (max - min)));
    };
    upd(e.clientX);
    const move = (ev: MouseEvent) => upd(ev.clientX);
    const up = () => {
      window.removeEventListener('mousemove', move);
      window.removeEventListener('mouseup', up);
    };
    window.addEventListener('mousemove', move);
    window.addEventListener('mouseup', up);
  };

  const pct = max > min ? ((value - min) / (max - min)) * 100 : 0;
  const decimals = step >= 1 ? 0 : 1;

  return (
    <div className="pa-slider">
      <div ref={trackRef} className="pa-slider-track" onMouseDown={startDrag}>
        <div className="pa-slider-fill" style={{ width: `${pct}%` }} />
      </div>
      <span className="pa-slider-val">
        {value.toFixed(decimals)}
        <em>{unit}</em>
      </span>
    </div>
  );
}

export function PaSettingsPanel() {
  const settings = usePaStore((s) => s.settings);
  const loaded = usePaStore((s) => s.loaded);
  const inflight = usePaStore((s) => s.inflight);
  const error = usePaStore((s) => s.error);
  const load = usePaStore((s) => s.load);
  const setGlobal = usePaStore((s) => s.setGlobal);
  const setBand = usePaStore((s) => s.setBand);
  const resetToBoardDefaults = usePaStore((s) => s.resetToBoardDefaults);
  const copyOcMasks = usePaStore((s) => s.copyOcMasks);
  const selection = useRadioStore((s) => s.selection);
  const capabilities = useRadioStore((s) => s.capabilities);
  const txTimeoutSec = useTxStore((s) => s.txTimeoutSec);
  const setTxTimeoutSecLocal = useTxStore((s) => s.setTxTimeoutSec);

  // Active inner tab — TX / RX / AUTO (HL2 only). Persisted in
  // localStorage so reload doesn't pop the operator back to TX on every
  // visit. Falls back to 'tx' on first load and whenever AUTO is the
  // stored value but the connected radio doesn't expose the N2ADR auto-
  // mask (the AUTO tab button is not even rendered in that case).
  const [activeTab, setActiveTab] = useState<PerBandTab>(() => {
    try {
      const stored = localStorage.getItem(PERBAND_TAB_KEY);
      if (stored === 'tx' || stored === 'rx' || stored === 'auto') return stored;
    } catch {
      /* localStorage unavailable — fall through. */
    }
    return 'tx';
  });
  useEffect(() => {
    try {
      localStorage.setItem(PERBAND_TAB_KEY, activeTab);
    } catch {
      /* ignore */
    }
  }, [activeTab]);

  // Show the AUTO N2ADR tab only when the board has a non-zero firmware
  // auto-mask for at least one band — i.e. an HL2. Bare-Hermes and
  // ANAN/Orion users don't need a tab that would always be empty, and
  // showing it on first-boot (Unknown board, autoOcMask=0) would be
  // visual noise.
  const showAutoCol = settings.bands.some((b) => b.autoOcMask > 0);
  // If the operator's persisted choice was AUTO but the connected board
  // doesn't expose the LPF mask, snap back to TX so the panel never
  // renders an active tab without its companion content.
  useEffect(() => {
    if (activeTab === 'auto' && !showAutoCol) {
      setActiveTab('tx');
    }
  }, [activeTab, showAutoCol]);

  // Anvelina-PRO3 DX OC columns (issue #407 / EU2AV
  // Open_Collector_Anvelina_DX). Visibility rules:
  //   * Connected to AnvelinaPro3 over P2 → always show, fully interactive
  //   * Not Anvelina + dev toggle off → hide ext side entirely
  //   * Not Anvelina + dev toggle on  → show ext side disabled (for
  //     bench-testing without an actual Anvelina; remove once on-radio
  //     verification is done)
  const anvelinaDxSupported = capabilities.supportsAnvelinaDxOc;
  // Dev-only escape hatch — see ANVELINA_EXT_TESTING_KEY comment. Read
  // once at mount; there is no UI to flip it. A dev exercising the
  // wire path sets the localStorage key from the console and reloads.
  const [showAnvelinaExtForTesting] = useState<boolean>(() => {
    try {
      return localStorage.getItem(ANVELINA_EXT_TESTING_KEY) === '1';
    } catch {
      return false;
    }
  });
  const showAnvelinaExt = anvelinaDxSupported || showAnvelinaExtForTesting;
  const anvelinaDxTooltip = anvelinaDxSupported
    ? 'Anvelina-PRO3 Open-Collector DX outputs (USEROUT 7–10). EU2AV spec — Protocol 2 byte 1397.'
    : 'Anvelina-PRO3 only (Protocol 2). Connect an Anvelina-PRO3 to enable these outputs.';

  // HL2 overloads the "PA Gain" field into an output percentage. Switch
  // label + clamp range + step when the effective board (connected wins
  // when present, else the preferred radio) is HL2. Non-HL2 boards keep
  // the dB convention (Hermes / ANAN / Orion).
  const isHl2 = selection.effective === HL2_BOARD_ID;
  const paFieldLabel = isHl2 ? 'PA Output (%)' : 'PA Gain (dB)';
  const paFieldMax   = isHl2 ? PA_GAIN_MAX_PCT : PA_GAIN_MAX_DB;
  const paFieldStep  = isHl2 ? 1 : 0.1;
  const paFieldTitle = isHl2
    ? 'HL2 output percentage per band (0..100). HL2 uses a different PA model than other HPSDR radios: 100 = no attenuation (rated power); lower values soft-cap output for weaker bands (6 m stock is ~38.8). NOT decibels.'
    : 'PA forward gain in dB per band — the amplifier\'s own gain from DUC output to antenna. NOT a trim. Seeded from the board kind (e.g. G2 MkII ≈ 48-51 dB on HF). Used together with Rated PA Output (W) to compute the drive byte: lower gain here → more drive byte → more output at a given slider %.';

  useEffect(() => {
    load();
  }, [load]);

  // Reset targets the operator's explicit pick when set, else the effective
  // board (connected > preferred). Undefined override = server decides.
  const resetTargetBoard =
    selection.preferred !== 'Auto' ? selection.preferred : selection.effective;
  const resetTargetLabel =
    resetTargetBoard === 'Unknown'
      ? 'defaults'
      : `${BOARD_LABELS[resetTargetBoard]} defaults`;
  const canReset = resetTargetBoard !== 'Unknown' && !inflight;

  const handleResetToDefaults = () => {
    const override = selection.preferred !== 'Auto' ? selection.preferred : undefined;
    resetToBoardDefaults(override);
  };

  return (
    <div className="pa-settings density-compact space-y-6">
      <section>
        <div className="mb-2 flex items-center justify-between gap-3">
          <h3 className="pa-section-h">Global</h3>
          <button
            type="button"
            onClick={handleResetToDefaults}
            disabled={!canReset}
            title={
              canReset
                ? `Replace PA Gain (all bands) and Rated PA Output with ${BOARD_LABELS[resetTargetBoard]}'s factory defaults. Your OC masks and Disable-PA checkboxes are not touched. APPLY to persist.`
                : 'Select a radio above to reset to its defaults.'
            }
            className="btn sm"
            style={{ fontSize: 10, letterSpacing: '0.1em', textTransform: 'uppercase' }}
          >
            Reset to {resetTargetLabel}
          </button>
        </div>
        <div className="pa-card grid grid-cols-1 gap-4 p-3 md:grid-cols-2">
          <label className="pa-field flex items-center gap-2 text-xs">
            <input
              type="checkbox"
              checked={settings.global.paEnabled}
              onChange={(e) => setGlobal({ paEnabled: e.target.checked })}
              className="h-4 w-4"
              style={{ accentColor: 'var(--accent)' }}
            />
            PA Enabled
          </label>

          <label
            className="pa-field flex items-center gap-2 text-xs"
            title="Rated PA output in watts. Slider 100% targets this wattage. Seeded from the connected board kind — HL2 = 5 W, Hermes-class = 10 W, ANAN/Orion/G2 = 100 W. Set to 0 to fall back to the raw drive-byte mode (PA Gain field is ignored)."
          >
            Rated PA Output (W)
            <input
              type="number"
              min={PA_MAX_W_MIN}
              max={PA_MAX_W_MAX}
              step={1}
              value={settings.global.paMaxPowerWatts}
              onChange={(e) =>
                setGlobal({
                  paMaxPowerWatts: clamp(Number(e.target.value) || 0, PA_MAX_W_MIN, PA_MAX_W_MAX),
                })
              }
              className="pa-num-input w-20 rounded px-2 py-0.5 text-right text-xs"
            />
            {settings.global.paMaxPowerWatts === 0 && (
              <span className="text-[10px]" style={{ color: 'var(--amber)' }}>
                (0 = raw drive-byte mode — PA Gain ignored)
              </span>
            )}
          </label>

          <label
            className="pa-field flex items-center gap-2 text-xs"
            title="Maximum length of a single MOX or TUN transmission in seconds. When exceeded, Zeus drops TX to protect the PA and shows an alert. Range 30..600; default 120. About 30 seconds before the timeout fires you'll see a pre-warning banner so you can un-key or reset. Issue #1270."
          >
            TX Timeout (s)
            <input
              type="number"
              min={TX_TIMEOUT_MIN_S}
              max={TX_TIMEOUT_MAX_S}
              step={5}
              value={txTimeoutSec}
              onChange={(e) => {
                const v = clamp(Number(e.target.value) || 0, TX_TIMEOUT_MIN_S, TX_TIMEOUT_MAX_S);
                // Optimistic; reconcile with server-applied value on success.
                setTxTimeoutSecLocal(v);
                setTxTimeout(v)
                  .then((r) => setTxTimeoutSecLocal(r.txTimeoutSec))
                  .catch(() => {});
              }}
              className="pa-num-input w-20 rounded px-2 py-0.5 text-right text-xs"
            />
          </label>
        </div>
      </section>

      <section>
        <h3 className="pa-section-h" style={{ marginBottom: 10 }}>Per Band</h3>
        <div className="pa-perband-bar mb-3">
          <div className="pa-tabs" role="tablist" aria-label="Per-band OC layer">
            <button
              type="button"
              role="tab"
              aria-selected={activeTab === 'tx'}
              className={'pa-tab' + (activeTab === 'tx' ? ' is-active' : '')}
              onClick={() => setActiveTab('tx')}
            >
              OC&nbsp;TX
              <span className="pa-tab-badge">{showAnvelinaExt ? '1–7 · 8–11' : '1–7'}</span>
            </button>
            <button
              type="button"
              role="tab"
              aria-selected={activeTab === 'rx'}
              className={'pa-tab' + (activeTab === 'rx' ? ' is-active' : '')}
              onClick={() => setActiveTab('rx')}
            >
              OC&nbsp;RX
              <span className="pa-tab-badge">{showAnvelinaExt ? '1–7 · 8–11' : '1–7'}</span>
            </button>
            {showAutoCol && (
              <button
                type="button"
                role="tab"
                aria-selected={activeTab === 'auto'}
                className={'pa-tab' + (activeTab === 'auto' ? ' is-active' : '')}
                onClick={() => setActiveTab('auto')}
                title="Hermes Lite 2 only — read-only mirror of the N2ADR LPF mask the firmware asserts on every band change."
              >
                AUTO&nbsp;N2ADR
                <span className="pa-tab-badge">HL2</span>
              </button>
            )}
          </div>

          {(activeTab === 'tx' || activeTab === 'rx') && (
            <div className="pa-perband-tools">
              <button
                type="button"
                className="pa-copy-btn"
                onClick={() => copyOcMasks(activeTab === 'tx' ? 'rx->tx' : 'tx->rx')}
                title={
                  activeTab === 'tx'
                    ? 'Mirror every band’s OC RX mask (and any Anvelina ext bits) onto OC TX. APPLY below to persist.'
                    : 'Mirror every band’s OC TX mask (and any Anvelina ext bits) onto OC RX. APPLY below to persist.'
                }
              >
                {COPY_ICON}
                {activeTab === 'tx' ? 'Copy from OC RX' : 'Copy from OC TX'}
              </button>
            </div>
          )}
          {activeTab === 'auto' && (
            <div className="pa-perband-tools">
              <span className="pa-perband-notice">
                <strong>Hermes Lite 2 only.</strong>&nbsp; Read-only mirror of the N2ADR LPF
                mask the firmware asserts on every band change.
              </span>
            </div>
          )}
        </div>

        {activeTab === 'tx' && (
          <OcPane
            side="tx"
            settings={settings}
            paFieldLabel={paFieldLabel}
            paFieldMax={paFieldMax}
            paFieldStep={paFieldStep}
            paFieldTitle={paFieldTitle}
            isHl2={isHl2}
            setBand={setBand}
            showAnvelinaExt={showAnvelinaExt}
            anvelinaDxTooltip={anvelinaDxTooltip}
          />
        )}
        {activeTab === 'rx' && (
          <OcPane
            side="rx"
            settings={settings}
            paFieldLabel={paFieldLabel}
            paFieldMax={paFieldMax}
            paFieldStep={paFieldStep}
            paFieldTitle={paFieldTitle}
            isHl2={isHl2}
            setBand={setBand}
            showAnvelinaExt={showAnvelinaExt}
            anvelinaDxTooltip={anvelinaDxTooltip}
          />
        )}
        {activeTab === 'auto' && (
          <AutoPane
            settings={settings}
            paFieldLabel={paFieldLabel}
            paFieldMax={paFieldMax}
            paFieldStep={paFieldStep}
            paFieldTitle={paFieldTitle}
            isHl2={isHl2}
            setBand={setBand}
          />
        )}
      </section>

      {/* Legend strip — restates the std vs ext distinction the per-tab
          toolbar signals, and folds the load/save status that used to
          live in a separate hint line into the right edge so the per-
          band table still has its single status channel below it. */}
      <div className="pa-oc-legend">
        <span>
          <span className="swatch std" />
          Standard pin (OC&nbsp;0..6)
        </span>
        {showAnvelinaExt && (
          <span>
            <span className="swatch ext" />
            Anvelina extension (USEROUT&nbsp;7..10)
          </span>
        )}
        <span className="pa-oc-legend-status">
          {inflight
            ? 'Saving…'
            : loaded
              ? 'Loaded from server — use APPLY below to persist edits'
              : 'Loading…'}
          {error ? ` · error: ${error}` : ''}
        </span>
      </div>
    </div>
  );
}

// ── OC TX / OC RX pane ───────────────────────────────────────
// Standard 7-pin bar on the left, Anvelina ext 4-pin bar on the right,
// separated by a hairline divider. Shares grid template with the pane
// header so column edges line up. When `showAnvelinaExt` is false the
// row collapses to four columns (no divider, no ext bar) via the
// `.is-ext-hidden` modifier on the table shell.
function OcPane(props: {
  side: 'tx' | 'rx';
  settings: ReturnType<typeof usePaStore.getState>['settings'];
  paFieldLabel: string;
  paFieldMax: number;
  paFieldStep: number;
  paFieldTitle: string;
  isHl2: boolean;
  setBand: (band: string, patch: Partial<Omit<PaBandSettings, 'band'>>) => void;
  showAnvelinaExt: boolean;
  anvelinaDxTooltip: string;
}) {
  const {
    side,
    settings,
    paFieldLabel,
    paFieldMax,
    paFieldStep,
    paFieldTitle,
    isHl2,
    setBand,
    showAnvelinaExt,
    anvelinaDxTooltip,
  } = props;
  const stdLabel = side === 'tx' ? 'OC TX' : 'OC RX';
  return (
    <div className={'pa-card pa-table-shell oc' + (showAnvelinaExt ? '' : ' is-ext-hidden')}>
      <div className="pa-oc-row is-thead">
        <div className="pa-th">Band</div>
        <div className="pa-th" title={paFieldTitle}>{paFieldLabel}</div>
        <div className="pa-th" style={{ justifyContent: 'center' }}>Dis PA</div>
        <div className="pa-th">
          {stdLabel}&nbsp;<span className="pa-pill-mute">pins 1–7</span>
        </div>
        {showAnvelinaExt && (
          <>
            <div aria-hidden="true" />
            <div className="pa-th pa-th-ext" title={anvelinaDxTooltip}>
              <img src={anvelinaLogo} alt="Anvelina" />
              <span className="pa-pill-mute">EXT 8–11</span>
            </div>
          </>
        )}
      </div>
      {HF_BANDS.map((bandName) => {
        const b = settings.bands.find((x) => x.band === bandName);
        if (!b) return null;
        const stdMask = side === 'tx' ? b.ocTx : b.ocRx;
        const extMask = side === 'tx' ? b.ocDxTx : b.ocDxRx;
        const stdKey = side === 'tx' ? ('ocTx' as const) : ('ocRx' as const);
        const extKey = side === 'tx' ? ('ocDxTx' as const) : ('ocDxRx' as const);
        return (
          <div key={bandName} className="pa-oc-row">
            <div className="pa-band">{b.band}</div>
            <PaSlider
              value={b.paGainDb}
              min={PA_GAIN_MIN_DB}
              max={paFieldMax}
              step={paFieldStep}
              unit={isHl2 ? '%' : 'dB'}
              onChange={(v) =>
                setBand(b.band, { paGainDb: clamp(v, PA_GAIN_MIN_DB, paFieldMax) })
              }
            />
            <div style={{ display: 'flex', justifyContent: 'center' }}>
              <input
                type="checkbox"
                checked={b.disablePa}
                onChange={(e) => setBand(b.band, { disablePa: e.target.checked })}
                className="h-4 w-4"
                style={{ accentColor: 'var(--accent)' }}
                title="Disable PA for this band entirely — drive byte is forced to 0 regardless of slider."
              />
            </div>
            <PillBar
              label={`${bandName} ${stdLabel}`}
              mask={stdMask}
              onChange={(next) => setBand(b.band, { [stdKey]: next })}
              size="lg"
            />
            {showAnvelinaExt && (
              <>
                <span className="pa-oc-vdivider" aria-hidden="true" />
                {/* Always interactive when visible. On non-Anvelina radios
                    the server-side gate in Protocol2Client.SendCmdHighPriority
                    keeps byte 1397 at 0 anyway — clicking here only edits the
                    persisted mask in zeus-prefs.db, which is exactly what
                    you want for exercising the wire path during bench tests
                    (the operator opted in via the EXT 8–11 toggle). */}
                <PillBar
                  label={`${bandName} Anvelina ${stdLabel}`}
                  mask={extMask}
                  onChange={(next) => setBand(b.band, { [extKey]: next })}
                  pins={ANVELINA_DX_PINS}
                  bitOffset={ANVELINA_DX_BIT_OFFSET}
                  pinTitles={ANVELINA_DX_PIN_LABELS}
                  size="lg"
                  ext
                />
              </>
            )}
          </div>
        );
      })}
    </div>
  );
}

// ── AUTO N2ADR pane ──────────────────────────────────────────
// HL2-only mirror of the firmware's per-band LPF mask. Each LPF pin
// is a stacked tile (number on top, band-range label below) so the
// operator can see at a glance which filter bank the radio asserts
// for each band. Read-only — the firmware decides this, not the
// user — so the visual idiom is intentionally calmer than the
// blue/green pin bars on the OC tabs.
function AutoPane(props: {
  settings: ReturnType<typeof usePaStore.getState>['settings'];
  paFieldLabel: string;
  paFieldMax: number;
  paFieldStep: number;
  paFieldTitle: string;
  isHl2: boolean;
  setBand: (band: string, patch: Partial<Omit<PaBandSettings, 'band'>>) => void;
}) {
  const { settings, paFieldLabel, paFieldMax, paFieldStep, paFieldTitle, isHl2, setBand } = props;
  return (
    <div className="pa-card pa-table-shell auto">
      <div className="pa-auto-row is-thead">
        <div className="pa-th">Band</div>
        <div className="pa-th" title={paFieldTitle}>{paFieldLabel}</div>
        <div className="pa-th" style={{ justifyContent: 'center' }}>Dis PA</div>
        <div className="pa-th">
          N2ADR&nbsp;LPF&nbsp;pins&nbsp;<span className="pa-pill-mute">1–7 · auto-asserted</span>
        </div>
      </div>
      {HF_BANDS.map((bandName) => {
        const b = settings.bands.find((x) => x.band === bandName);
        if (!b) return null;
        return (
          <div key={bandName} className="pa-auto-row">
            <div className="pa-band">{b.band}</div>
            <PaSlider
              value={b.paGainDb}
              min={PA_GAIN_MIN_DB}
              max={paFieldMax}
              step={paFieldStep}
              unit={isHl2 ? '%' : 'dB'}
              onChange={(v) =>
                setBand(b.band, { paGainDb: clamp(v, PA_GAIN_MIN_DB, paFieldMax) })
              }
            />
            <div style={{ display: 'flex', justifyContent: 'center' }}>
              <input
                type="checkbox"
                checked={b.disablePa}
                onChange={(e) => setBand(b.band, { disablePa: e.target.checked })}
                className="h-4 w-4"
                style={{ accentColor: 'var(--accent)' }}
              />
            </div>
            <div className="pa-lpf-bar">
              {[1, 2, 3, 4, 5, 6, 7].map((pin) => {
                const active = (b.autoOcMask & (1 << (pin - 1))) !== 0;
                return (
                  <div
                    key={pin}
                    className={'pa-lpf-tile' + (active ? ' is-active' : '')}
                    title={`LPF pin ${pin} — ${LPF_RANGES[pin]} — ${active ? 'asserted on this band' : 'not asserted'}`}
                  >
                    <span className="pa-lpf-n">{pin}</span>
                    <span className="pa-lpf-rng">{LPF_RANGES[pin]}</span>
                  </div>
                );
              })}
            </div>
          </div>
        );
      })}
    </div>
  );
}
