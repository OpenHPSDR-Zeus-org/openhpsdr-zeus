// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
//
// Audio Chain Monitor — Factory Widget (Direction A: horizontal pipeline).
// Implements the design handoff bundle's recommended canonical full-size
// widget. Renders the TX audio chain as a left-to-right pipeline of nine
// tiles (Mic → EQ → Lev → CFC → Cmp → ALC → Out · Wire · PA) with an
// advisory rail underneath carrying verdict messages + Show me / Apply /
// Dismiss buttons. Subscribes to:
//   - useAudioChainHealthStore — verdict overlay (MsgType 0x32, ~2 Hz)
//   - useTxStore                — raw stage readings (MsgType 0x16, ~10 Hz)
//
// Per ADR-0002, the two streams are joined in the frontend by StageId.
// The wire never duplicates the raw numbers — they stay on TxMetersV2.

import { useMemo } from 'react';
import './AudioChainMonitor.css';
import {
  AudioChainSeverity,
  AudioChainStageId,
  VERDICT_FLAG_HAS_APPLY,
  VERDICT_FLAG_IMMEDIATE_ACTION,
  useAudioChainHealthStore,
} from '../state/audio-chain-health-store';
import type { AudioChainVerdict } from '../state/audio-chain-health-store';
import { useTxStore } from '../state/tx-store';

type TileSpec = {
  id: AudioChainStageId;
  idx: string;
  name: string;
  groupBreak?: boolean;
};

// The nine factory-widget tiles in pipeline order. Indexes are 01..09
// (one-based, two-digit) because the design uses them as a literal
// teaching aid — operators learn the chain by reading left-to-right.
const TILES: readonly TileSpec[] = [
  { id: AudioChainStageId.Mic, idx: '01', name: 'Mic' },
  { id: AudioChainStageId.Eq, idx: '02', name: 'EQ' },
  { id: AudioChainStageId.Leveler, idx: '03', name: 'Lev' },
  { id: AudioChainStageId.Cfc, idx: '04', name: 'CFC' },
  { id: AudioChainStageId.Comp, idx: '05', name: 'Cmp' },
  { id: AudioChainStageId.Alc, idx: '06', name: 'ALC' },
  { id: AudioChainStageId.Out, idx: '07', name: 'Out', groupBreak: true },
  { id: AudioChainStageId.Wire, idx: '08', name: 'Wire', groupBreak: true },
  { id: AudioChainStageId.Pa, idx: '09', name: 'PA' },
];

// dBFS sentinel: TxMetersV2 emits values ≤ -200 dBFS when the stage is
// bypassed (the WDSP −400 silence sentinel makes its way to the wire
// near −∞). We render those as "—" rather than a misleadingly precise
// "−400 dBFS" number.
const isBypassedReading = (db: number): boolean =>
  !Number.isFinite(db) || db <= -200;

const formatDbfs = (db: number): string => {
  if (isBypassedReading(db)) return '—';
  // Match the design's compact "−24" / "+2" / "−6" notation. Use the
  // minus sign character (U+2212) for typographic parity with the
  // mocked HTML (which uses '−', not ASCII '-').
  const n = Math.round(db);
  if (n === 0) return '0';
  if (n > 0) return `+${n}`;
  return `−${Math.abs(n)}`;
};

const formatGr = (gr: number): string => {
  if (!Number.isFinite(gr) || gr === 0) return 'gr 0 dB';
  return `gr ${gr.toFixed(0)} dB`;
};

const severityClass = (s: AudioChainSeverity, immediate: boolean): string => {
  if (s === AudioChainSeverity.Error && immediate) return 'acm__tile--s-imm';
  if (s === AudioChainSeverity.Error) return 'acm__tile--s-err';
  if (s === AudioChainSeverity.Warn) return 'acm__tile--s-warn';
  if (s === AudioChainSeverity.Info) return 'acm__tile--s-info';
  // Explicit "ok" class so healthy tiles read green at a glance —
  // the original design left ok visually neutral, which made it
  // impossible to tell at a distance whether the chain was green or
  // simply unverdicted.
  return 'acm__tile--s-ok';
};

