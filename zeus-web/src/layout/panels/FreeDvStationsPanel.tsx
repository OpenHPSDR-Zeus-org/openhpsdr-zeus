// SPDX-License-Identifier: GPL-2.0-or-later
//
// FreeDvStationsPanel — workspace tile listing live FreeDV stations from the
// FreeDV Reporter network (qso.freedv.org), with click-to-tune into the Zeus
// VFO. Backed by freedv-stations-store, which polls GET /api/freedv/stations
// and tunes via setVfo + setMode + setFreeDvConfig.

import { useEffect, useMemo, useState } from 'react';
import {
  useFreeDvStationsStore,
  stationMatchesQuery,
  freqHzToBand,
  type FreeDvReporterSettings,
} from '../../state/freedv-stations-store';
import { useQrzStore } from '../../state/qrz-store';
import type { FreeDvStationDto } from '../../api/client';

const POLL_MS = 5_000;

type SortKey = 'call' | 'freq' | 'band' | 'mode' | 'grid' | 'snr' | 'status' | 'age';
type SortDir = 'asc' | 'desc';

const COLUMNS: ReadonlyArray<{ key: SortKey; label: string; align?: 'right' }> = [
  { key: 'call', label: 'Call' },
  { key: 'freq', label: 'Freq' },
  { key: 'band', label: 'Band' },
  { key: 'mode', label: 'Mode' },
  { key: 'grid', label: 'Grid' },
  { key: 'snr', label: 'SNR', align: 'right' },
  { key: 'status', label: 'Status' },
  { key: 'age', label: 'Age', align: 'right' },
];

function fmtFreq(hz: number): string {
  return (hz / 1_000_000).toFixed(3);
}

function stationAgeSeconds(lastUpdate: string, nowMs: number = Date.now()): number | null {
  const t = Date.parse(lastUpdate);
  if (Number.isNaN(t)) return null;
  return Math.max(0, Math.round((nowMs - t) / 1000));
}

function fmtAge(lastUpdate: string): string {
  const secs = stationAgeSeconds(lastUpdate);
  if (secs === null) return '';
  if (secs < 60) return `${secs}s`;
  const mins = Math.round(secs / 60);
  if (mins < 60) return `${mins}m`;
  return `${Math.round(mins / 60)}h`;
}

function compareStations(a: FreeDvStationDto, b: FreeDvStationDto, key: SortKey): number {
  let d = 0;
  switch (key) {
    case 'call':
      d = a.callsign.localeCompare(b.callsign);
      break;
    case 'freq':
    case 'band':
      d = a.freqHz - b.freqHz;
      break;
    case 'mode':
      d = (a.mode || '').localeCompare(b.mode || '');
      break;
    case 'grid':
      d = (a.gridSquare ?? '').localeCompare(b.gridSquare ?? '');
      break;
    case 'snr':
      d = (a.lastRxSnr ?? -Infinity) - (b.lastRxSnr ?? -Infinity);
      break;
    case 'status': {
      // TX first, then RX-only, then idle
      const rank = (st: FreeDvStationDto) => (st.transmitting ? 0 : st.rxOnly ? 1 : 2);
      d = rank(a) - rank(b);
      break;
    }
    case 'age':
      d = (stationAgeSeconds(a.lastUpdate) ?? Infinity) - (stationAgeSeconds(b.lastUpdate) ?? Infinity);
      break;
  }
  if (d !== 0) return d;
  if (a.freqHz !== b.freqHz) return a.freqHz - b.freqHz;
  return a.callsign.localeCompare(b.callsign);
}

function connectionStateColor(state: string): string {
  return state === 'Connected' ? 'var(--accent)' : 'var(--fg-3)';
}

