// SPDX-License-Identifier: GPL-2.0-or-later
//
// FT8 decode table — the hero of the FT8 workspace. Renders the live decode
// stream from ft8-store (newest first), color-coded by message class. Pure
// presentation over the store; clicking a row will (later) prefill the QSO /
// move the audio cursor.

import { useFt8Store, type Ft8Row } from '../../state/ft8-store';
import type { Ft8TxEcho } from '../../state/ft8-tx-store';
import { useDigitalWorkedStore } from '../../state/digital-worked-store';
import { parseFt8Message } from '../../dsp/ft8-message';
import { tryParseSender } from '../../dsp/ft8-sender';

export type Ft8RowClass = 'cq' | 'me' | 'worked' | 'new' | 'normal';

/**
 * Classify a decode for color-coding. `myCall` enables the directed-at-me
 * highlight. The worked-before highlight is decorated at RENDER time from
 * `workedCalls` — the digital-worked-store set (prior FT8/FT4 QSO with the
 * sender, full logbook history, fed by GET /api/log/digital-worked) — probing
 * the sender extracted by tryParseSender, the same parser+set semantics the
 * server used before the digital suite moved into the Zeus Digital plugin.
 * Render-time decoration self-heals: rows ingested before the worked-set fetch
 * resolves light up as soon as it lands. `row.workedBefore` stays honoured as
 * a fallback for payloads that still carry the server flag. `workedGrids`
 * (optional, 4-char upper-case) lights an otherwise-plain decode whose grid we
 * have NOT worked yet ('new'). CQ keeps its own green class even when its grid
 * is new — CQ is the louder signal in the table and the existing precedence is
 * relied on elsewhere.
 *
 * Precedence: cq > me > worked > new > normal. 'me' (someone calling YOU)
 * outranks worked-before; CQ outranks everything.
 */
export function classifyDecode(
  row: Ft8Row,
  myCall?: string,
  workedGrids?: ReadonlySet<string>,
  workedCalls?: ReadonlySet<string>,
): Ft8RowClass {
  const tokens = row.text.trim().split(/\s+/);
  const first = tokens[0]?.toUpperCase() ?? '';
  const me = myCall?.toUpperCase();

  // FT8 standard message: "<call-to> <call-from> <grid/report>".
  if (first === 'CQ') return 'cq';                  // a CQ row (even my own)
  if (me && first === me) return 'me';              // someone is calling ME
  if (isWorkedBefore(row, workedCalls)) return 'worked'; // prior FT8/FT4 QSO
  if (workedGrids) {
    const grid = parseFt8Message(row.text).grid;
    if (grid && !workedGrids.has(grid.toUpperCase())) return 'new';
  }
  return 'normal';
}

function isWorkedBefore(row: Ft8Row, workedCalls?: ReadonlySet<string>): boolean {
  if (row.workedBefore === true) return true; // legacy server flag (fallback)
  if (!workedCalls || workedCalls.size === 0) return false;
  const sender = tryParseSender(row.text);
  return sender != null && workedCalls.has(sender.call);
}

function fmtUtc(ms: number): string {
  const d = new Date(ms);
  const p = (n: number) => n.toString().padStart(2, '0');
  return `${p(d.getUTCHours())}:${p(d.getUTCMinutes())}:${p(d.getUTCSeconds())}`;
}

const CLASS_CSS: Record<Ft8RowClass, string> = {
  cq: 'ft8-row--cq',
  me: 'ft8-row--me',
  worked: 'ft8-row--worked',
  new: 'ft8-row--new',
  normal: '',
};

