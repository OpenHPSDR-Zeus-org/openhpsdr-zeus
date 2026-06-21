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

import { useId, useMemo } from 'react';
import { useDisplaySettingsStore } from '../state/display-settings-store';

const SWATCHES: ReadonlyArray<string> = [
  '#FFA028', '#FF7A1A', '#FFD93D', '#FF5C72', '#B8E83C', '#3CCB6E',
  '#3CE0B5', '#5BD4FF', '#4D8BFF', '#A777FF', '#FF55C8', '#E6E8EE',
  '#FFFFFF', '#A0A6B2', '#5C6470', '#262A33', '#FF3B30', '#34C759',
  '#0A84FF', '#BF5AF2', '#FF9F0A', '#5AC8FA', '#FFD60A', '#30D158',
];

function isHexColor(v: string): boolean {
  return /^#[0-9A-Fa-f]{6}$/.test(v);
}

// Generate a canned panadapter trace that's recomputed only when the panel
// mounts. We don't want it dancing on every render — the purpose is to show
// how the user's chosen RX color reads against typical spectrum content, not
// to animate.
function buildTracePath(): string {
  const W = 360;
  const H = 108;
  const N = 90;
  const peaks = [
    { x: 80, h: 60, w: 4 },
    { x: 180, h: 40, w: 6 },
    { x: 270, h: 70, w: 3 },
  ];
  let d = `M0 ${H} `;
  let seed = 1337;
  const rand = () => {
    // Mulberry32 — small, repeatable. Lets the preview look "noisy" without
    // re-rolling on every render.
    seed = (seed + 0x6d2b79f5) | 0;
    let t = seed;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
  for (let i = 0; i <= N; i++) {
    const x = (i / N) * W;
    let y = 80 + Math.sin(i * 0.4) * 4 + (rand() - 0.5) * 6;
    for (const p of peaks) y -= p.h * Math.exp(-Math.pow((x - p.x) / p.w, 2));
    d += `L${x.toFixed(1)} ${Math.max(4, y).toFixed(1)} `;
  }
  return d + `L${W} ${H} Z`;
}

export function TraceColorPanel() {
  const rxTraceColor = useDisplaySettingsStore((s) => s.rxTraceColor);
  const setRxTraceColor = useDisplaySettingsStore((s) => s.setRxTraceColor);
  const norm = rxTraceColor.toUpperCase();
  const gradId = useId();
  const tracePath = useMemo(() => buildTracePath(), []);

  return (
    <section>
      <div style={sectionHead}>
        <h3 style={sectionH3}>RX Trace Color</h3>
        <p style={sectionP}>
          Affects only the spectrum graph — meters and passband keep their own colors.
        </p>
      </div>

      <div style={colorCard}>
        <div style={{ display: 'flex', flexDirection: 'column' }}>
          <div style={swatchesGrid}>
            {SWATCHES.map((c) => {
              const active = c.toUpperCase() === norm;
              return (
                <button
                  key={c}
                  type="button"
                  title={c}
                  aria-label={c}
                  aria-pressed={active}
                  onClick={() => setRxTraceColor(c)}
                  style={swatchStyle(c, active)}
                />
              );
            })}
          </div>

          <div style={customRow}>
            <span style={inlineLabel}>Custom</span>
            <input
              type="color"
              value={norm}
              onChange={(e) => setRxTraceColor(e.target.value.toUpperCase())}
              style={pickerStyle}
              aria-label="Custom RX trace color"
            />
            <input
              type="text"
              value={norm}
              onChange={(e) => {
                const v = e.target.value.startsWith('#') ? e.target.value : `#${e.target.value}`;
                if (isHexColor(v)) setRxTraceColor(v.toUpperCase());
              }}
              maxLength={7}
              spellCheck={false}
              style={hexInputStyle}
              aria-label="Hex color value"
            />
          </div>
        </div>

        <div style={previewCard} aria-hidden>
          <div style={previewHead}>
            <span>RX SPECTRUM</span>
            <span style={{ color: 'var(--fg-1)' }}>14.074 MHz</span>
          </div>
          <div style={previewCanvas}>
            <svg
              viewBox="0 0 360 108"
              preserveAspectRatio="none"
              style={{ position: 'absolute', inset: 0, width: '100%', height: '100%' }}
            >
              <defs>
                <linearGradient id={gradId} x1="0" x2="0" y1="0" y2="1">
                  <stop offset="0" stopColor={norm} stopOpacity="0.5" />
                  <stop offset="1" stopColor={norm} stopOpacity="0.04" />
                </linearGradient>
              </defs>
              <path
                d={tracePath}
                fill={`url(#${gradId})`}
                stroke={norm}
                strokeWidth={1.4}
                strokeLinejoin="round"
              />
            </svg>
          </div>
          <div style={previewFoot}>
            <span>Trace</span>
            <span style={{ color: 'var(--fg-0)' }}>{norm}</span>
          </div>
        </div>
      </div>
    </section>
  );
}

const sectionHead: React.CSSProperties = {
  display: 'flex',
  alignItems: 'baseline',
  flexWrap: 'wrap',
  gap: 10,
  marginBottom: 10,
};
const sectionH3: React.CSSProperties = {
  margin: 0,
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: '0.18em',
  textTransform: 'uppercase',
  color: 'var(--fg-0)',
};
const sectionP: React.CSSProperties = {
  margin: 0,
  fontSize: 12,
  lineHeight: 1.5,
  color: 'var(--fg-2)',
};
const inlineLabel: React.CSSProperties = {
  fontSize: 10,
  letterSpacing: '0.16em',
  textTransform: 'uppercase',
  color: 'var(--fg-2)',
  fontWeight: 600,
};

const colorCard: React.CSSProperties = {
  display: 'grid',
  gridTemplateColumns: '1fr 230px',
  gap: 18,
  alignItems: 'stretch',
  padding: 14,
  background: 'linear-gradient(180deg, var(--bg-1), var(--bg-0))',
  border: '1px solid var(--line)',
  borderRadius: 'var(--r-md)',
};

const swatchesGrid: React.CSSProperties = {
  display: 'grid',
  gridTemplateColumns: 'repeat(12, 1fr)',
  gap: 6,
};

function swatchStyle(color: string, active: boolean): React.CSSProperties {
  return {
    width: '100%',
    aspectRatio: '1 / 1',
    padding: 0,
    borderRadius: 'var(--r-sm)',
    background: color,
    border: active ? '1.5px solid var(--fg-0)' : '1.5px solid transparent',
    boxShadow: active
      ? '0 0 0 2px var(--bg-1), 0 0 0 3px var(--fg-0), inset 0 0 0 1px rgba(255,255,255,0.08)'
      : 'inset 0 0 0 1px rgba(255,255,255,0.08), inset 0 -6px 10px -6px rgba(0,0,0,0.35)',
    cursor: 'pointer',
    transition: 'transform var(--dur-fast), border-color var(--dur-fast)',
    transform: active ? 'translateY(-1px)' : 'none',
  };
}

const customRow: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 10,
  marginTop: 12,
  paddingTop: 12,
  borderTop: '1px dashed var(--line)',
};

