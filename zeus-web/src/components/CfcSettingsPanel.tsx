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

import { useCallback, useEffect, useRef, useState } from 'react';

import type { CfcBandDto, CfcConfigDto, CfcPresetDto } from '../api/client';
import { fetchCfcPresets, saveCfcPreset, setCfcConfig } from '../api/client';
import {
  DX_CFC_CONFIG,
  ESSB_CFC_CONFIG,
  STUDIO_SSB_CFC_CONFIG,
} from '../audio/tx-station-profile';
import { TextInputDialog } from '../layout/TextInputDialog';
import { useTxStore } from '../state/tx-store';

import './CfcSettingsPanel.css';

// 10-band Continuous Frequency Compressor — issue #123. Variant A of the
// Claude Design handoff bundle (CFC Settings.html / cfc-curve.jsx): a
// log-frequency curve with draggable knots for compression (downward) and
// post-gain (centred), plus a per-band numerical strip and preset chips.

const FREQ_MIN = 10;
const FREQ_MAX = 9990;
const COMP_MIN = 0;
const COMP_MAX = 20;
const POST_MIN = -20;
const POST_MAX = 20;
const PRECOMP_MIN = -50;
const PRECOMP_MAX = 16;
const PREPEQ_MIN = -50;
const PREPEQ_MAX = 16;

// Curve geometry (viewBox units; rendered with preserveAspectRatio="none"
// so x stretches to container width but y stays 1:1 with the 280px canvas).
const W = 760;
const H = 280;
const PAD_L = 36;
const PAD_R = 12;
const PAD_T = 16;
const PAD_B = 28;
const INNER_W = W - PAD_L - PAD_R;
const INNER_H = H - PAD_T - PAD_B;

// Visual maxima for the curve. Comp axis 0..16 dB (top→bottom);
// post axis ±8 dB (centred). The DTO bounds (COMP_MAX = 20, POST_MAX = 20)
// stay authoritative — the curve just clips its visualisation here.
const VIS_COMP_MAX = 16;
const VIS_POST_MAX = 8;

const FREQ_TICKS = [50, 100, 200, 500, 1000, 2000, 5000];
const DB_TICKS = [0, 4, 8, 12, 16];

type DragKind = 'comp' | 'post';
type DragState = {
  idx: number;
  kind: DragKind;
  startY: number;
  startV: number;
};

function clamp(v: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(hi, v));
}

function logX(hz: number, lo = 20, hi = 8000): number {
  const a = Math.log10(lo);
  const b = Math.log10(hi);
  const t = (Math.log10(Math.max(lo, hz)) - a) / (b - a);
  return clamp(t, 0, 1);
}

function fmtHz(hz: number): string {
  if (hz >= 1000) {
    const k = hz / 1000;
    return `${k % 1 === 0 ? k.toFixed(0) : k.toFixed(1)}k`;
  }
  return String(hz);
}

const xFor = (hz: number, innerW = INNER_W) => PAD_L + logX(hz) * innerW;
const yComp = (db: number) =>
  PAD_T + clamp(db, 0, VIS_COMP_MAX) / VIS_COMP_MAX * INNER_H;
const yPost = (db: number) =>
  PAD_T + INNER_H / 2 - clamp(db, -VIS_POST_MAX, VIS_POST_MAX) / VIS_POST_MAX * (INNER_H / 2);

// Catmull-Rom-ish smooth path through a series of points.
function smoothPath(pts: ReadonlyArray<readonly [number, number]>): string {
  if (pts.length === 0) return '';
  const first = pts[0]!;
  let d = `M ${first[0]} ${first[1]}`;
  for (let i = 0; i < pts.length - 1; i++) {
    const p0 = pts[i]!;
    const p1 = pts[i + 1]!;
    const cx = (p0[0] + p1[0]) / 2;
    d += ` C ${cx} ${p0[1]}, ${cx} ${p1[1]}, ${p1[0]} ${p1[1]}`;
  }
  return d;
}

