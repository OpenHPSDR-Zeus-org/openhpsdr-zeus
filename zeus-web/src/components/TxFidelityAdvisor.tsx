// SPDX-License-Identifier: GPL-2.0-or-later

import { analyzeTxFidelity } from '../audio/tx-fidelity';
import { useTxStore } from '../state/tx-store';

function fmtDb(v: number | null): string {
  return v === null ? '--' : `${v.toFixed(1)} dBFS`;
}

function fmtGr(v: number): string {
  return `${v.toFixed(1)} dB`;
}

function fmtValue(v: number | null, suffix = ''): string {
  return v === null ? '--' : `${v.toFixed(0)}${suffix}`;
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
  const swr = useTxStore((s) => s.swr);
  const psEnabled = useTxStore((s) => s.psEnabled);
  const psCorrecting = useTxStore((s) => s.psCorrecting);
  const psFeedbackLevel = useTxStore((s) => s.psFeedbackLevel);
  const psCalState = useTxStore((s) => s.psCalState);
  const psCalibrationStalled = useTxStore((s) => s.psCalibrationStalled);
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
    swr,
    psEnabled,
    psCorrecting,
    psFeedbackLevel,
    psCalState,
    psCalibrationStalled,
  };
  const analysis = analyzeTxFidelity(snapshot);
  const color = stateColor(analysis.state);

  return (
    <section
      aria-label="TX fidelity advisor"
      title={analysis.detail}
      style={{
        display: 'grid',
        gridTemplateColumns: '54px minmax(0, 1fr)',
        gridTemplateAreas: "'score summary' 'metrics metrics'",
        columnGap: 10,
        rowGap: 5,
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
          gridArea: 'score',
          width: 54,
          boxSizing: 'border-box',
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
      <div style={{ gridArea: 'summary', minWidth: 0 }}>
        <div
          style={{
            display: 'flex',
            gap: 8,
            alignItems: 'baseline',
            flexWrap: 'wrap',
            minWidth: 0,
          }}
        >
          <span
            style={{
              color: 'var(--fg-0)',
              fontWeight: 800,
              fontSize: 12,
              whiteSpace: 'nowrap',
            }}
          >
            {analysis.label}
          </span>
          <span className="label-xs" style={{ color, fontWeight: 800, flexShrink: 0 }}>
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
          gridArea: 'metrics',
          display: 'grid',
          gridTemplateColumns: 'repeat(auto-fit, minmax(96px, 1fr))',
          gap: '4px 10px',
          justifyItems: 'start',
          color: 'var(--fg-2)',
          fontSize: 10,
          lineHeight: 1.25,
          minWidth: 0,
        }}
      >
        <span style={{ whiteSpace: 'nowrap' }}>MIC {fmtDb(analysis.micDbfs)}</span>
        <span style={{ whiteSpace: 'nowrap' }}>OUT {fmtDb(analysis.outDbfs)}</span>
        <span style={{ whiteSpace: 'nowrap' }}>ALC {fmtGr(analysis.alcGr)}</span>
        <span style={{ whiteSpace: 'nowrap' }}>LVL {fmtGr(analysis.lvlrGr)}</span>
        <span style={{ whiteSpace: 'nowrap' }}>CFC {fmtGr(analysis.cfcGr)}</span>
        <span style={{ whiteSpace: 'nowrap' }}>SWR {analysis.swr.toFixed(2)}</span>
        <span style={{ whiteSpace: 'nowrap' }}>PSFB {fmtValue(analysis.psFeedbackLevel)}</span>
      </div>
    </section>
  );
}
