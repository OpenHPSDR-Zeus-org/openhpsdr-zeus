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
import { BOARD_LABELS } from '../api/radio';

const OC_PINS = [1, 2, 3, 4, 5, 6, 7] as const;

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
// container holds 7 tappable pins; clicking flips the bit, and dragging
// across multiple pins paints them to the first-clicked target value
// (so changing a whole row is one drag, not 7 clicks). Read-only mode is
// used for the Auto N2ADR column and disables the click + drag handlers.
function PillBar({
  label,
  mask,
  onChange,
  readOnly = false,
}: {
  label: string;
  mask: number;
  onChange?: (next: number) => void;
  readOnly?: boolean;
}) {
  const [paintTo, setPaintTo] = useState<0 | 1 | null>(null);
  useEffect(() => {
    if (paintTo === null) return;
    const up = () => setPaintTo(null);
    window.addEventListener('mouseup', up);
    return () => window.removeEventListener('mouseup', up);
  }, [paintTo]);

  const setBit = (bit: number, on: 0 | 1) => {
    if (!onChange) return;
    const b = 1 << (bit - 1);
    onChange(on ? mask | b : mask & ~b);
  };

  return (
    <span className={'pa-pill-bar' + (readOnly ? ' ro' : '')}>
      {OC_PINS.map((bit) => {
        const active = (mask & (1 << (bit - 1))) !== 0;
        const title = readOnly
          ? `${label} pin ${bit} — ${active ? 'firmware-driven' : 'not driven'}`
          : `${label} pin ${bit}`;
        return (
          <span
            key={bit}
            role={readOnly ? undefined : 'button'}
            aria-pressed={readOnly ? undefined : active}
            title={title}
            className={'pa-pill' + (active ? ' on' : '') + (readOnly ? ' ro' : '')}
            onMouseDown={
              readOnly
                ? undefined
                : (e) => {
                    e.preventDefault();
                    const next: 0 | 1 = active ? 0 : 1;
                    setPaintTo(next);
                    setBit(bit, next);
                  }
            }
            onMouseEnter={
              readOnly
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
  const selection = useRadioStore((s) => s.selection);

  // Show the "Auto N2ADR" column only when the board has a non-zero
  // firmware auto-mask for at least one band — i.e. an HL2. Bare-Hermes
  // and ANAN/Orion users don't need a column of empty chips, and showing
  // it on first-boot (Unknown board, autoOcMask=0) would be visual noise.
  const showAutoCol = settings.bands.some((b) => b.autoOcMask > 0);

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
        </div>
      </section>

      <section>
        <h3 className="pa-section-h mb-2">Per Band</h3>
        {showAutoCol && (
          <p className="pa-hint mb-2 text-[10px]">
            Auto column shows the N2ADR LPF mask the HL2 fires automatically on band change.
            These pins assert even when your OC TX / OC RX rows below are empty — pin colour matches state.
          </p>
        )}
        <div className="pa-card overflow-x-auto">
          <table className="w-full border-collapse text-xs">
            <thead className="pa-col-head text-[10px] uppercase tracking-wider">
              <tr>
                <th className="px-2 py-2 text-left">Band</th>
                <th className="px-2 py-2 text-right" title={paFieldTitle}>
                  {paFieldLabel}
                </th>
                <th className="px-2 py-2 text-center">Disable PA</th>
                {showAutoCol && (
                  <th
                    className="px-2 py-2 text-left"
                    title="Read-only: OC pins the firmware drives automatically per band (N2ADR LPF on HL2). OR'd with your OC TX / OC RX masks before the wire."
                  >
                    Auto N2ADR (1..7)
                  </th>
                )}
                <th className="px-2 py-2 text-left">OC TX (1..7)</th>
                <th className="px-2 py-2 text-left">OC RX (1..7)</th>
              </tr>
            </thead>
            <tbody>
              {HF_BANDS.map((bandName) => {
                const b = settings.bands.find((x) => x.band === bandName);
                if (!b) return null;
                return (
                  <tr key={bandName} className="pa-row pa-band-row">
                    <td className="px-2 font-mono">{b.band}</td>
                    <td className="px-2">
                      <PaSlider
                        value={b.paGainDb}
                        min={PA_GAIN_MIN_DB}
                        max={paFieldMax}
                        step={paFieldStep}
                        unit={isHl2 ? '%' : 'dB'}
                        onChange={(v) =>
                          setBand(b.band, {
                            paGainDb: clamp(v, PA_GAIN_MIN_DB, paFieldMax),
                          })
                        }
                      />
                    </td>
                    <td className="px-2 text-center">
                      <input
                        type="checkbox"
                        checked={b.disablePa}
                        onChange={(e) => setBand(b.band, { disablePa: e.target.checked })}
                        className="h-3 w-3"
                        style={{ accentColor: 'var(--accent)' }}
                      />
                    </td>
                    {showAutoCol && (
                      <td className="px-2">
                        <PillBar
                          label={`${bandName} N2ADR auto`}
                          mask={b.autoOcMask}
                          readOnly
                        />
                      </td>
                    )}
                    <td className="px-2">
                      <PillBar
                        label={`${bandName} OC-TX`}
                        mask={b.ocTx}
                        onChange={(next) => setBand(b.band, { ocTx: next })}
                      />
                    </td>
                    <td className="px-2">
                      <PillBar
                        label={`${bandName} OC-RX`}
                        mask={b.ocRx}
                        onChange={(next) => setBand(b.band, { ocRx: next })}
                      />
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </section>

      <p className="pa-hint text-[11px]">
        {inflight ? 'Saving…' : loaded ? 'Loaded from server — use APPLY below to persist edits' : 'Loading…'}
        {error ? ` · error: ${error}` : ''}
      </p>
    </div>
  );
}
