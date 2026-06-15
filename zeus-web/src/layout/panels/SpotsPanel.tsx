// SPDX-License-Identifier: GPL-2.0-or-later
//
// SpotsPanel — workspace tile listing live POTA + SOTA + DX activation spots,
// with click-to-tune into the Zeus VFO over the native radio connection. Backed
// by spots-store, which polls GET /api/spots/activations (server-side
// ActivationSpotsService) and tunes via /api/vfo + /api/mode.
//
// Beyond the table, the panel integrates the rest of Zeus rather than
// duplicating it: QRZ operator names (qrz-store, lazily + quota-friendly),
// worked-before badges and one-click "log this spot" (logger-store → ADIF),
// a watchlist with desktop/sound alerts, and a scan mode that steps the dial
// through the filtered list. The world map / propagation view deliberately
// lives in the HamClock panel — this panel is the actionable, radio-coupled
// surface.
//
// Feed toggles, poll interval, watchlist and alert prefs live in
// Settings → Spots (SpotsSettingsPanel); this panel honours the master enable
// switch and the configured CW sideband.
//
// Inspired by POTACAT (github.com/Waffleslop/POTACAT) — same public feeds,
// re-implemented as a native Zeus panel so a click drives the connected radio
// instead of Hamlib rigctld.

import { useEffect, useMemo, useRef, useState } from 'react';
import {
  useSpotsStore,
  spotMatchesFilters,
  spotModeToRxMode,
  spotModeGroup,
  applySpotSettingsFilters,
  isWatchedCall,
  spotIsWorked,
  freqHzToBand,
  spotAgeSeconds,
  type SpotSourceFilter,
} from '../../state/spots-store';
import { useQrzStore } from '../../state/qrz-store';
import { useLoggerStore } from '../../state/logger-store';
import type { ActivationSpotDto, RxMode } from '../../api/client';
import type { CreateLogEntryRequest } from '../../api/log';

const POLL_MS = 45_000;
const SOURCES: SpotSourceFilter[] = ['ALL', 'POTA', 'SOTA', 'DX'];