const pickerStyle: React.CSSProperties = {
  width: 30,
  height: 22,
  borderRadius: 'var(--r-sm)',
  border: '1px solid var(--line)',
  padding: 0,
  background: 'transparent',
  cursor: 'pointer',
  overflow: 'hidden',
};

const hexInputStyle: React.CSSProperties = {
  background: 'var(--bg-2)',
  border: '1px solid var(--line)',
  color: 'var(--fg-0)',
  borderRadius: 'var(--r-sm)',
  padding: '5px 8px',
  width: 96,
  fontSize: 12,
  letterSpacing: '0.04em',
};

const previewCard: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  border: '1px solid var(--line)',
  borderRadius: 'var(--r-sm)',
  background: 'var(--spec-bg)',
  overflow: 'hidden',
};

const previewHead: React.CSSProperties = {
  padding: '6px 10px',
  borderBottom: '1px solid var(--line)',
  display: 'flex',
  justifyContent: 'space-between',
  alignItems: 'center',
  fontSize: 9.5,
  letterSpacing: '0.14em',
  color: 'var(--fg-3)',
  textTransform: 'uppercase',
};

const previewCanvas: React.CSSProperties = {
  flex: 1,
  position: 'relative',
  height: 108,
  background: [
    'repeating-linear-gradient(0deg, rgba(255,255,255,0.04) 0 1px, transparent 1px 22px)',
    'repeating-linear-gradient(90deg, rgba(255,255,255,0.04) 0 1px, transparent 1px 36px)',
    'radial-gradient(60% 80% at 50% 100%, rgba(255,255,255,0.04), transparent 70%)',
  ].join(', '),
};

const previewFoot: React.CSSProperties = {
  padding: '6px 10px',
  display: 'flex',
  justifyContent: 'space-between',
  alignItems: 'center',
  borderTop: '1px solid var(--line)',
  fontSize: 10,
  color: 'var(--fg-2)',
};
