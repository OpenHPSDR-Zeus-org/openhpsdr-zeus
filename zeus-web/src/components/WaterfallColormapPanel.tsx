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

import { useMemo } from 'react';
import { COLORMAPS, lutFor, type ColormapId } from '../gl/colormap';
import {
  useDisplaySettingsStore,
  type WaterfallRowCadence,
} from '../state/display-settings-store';

const CADENCE_OPTIONS: Array<{ value: WaterfallRowCadence; label: string; detail: string }> = [
  { value: 1, label: 'Smooth', detail: '30 Hz' },
  { value: 2, label: 'Balanced', detail: '15 Hz' },
  { value: 3, label: 'Economy', detail: '10 Hz' },
];

// Build a horizontal CSS gradient that mirrors the actual 256-entry LUT the
// WebGL waterfall samples, so the swatch the operator picks is exactly the
// palette they get. Sampled at 16 stops — dense enough that the eye can't tell
// it from the continuous LUT.
function gradientCss(id: ColormapId): string {
  const lut = lutFor(id);
  const stops: string[] = [];
  const N = 16;
  for (let i = 0; i <= N; i++) {
    const t = i / N;
    const base = Math.round(t * 255) * 4;
    const r = lut[base];
    const g = lut[base + 1];
    const b = lut[base + 2];
    stops.push(`rgb(${r}, ${g}, ${b}) ${((t * 100) | 0)}%`);
  }
  return `linear-gradient(90deg, ${stops.join(', ')})`;
}

export function WaterfallColormapPanel() {
  const colormap = useDisplaySettingsStore((s) => s.colormap);
  const setColormap = useDisplaySettingsStore((s) => s.setColormap);
  const waterfallRowCadence = useDisplaySettingsStore((s) => s.waterfallRowCadence);
  const setWaterfallRowCadence = useDisplaySettingsStore((s) => s.setWaterfallRowCadence);
  const gradients = useMemo(
    () => Object.fromEntries(COLORMAPS.map((cm) => [cm.id, gradientCss(cm.id)])) as Record<ColormapId, string>,
    [],
  );

  return (
    <section>
      <div style={sectionHead}>
        <h3 style={sectionH3}>Waterfall Colormap</h3>
        <p style={sectionP}>
          The palette the waterfall maps signal strength to — low power on the left, peaks on the right.
        </p>
      </div>

      <div role="radiogroup" aria-label="Waterfall colormap" style={cardGrid}>
        {COLORMAPS.map((cm) => {
          const active = colormap === cm.id;
          return (
            <button
              key={cm.id}
              type="button"
              role="radio"
              aria-checked={active}
              onClick={() => setColormap(cm.id)}
              style={swatchCard(active)}
            >
              <div style={{ ...gradientStrip, background: gradients[cm.id] }} aria-hidden />
              <span style={swatchLabel(active)}>{cm.label}</span>
            </button>
          );
        })}
      </div>

      <div style={cadenceBlock}>
        <span style={cadenceLabel}>Row Cadence</span>
        <div role="radiogroup" aria-label="Waterfall row cadence" style={cadenceRow}>
          {CADENCE_OPTIONS.map((opt) => {
            const active = waterfallRowCadence === opt.value;
            return (
              <button
                key={opt.value}
                type="button"
                role="radio"
                aria-checked={active}
                onClick={() => setWaterfallRowCadence(opt.value)}
                style={cadenceButton(active)}
              >
                <span style={cadenceButtonLabel(active)}>{opt.label}</span>
                <span style={cadenceButtonDetail}>{opt.detail}</span>
              </button>
            );
          })}
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

const cardGrid: React.CSSProperties = {
  display: 'grid',
  gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))',
  gap: 10,
};

const cadenceBlock: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: 12,
  marginTop: 14,
  flexWrap: 'wrap',
};

const cadenceLabel: React.CSSProperties = {
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: '0.14em',
  textTransform: 'uppercase',
  color: 'var(--fg-1)',
};

const cadenceRow: React.CSSProperties = {
  display: 'inline-grid',
  gridTemplateColumns: 'repeat(3, minmax(84px, 1fr))',
  border: '1px solid var(--line)',
  borderRadius: 'var(--r-sm)',
  overflow: 'hidden',
  minWidth: 276,
};

function cadenceButton(active: boolean): React.CSSProperties {
  return {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 2,
    minHeight: 42,
    padding: '6px 10px',
    border: 0,
    borderRight: '1px solid var(--line)',
    background: active ? 'var(--accent)' : 'var(--bg-1)',
    color: active ? 'var(--bg-0)' : 'var(--fg-1)',
    cursor: 'pointer',
  };
}

function cadenceButtonLabel(active: boolean): React.CSSProperties {
  return {
    fontSize: 11,
    fontWeight: 800,
    letterSpacing: 0,
    color: active ? 'var(--bg-0)' : 'var(--fg-0)',
  };
}

const cadenceButtonDetail: React.CSSProperties = {
  fontSize: 10,
  letterSpacing: 0,
  color: 'inherit',
  opacity: 0.72,
};

function swatchCard(active: boolean): React.CSSProperties {
  return {
    display: 'flex',
    flexDirection: 'column',
    gap: 8,
    padding: 10,
    textAlign: 'left',
    background: 'linear-gradient(180deg, var(--bg-1), var(--bg-0))',
    border: active ? '1.5px solid var(--accent)' : '1.5px solid var(--line)',
    borderRadius: 'var(--r-md)',
    boxShadow: active ? '0 0 0 1px var(--accent)' : 'none',
    cursor: 'pointer',
    transition: 'border-color var(--dur-fast), box-shadow var(--dur-fast)',
  };
}

const gradientStrip: React.CSSProperties = {
  width: '100%',
  height: 40,
  borderRadius: 'var(--r-sm)',
  border: '1px solid rgba(0, 0, 0, 0.5)',
  boxShadow: 'inset 0 0 0 1px rgba(255, 255, 255, 0.06)',
};

function swatchLabel(active: boolean): React.CSSProperties {
  return {
    fontSize: 11,
    fontWeight: 700,
    letterSpacing: '0.06em',
    textTransform: 'uppercase',
    color: active ? 'var(--fg-0)' : 'var(--fg-1)',
  };
}
