// SPDX-License-Identifier: GPL-2.0-or-later
//
// SpotsPanel — workspace tile listing live POTA + SOTA activation spots, with
// click-to-tune into the Zeus VFO over the native radio connection. Backed by
// spots-store, which polls GET /api/spots/activations (server-side
// ActivationSpotsService) and tunes via /api/vfo + /api/mode.
//
// Feed toggles, poll interval, and click-to-tune behaviour live in
// Settings → Spots (SpotsSettingsPanel); this panel honours the master
// enable switch and the configured CW sideband.
//
// Inspired by POTACAT (github.com/Waffleslop/POTACAT) — same public feeds,
// re-implemented as a native Zeus panel so a click drives the connected radio
// instead of Hamlib rigctld.

import { useEffect, useMemo, useState } from 'react';
import {
  useSpotsStore,
  spotMatchesFilters,
  spotModeToRxMode,
  applySpotSettingsFilters,
  freqHzToBand,
  spotAgeSeconds,
  type SpotSourceFilter,
} from '../../state/spots-store';
import type { ActivationSpotDto } from '../../api/client';

const POLL_MS = 45_000;
const SOURCES: SpotSourceFilter[] = ['ALL', 'POTA', 'SOTA'];

type SortKey = 'call' | 'freq' | 'band' | 'mode' | 'ref' | 'age';
type SortDir = 'asc' | 'desc';

const COLUMNS: ReadonlyArray<{ key: SortKey; label: string; align?: 'right' }> = [
  { key: 'call', label: 'Call' },
  { key: 'freq', label: 'Freq' },
  { key: 'band', label: 'Band' },
  { key: 'mode', label: 'Mode' },
  { key: 'ref', label: 'Ref' },
  { key: 'age', label: 'Age', align: 'right' },
];

// Comparator for ascending order. Freq and Band both order by frequency (band
// is just a coarse label over the same axis, so alpha-sorting '10m' before
// '160m' would be wrong). Age uses elapsed seconds, so ascending = newest
// first; unknown ages sink to the bottom. Ties break by frequency then call so
// the order is stable across re-sorts.
function compareSpots(a: ActivationSpotDto, b: ActivationSpotDto, key: SortKey): number {
  let d = 0;
  switch (key) {
    case 'call':
      d = a.activator.localeCompare(b.activator);
      break;
    case 'freq':
    case 'band':
      d = a.freqHz - b.freqHz;
      break;
    case 'mode':
      d = (a.mode || '').localeCompare(b.mode || '');
      break;
    case 'ref':
      d = a.reference.localeCompare(b.reference);
      break;
    case 'age':
      d = (spotAgeSeconds(a) ?? Infinity) - (spotAgeSeconds(b) ?? Infinity);
      break;
  }
  if (d !== 0) return d;
  if (a.freqHz !== b.freqHz) return a.freqHz - b.freqHz;
  return a.activator.localeCompare(b.activator);
}

function fmtFreq(hz: number): string {
  return (hz / 1_000_000).toFixed(3);
}

function fmtAge(spotTime: string): string {
  const t = Date.parse(`${spotTime}Z`); // feeds emit UTC without an offset
  if (Number.isNaN(t)) return '';
  const secs = Math.max(0, Math.round((Date.now() - t) / 1000));
  if (secs < 60) return `${secs}s`;
  const mins = Math.round(secs / 60);
  if (mins < 60) return `${mins}m`;
  return `${Math.round(mins / 60)}h`;
}

