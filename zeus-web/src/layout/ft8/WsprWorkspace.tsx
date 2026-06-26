// SPDX-License-Identifier: GPL-2.0-or-later
//
// WsprWorkspace — full-screen WSPR spot monitor. WSPR is a beacon mode: no QSO,
// no TX sequencing here — just the live received-spot table for the band, with
// a band selector and VFO readout. Reuses the FT8 dark-HUD theme. Spotting to
// WSPRnet and WSPR TX (beacon) are tracked as follow-ups.

import { useEffect, useState } from 'react';
import { useWsprStore, type WsprRow } from '../../state/wspr-store';
import { useConnectionStore } from '../../state/connection-store';
import { DIGITAL_BANDS } from '../../dsp/digital-segments';
import '../../styles/ft8-theme.css';

function useUtcClock(): string {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const id = setInterval(() => setNow(new Date()), 1000);
    return () => clearInterval(id);
  }, []);
  const p = (n: number) => n.toString().padStart(2, '0');
  return `${p(now.getUTCHours())}:${p(now.getUTCMinutes())}:${p(now.getUTCSeconds())} UTC`;
}

function fmtUtc(ms: number): string {
  const d = new Date(ms);
  const p = (n: number) => n.toString().padStart(2, '0');
  return `${p(d.getUTCHours())}:${p(d.getUTCMinutes())}`;
}

export function WsprWorkspace({ onClose }: { onClose?: () => void }) {
  const clock = useUtcClock();
  const band = useWsprStore((s) => s.band);
  const rows = useWsprStore((s) => s.rows);
  const enabled = useWsprStore((s) => s.enabled);
  const nativeAvailable = useWsprStore((s) => s.nativeAvailable);
  const error = useWsprStore((s) => s.error);
  const qsyBand = useWsprStore((s) => s.qsyBand);
  const vfoHz = useConnectionStore((s) => s.vfoHz);

  useEffect(() => {
    if (!onClose) return;
    const h = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault();
        onClose();
      }
    };
    window.addEventListener('keydown', h);
    return () => window.removeEventListener('keydown', h);
  }, [onClose]);

  const wsprBands = DIGITAL_BANDS.filter((b) => b.wsprHz != null);

  return (
    <div className="ft8-workspace" role="region" aria-label="WSPR workspace">
      <header className="ft8-ws-header">
        <span className="ft8-ws-title">WSPR · BEACON MONITOR</span>
        <span className="ft8-ws-clock">{clock}</span>
        {onClose && (
          <button type="button" className="ft8-ws-close" onClick={onClose}>
            Exit · Esc
          </button>
        )}
      </header>

      <div className="ft8-ws-body">
        <div className="ft8-ws-col">
          <section className="ft8-region">
            <div className="ft8-region__head">Radio</div>
            <div className="ft8-vfo">
              {vfoHz > 0 ? (vfoHz / 1e6).toFixed(6) : '—.———.———'} <small>MHz · USB/DIGU</small>
            </div>
            <div className="ft8-band-grid">
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
          </section>
          <section className="ft8-region ft8-region--grow">
            <div className="ft8-region__head">About</div>
            <div className="ft8-placeholder">
              WSPR beacon spots received here. WSPRnet upload + WSPR TX are
              follow-ups.
            </div>
          </section>
        </div>

        <div className="ft8-ws-col ft8-ws-col--center" style={{ flex: 2 }}>
          <section className="ft8-region ft8-region--decodes ft8-region--grow">
            <div className="ft8-region__head">Received spots · {band}</div>
            <div className="ft8-region__body" style={{ padding: 0 }}>
              <WsprSpotTable rows={rows} />
            </div>
          </section>
        </div>
      </div>

      <footer className="ft8-ws-status">
        <span className={nativeAvailable ? 'ok' : 'warn'}>
          {nativeAvailable ? (enabled ? 'DECODING' : 'DECODER READY') : 'DECODER UNAVAILABLE'}
        </span>
        <span>{rows.length} spots</span>
        {error && <span className="warn">{error}</span>}
        <span style={{ marginLeft: 'auto' }}>WSPR native · {band}</span>
      </footer>
    </div>
  );
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

/** Self-contained mount: renders the WSPR overlay when the store is open. */
export function WsprWorkspaceMount() {
  const open = useWsprStore((s) => s.open);
  const close = useWsprStore((s) => s.closeWorkspace);
  if (!open) return null;
  return <WsprWorkspace onClose={close} />;
}