const pillClass = (s: AudioChainSeverity): string => {
  switch (s) {
    case AudioChainSeverity.Error:
      return 'acm__pill acm__pill--err';
    case AudioChainSeverity.Warn:
      return 'acm__pill acm__pill--warn';
    case AudioChainSeverity.Info:
      return 'acm__pill acm__pill--info';
    default:
      return 'acm__pill acm__pill--ok';
  }
};

const pillLabel = (v: AudioChainVerdict): string => {
  const immediate = (v.flags & VERDICT_FLAG_IMMEDIATE_ACTION) !== 0;
  if (immediate) return 'STOP TX';
  switch (v.severity) {
    case AudioChainSeverity.Error:
      return 'err';
    case AudioChainSeverity.Warn:
      return 'warn';
    case AudioChainSeverity.Info:
      return 'info';
    default:
      return 'ok';
  }
};

// Tile reading + sub-reading composer. Reads from useTxStore (raw stage
// meters) and the drive/PA hooks. Returns "—" placeholders for
// fields not yet on the wire (drive byte, IQ peak, volts/amps).
type TileReadings = {
  main: string;
  unit: string;
  sub: string;
};

const composeReading = (
  id: AudioChainStageId,
  tx: ReturnType<typeof useTxStore.getState>,
): TileReadings => {
  switch (id) {
    case AudioChainStageId.Mic:
      return {
        main: formatDbfs(tx.wdspMicPk),
        unit: 'dBFS',
        sub: `pk ${formatDbfs(tx.wdspMicPk)}`,
      };
    case AudioChainStageId.Eq:
      return {
        main: formatDbfs(tx.eqAv),
        unit: 'dBFS',
        sub: `pk ${formatDbfs(tx.eqPk)}`,
      };
    case AudioChainStageId.Leveler:
      return {
        main: formatDbfs(tx.lvlrAv),
        unit: 'dBFS',
        sub: formatGr(tx.lvlrGr),
      };
    case AudioChainStageId.Cfc:
      return {
        main: formatDbfs(tx.cfcAv),
        unit: 'dBFS',
        sub: formatGr(tx.cfcGr),
      };
    case AudioChainStageId.Comp:
      return {
        main: formatDbfs(tx.compAv),
        unit: 'dBFS',
        sub: `pk ${formatDbfs(tx.compPk)}`,
      };
    case AudioChainStageId.Alc:
      return {
        main: formatDbfs(tx.alcAv),
        unit: 'dBFS',
        sub: formatGr(tx.alcGr),
      };
    case AudioChainStageId.Out:
      return {
        main: formatDbfs(tx.outAv),
        unit: 'dBFS',
        sub: `pk ${formatDbfs(tx.outPk)}`,
      };
    case AudioChainStageId.Wire:
      // Drive byte / IQ peak / packet rate are not on the wire yet —
      // they live in-process on the server and only verdicts are
      // published. Show drive % as a stand-in for the moment.
      return {
        main: `${tx.drivePercent}`,
        unit: '%',
        sub: '— iq · pkt',
      };
    case AudioChainStageId.Pa: {
      const watts = Number.isFinite(tx.fwdWatts) ? tx.fwdWatts.toFixed(0) : '—';
      const swrText = Number.isFinite(tx.swr) ? `swr ${tx.swr.toFixed(1)}` : '—';
      // Volts / amps not yet wired through — drive byte / IQ peak ditto.
      return { main: watts, unit: 'W', sub: swrText };
    }
    default:
      return { main: '—', unit: '', sub: '' };
  }
};

const sevRank = (s: AudioChainSeverity, immediate: boolean): number => {
  if (s === AudioChainSeverity.Error && immediate) return 4;
  if (s === AudioChainSeverity.Error) return 3;
  if (s === AudioChainSeverity.Warn) return 2;
  if (s === AudioChainSeverity.Info) return 1;
  return 0;
};