function compPath(bands: CfcBandDto[], innerW = INNER_W): string {
  if (!bands.length) return '';
  const pts = bands.map((b) => [xFor(b.freqHz, innerW), yComp(b.compLevelDb)] as const);
  const first = pts[0]!;
  return `M ${PAD_L} ${yComp(0)} L ${first[0]} ${first[1]}` +
    smoothPath(pts).slice(`M ${first[0]} ${first[1]}`.length) +
    ` L ${PAD_L + innerW} ${yComp(0)}`;
}

function postPath(bands: CfcBandDto[], innerW = INNER_W): string {
  if (!bands.length) return '';
  const pts = bands.map((b) => [xFor(b.freqHz, innerW), yPost(b.postGainDb)] as const);
  return smoothPath(pts);
}

// "Voice" preset — operator-recognisable starting point that demonstrates
// the curve. Master pre-comp lifts 3 dB to give the bands something to
// chew on. Mirrors the design's CFC_BANDS_VOICE.
const VOICE_BANDS: CfcBandDto[] = [
  { freqHz: 80,   compLevelDb: 2,  postGainDb: -2 },
  { freqHz: 150,  compLevelDb: 4,  postGainDb: -1 },
  { freqHz: 250,  compLevelDb: 6,  postGainDb: 0  },
  { freqHz: 500,  compLevelDb: 8,  postGainDb: 1  },
  { freqHz: 900,  compLevelDb: 10, postGainDb: 2  },
  { freqHz: 1500, compLevelDb: 12, postGainDb: 3  },
  { freqHz: 2200, compLevelDb: 10, postGainDb: 3  },
  { freqHz: 2800, compLevelDb: 7,  postGainDb: 2  },
  { freqHz: 3500, compLevelDb: 4,  postGainDb: 0  },
  { freqHz: 5000, compLevelDb: 1,  postGainDb: -1 },
];

const FLAT_BANDS: CfcBandDto[] = [
  { freqHz: 50,   compLevelDb: 0, postGainDb: 0 },
  { freqHz: 100,  compLevelDb: 0, postGainDb: 0 },
  { freqHz: 200,  compLevelDb: 0, postGainDb: 0 },
  { freqHz: 500,  compLevelDb: 0, postGainDb: 0 },
  { freqHz: 1000, compLevelDb: 0, postGainDb: 0 },
  { freqHz: 1500, compLevelDb: 0, postGainDb: 0 },
  { freqHz: 2000, compLevelDb: 0, postGainDb: 0 },
  { freqHz: 2500, compLevelDb: 0, postGainDb: 0 },
  { freqHz: 3000, compLevelDb: 0, postGainDb: 0 },
  { freqHz: 5000, compLevelDb: 0, postGainDb: 0 },
];

// Detect which preset (if any) the current bands match, so the chip
// highlights stay accurate after manual edits + reloads.
function bandsMatch(a: CfcBandDto[], b: CfcBandDto[]): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) {
    const ai = a[i]!;
    const bi = b[i]!;
    if (ai.freqHz !== bi.freqHz) return false;
    if (ai.compLevelDb !== bi.compLevelDb) return false;
    if (ai.postGainDb !== bi.postGainDb) return false;
  }
  return true;
}

function cloneCfcConfig(config: CfcConfigDto): CfcConfigDto {
  return {
    enabled: config.enabled,
    postEqEnabled: config.postEqEnabled,
    preCompDb: config.preCompDb,
    prePeqDb: config.prePeqDb,
    bands: config.bands.map((b) => ({ ...b })),
  };
}

function presetKey(name: string): string {
  return name.trim().toLowerCase();
}

function sortPresets(presets: CfcPresetDto[]): CfcPresetDto[] {
  return [...presets].sort((a, b) =>
    a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }),
  );
}

function upsertPreset(presets: CfcPresetDto[], saved: CfcPresetDto): CfcPresetDto[] {
  const key = presetKey(saved.name);
  return sortPresets([
    ...presets.filter((preset) => presetKey(preset.name) !== key),
    saved,
  ]);
}

