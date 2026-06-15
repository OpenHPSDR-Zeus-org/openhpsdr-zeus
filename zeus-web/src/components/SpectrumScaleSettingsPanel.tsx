// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.

import type { CSSProperties } from 'react';
import {
  FIXED_DB_MAX,
  FIXED_DB_MIN,
  TX_FIXED_DB_MAX,
  TX_FIXED_DB_MIN,
  useDisplaySettingsStore,
} from '../state/display-settings-store';

const ABS_DB_MIN = -200;
const ABS_DB_MAX = 200;
const MIN_SPAN_DB = 20;

type RangeRowProps = {
  title: string;
  detail: string;
  minValue: number;
  maxValue: number;
  defaultMin: number;
  defaultMax: number;
  onChange: (min: number, max: number) => void;
};

function clamp(v: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, v));
}

function readNumber(raw: string): number | null {
  const n = Number(raw);
  return Number.isFinite(n) ? n : null;
}

function RangeRow({
  title,
  detail,
  minValue,
  maxValue,
  defaultMin,
  defaultMax,
  onChange,
}: RangeRowProps) {
  const setMin = (raw: string) => {
    const n = readNumber(raw);
    if (n === null) return;
    onChange(clamp(n, ABS_DB_MIN, maxValue - MIN_SPAN_DB), maxValue);
  };
  const setMax = (raw: string) => {
    const n = readNumber(raw);
    if (n === null) return;
    onChange(minValue, clamp(n, minValue + MIN_SPAN_DB, ABS_DB_MAX));
  };

  return (
    <div style={rangeRow}>
      <div style={rangeText}>
        <span style={rangeTitle}>{title}</span>
        <span style={rangeDetail}>{detail}</span>
      </div>
      <label style={rangeLabel}>
        Min
        <input
          type="number"
          min={ABS_DB_MIN}
          max={maxValue - MIN_SPAN_DB}
          step={1}
          value={Math.round(minValue)}
          onChange={(event) => setMin(event.currentTarget.value)}
          style={numberInput}
        />
      </label>
      <label style={rangeLabel}>
        Max
        <input
          type="number"
          min={minValue + MIN_SPAN_DB}
          max={ABS_DB_MAX}
          step={1}
          value={Math.round(maxValue)}
          onChange={(event) => setMax(event.currentTarget.value)}
          style={numberInput}
        />
      </label>
      <button
        type="button"
        className="btn sm"
        onClick={() => onChange(defaultMin, defaultMax)}
        title={`Reset ${title} to ${defaultMin}..${defaultMax} dB`}
        style={resetButton}
      >
        Reset
      </button>
    </div>
  );
}

export function SpectrumScaleSettingsPanel() {
  const autoRange = useDisplaySettingsStore((s) => s.autoRange);
  const dbMin = useDisplaySettingsStore((s) => s.dbMin);
  const dbMax = useDisplaySettingsStore((s) => s.dbMax);
  const txDbMin = useDisplaySettingsStore((s) => s.txDbMin);
  const txDbMax = useDisplaySettingsStore((s) => s.txDbMax);
  const wfDbMin = useDisplaySettingsStore((s) => s.wfDbMin);
  const wfDbMax = useDisplaySettingsStore((s) => s.wfDbMax);
  const wfTxDbMin = useDisplaySettingsStore((s) => s.wfTxDbMin);
  const wfTxDbMax = useDisplaySettingsStore((s) => s.wfTxDbMax);
  const setAutoRange = useDisplaySettingsStore((s) => s.setAutoRange);
  const setDbRange = useDisplaySettingsStore((s) => s.setDbRange);
  const setTxDbRange = useDisplaySettingsStore((s) => s.setTxDbRange);
  const setWfDbRange = useDisplaySettingsStore((s) => s.setWfDbRange);
  const setWfTxDbRange = useDisplaySettingsStore((s) => s.setWfTxDbRange);
  const resetDbRanges = useDisplaySettingsStore((s) => s.resetDbRanges);

  return (
    <section>
      <div style={sectionHead}>
        <h3 style={sectionH3}>Spectrum Scale</h3>
        <p style={sectionP}>
          Persisted dB windows for RX/TX panadapter and waterfall rendering.
        </p>
        <button type="button" className="btn sm" onClick={resetDbRanges} style={allResetButton}>
          Reset All
        </button>
      </div>

      <div style={card}>
        <div style={autoRow}>
          <label style={switchLabel}>
            <input
              type="checkbox"
              checked={autoRange}
              onChange={(event) => setAutoRange(event.currentTarget.checked)}
              style={{ accentColor: 'var(--accent)' }}
            />
            Auto RX Panadapter Range
          </label>
          <span style={autoHint}>
            Tracks live spectrum percentiles while enabled; any manual range edit returns to fixed mode.
          </span>
        </div>

        <RangeRow
          title="RX Panadapter"
          detail="Trace window used while receiving."
          minValue={dbMin}
          maxValue={dbMax}
          defaultMin={FIXED_DB_MIN}
          defaultMax={FIXED_DB_MAX}
          onChange={setDbRange}
        />
        <RangeRow
          title="TX Panadapter"
          detail="Trace window used while MOX or TUN is keyed."
          minValue={txDbMin}
          maxValue={txDbMax}
          defaultMin={TX_FIXED_DB_MIN}
          defaultMax={TX_FIXED_DB_MAX}
          onChange={setTxDbRange}
        />
        <RangeRow
          title="RX Waterfall"
          detail="Colour-map dB window for receive waterfall rows."
          minValue={wfDbMin}
          maxValue={wfDbMax}
          defaultMin={FIXED_DB_MIN}
          defaultMax={FIXED_DB_MAX}
          onChange={setWfDbRange}
        />
        <RangeRow
          title="TX Waterfall"
          detail="Colour-map dB window for keyed waterfall rows."
          minValue={wfTxDbMin}
          maxValue={wfTxDbMax}
          defaultMin={TX_FIXED_DB_MIN}
          defaultMax={TX_FIXED_DB_MAX}
          onChange={setWfTxDbRange}
        />
      </div>
    </section>
  );
}