export function FreeDvStationsPanel() {
  const stations = useFreeDvStationsStore((s) => s.stations);
  const connectionState = useFreeDvStationsStore((s) => s.connectionState);
  const loading = useFreeDvStationsStore((s) => s.loading);
  const error = useFreeDvStationsStore((s) => s.error);
  const tuneError = useFreeDvStationsStore((s) => s.tuneError);
  const query = useFreeDvStationsStore((s) => s.query);
  const setQuery = useFreeDvStationsStore((s) => s.setQuery);
  const loadStations = useFreeDvStationsStore((s) => s.loadStations);
  const tuneToStation = useFreeDvStationsStore((s) => s.tuneToStation);

  const reporting = useFreeDvStationsStore((s) => s.reporting);
  const mySid = useFreeDvStationsStore((s) => s.mySid);
  const reporterSettings = useFreeDvStationsStore((s) => s.reporterSettings);
  const reporterError = useFreeDvStationsStore((s) => s.reporterError);
  const reporterSaving = useFreeDvStationsStore((s) => s.reporterSaving);
  const loadReporterSettings = useFreeDvStationsStore((s) => s.loadReporterSettings);
  const saveReporterSettings = useFreeDvStationsStore((s) => s.saveReporterSettings);
  const requestQsy = useFreeDvStationsStore((s) => s.requestQsy);

  const qrzHome = useQrzStore((s) => s.home);

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

  // Poll on mount + every POLL_MS. AbortController so in-flight fetches are
  // cancelled if the panel unmounts mid-request.
  useEffect(() => {
    const ac = new AbortController();
    void loadStations();
    void loadReporterSettings();
    const id = window.setInterval(() => void loadStations(), POLL_MS);
    return () => {
      window.clearInterval(id);
      ac.abort();
    };
  }, [loadStations, loadReporterSettings]);

  const filtered = useMemo(
    () => stations.filter((st) => stationMatchesQuery(st, query)),
    [stations, query],
  );

  const visible = useMemo(() => {
    const sorted = [...filtered].sort((a, b) => compareStations(a, b, sortKey));
    if (sortDir === 'desc') sorted.reverse();
    return sorted;
  }, [filtered, sortKey, sortDir]);

  const emptyMessage =
    connectionState !== 'Connected'
      ? 'Connecting to FreeDV Reporter…'
      : 'No active FreeDV stations.';

  return (
    <div style={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden', position: 'relative' }}>
      {/* Header toolbar */}
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
        <span
          style={{
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: '0.08em',
            textTransform: 'uppercase',
            color: 'var(--fg-1)',
            whiteSpace: 'nowrap',
          }}
        >
          FreeDV Stations
        </span>

        {/* Connection-state lamp */}
        <span
          title={`Reporter: ${connectionState}`}
          style={{
            width: 7,
            height: 7,
            borderRadius: '50%',
            background: connectionStateColor(connectionState),
            flexShrink: 0,
            display: 'inline-block',
          }}
        />
        <span style={{ fontSize: 10, color: connectionStateColor(connectionState), whiteSpace: 'nowrap' }}>
          {connectionState}
        </span>

        <span style={{ fontSize: 10, color: 'var(--fg-3)', whiteSpace: 'nowrap' }}>
          {stations.length} station{stations.length !== 1 ? 's' : ''}
        </span>

        <input
          className="cs-input mono"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Filter call / grid / mode / band…"
          style={{ flex: 1, minWidth: 90 }}
        />

        <button
          type="button"
          className="btn sm"
          disabled={loading}
          onClick={() => void loadStations()}
          title="Refresh now"
        >
          {loading ? '…' : '⟳'}
        </button>
      </div>

      {/* Report-me section — opt-in onto the public FreeDV Reporter map. */}
      <ReportSection
        reporting={reporting}
        settings={reporterSettings}
        error={reporterError}
        saving={reporterSaving}
        qrzCall={qrzHome?.callsign ?? ''}
        qrzGrid={qrzHome?.grid ?? ''}
        onSave={saveReporterSettings}
      />

      {/* Tune error banner */}
      {tuneError && (
        <div
          style={{
            padding: '4px 8px',
            fontSize: 11,
            color: 'var(--tx)',
            borderBottom: '1px solid var(--panel-border)',
          }}
        >
          {tuneError}
        </div>
      )}

      {/* Table / empty state */}
      <div style={{ flex: 1, overflow: 'auto', minHeight: 0 }}>
        {error ? (
          <div style={{ padding: 12, fontSize: 12, color: 'var(--tx)' }}>{error}</div>
        ) : visible.length === 0 ? (
          <div style={{ padding: 12, fontSize: 12, color: 'var(--fg-2)' }}>{emptyMessage}</div>
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
              {visible.map((st) => (
                <StationRow
                  key={st.sid || `${st.callsign}|${st.freqHz}`}
                  station={st}
                  isSelf={!!mySid && st.sid === mySid}
                  canQsy={reporting && !!st.sid && st.sid !== mySid}
                  onTune={() => void tuneToStation(st)}
                  onQsy={() => void requestQsy(st.sid)}
                />
              ))}
            </tbody>
          </table>
        )}
      </div>

      {/* Footer */}
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
        <span>{visible.length} shown</span>
        <span>{stations.length} total</span>
      </div>
    </div>
  );
}

function StationRow({
  station,
  isSelf,
  canQsy,
  onTune,
  onQsy,
}: {
  station: FreeDvStationDto;
  isSelf: boolean;
  canQsy: boolean;
  onTune: () => void;
  onQsy: () => void;
}) {
  const band = freqHzToBand(station.freqHz);
  const [hovered, setHovered] = useState(false);

  const statusBadge = (() => {
    if (station.transmitting) {
      return (
        <span
          style={{
            fontSize: 10,
            fontWeight: 700,
            letterSpacing: '0.04em',
            color: 'var(--tx)',
            padding: '1px 4px',
            border: '1px solid var(--tx)',
            borderRadius: 'var(--r-sm, 3px)',
          }}
        >
          TX
        </span>
      );
    }
    if (station.rxOnly) {
      return <span style={{ fontSize: 10, color: 'var(--fg-3)' }}>RX-only</span>;
    }
    return null;
  })();

  const heardLine =
    station.lastRxCallsign
      ? `hears ${station.lastRxCallsign}${station.lastRxSnr !== null ? ` ${station.lastRxSnr}dB` : ''}`
      : null;

  const title = [
    station.message,
    station.version ? `v${station.version}` : null,
    heardLine,
  ]
    .filter(Boolean)
    .join(' · ');

  return (
    <tr
      onClick={onTune}
      title={`Tune ${fmtFreq(station.freqHz)} MHz FreeDV ${station.mode}${title ? ` — ${title}` : ''}${
        isSelf ? ' — this is you' : ''
      }`}
      style={{
        cursor: 'pointer',
        borderBottom: '1px solid var(--panel-border)',
        background: isSelf ? 'var(--accent-soft)' : hovered ? 'var(--accent-soft)' : '',
        // Subtle left accent marks the operator's own row on the map.
        boxShadow: isSelf ? 'inset 3px 0 0 var(--accent)' : undefined,
      }}
      onMouseEnter={() => setHovered(true)}
      onMouseLeave={() => setHovered(false)}
    >
      {/* Call */}
      <td style={{ ...tdStyle, fontWeight: 700 }}>{station.callsign}</td>
      {/* Freq */}
      <td style={tdStyle}>{fmtFreq(station.freqHz)}</td>
      {/* Band */}
      <td style={{ ...tdStyle, color: 'var(--fg-2)' }}>{band ?? '—'}</td>
      {/* Mode */}
      <td style={tdStyle}>{station.mode || '—'}</td>
      {/* Grid */}
      <td style={{ ...tdStyle, color: 'var(--fg-2)' }}>{station.gridSquare ?? '—'}</td>
      {/* SNR */}
      <td style={{ ...tdStyle, textAlign: 'right', color: 'var(--fg-2)' }}>
        {station.lastRxSnr !== null ? `${station.lastRxSnr}` : '—'}
      </td>
      {/* Status */}
      <td style={tdStyle}>
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
          {statusBadge}
          {heardLine && !station.transmitting && (
            <span style={{ fontSize: 10, color: 'var(--fg-3)' }}>{heardLine}</span>
          )}
          {canQsy && (
            <button
              type="button"
              className="btn sm"
              title="Ask this station to QSY to my current frequency"
              onClick={(e) => {
                e.stopPropagation();
                onQsy();
              }}
              style={{ fontSize: 10, padding: '0 5px', lineHeight: '16px' }}
            >
              QSY→me
            </button>
          )}
        </span>
      </td>
      {/* Age */}
      <td style={{ ...tdStyle, textAlign: 'right', color: 'var(--fg-2)' }}>
        {fmtAge(station.lastUpdate)}
      </td>
    </tr>
  );
}

// Opt-in "Report me" controls. Default OFF; when enabled, Zeus connects to the
// FreeDV Reporter in "report" role and broadcasts the operator's callsign /
// grid / freq / TX activity to the public map. Callsign + grid pre-fill from QRZ
// home (editable). Reporting only engages when toggled on AND both fields set.
function ReportSection({
  reporting,
  settings,
  error,
  saving,
  qrzCall,
  qrzGrid,
  onSave,
}: {
  reporting: boolean;
  settings: FreeDvReporterSettings | null;
  error: string | null;
  saving: boolean;
  qrzCall: string;
  qrzGrid: string;
  onSave: (settings: FreeDvReporterSettings) => void;
}) {
  const [open, setOpen] = useState(false);
  const [call, setCall] = useState('');
  const [grid, setGrid] = useState('');
  const [message, setMessage] = useState('');
  const [seeded, setSeeded] = useState(false);

  // Seed the editable fields once from the persisted settings, falling back to
  // the QRZ home station for any blank field so a fresh operator sees their call.
  useEffect(() => {
    if (seeded || !settings) return;
    setCall(settings.callsign || qrzCall || '');
    setGrid(settings.gridSquare || qrzGrid || '');
    setMessage(settings.message || '');
    setSeeded(true);
  }, [seeded, settings, qrzCall, qrzGrid]);

  const enabled = settings?.reportEnabled ?? false;
  const canEnable = call.trim().length > 0 && grid.trim().length > 0;

  const persist = (next: Partial<FreeDvReporterSettings>) => {
    onSave({
      reportEnabled: next.reportEnabled ?? enabled,
      callsign: next.callsign ?? call,
      gridSquare: next.gridSquare ?? grid,
      message: next.message ?? message,
    });
  };

  return (
    <div
      style={{
        borderBottom: '1px solid var(--panel-border)',
        padding: '4px 8px',
        display: 'flex',
        flexDirection: 'column',
        gap: 4,
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <button
          type="button"
          className="btn sm"
          onClick={() => setOpen((o) => !o)}
          title="Report my station onto the FreeDV Reporter map"
          style={{ fontSize: 10 }}
        >
          {open ? '▾' : '▸'} Report me
        </button>

        {/* Enable toggle — disabled until call + grid are present. */}
        <label
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            gap: 4,
            fontSize: 11,
            color: canEnable ? 'var(--fg-1)' : 'var(--fg-3)',
            cursor: canEnable ? 'pointer' : 'not-allowed',
          }}
          title={canEnable ? 'Broadcast my station publicly' : 'Enter callsign + grid first'}
        >
          <input
            type="checkbox"
            checked={enabled}
            disabled={!canEnable || saving}
            onChange={(e) => persist({ reportEnabled: e.target.checked })}
          />
          On the map
        </label>

        {reporting && (
          <span
            style={{ fontSize: 10, color: 'var(--accent)', whiteSpace: 'nowrap' }}
            title="Your station is live on qso.freedv.org"
          >
            ● on the map
          </span>
        )}
      </div>

      {open && (
        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, alignItems: 'center' }}>
          <input
            className="cs-input mono"
            value={call}
            onChange={(e) => setCall(e.target.value)}
            onBlur={() => persist({ callsign: call })}
            placeholder="Callsign"
            spellCheck={false}
            style={{ width: 90 }}
          />
          <input
            className="cs-input mono"
            value={grid}
            onChange={(e) => setGrid(e.target.value)}
            onBlur={() => persist({ gridSquare: grid })}
            placeholder="Grid"
            spellCheck={false}
            style={{ width: 70 }}
          />
          <input
            className="cs-input"
            value={message}
            onChange={(e) => setMessage(e.target.value)}
            onBlur={() => persist({ message })}
            placeholder="Status message (optional)"
            maxLength={80}
            style={{ flex: 1, minWidth: 110 }}
          />
        </div>
      )}

      {error && <span style={{ fontSize: 10, color: 'var(--tx)' }}>{error}</span>}
    </div>
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