export function SpotsPanel() {
  const spots = useSpotsStore((s) => s.spots);
  const loading = useSpotsStore((s) => s.loading);
  const error = useSpotsStore((s) => s.error);
  const tuneError = useSpotsStore((s) => s.tuneError);
  const source = useSpotsStore((s) => s.source);
  const query = useSpotsStore((s) => s.query);
  const settings = useSpotsStore((s) => s.settings);
  const settingsLoaded = useSpotsStore((s) => s.settingsLoaded);
  const setSource = useSpotsStore((s) => s.setSource);
  const setQuery = useSpotsStore((s) => s.setQuery);
  const loadSpots = useSpotsStore((s) => s.loadSpots);
  const loadSettings = useSpotsStore((s) => s.loadSettings);
  const tuneToSpot = useSpotsStore((s) => s.tuneToSpot);

  // Column sort (panel-local). Default Age ascending = newest first, matching
  // the server's default ordering.
  const [sortKey, setSortKey] = useState<SortKey>('age');
  const [sortDir, setSortDir] = useState<SortDir>('asc');

  const toggleSort = (key: SortKey) => {
    if (key === sortKey) {
      setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      setSortKey(key);
      setSortDir('asc');
    }
  };

  // Pull settings once, then poll the cache on the interval. The server polls
  // upstream on its own timer; this just refreshes our view of its cache.
  useEffect(() => {
    void loadSettings();
    void loadSpots();
    const id = window.setInterval(() => void loadSpots(), POLL_MS);
    return () => window.clearInterval(id);
  }, [loadSettings, loadSpots]);

  // Two stages: the persisted operator settings (band / mode / QRT / age /
  // dedup) gate the list globally, then the panel-local source chips + search
  // box narrow the view.
  const settingsFiltered = useMemo(
    () => applySpotSettingsFilters(spots, settings),
    [spots, settings],
  );
  const filtered = useMemo(
    () => settingsFiltered.filter((s) => spotMatchesFilters(s, source, query)),
    [settingsFiltered, source, query],
  );
  const visible = useMemo(() => {
    const sorted = [...filtered].sort((a, b) => compareSpots(a, b, sortKey));
    if (sortDir === 'desc') sorted.reverse();
    return sorted;
  }, [filtered, sortKey, sortDir]);
  const hiddenByFilters = spots.length - settingsFiltered.length;

  if (settingsLoaded && !settings.enabled) {
    return (
      <div
        style={{
          flex: 1,
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          justifyContent: 'center',
          gap: 10,
          padding: 24,
          textAlign: 'center',
        }}
      >
        <div style={{ fontSize: 12, fontWeight: 700, letterSpacing: '0.12em', textTransform: 'uppercase', color: 'var(--fg-1)' }}>
          Spots Disabled
        </div>
        <div style={{ fontSize: 12, color: 'var(--fg-2)', maxWidth: 320, lineHeight: 1.5 }}>
          The POTA / SOTA feed is turned off. Enable it in Settings → Spots.
        </div>
      </div>
    );
  }

  return (
    <div style={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <div
        style={{
          padding: '4px 8px',
          borderBottom: '1px solid var(--panel-border)',
          display: 'flex',
          gap: 6,
          alignItems: 'center',
          flexWrap: 'wrap',
        }}
      >
        {SOURCES.map((s) => (
          <button
            key={s}
            type="button"
            className={`btn sm${source === s ? ' active' : ''}`}
            onClick={() => setSource(s)}
          >
            {s}
          </button>
        ))}
        <input
          className="cs-input mono"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Filter call / park / mode…"
          style={{ flex: 1, minWidth: 90 }}
        />
        <button
          type="button"
          className="btn sm"
          disabled={loading}
          onClick={() => void loadSpots()}
          title="Refresh now"
        >
          {loading ? '…' : '⟳'}
        </button>
      </div>

      {tuneError && (
        <div style={{ padding: '4px 8px', fontSize: 11, color: 'var(--tx)', borderBottom: '1px solid var(--panel-border)' }}>
          {tuneError}
        </div>
      )}

      <div style={{ flex: 1, overflow: 'auto', minHeight: 0 }}>
        {error ? (
          <div style={{ padding: 12, fontSize: 12, color: 'var(--tx)' }}>{error}</div>
        ) : visible.length === 0 ? (
          <div style={{ padding: 12, fontSize: 12, color: 'var(--fg-2)' }}>
            {spots.length === 0
              ? 'No spots yet — waiting for the feed…'
              : hiddenByFilters > 0 && settingsFiltered.length === 0
                ? 'All spots hidden by your filters (Settings → Spots).'
                : 'No spots match the filter.'}
          </div>
        ) : (
          <table className="mono" style={{ width: '100%', borderCollapse: 'collapse', fontSize: 12 }}>
            <thead>
              <tr style={{ color: 'var(--fg-2)', textAlign: 'left' }}>
                {COLUMNS.map((col) => {
                  const active = sortKey === col.key;
                  return (
                    <th
                      key={col.key}
                      onClick={() => toggleSort(col.key)}
                      title={`Sort by ${col.label}`}
                      aria-sort={active ? (sortDir === 'asc' ? 'ascending' : 'descending') : 'none'}
                      style={{
                        ...thStyle,
                        textAlign: col.align ?? 'left',
                        cursor: 'pointer',
                        userSelect: 'none',
                        color: active ? 'var(--accent)' : undefined,
                      }}
                    >
                      {col.label}
                      <span style={{ opacity: active ? 1 : 0.25, marginLeft: 3 }}>
                        {active ? (sortDir === 'asc' ? '▲' : '▼') : '↕'}
                      </span>
                    </th>
                  );
                })}
              </tr>
            </thead>
            <tbody>
              {visible.map((spot) => (
                <SpotRow
                  key={rowKey(spot)}
                  spot={spot}
                  cwSideband={settings.cwSideband}
                  onTune={() => void tuneToSpot(spot)}
                />
              ))}
            </tbody>
          </table>
        )}
      </div>

      <div
        style={{
          padding: '3px 8px',
          borderTop: '1px solid var(--panel-border)',
          fontSize: 10,
          color: 'var(--fg-3)',
          display: 'flex',
          justifyContent: 'space-between',
          gap: 8,
        }}
      >
        <span>
          {visible.length} shown
          {hiddenByFilters > 0 ? ` · ${hiddenByFilters} hidden by filters` : ''}
        </span>
        <span>{spots.length} total</span>
      </div>
    </div>
  );
}

function SpotRow({
  spot,
  cwSideband,
  onTune,
}: {
  spot: ActivationSpotDto;
  cwSideband: 'CWU' | 'CWL';
  onTune: () => void;
}) {
  const rx = spotModeToRxMode(spot.mode, spot.freqHz, cwSideband);
  const band = freqHzToBand(spot.freqHz);
  const title = [spot.name, spot.location, spot.comments].filter(Boolean).join(' · ');
  return (
    <tr
      onClick={onTune}
      title={`Tune ${fmtFreq(spot.freqHz)} MHz ${rx}${title ? ` — ${title}` : ''}`}
      style={{ cursor: 'pointer', borderBottom: '1px solid var(--panel-border)' }}
      onMouseEnter={(e) => (e.currentTarget.style.background = 'var(--accent-soft)')}
      onMouseLeave={(e) => (e.currentTarget.style.background = '')}
    >
      <td style={{ ...tdStyle, fontWeight: 700 }}>
        <span style={{ color: spot.source === 'POTA' ? 'var(--accent)' : 'var(--power)' }}>●</span>{' '}
        {spot.activator}
      </td>
      <td style={tdStyle}>{fmtFreq(spot.freqHz)}</td>
      <td style={{ ...tdStyle, color: 'var(--fg-2)' }}>{band ?? '—'}</td>
      <td style={tdStyle}>{spot.mode || rx}</td>
      <td style={{ ...tdStyle, color: 'var(--fg-2)' }}>{spot.reference}</td>
      <td style={{ ...tdStyle, textAlign: 'right', color: 'var(--fg-2)' }}>{fmtAge(spot.spotTime)}</td>
    </tr>
  );
}

const thStyle: React.CSSProperties = {
  padding: '3px 8px',
  position: 'sticky',
  top: 0,
  background: 'var(--panel-top)',
  fontWeight: 600,
};

const tdStyle: React.CSSProperties = {
  padding: '3px 8px',
  whiteSpace: 'nowrap',
};

function rowKey(spot: ActivationSpotDto): string {
  return `${spot.source}|${spot.activator}|${spot.freqHz}|${spot.spotTime}`;
}