export interface Ft8DecodeTableProps {
  myCall?: string;
  workedGrids?: ReadonlySet<string>;
  onRowClick?: (row: Ft8Row) => void;
  /** FT8 Settings → Decode filters (client-side predicates over the live rows).
   *  `showOnlyCq` keeps only CQ rows plus anything directed at my call (so the
   *  operator never misses a station answering them); `hideWorkedBefore` drops
   *  rows whose sender is already in the logbook. */
  showOnlyCq?: boolean;
  hideWorkedBefore?: boolean;
  /** Our own transmissions, echoed into the decode flow (WSJT-X yellow Tx line).
   *  Rendered as distinct, non-clickable TX rows interleaved by timestamp. */
  txEchoes?: readonly Ft8TxEcho[];
}

/** A unified, time-sorted render item: a received decode or one of our TX echoes. */
type FlowItem =
  | { kind: 'rx'; t: number; row: Ft8Row; cls: Ft8RowClass }
  | { kind: 'tx'; t: number; echo: Ft8TxEcho };

export function Ft8DecodeTable({
  myCall,
  workedGrids,
  onRowClick,
  showOnlyCq,
  hideWorkedBefore,
  txEchoes,
}: Ft8DecodeTableProps) {
  const allRows = useFt8Store((s) => s.rows);
  // Render-time worked-before decoration (see classifyDecode): re-renders when
  // the worked-set fetch lands, so early rows self-heal.
  const workedCalls = useDigitalWorkedStore((s) => s.calls);

  const rows =
    showOnlyCq || hideWorkedBefore
      ? allRows.filter((r) => {
          const cls = classifyDecode(r, myCall, workedGrids, workedCalls);
          if (showOnlyCq && cls !== 'cq' && cls !== 'me') return false;
          if (hideWorkedBefore && cls === 'worked') return false;
          return true;
        })
      : allRows;

  // Merge received rows + our TX echoes into one newest-first flow. TX rows are
  // never filtered out — the operator always sees what they sent.
  const items: FlowItem[] = [
    ...rows.map<FlowItem>((r) => ({
      kind: 'rx',
      t: r.slotStartUnixMs,
      row: r,
      cls: classifyDecode(r, myCall, workedGrids, workedCalls),
    })),
    ...(txEchoes ?? []).map<FlowItem>((e) => ({ kind: 'tx', t: e.timeUtcMs, echo: e })),
  ].sort((a, b) => b.t - a.t);

  if (items.length === 0) {
    return (
      <div className="ft8-decode-empty">
        {allRows.length === 0 ? 'Waiting for decodes…' : 'No decodes match the active filter'}
      </div>
    );
  }

  return (
    <table className="ft8-decode-table">
      <thead>
        <tr>
          <th>UTC</th>
          <th>dB</th>
          <th>DT</th>
          <th>Freq</th>
          <th>Message</th>
          <th>Country</th>
        </tr>
      </thead>
      <tbody>
        {items.map((item) => {
          if (item.kind === 'tx') {
            const e = item.echo;
            return (
              <tr key={e.id} className="ft8-row--tx" title="Your transmission">
                <td>{fmtUtc(e.timeUtcMs)}</td>
                <td className="num">Tx</td>
                <td className="num">—</td>
                <td className="num">{e.audioHz.toFixed(0)}</td>
                <td>
                  <span className="ft8-row__tx-badge">TX</span> {e.message}
                </td>
                <td className="ft8-country" />
              </tr>
            );
          }
          const r = item.row;
          return (
            <tr
              key={r.id}
              className={CLASS_CSS[item.cls]}
              onClick={onRowClick ? () => onRowClick(r) : undefined}
              style={onRowClick ? { cursor: 'pointer' } : undefined}
            >
              <td>{fmtUtc(r.slotStartUnixMs)}</td>
              <td className="num">{r.snrDb >= 0 ? `+${r.snrDb.toFixed(0)}` : r.snrDb.toFixed(0)}</td>
              <td className="num">{r.dtSec.toFixed(1)}</td>
              <td className="num">{r.freqHz.toFixed(0)}</td>
              <td>{r.text}</td>
              <td className="ft8-country">{r.country ?? ''}</td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}