const sectionHead: CSSProperties = {
  display: 'flex',
  alignItems: 'baseline',
  flexWrap: 'wrap',
  gap: 10,
  marginBottom: 10,
};

const sectionH3: CSSProperties = {
  margin: 0,
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: '0.18em',
  textTransform: 'uppercase',
  color: 'var(--fg-0)',
};

const sectionP: CSSProperties = {
  margin: 0,
  flex: '1 1 260px',
  fontSize: 12,
  lineHeight: 1.5,
  color: 'var(--fg-2)',
};

const card: CSSProperties = {
  display: 'grid',
  gap: 8,
  padding: 10,
  border: '1px solid var(--line)',
  borderRadius: 'var(--r-md)',
  background: 'linear-gradient(180deg, var(--bg-1), var(--bg-0))',
};

const autoRow: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: 10,
  flexWrap: 'wrap',
  padding: '4px 0 7px',
  borderBottom: '1px solid var(--line)',
};

const switchLabel: CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 8,
  fontSize: 11,
  fontWeight: 800,
  letterSpacing: '0.08em',
  textTransform: 'uppercase',
  color: 'var(--fg-0)',
};

const autoHint: CSSProperties = {
  flex: '1 1 260px',
  fontSize: 11,
  lineHeight: 1.35,
  color: 'var(--fg-3)',
};

const rangeRow: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  alignItems: 'center',
};

const rangeText: CSSProperties = {
  display: 'grid',
  gap: 2,
  flex: '1 1 220px',
  minWidth: 0,
};

const rangeTitle: CSSProperties = {
  fontSize: 12,
  fontWeight: 800,
  color: 'var(--fg-0)',
};

const rangeDetail: CSSProperties = {
  fontSize: 11,
  lineHeight: 1.35,
  color: 'var(--fg-3)',
};

const rangeLabel: CSSProperties = {
  display: 'grid',
  gap: 2,
  flex: '0 0 88px',
  minWidth: 0,
  fontSize: 9,
  fontWeight: 800,
  letterSpacing: '0.08em',
  textTransform: 'uppercase',
  color: 'var(--fg-3)',
};

const numberInput: CSSProperties = {
  width: '100%',
  minWidth: 0,
  height: 26,
  boxSizing: 'border-box',
  border: '1px solid var(--line)',
  borderRadius: 4,
  background: 'var(--bg-0)',
  color: 'var(--fg-0)',
  fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
  fontSize: 11,
  fontWeight: 800,
  padding: '0 6px',
};

const resetButton: CSSProperties = {
  flex: '0 0 auto',
  height: 26,
  minWidth: 58,
};

const allResetButton: CSSProperties = {
  height: 26,
};