// Per-source marker colour. POTA = accent blue, SOTA = output yellow, DX = TX
// red — all existing palette tokens, no new colours introduced.
function sourceColor(source: ActivationSpotDto['source']): string {
  switch (source) {
    case 'POTA':
      return 'var(--accent)';
    case 'SOTA':
      return 'var(--power)';
    default:
      return 'var(--tx)';
  }
}

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
  const saveSettings = useSpotsStore((s) => s.saveSettings);
  const tuneToSpot = useSpotsStore((s) => s.tuneToSpot);

  // Logbook is the source of truth for worked-before badges + the hide-worked
  // filter. Entries load on module init; refresh on mount so a freshly-opened
  // panel reflects recent QSOs.
  const logEntries = useLoggerStore((s) => s.entries);
  const loadLogEntries = useLoggerStore((s) => s.loadEntries);
  const workedCalls = useMemo(
    () => new Set(logEntries.map((e) => e.callsign.trim().toUpperCase())),
    [logEntries],
  );

  // Column sort (panel-local). Default Age ascending = newest first, matching
  // the server's default ordering.
  const [sortKey, setSortKey] = useState<SortKey>('age');
  const [sortDir, setSortDir] = useState<SortDir>('asc');

  // Scan mode (panel-local): step the dial through the visible list, dwelling
  // settings.scanDwellSeconds on each. scanIdx advances on a timer.
  const [scanOn, setScanOn] = useState(false);
  const [scanIdx, setScanIdx] = useState(0);

  // The spot to log, if the log dialog is open.
  const [logSpot, setLogSpot] = useState<ActivationSpotDto | null>(null);

  const toggleSort = (key: SortKey) => {
    if (key === sortKey) {
      setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      setSortKey(key);
      setSortDir('asc');
    }
  };

  // Pull settings + log once, then poll the spot cache on the interval. The
  // server polls upstream on its own timer; this just refreshes our view.
  useEffect(() => {
    void loadSettings();
    void loadSpots();
    void loadLogEntries();
    const id = window.setInterval(() => void loadSpots(), POLL_MS);
    return () => window.clearInterval(id);
  }, [loadSettings, loadSpots, loadLogEntries]);

  // Two stages: the persisted operator settings (band / mode / QRT / worked /
  // age / dedup) gate the list globally, then the panel-local source chips +
  // search box narrow the view.
  const settingsFiltered = useMemo(
    () => applySpotSettingsFilters(spots, settings, Date.now(), workedCalls),
    [spots, settings, workedCalls],
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

  // Scan stepper. Read the latest visible list via a ref so the timer doesn't
  // restart (and re-tune) on every 45 s poll — it advances only on its own
  // tick or when the operator toggles scan.
  const visibleRef = useRef(visible);
  visibleRef.current = visible;
  const dwellMs = Math.max(2, settings.scanDwellSeconds) * 1000;
  useEffect(() => {
    if (!scanOn) return;
    const list = visibleRef.current;
    if (list.length === 0) return;
    const spot = list[scanIdx % list.length];
    if (!spot) return;
    void tuneToSpot(spot);
    const id = window.setTimeout(() => setScanIdx((i) => i + 1), dwellMs);
    return () => window.clearTimeout(id);
  }, [scanOn, scanIdx, dwellMs, tuneToSpot]);

  const scanSpot = scanOn && visible.length > 0 ? visible[scanIdx % visible.length] : undefined;
  const scanKey = scanSpot ? rowKey(scanSpot) : null;

  const startScan = () => {
    setScanIdx(0);
    setScanOn(true);
  };
  const stopScan = () => setScanOn(false);

  const toggleWatch = (call: string) => {
    const c = call.trim().toUpperCase();
    if (!c) return;
    const next = settings.watchlist.includes(c)
      ? settings.watchlist.filter((x) => x !== c)
      : [...settings.watchlist, c];
    void saveSettings({ ...settings, watchlist: next });
  };

  // Manual tune (row click) stops the scan so the operator stays where they
  // landed instead of being yanked onward at the next dwell.
  const manualTune = (spot: ActivationSpotDto) => {
    if (scanOn) setScanOn(false);
    void tuneToSpot(spot);
  };

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
    <div style={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden', position: 'relative' }}>
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
          className={`btn sm${scanOn ? ' active' : ''}`}
          disabled={visible.length === 0}
          onClick={() => (scanOn ? stopScan() : startScan())}
          title={
            scanOn
              ? 'Stop scanning'
              : `Scan the visible list, dwelling ${settings.scanDwellSeconds}s per spot`
          }
        >
          {scanOn ? '■ Scan' : '▶ Scan'}
        </button>
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
                <th style={{ ...thStyle, textAlign: 'right' }} aria-label="actions" />
              </tr>
            </thead>
            <tbody>
              {visible.map((spot) => (
                <SpotRow
                  key={rowKey(spot)}
                  spot={spot}
                  cwSideband={settings.cwSideband}
                  enrichQrz={settings.enrichQrz}
                  watched={isWatchedCall(spot.activator, settings.watchlist)}
                  worked={spotIsWorked(spot, workedCalls)}
                  scanning={scanKey === rowKey(spot)}
                  onTune={() => manualTune(spot)}
                  onToggleWatch={() => toggleWatch(spot.activator)}
                  onLog={() => setLogSpot(spot)}
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
          {settings.watchlist.length > 0 ? ` · ${settings.watchlist.length} watched` : ''}
        </span>
        <span>{spots.length} total</span>
      </div>

      {logSpot && (
        <SpotLogDialog
          spot={logSpot}
          cwSideband={settings.cwSideband}
          onClose={() => setLogSpot(null)}
        />
      )}
    </div>
  );
}

function SpotRow({
  spot,
  cwSideband,
  enrichQrz,
  watched,
  worked,
  scanning,
  onTune,
  onToggleWatch,
  onLog,
}: {
  spot: ActivationSpotDto;
  cwSideband: 'CWU' | 'CWL';
  enrichQrz: boolean;
  watched: boolean;
  worked: boolean;
  scanning: boolean;
  onTune: () => void;
  onToggleWatch: () => void;
  onLog: () => void;
}) {
  const rx = spotModeToRxMode(spot.mode, spot.freqHz, cwSideband);
  const band = freqHzToBand(spot.freqHz);
  const [hovered, setHovered] = useState(false);

  // Lazy QRZ enrichment: when enabled, resolve the operator name (cached +
  // deduped in qrz-store, and a no-op when QRZ isn't connected).
  const lookupCached = useQrzStore((s) => s.lookupCached);
  const cached = useQrzStore((s) => s.nameCache[spot.activator.toUpperCase()]);
  useEffect(() => {
    if (enrichQrz) void lookupCached(spot.activator);
  }, [enrichQrz, spot.activator, lookupCached]);
  const opName = cached ? cached.firstName || cached.name : null;

  const title = [opName, spot.name, spot.location, spot.comments].filter(Boolean).join(' · ');
  const bg = scanning ? 'var(--accent-soft)' : hovered ? 'var(--accent-soft)' : '';
  const actionsVisible = hovered || watched;

  return (
    <tr
      onClick={onTune}
      title={`Tune ${fmtFreq(spot.freqHz)} MHz ${rx}${title ? ` — ${title}` : ''}`}
      style={{
        cursor: 'pointer',
        borderBottom: '1px solid var(--panel-border)',
        background: bg,
        borderLeft: scanning ? '2px solid var(--accent)' : '2px solid transparent',
      }}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
    >
      <td style={{ ...tdStyle, fontWeight: 700 }}>
        <button
          type="button"
          onClick={(e) => {
            e.stopPropagation();
            onToggleWatch();
          }}
          title={watched ? 'Remove from watchlist' : 'Add to watchlist'}
          style={{
            background: 'none',
            border: 'none',
            cursor: 'pointer',
            padding: 0,
            marginRight: 4,
            fontSize: 12,
            lineHeight: 1,
            color: watched ? 'var(--power)' : 'var(--fg-3)',
            opacity: watched ? 1 : actionsVisible ? 0.7 : 0,
          }}
        >
          {watched ? '★' : '☆'}
        </button>
        <span style={{ color: sourceColor(spot.source) }}>●</span>{' '}
        {spot.activator}
        {worked && (
          <span
            title="Already in your logbook"
            style={{ marginLeft: 4, fontSize: 10, color: 'var(--fg-3)' }}
          >
            ✓
          </span>
        )}
        {opName && (
          <span style={{ marginLeft: 6, fontWeight: 400, color: 'var(--fg-3)', fontSize: 11 }}>
            {opName}
          </span>
        )}
      </td>
      <td style={tdStyle}>{fmtFreq(spot.freqHz)}</td>
      <td style={{ ...tdStyle, color: 'var(--fg-2)' }}>{band ?? '—'}</td>
      <td style={tdStyle}>{spot.mode || rx}</td>
      <td style={{ ...tdStyle, color: 'var(--fg-2)' }}>{spot.reference}</td>
      <td style={{ ...tdStyle, textAlign: 'right', color: 'var(--fg-2)' }}>{fmtAge(spot.spotTime)}</td>
      <td style={{ ...tdStyle, textAlign: 'right', padding: '0 6px' }}>
        <button
          type="button"
          className="btn sm"
          onClick={(e) => {
            e.stopPropagation();
            onLog();
          }}
          title="Log this QSO"
          style={{ opacity: actionsVisible ? 1 : 0, fontSize: 10, padding: '1px 5px' }}
        >
          LOG
        </button>
      </td>
    </tr>
  );
}

// --- Log dialog -------------------------------------------------------------

function adifMode(rawMode: string, rx: RxMode): string {
  const m = (rawMode || '').toUpperCase().trim();
  if (m && !['USB', 'LSB', 'SSB', 'PHONE', 'VOICE'].includes(m)) return m; // CW, FT8, RTTY, …
  if (m === 'SSB' || m === 'PHONE' || m === 'VOICE' || m === 'USB' || m === 'LSB') return 'SSB';
  // Fall back from the resolved RxMode when the feed gave no mode string.
  if (rx === 'CWU' || rx === 'CWL') return 'CW';
  if (rx === 'DIGU' || rx === 'DIGL') return 'DATA';
  if (rx === 'AM') return 'AM';
  if (rx === 'FM') return 'FM';
  return 'SSB';
}

function defaultRst(mode: string): string {
  const g = spotModeGroup(mode);
  return g === 'CW' || g === 'DIGITAL' ? '599' : '59';
}

function SpotLogDialog({
  spot,
  cwSideband,
  onClose,
}: {
  spot: ActivationSpotDto;
  cwSideband: 'CWU' | 'CWL';
  onClose: () => void;
}) {
  const rx = spotModeToRxMode(spot.mode, spot.freqHz, cwSideband);
  const addLogEntry = useLoggerStore((s) => s.addLogEntry);
  const lookupCached = useQrzStore((s) => s.lookupCached);
  const qrzConnected = useQrzStore((s) => s.connected);

  const [callsign, setCallsign] = useState(spot.activator);
  const [freqMhz, setFreqMhz] = useState(fmtFreq(spot.freqHz));
  const [mode, setMode] = useState(adifMode(spot.mode, rx));
  const rstSeed = defaultRst(spot.mode);
  const [rstSent, setRstSent] = useState(rstSeed);
  const [rstRcvd, setRstRcvd] = useState(rstSeed);
  const [name, setName] = useState('');
  const [comment, setComment] = useState(
    [spot.source, spot.reference, spot.name].filter(Boolean).join(' '),
  );
  const [station, setStation] = useState<{
    grid: string | null;
    country: string | null;
    state: string | null;
    dxcc: number | null;
    cqZone: number | null;
    ituZone: number | null;
  } | null>(null);
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  // Enrich from QRZ on open (name/grid/country/DXCC) — best effort.
  useEffect(() => {
    if (!qrzConnected) return;
    let cancelled = false;
    void lookupCached(spot.activator).then((s) => {
      if (cancelled || !s) return;
      setName((n) => n || s.firstName || s.name || '');
      setStation({
        grid: s.grid,
        country: s.country,
        state: s.state,
        dxcc: s.dxcc,
        cqZone: s.cqZone,
        ituZone: s.ituZone,
      });
    });
    return () => {
      cancelled = true;
    };
  }, [qrzConnected, spot.activator, lookupCached]);

  const submit = async () => {
    const freq = Number(freqMhz);
    if (!callsign.trim() || !Number.isFinite(freq) || freq <= 0) {
      setErr('Callsign and a valid frequency are required.');
      return;
    }
    setSaving(true);
    setErr(null);
    const req: CreateLogEntryRequest = {
      callsign: callsign.trim().toUpperCase(),
      name: name.trim() || null,
      frequencyMhz: freq,
      band: freqHzToBand(Math.round(freq * 1_000_000)) ?? '',
      mode: mode.trim().toUpperCase() || 'SSB',
      rstSent: rstSent.trim() || rstSeed,
      rstRcvd: rstRcvd.trim() || rstSeed,
      grid: station?.grid ?? null,
      country: station?.country ?? null,
      dxcc: station?.dxcc ?? null,
      cqZone: station?.cqZone ?? null,
      ituZone: station?.ituZone ?? null,
      state: station?.state ?? null,
      comment: comment.trim() || null,
    };
    const entry = await addLogEntry(req);
    setSaving(false);
    if (entry) onClose();
    else setErr('Failed to save the log entry.');
  };

  return (
    <div
      onClick={onClose}
      style={{
        position: 'absolute',
        inset: 0,
        background: 'rgba(0,0,0,0.5)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        zIndex: 20,
        padding: 16,
      }}
    >
      <div
        onClick={(e) => e.stopPropagation()}
        style={{
          background: 'var(--panel-top)',
          border: '1px solid var(--panel-border)',
          borderRadius: 'var(--r-md, 6px)',
          padding: 16,
          width: 340,
          maxWidth: '100%',
          boxShadow: '0 8px 32px rgba(0,0,0,0.5)',
          display: 'flex',
          flexDirection: 'column',
          gap: 10,
        }}
      >
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
          <span style={{ fontSize: 11, fontWeight: 700, letterSpacing: '0.1em', textTransform: 'uppercase', color: 'var(--fg-1)' }}>
            Log QSO
          </span>
          <button type="button" className="btn sm" onClick={onClose} title="Close">
            ✕
          </button>
        </div>

        <DialogField label="Callsign">
          <input className="cs-input mono" value={callsign} onChange={(e) => setCallsign(e.target.value)} style={dlgInput} />
        </DialogField>
        <div style={{ display: 'flex', gap: 8 }}>
          <DialogField label="Freq (MHz)">
            <input className="cs-input mono" value={freqMhz} onChange={(e) => setFreqMhz(e.target.value)} style={dlgInput} />
          </DialogField>
          <DialogField label="Mode">
            <input className="cs-input mono" value={mode} onChange={(e) => setMode(e.target.value)} style={dlgInput} />
          </DialogField>
        </div>
        <div style={{ display: 'flex', gap: 8 }}>
          <DialogField label="RST sent">
            <input className="cs-input mono" value={rstSent} onChange={(e) => setRstSent(e.target.value)} style={dlgInput} />
          </DialogField>
          <DialogField label="RST rcvd">
            <input className="cs-input mono" value={rstRcvd} onChange={(e) => setRstRcvd(e.target.value)} style={dlgInput} />
          </DialogField>
        </div>
        <DialogField label="Name">
          <input className="cs-input" value={name} onChange={(e) => setName(e.target.value)} style={dlgInput} />
        </DialogField>
        <DialogField label="Comment">
          <input className="cs-input" value={comment} onChange={(e) => setComment(e.target.value)} style={dlgInput} />
        </DialogField>

        {err && <div style={{ fontSize: 11, color: 'var(--tx)' }}>{err}</div>}

        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, marginTop: 4 }}>
          <button type="button" className="btn sm" onClick={onClose} disabled={saving}>
            Cancel
          </button>
          <button type="button" className="btn sm active" onClick={() => void submit()} disabled={saving}>
            {saving ? 'Saving…' : 'Log it'}
          </button>
        </div>
      </div>
    </div>
  );
}

function DialogField({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label style={{ display: 'flex', flexDirection: 'column', gap: 3, flex: 1 }}>
      <span style={{ fontSize: 10, color: 'var(--fg-3)', letterSpacing: '0.04em' }}>{label}</span>
      {children}
    </label>
  );
}

const dlgInput: React.CSSProperties = {
  width: '100%',
  padding: '5px 7px',
  fontSize: 12,
  background: 'var(--bg-0)',
  border: '1px solid var(--panel-border)',
  borderRadius: 'var(--r-sm)',
  color: 'var(--fg-0)',
};

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