function presetErrorMessage(error: unknown, fallback: string): string {
  return error instanceof Error && error.message ? error.message : fallback;
}

export function CfcSettingsPanel() {
  const cfc = useTxStore((s) => s.cfcConfig);
  const setLocal = useTxStore((s) => s.setCfcConfig);

  const [activeIdx, setActiveIdx] = useState(4);
  const [savedPresets, setSavedPresets] = useState<CfcPresetDto[]>([]);
  const [selectedSavedPreset, setSelectedSavedPreset] = useState('');
  const [presetDialogOpen, setPresetDialogOpen] = useState(false);
  const [presetError, setPresetError] = useState<string | null>(null);
  const [presetSaving, setPresetSaving] = useState(false);
  const dragRef = useRef<DragState | null>(null);
  const dragMovedRef = useRef(false);

  // Track the SVG's rendered pixel width and feed it back into the viewBox so
  // the curve scales 1:1 on both axes. With a fixed 760-unit viewBox and
  // preserveAspectRatio="none", any container width ≠ 760 stretches x relative
  // to y — turning the knot circles into ellipses and distorting strokes/text.
  // Matching the viewBox width to the measured width keeps the y-axis pinned to
  // the 280px canvas (so the drag math below stays 1 client-px ≈ 1 viewBox-px)
  // while x stays undistorted at any size.
  const svgRef = useRef<SVGSVGElement>(null);
  const [vbW, setVbW] = useState(W);
  useEffect(() => {
    const el = svgRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver((entries) => {
      const w = entries[0]?.contentRect.width;
      if (w && w > 0) setVbW(Math.round(w));
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  const innerW = vbW - PAD_L - PAD_R;

  useEffect(() => {
    const ac = new AbortController();
    fetchCfcPresets(ac.signal)
      .then((presets) => setSavedPresets(sortPresets(presets)))
      .catch((error) => {
        if (!ac.signal.aborted) {
          setPresetError(presetErrorMessage(error, 'Unable to load CFC presets'));
        }
      });
    return () => ac.abort();
  }, []);

  // Optimistic-update gate (used outside of drag). Drag updates local state
  // every mousemove and only POSTs once on mouseup — POSTing per-pixel
  // would saturate the radio's serial-config queue.
  const push = useCallback(
    (next: CfcConfigDto) => {
      const prev = useTxStore.getState().cfcConfig;
      setLocal(next);
      setCfcConfig(next).catch(() => setLocal(prev));
    },
    [setLocal],
  );

  useEffect(() => {
    const onMove = (e: MouseEvent) => {
      const drag = dragRef.current;
      if (!drag) return;
      e.preventDefault();
      const dy = e.clientY - drag.startY;
      // SVG renders at fixed 280 CSS px height (preserveAspectRatio="none"
      // stretches only x), so 1 client-px ≈ 1 viewBox-px on the y axis.
      const range = drag.kind === 'comp' ? VIS_COMP_MAX : VIS_POST_MAX * 2;
      const scale = range / INNER_H;
      let v = drag.startV + (drag.kind === 'comp' ? dy : -dy) * scale;
      v = drag.kind === 'comp'
        ? clamp(v, COMP_MIN, COMP_MAX)
        : clamp(v, POST_MIN, POST_MAX);
      v = Math.round(v * 10) / 10;
      const cur = useTxStore.getState().cfcConfig;
      const field = drag.kind === 'comp' ? 'compLevelDb' : 'postGainDb';
      const bands = cur.bands.map((b, i) =>
        i === drag.idx ? { ...b, [field]: v } : b,
      );
      setLocal({ ...cur, bands });
      dragMovedRef.current = true;
    };
    const onUp = () => {
      const drag = dragRef.current;
      dragRef.current = null;
      if (!drag) return;
      if (dragMovedRef.current) {
        dragMovedRef.current = false;
        const final = useTxStore.getState().cfcConfig;
        setCfcConfig(final).catch(() => {
          // Roll back to the pre-drag value on POST failure.
          const prev = useTxStore.getState().cfcConfig;
          const field = drag.kind === 'comp' ? 'compLevelDb' : 'postGainDb';
          const bands = prev.bands.map((b, i) =>
            i === drag.idx ? { ...b, [field]: drag.startV } : b,
          );
          setLocal({ ...prev, bands });
        });
      }
    };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
    return () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    };
  }, [setLocal]);

  const onKnotDown = useCallback(
    (idx: number, kind: DragKind, e: React.MouseEvent) => {
      e.preventDefault();
      e.stopPropagation();
      setActiveIdx(idx);
      const cur = useTxStore.getState().cfcConfig;
      const band = cur.bands[idx];
      if (!band) return;
      const startV = kind === 'comp' ? band.compLevelDb : band.postGainDb;
      dragRef.current = { idx, kind, startY: e.clientY, startV };
      dragMovedRef.current = false;
    },
    [],
  );

  const setMaster = useCallback(
    (overrides: Partial<Omit<CfcConfigDto, 'bands'>>) => {
      push({ ...cfc, ...overrides });
    },
    [cfc, push],
  );

  const setBand = useCallback(
    (idx: number, overrides: Partial<CfcBandDto>) => {
      const bands = cfc.bands.map((b, i) => (i === idx ? { ...b, ...overrides } : b));
      push({ ...cfc, bands });
    },
    [cfc, push],
  );

  const applySavedPreset = useCallback(
    (name: string) => {
      const preset = savedPresets.find((item) => presetKey(item.name) === presetKey(name));
      if (!preset) {
        setSelectedSavedPreset('');
        return;
      }
      setSelectedSavedPreset(preset.name);
      setPresetError(null);
      push(cloneCfcConfig(preset.config));
    },
    [push, savedPresets],
  );

  const saveCurrentPreset = useCallback(
    async (name: string): Promise<boolean> => {
      const cleanName = name.trim();
      if (!cleanName) {
        setPresetError('Preset name required');
        return false;
      }

      setPresetSaving(true);
      setPresetError(null);
      try {
        const saved = await saveCfcPreset(cleanName, useTxStore.getState().cfcConfig);
        setSavedPresets((presets) => upsertPreset(presets, saved));
        setSelectedSavedPreset(saved.name);
        return true;
      } catch (error) {
        setPresetError(presetErrorMessage(error, 'Unable to save CFC preset'));
        return false;
      } finally {
        setPresetSaving(false);
      }
    },
    [],
  );

  const applyPreset = useCallback(
    (preset: 'flat' | 'voice' | 'studio' | 'essb' | 'dx') => {
      setSelectedSavedPreset('');
      setPresetError(null);
      if (preset === 'flat') {
        push({
          ...cfc,
          enabled: true,
          postEqEnabled: false,
          preCompDb: 0,
          prePeqDb: 0,
          bands: FLAT_BANDS.map((b) => ({ ...b })),
        });
      } else if (preset === 'studio') {
        push({
          ...cfc,
          enabled: true,
          preCompDb: STUDIO_SSB_CFC_CONFIG.preCompDb,
          prePeqDb: STUDIO_SSB_CFC_CONFIG.prePeqDb,
          postEqEnabled: STUDIO_SSB_CFC_CONFIG.postEqEnabled,
          bands: STUDIO_SSB_CFC_CONFIG.bands.map((b) => ({ ...b })),
        });
      } else if (preset === 'essb') {
        push({
          ...cfc,
          enabled: true,
          preCompDb: ESSB_CFC_CONFIG.preCompDb,
          prePeqDb: ESSB_CFC_CONFIG.prePeqDb,
          postEqEnabled: ESSB_CFC_CONFIG.postEqEnabled,
          bands: ESSB_CFC_CONFIG.bands.map((b) => ({ ...b })),
        });
      } else if (preset === 'dx') {
        push({
          ...cfc,
          enabled: true,
          preCompDb: DX_CFC_CONFIG.preCompDb,
          prePeqDb: DX_CFC_CONFIG.prePeqDb,
          postEqEnabled: DX_CFC_CONFIG.postEqEnabled,
          bands: DX_CFC_CONFIG.bands.map((b) => ({ ...b })),
        });
      } else {
        push({
          ...cfc,
          enabled: true,
          postEqEnabled: true,
          preCompDb: 3,
          prePeqDb: 0,
          bands: VOICE_BANDS.map((b) => ({ ...b })),
        });
      }
    },
    [cfc, push],
  );

  const isFlat = bandsMatch(cfc.bands, FLAT_BANDS) && cfc.preCompDb === 0 && cfc.prePeqDb === 0;
  const isVoice = bandsMatch(cfc.bands, VOICE_BANDS) && cfc.preCompDb === 3 && cfc.prePeqDb === 0;
  const isStudio =
    bandsMatch(cfc.bands, STUDIO_SSB_CFC_CONFIG.bands) &&
    cfc.preCompDb === STUDIO_SSB_CFC_CONFIG.preCompDb &&
    cfc.prePeqDb === STUDIO_SSB_CFC_CONFIG.prePeqDb &&
    cfc.postEqEnabled === STUDIO_SSB_CFC_CONFIG.postEqEnabled;
  const isEssb =
    bandsMatch(cfc.bands, ESSB_CFC_CONFIG.bands) &&
    cfc.preCompDb === ESSB_CFC_CONFIG.preCompDb &&
    cfc.prePeqDb === ESSB_CFC_CONFIG.prePeqDb &&
    cfc.postEqEnabled === ESSB_CFC_CONFIG.postEqEnabled;
  const isDx =
    bandsMatch(cfc.bands, DX_CFC_CONFIG.bands) &&
    cfc.preCompDb === DX_CFC_CONFIG.preCompDb &&
    cfc.prePeqDb === DX_CFC_CONFIG.prePeqDb &&
    cfc.postEqEnabled === DX_CFC_CONFIG.postEqEnabled;

  return (
    <div className="cfc-panel">
      <div className="cfc-blurb">
        <span className="tag">CFC</span>
        <p>
          <strong>Continuous Frequency Compressor</strong> — multi-band
          frequency-domain compressor mirroring pihpsdr's classic 10-band
          design. Defaults to OFF; enabling with neutral settings (0/0) is
          audibly transparent.
        </p>
      </div>

      <div className="cfc-master">
        <label className={`cfc-power ${cfc.enabled ? '' : 'is-off'}`}>
          <input
            type="checkbox"
            checked={cfc.enabled}
            onChange={(e) => setMaster({ enabled: e.target.checked })}
          />
          <span className="sw" />
          <span className="lbl">{cfc.enabled ? 'CFC On' : 'Bypass'}</span>
        </label>

        <div className="m-toggles">
          <label className="cfc-check">
            <input
              type="checkbox"
              checked={cfc.postEqEnabled}
              onChange={(e) => setMaster({ postEqEnabled: e.target.checked })}
            />
            <span className="box" />
            <span>Post-EQ chain</span>
          </label>
        </div>

        <div className="m-trims">
          <div className="m-trim">
            <label htmlFor="cfc-precomp">Pre-comp</label>
            <div className="ninput">
              <input
                id="cfc-precomp"
                type="number"
                value={cfc.preCompDb}
                step={0.5}
                min={PRECOMP_MIN}
                max={PRECOMP_MAX}
                onChange={(e) => {
                  const v = Number(e.target.value);
                  if (Number.isFinite(v)) setMaster({ preCompDb: clamp(v, PRECOMP_MIN, PRECOMP_MAX) });
                }}
              />
              <span className="u">dB</span>
            </div>
          </div>
          <div className="m-trim">
            <label htmlFor="cfc-prepeq">Pre-peq</label>
            <div className="ninput">
              <input
                id="cfc-prepeq"
                type="number"
                value={cfc.prePeqDb}
                step={0.5}
                min={PREPEQ_MIN}
                max={PREPEQ_MAX}
                onChange={(e) => {
                  const v = Number(e.target.value);
                  if (Number.isFinite(v)) setMaster({ prePeqDb: clamp(v, PREPEQ_MIN, PREPEQ_MAX) });
                }}
              />
              <span className="u">dB</span>
            </div>
          </div>
        </div>

        <div className="cfc-presets">
          <span className="lab">Preset</span>
          <button
            type="button"
            className={`chip ${isFlat ? 'on' : ''}`}
            onClick={() => applyPreset('flat')}
          >
            Flat
          </button>
          <button
            type="button"
            className={`chip ${isVoice ? 'on' : ''}`}
            onClick={() => applyPreset('voice')}
          >
            Voice
          </button>
          <button
            type="button"
            className={`chip ${isStudio ? 'on' : ''}`}
            onClick={() => applyPreset('studio')}
          >
            Studio
          </button>
          <button
            type="button"
            className={`chip ${isEssb ? 'on' : ''}`}
            onClick={() => applyPreset('essb')}
          >
            ESSB
          </button>
          <button
            type="button"
            className={`chip ${isDx ? 'on' : ''}`}
            onClick={() => applyPreset('dx')}
          >
            DX
          </button>
          <div className="cfc-user-presets">
            <select
              value={selectedSavedPreset}
              aria-label="Saved CFC presets"
              title="Saved CFC presets"
              disabled={presetSaving || savedPresets.length === 0}
              onChange={(e) => {
                const name = e.target.value;
                if (!name) {
                  setSelectedSavedPreset('');
                  return;
                }
                applySavedPreset(name);
              }}
            >
              <option value="">{savedPresets.length ? 'Saved' : 'No saved'}</option>
              {savedPresets.map((preset) => (
                <option key={preset.name} value={preset.name}>
                  {preset.name}
                </option>
              ))}
            </select>
            <button
              type="button"
              className="chip action"
              disabled={presetSaving}
              onClick={() => {
                setPresetError(null);
                setPresetDialogOpen(true);
              }}
            >
              Save
            </button>
            <button
              type="button"
              className="chip action"
              disabled={presetSaving || !selectedSavedPreset}
              onClick={() => { void saveCurrentPreset(selectedSavedPreset); }}
            >
              Overwrite
            </button>
          </div>
          {presetError && !presetDialogOpen && (
            <span className="preset-error" role="status">{presetError}</span>
          )}
        </div>
      </div>

      <div className={`cfc-curve cfc-body ${cfc.enabled ? '' : 'is-bypass'}`}>
        <div className="canvas">
          <svg ref={svgRef} viewBox={`0 0 ${vbW} ${H}`} preserveAspectRatio="none">
            <defs>
              <linearGradient id="cfc-curve-fill" x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor="var(--accent)" stopOpacity="0.45" />
                <stop offset="100%" stopColor="var(--accent)" stopOpacity="0.02" />
              </linearGradient>
            </defs>

            {DB_TICKS.map((d) => (
              <g key={`db${d}`}>
                <line
                  className="gridline"
                  x1={PAD_L}
                  x2={PAD_L + innerW}
                  y1={yComp(d)}
                  y2={yComp(d)}
                />
                <text
                  className="axis-text"
                  x={PAD_L - 6}
                  y={yComp(d) + 3}
                  textAnchor="end"
                >
                  −{d}
                </text>
              </g>
            ))}

            <line
              className="gridmid"
              x1={PAD_L}
              x2={PAD_L + innerW}
              y1={PAD_T + INNER_H / 2}
              y2={PAD_T + INNER_H / 2}
            />

            {FREQ_TICKS.map((f) => (
              <g key={`f${f}`}>
                <line
                  className="freq-tick"
                  x1={xFor(f, innerW)}
                  x2={xFor(f, innerW)}
                  y1={PAD_T + INNER_H}
                  y2={PAD_T + INNER_H + 4}
                />
                <text
                  className="axis-text"
                  x={xFor(f, innerW)}
                  y={PAD_T + INNER_H + 16}
                  textAnchor="middle"
                >
                  {fmtHz(f)}
                </text>
              </g>
            ))}
            <text
              className="axis-text"
              x={PAD_L + innerW}
              y={PAD_T + INNER_H + 16}
              textAnchor="end"
            >
              Hz
            </text>

            <path className="band-area" d={compPath(cfc.bands, innerW) + ` L ${PAD_L} ${yComp(0)} Z`} />
            <path className="band-line" d={compPath(cfc.bands, innerW)} />
            <path className="post-line" d={postPath(cfc.bands, innerW)} />

            {cfc.bands.map((b, i) => (
              <g key={`k${i}`}>
                <text
                  className="band-label"
                  x={xFor(b.freqHz, innerW)}
                  y={PAD_T - 4}
                >
                  {i + 1}
                </text>
                <circle
                  className={`knot ${activeIdx === i ? 'is-active' : ''}`}
                  cx={xFor(b.freqHz, innerW)}
                  cy={yComp(b.compLevelDb)}
                  r={5.5}
                  onMouseDown={(e) => onKnotDown(i, 'comp', e)}
                />
                <circle
                  className={`knot post ${activeIdx === i ? 'is-active' : ''}`}
                  cx={xFor(b.freqHz, innerW)}
                  cy={yPost(b.postGainDb)}
                  r={4}
                  onMouseDown={(e) => onKnotDown(i, 'post', e)}
                />
              </g>
            ))}
          </svg>
        </div>

        <div className="legend">
          <span className="k">Compression (dB ↓)</span>
          <span className="k post">Post-gain (±dB)</span>
          <span className="sp" />
          <span>Drag knots to edit</span>
        </div>

        <div className="strip">
          <div className="col lbl">
            <em>Band</em>
            <em className="t">Freq · Hz</em>
            <em className="t b">Comp · dB</em>
            <em className="t b">Post · dB</em>
          </div>
          {cfc.bands.map((b, i) => (
            <div
              key={i}
              className={`col ${activeIdx === i ? 'is-active' : ''}`}
              onClick={() => setActiveIdx(i)}
            >
              <div className="bnum">{i + 1}</div>
              <input
                type="number"
                value={b.freqHz}
                min={FREQ_MIN}
                max={FREQ_MAX}
                step={10}
                onChange={(e) => {
                  const v = Number(e.target.value);
                  if (Number.isFinite(v)) setBand(i, { freqHz: clamp(Math.round(v), FREQ_MIN, FREQ_MAX) });
                }}
              />
              <input
                type="number"
                value={b.compLevelDb}
                min={COMP_MIN}
                max={COMP_MAX}
                step={0.5}
                onChange={(e) => {
                  const v = Number(e.target.value);
                  if (Number.isFinite(v)) setBand(i, { compLevelDb: clamp(v, COMP_MIN, COMP_MAX) });
                }}
              />
              <input
                type="number"
                value={b.postGainDb}
                min={POST_MIN}
                max={POST_MAX}
                step={0.5}
                onChange={(e) => {
                  const v = Number(e.target.value);
                  if (Number.isFinite(v)) setBand(i, { postGainDb: clamp(v, POST_MIN, POST_MAX) });
                }}
              />
            </div>
          ))}
        </div>
      </div>

      {presetDialogOpen && (
        <TextInputDialog
          title="Save CFC preset"
          label="Preset name"
          initialValue={selectedSavedPreset}
          placeholder="Ragchew"
          confirmLabel={presetSaving ? 'Saving' : 'Save Preset'}
          onCancel={() => {
            if (presetSaving) return;
            setPresetDialogOpen(false);
            setPresetError(null);
          }}
          onSubmit={async (name) => {
            const saved = await saveCurrentPreset(name);
            if (saved) setPresetDialogOpen(false);
          }}
        >
          {presetError && (
            <p className="cfc-preset-dialog-error" role="alert">{presetError}</p>
          )}
        </TextInputDialog>
      )}
    </div>
  );
}
