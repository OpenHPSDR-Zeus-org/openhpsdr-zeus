// SPDX-License-Identifier: GPL-2.0-or-later
//
// FT8 decode table — the hero of the FT8 workspace. Renders the live decode
// stream from ft8-store (newest first), color-coded by message class. Pure
// presentation over the store; clicking a row will (later) prefill the QSO /
// move the audio cursor.

import { useFt8Store, type Ft8Row } from '../../state/ft8-store';
import { parseFt8Message } from '../../dsp/ft8-message';

export type Ft8RowClass = 'cq' | 'me' | 'worked' | 'new' | 'normal';

/**
 * Classify a decode for color-coding. `myCall` enables the directed-at-me
 * highlight; `workedCalls` (optional) dims stations already worked; `workedGrids`
 * (optional, 4-char upper-case) lights an otherwise-plain decode whose grid we
 * have NOT worked yet ('new'). CQ keeps its own green class even when its grid is
 * new — CQ is the louder signal in the table and the existing precedence is
 * relied on elsewhere.
 */
export function classifyDecode(
  row: Ft8Row,
  myCall?: string,
  workedCalls?: ReadonlySet<string>,
  workedGrids?: ReadonlySet<string>,
): Ft8RowClass {
  const tokens = row.text.trim().split(/\s+/);
  const first = tokens[0]?.toUpperCase() ?? '';
  const second = tokens[1]?.toUpperCase() ?? '';
  const me = myCall?.toUpperCase();

  // FT8 standard message: "<call-to> <call-from> <grid/report>".
  if (first === 'CQ') return 'cq';                  // a CQ row (even my own)
  if (me && first === me) return 'me';              // someone is calling ME
  if (workedCalls && second && workedCalls.has(second)) return 'worked';
  if (workedGrids) {
    const grid = parseFt8Message(row.text).grid;
    if (grid && !workedGrids.has(grid.toUpperCase())) return 'new';
  }
  return 'normal';
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
  workedCalls?: ReadonlySet<string>;
  workedGrids?: ReadonlySet<string>;
  onRowClick?: (row: Ft8Row) => void;
}

export function Ft8DecodeTable({ myCall, workedCalls, workedGrids, onRowClick }: Ft8DecodeTableProps) {
  const rows = useFt8Store((s) => s.rows);

  if (rows.length === 0) {
    return <div className="ft8-decode-empty">Waiting for decodes…</div>;
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
        </tr>
      </thead>
      <tbody>
        {rows.map((r) => {
          const cls = classifyDecode(r, myCall, workedCalls, workedGrids);
          return (
            <tr
              key={r.id}
              className={CLASS_CSS[cls]}
              onClick={onRowClick ? () => onRowClick(r) : undefined}
              style={onRowClick ? { cursor: 'pointer' } : undefined}
            >
              <td>{fmtUtc(r.slotStartUnixMs)}</td>
              <td className="num">{r.snrDb >= 0 ? `+${r.snrDb.toFixed(0)}` : r.snrDb.toFixed(0)}</td>
              <td className="num">{r.dtSec.toFixed(1)}</td>
              <td className="num">{r.freqHz.toFixed(0)}</td>
              <td>{r.text}</td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}
