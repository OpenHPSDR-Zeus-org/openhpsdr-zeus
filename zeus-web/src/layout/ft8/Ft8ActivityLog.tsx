// SPDX-License-Identifier: GPL-2.0-or-later
//
// Ft8ActivityLog — the FT8/FT4 workspace ACTIVITY LOG (center column, fixed
// slot, no close button). Pure presentation over the EXISTING logbook store
// (useLoggerStore / /api/log / LogService) — it adds no store, API, or DB. QSOs
// land here automatically when the sequencer completes one (auto-log) or when
// the operator hits LOG QSO; EXPORT ADIF and VIEW LOG reuse the same backend.
//
// Columns follow docs/designs/ft8-ui.md: DATE/UTC/CALLSIGN/BAND/MODE/RST TX/RST
// RX/COUNTRY/GRID/QSL. HUD tokens only (ft8-theme.css) — no raw hex.

import { useLoggerStore } from '../../state/logger-store';
import type { LogEntry } from '../../api/log';

function fmtDate(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  const p = (n: number) => n.toString().padStart(2, '0');
  return `${d.getUTCFullYear()}-${p(d.getUTCMonth() + 1)}-${p(d.getUTCDate())}`;
}

function fmtTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  const p = (n: number) => n.toString().padStart(2, '0');
  return `${p(d.getUTCHours())}:${p(d.getUTCMinutes())}`;
}

export interface Ft8ActivityLogProps {
  /** Manual LOG QSO — logs the in-progress QSO. Disabled when `canLog` is false. */
  onLogQso?: () => void;
  /** Whether there is a DX call staged to log right now. */
  canLog?: boolean;
}

export function Ft8ActivityLog({ onLogQso, canLog = false }: Ft8ActivityLogProps) {
  const entries = useLoggerStore((s) => s.entries);
  const totalCount = useLoggerStore((s) => s.totalCount);
  const exportAdif = useLoggerStore((s) => s.exportAdif);
  const loadEntries = useLoggerStore((s) => s.loadEntries);

  return (
    <div className="ft8-log">
      <div className="ft8-log__bar">
        <span className="ft8-log__count">
          {entries.length}
          {totalCount > entries.length ? ` of ${totalCount}` : ''} QSOs
        </span>
        <span style={{ flex: 1 }} />
        {onLogQso && (
          <button
            type="button"
            className="ft8-log__btn"
            onClick={onLogQso}
            disabled={!canLog}
            title="Log the in-progress QSO"
          >
            LOG QSO
          </button>
        )}
        <button
          type="button"
          className="ft8-log__btn"
          onClick={() => void loadEntries()}
          title="Refresh the log from the server"
        >
          VIEW LOG
        </button>
        <button
          type="button"
          className="ft8-log__btn"
          onClick={() => void exportAdif()}
          disabled={entries.length === 0}
          title="Download the logbook as an ADIF file"
        >
          EXPORT ADIF
        </button>
      </div>

      {entries.length === 0 ? (
        <div className="ft8-log__empty">No QSOs logged yet.</div>
      ) : (
        <div className="ft8-log__scroll">
          <table className="ft8-log-table">
            <thead>
              <tr>
                <th>Date</th>
                <th>UTC</th>
                <th>Callsign</th>
                <th>Band</th>
                <th>Mode</th>
                <th>RST TX</th>
                <th>RST RX</th>
                <th>Country</th>
                <th>Grid</th>
                <th>QSL</th>
              </tr>
            </thead>
            <tbody>
              {entries.map((e: LogEntry) => (
                <tr key={e.id}>
                  <td>{fmtDate(e.qsoDateTimeUtc)}</td>
                  <td className="num">{fmtTime(e.qsoDateTimeUtc)}</td>
                  <td className="call">{e.callsign}</td>
                  <td>{e.band}</td>
                  <td>{e.mode}</td>
                  <td className="num">{e.rstSent || '—'}</td>
                  <td className="num">{e.rstRcvd || '—'}</td>
                  <td>{e.country ?? '—'}</td>
                  <td>{e.grid ?? '—'}</td>
                  <td className="qsl">{e.qrzUploadedUtc ? '✓' : ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
