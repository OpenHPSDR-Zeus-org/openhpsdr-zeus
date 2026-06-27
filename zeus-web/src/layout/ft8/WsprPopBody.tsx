// SPDX-License-Identifier: GPL-2.0-or-later
//
// WsprPopBody — the WSPR beacon body hosted inside the floating DigitalWindow.
// WSPR is a beacon mode (no QSO, no sequencer, no QRZ), so this is just the live
// received-spot table, a band row that re-QSYs the main radio, and the beacon TX
// cluster. Re-housed from the old full-screen WsprWorkspace; the spot table and
// WsprTxControl logic are unchanged.

import { useEffect } from 'react';
import { useWsprStore, type WsprRow } from '../../state/wspr-store';
import { useConnectionStore } from '../../state/connection-store';
import { useOperatorStore } from '../../state/operator-store';
import { DIGITAL_BANDS, nearestDigitalBand } from '../../dsp/digital-segments';
import { WsprTxControl } from './WsprTxControl';

function fmtUtc(ms: number): string {
  const d = new Date(ms);
  const p = (n: number) => n.toString().padStart(2, '0');
  return `${p(d.getUTCHours())}:${p(d.getUTCMinutes())}`;
}

function WsprSpotTable({ rows }: { rows: WsprRow[] }) {
  if (rows.length === 0) {
    return <div className="ft8-decode-empty">Waiting for spots… (WSPR slots are 2 minutes)</div>;
  }
  return (
    <table className="ft8-decode-table">
      <thead>
        <tr>
          <th>UTC</th>
          <th>dB</th>
          <th>DT</th>
          <th>MHz</th>
          <th>Drift</th>
          <th>Call</th>
          <th>Grid</th>
          <th>dBm</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((r) => (
          <tr key={r.id}>
            <td>{fmtUtc(r.slotStartUnixMs)}</td>
            <td className="num">{r.snrDb >= 0 ? `+${r.snrDb.toFixed(0)}` : r.snrDb.toFixed(0)}</td>
            <td className="num">{r.dtSec.toFixed(1)}</td>
            <td className="num">{r.freqMhz.toFixed(6)}</td>
            <td className="num">{r.driftHz}</td>
            <td>{r.callsign}</td>
            <td>{r.grid}</td>
            <td className="num">{r.powerDbm ?? ''}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function WsprPopBody() {
  const band = useWsprStore((s) => s.band);
  const rows = useWsprStore((s) => s.rows);
  const qsyBand = useWsprStore((s) => s.qsyBand);
  const vfoHz = useConnectionStore((s) => s.vfoHz);
  const myCall = useOperatorStore((s) => s.call);
  const myGrid = useOperatorStore((s) => s.grid);
  const setCall = useOperatorStore((s) => s.setCall);
  const setGrid = useOperatorStore((s) => s.setGrid);

  // Band-follow: a main-band change re-QSYs the WSPR dial (see Ft8PopBody).
  useEffect(() => {
    if (vfoHz <= 0) return;
    const near = nearestDigitalBand(vfoHz).name;
    if (near !== band) qsyBand(near);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [vfoHz]);

  const wsprBands = DIGITAL_BANDS.filter((b) => b.wsprHz != null);

  return (
    <div className="dw-body">
      <div className="dw-subhead">
        <label className="ft8-ws-id">
          <span>Call</span>
          <input
            value={myCall}
            onChange={(e) => setCall(e.target.value)}
            placeholder="MYCALL"
            spellCheck={false}
            size={8}
          />
        </label>
        <label className="ft8-ws-id">
          <span>Grid</span>
          <input
            value={myGrid}
            onChange={(e) => setGrid(e.target.value)}
            placeholder="FN42"
            spellCheck={false}
            size={6}
          />
        </label>
      </div>

      <div className="ft8-band-grid dw-bands">
        {wsprBands.map((b) => (
          <button
            key={b.name}
            type="button"
            className={`ft8-band-btn${band === b.name ? ' is-active' : ''}`}
            onClick={() => qsyBand(b.name)}
            title={`QSY to ${b.name} WSPR dial`}
          >
            {b.name}
          </button>
        ))}
      </div>

      <section className="dw-section dw-section--grow">
        <div className="ft8-region__head">Received spots · {band}</div>
        <div className="dw-section__body dw-section__body--flush">
          <WsprSpotTable rows={rows} />
        </div>
      </section>

      <section className="dw-section">
        <div className="ft8-region__head">TX control · beacon</div>
        <div className="dw-section__body">
          <WsprTxControl myCall={myCall} myGrid={myGrid} />
        </div>
      </section>
    </div>
  );
}