// Fire and forget the apply endpoint. The server keeps the absolute
// target value per StageId in-process and applies it when the
// frontend posts the stageId — see ADR-0003 and zeus-pgn.
const postApply = (stageId: AudioChainStageId) => {
  void fetch('/api/audio-chain/apply', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ stageId }),
  }).catch(() => {
    /* widget stays silent on failure — operator will see the verdict
       persist if the apply didn't land */
  });
};

// "Show me" deeplink — emits a CustomEvent on the document that the
// per-control settings panels listen for (see useChainFocus hook,
// zeus-xe8). The widget knows the verdict's StageId; the hook knows
// the stable control identifiers each panel registered.
const fireShowMe = (stageId: AudioChainStageId) => {
  document.dispatchEvent(
    new CustomEvent('zeus:chain-focus', { detail: { stageId } }),
  );
};

type AdvisoryRowProps = {
  verdict: AudioChainVerdict;
  tile: TileSpec;
  onDismiss: () => void;
};

function AdvisoryRow({ verdict: v, tile, onDismiss }: AdvisoryRowProps) {
  const immediate = (v.flags & VERDICT_FLAG_IMMEDIATE_ACTION) !== 0;
  const canApply = (v.flags & VERDICT_FLAG_HAS_APPLY) !== 0;
  const cls =
    v.severity === AudioChainSeverity.Error && immediate
      ? 'acm__advisory acm__advisory--imm'
      : v.severity === AudioChainSeverity.Error
        ? 'acm__advisory acm__advisory--err'
        : 'acm__advisory acm__advisory--warn';

  // Split the message at the first em-dash so the leading clause
  // ("STOP TX —", "CFC pumping —") can be styled emphasised, matching
  // the design.
  const dashIdx = v.message.indexOf('—');
  const lead = dashIdx > 0 ? v.message.slice(0, dashIdx + 1) : '';
  const rest = dashIdx > 0 ? v.message.slice(dashIdx + 1) : v.message;

  return (
    <div className={cls}>
      <span className="acm__adv-icon" />
      <span className="acm__adv-stage">
        {tile.idx} · {tile.name}
      </span>
      <span className="acm__adv-msg">
        {lead && <span className="acm__adv-msg-em">{lead}</span>}
        {rest && <span>{rest}</span>}
      </span>
      <span className="acm__adv-btns">
        {immediate ? (
          <button
            type="button"
            className="acm__btn acm__btn--danger"
            onClick={() => fireShowMe(v.stageId)}
          >
            Unkey now
          </button>
        ) : null}
        <button
          type="button"
          className="acm__btn"
          onClick={() => fireShowMe(v.stageId)}
        >
          Show me
        </button>
        {canApply && v.applyLabel ? (
          <button
            type="button"
            className="acm__btn acm__btn--primary"
            onClick={() => postApply(v.stageId)}
          >
            {v.applyLabel}
          </button>
        ) : null}
        <button
          type="button"
          className="acm__btn acm__btn--icon"
          title="Dismiss (session only)"
          onClick={onDismiss}
        >
          ×
        </button>
      </span>
    </div>
  );
}

