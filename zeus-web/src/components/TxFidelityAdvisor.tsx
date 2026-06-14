// SPDX-License-Identifier: GPL-2.0-or-later

import { analyzeTxFidelity } from '../audio/tx-fidelity';
import { useTxStore } from '../state/tx-store';

function fmtDb(v: number | null): string {
  return v === null ? '--' : `${v.toFixed(1)} dBFS`;
}

function fmtGr(v: number): string {
  return `${v.toFixed(1)} dB`;
}

function stateColor(state: string): string {
  if (state === 'sweet' || state === 'monitor') return 'var(--signal)';
  if (state === 'clip' || state === 'hot') return 'var(--tx)';
  if (state === 'under') return 'var(--power)';
  return 'var(--fg-2)';
}

export function TxFidelityAdvisor() {
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const txMonitorEnabled = useTxStore((s) => s.txMonitorEnabled);
  const micDbfs = useTxStore((s) => s.micDbfs);
  const wdspMicPk = useTxStore((s) => s.wdspMicPk);
  const lvlrGr = useTxStore((s) => s.lvlrGr);
  const cfcGr = useTxStore((s) => s.cfcGr);
  const alcGr = useTxStore((s) => s.alcGr);
  const outPk = useTxStore((s) => s.outPk);
  const psEnabled = useTxStore((s) => s.psEnabled);
  const psCorrecting = useTxStore((s) => s.psCorrecting);
  const snapshot = {
    moxOn,
    tunOn,
    txMonitorEnabled,
    micDbfs,
    wdspMicPk,
    lvlrGr,
    cfcGr,
    alcGr,
    outPk,
    psEnabled,
    psCorrecting,
  };
  const analysis = analyzeTxFidelity(snapshot);
  const color = stateColor(analysis.state);

  return (
    <section
      aria-label="TX fidelity advisor"
      title={analysis.detail}
      style={{
        display: 'grid',
        gridTemplateColumns: 'auto minmax(0, 1fr) auto',
        gap: 10,
        alignItems: 'center',
        padding: '8px 12px',
        border: '1px solid var(--line)',
        borderRadius: 6,
        background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
        boxShadow: `inset 3px 0 0 ${color}`,
        minWidth: 0,
      }}
    >
      <div
        className="mono"
        style={{
          minWidth: 54,
          textAlign: 'center',
          padding: '3px 7px',
          border: `1px solid ${color}`,
          color,
          background: 'var(--bg-2)',
          fontSize: 11,
          fontWeight: 800,
        }}
      >
        {analysis.score > 0 ? `${analysis.score}` : '--'}
      </div>
      <div style={{ minWidth: 0 }}>
        <div
          style={{
            display: 'flex',
            gap: 8,
            alignItems: 'baseline',
            minWidth: 0,
          }}
        >
          <span style={{ color: 'var(--fg-0)', fontWeight: 800, fontSize: 12 }}>
            {analysis.label}
          </span>
          <span className="label-xs" style={{ color, fontWeight: 800 }}>
            FIDELITY
          </span>
        </div>
        <div
          style={{
            color: 'var(--fg-2)',
            fontSize: 11,
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}
        >
          {analysis.detail}
        </div>
      </div>
      <div
        className="mono"
        style={{
          display: 'flex',
          flexWrap: 'wrap',
          justifyContent: 'flex-end',
          gap: '4px 8px',
          color: 'var(--fg-2)',
          fontSize: 10,
          minWidth: 0,
        }}
      >
        <span>MIC {fmtDb(analysis.micDbfs)}</span>
        <span>ALC {fmtGr(analysis.alcGr)}</span>
        <span>LVL {fmtGr(analysis.lvlrGr)}</span>
        <span>CFC {fmtGr(analysis.cfcGr)}</span>
      </div>
    </section>
  );
}