export function AudioChainMonitor() {
  const snapshot = useAudioChainHealthStore((s) => s.snapshot);
  const dismissed = useAudioChainHealthStore((s) => s.dismissed);
  const dismiss = useAudioChainHealthStore((s) => s.dismiss);
  // Subscribe to the whole tx store so every meter tick re-renders the
  // tile readings. Cheap — the JSX below renders nine flexbox tiles, no
  // canvas, no GPU. Real meters (TxStageMeters etc.) are separate and
  // continue handling their high-frequency cadence.
  const tx = useTxStore();

  // Build the active advisory list: every non-OK, non-Info, non-dismissed
  // verdict, worst-severity first. Cap at 3 to keep the rail bounded —
  // operators with four simultaneous warnings already need to stop
  // transmitting and look at the radio.
  const advisories = useMemo(() => {
    const out: { verdict: AudioChainVerdict; tile: TileSpec }[] = [];
    for (const tile of TILES) {
      const v = snapshot.byStage.get(tile.id);
      if (!v) continue;
      if (v.severity === AudioChainSeverity.Ok) continue;
      if (v.severity === AudioChainSeverity.Info) continue;
      const dismissKey = `${v.stageId}|${v.severity}|${v.message}`;
      if (dismissed.has(dismissKey)) continue;
      out.push({ verdict: v, tile });
    }
    out.sort((a, b) => {
      const ra = sevRank(
        a.verdict.severity,
        (a.verdict.flags & VERDICT_FLAG_IMMEDIATE_ACTION) !== 0,
      );
      const rb = sevRank(
        b.verdict.severity,
        (b.verdict.flags & VERDICT_FLAG_IMMEDIATE_ACTION) !== 0,
      );
      return rb - ra;
    });
    return out.slice(0, 3);
  }, [snapshot.byStage, dismissed]);

  // Right-side status: tally the worst-case verdict for the panel-head
  // hint. Matches the design's "1 warn" / "stop tx" mode.
  const headRight = useMemo(() => {
    let imm = 0;
    let err = 0;
    let warn = 0;
    for (const [, v] of snapshot.byStage) {
      if (v.severity === AudioChainSeverity.Error) {
        if ((v.flags & VERDICT_FLAG_IMMEDIATE_ACTION) !== 0) imm += 1;
        else err += 1;
      } else if (v.severity === AudioChainSeverity.Warn) {
        warn += 1;
      }
    }
    if (imm > 0) return { text: 'stop tx', cls: 'acm__right acm__right--imm' };
    if (err > 0) return { text: `${err} err`, cls: 'acm__right acm__right--err' };
    if (warn > 0) return { text: `${warn} warn`, cls: 'acm__right acm__right--warn' };
    if (snapshot.byStage.size === 0) return { text: 'awaiting feed', cls: 'acm__right' };
    return { text: 'all stages nominal', cls: 'acm__right' };
  }, [snapshot.byStage]);

  return (
    <div className="acm">
      <div className="acm__head">
        <span className="acm__title">Audio Chain</span>
        <span className="acm__sub">tx · monitor</span>
        <span className={headRight.cls}>{headRight.text}</span>
      </div>
      <div className="acm__pipeline">
        {TILES.map((tile) => {
          const v = snapshot.byStage.get(tile.id);
          // Differentiate "no frame yet" (server hasn't ticked / WS
          // not connected) from "actually OK." Before any snapshot
          // arrives, render tiles in the neutral awaiting-feed state
          // so the operator doesn't read green-without-data as "all
          // good" before the radio has even reported in.
          const hasFeed = snapshot.receivedAt !== 0 && v !== undefined;
          const sev = v?.severity ?? AudioChainSeverity.Ok;
          const immediate =
            v !== undefined &&
            (v.flags & VERDICT_FLAG_IMMEDIATE_ACTION) !== 0;
          const reading = composeReading(tile.id, tx);
          const sevCls = hasFeed ? severityClass(sev, immediate) : '';
          return (
            <div
              key={tile.id}
              className={`acm__tile${tile.groupBreak ? ' acm__tile--group-break' : ''}${
                sevCls ? ` ${sevCls}` : ''
              }`}
            >
              {immediate && <div className="acm__imm-bar" />}
              <div className="acm__tile-head">
                <span className="acm__tile-idx">{tile.idx}</span>
                <span className="acm__tile-name">{tile.name}</span>
              </div>
              <div className="acm__tile-read">
                {reading.main}
                {reading.unit && <span className="acm__unit">{reading.unit}</span>}
              </div>
              <div className="acm__tile-sub">{reading.sub}</div>
              {hasFeed && (
                <div className={pillClass(sev)}>
                  {pillLabel(v ?? { stageId: tile.id, severity: AudioChainSeverity.Ok, flags: 0, message: '', applyLabel: '' })}
                </div>
              )}
            </div>
          );
        })}
      </div>
      <div className="acm__advisories">
        {advisories.length === 0 ? null : (
          advisories.map(({ verdict, tile }) => (
            <AdvisoryRow
              key={`${verdict.stageId}-${verdict.severity}-${verdict.message}`}
              verdict={verdict}
              tile={tile}
              onDismiss={() => dismiss(verdict)}
            />
          ))
        )}
      </div>
    </div>
  );
}
