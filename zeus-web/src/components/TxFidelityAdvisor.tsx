// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';

import { fetchTxDiagnostics, type TxDiagnosticsDto } from '../api/client';
import { analyzeTxFidelity } from '../audio/tx-fidelity';
import { useAudioSuiteStore } from '../state/audio-suite-store';
import { useTxStore } from '../state/tx-store';
import { startEfficientPolling } from '../util/efficient-polling';

const CHAIN_METER_POLL_MS = 250;
const TX_DIAG_POLL_MS = 500;
const HIDDEN_POLL_MS = false;
const CHAIN_DBFS_FLOOR = -119.5;

type ChainMetersDto = {
  inputDb?: unknown;
  outputDb?: unknown;
  inputDbfs?: unknown;
  outputDbfs?: unknown;
};

type ChainMeterSnapshot = {
  inputDbfs: number | null;
  outputDbfs: number | null;
};

const EMPTY_CHAIN_METERS: ChainMeterSnapshot = {
  inputDbfs: null,
  outputDbfs: null,
};

type TxHealthSnapshot = {
  vstDegradedBlocks: number | null;
  vstDegradedDelta: number | null;
  ingestDroppedFrames: number | null;
  ingestDroppedFrameDelta: number | null;
  p2QueuedPackets: number | null;
  p2TransportFailures: number | null;
  p2TransportFailureDelta: number | null;
  p2QueueWriteFailures: number | null;
  p2QueueFailureDelta: number | null;
  micUplinkStatus: string | null;
};

const EMPTY_TX_HEALTH: TxHealthSnapshot = {
  vstDegradedBlocks: null,
  vstDegradedDelta: null,
  ingestDroppedFrames: null,
  ingestDroppedFrameDelta: null,
  p2QueuedPackets: null,
  p2TransportFailures: null,
  p2TransportFailureDelta: null,
  p2QueueWriteFailures: null,
  p2QueueFailureDelta: null,
  micUplinkStatus: null,
};

function normalizeChainDb(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) && v > CHAIN_DBFS_FLOOR ? v : null;
}

function normalizeChainMeters(body: ChainMetersDto): ChainMeterSnapshot {
  return {
    inputDbfs: normalizeChainDb(body.inputDbfs ?? body.inputDb),
    outputDbfs: normalizeChainDb(body.outputDbfs ?? body.outputDb),
  };
}

function sameChainMeters(a: ChainMeterSnapshot, b: ChainMeterSnapshot): boolean {
  return Object.is(a.inputDbfs, b.inputDbfs) && Object.is(a.outputDbfs, b.outputDbfs);
}

function countOrNull(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) && v >= 0 ? v : null;
}

function delta(prev: number | null, next: number | null): number | null {
  if (next === null) return null;
  if (prev === null) return 0;
  return Math.max(0, next - prev);
}

function normalizeTxHealth(diag: TxDiagnosticsDto, prev: TxHealthSnapshot): TxHealthSnapshot {
  const degraded = countOrNull(diag.vstEngine?.degradedBlocks);
  const dropped = countOrNull(diag.ingest.droppedFrames);
  const queued = countOrNull(diag.protocol2?.queuedPackets);
  const transportFailures = countOrNull(diag.protocol2?.sendFailures);
  const queueFailures = countOrNull(diag.protocol2?.queueWriteFailures);
  return {
    vstDegradedBlocks: degraded,
    vstDegradedDelta: delta(prev.vstDegradedBlocks, degraded),
    ingestDroppedFrames: dropped,
    ingestDroppedFrameDelta: delta(prev.ingestDroppedFrames, dropped),
    p2QueuedPackets: queued,
    p2TransportFailures: transportFailures,
    p2TransportFailureDelta: delta(prev.p2TransportFailures, transportFailures),
    p2QueueWriteFailures: queueFailures,
    p2QueueFailureDelta: delta(prev.p2QueueWriteFailures, queueFailures),
    micUplinkStatus: diag.micUplink.status,
  };
}

function sameTxHealth(a: TxHealthSnapshot, b: TxHealthSnapshot): boolean {
  return (
    Object.is(a.vstDegradedBlocks, b.vstDegradedBlocks) &&
    Object.is(a.vstDegradedDelta, b.vstDegradedDelta) &&
    Object.is(a.ingestDroppedFrames, b.ingestDroppedFrames) &&
    Object.is(a.ingestDroppedFrameDelta, b.ingestDroppedFrameDelta) &&
    Object.is(a.p2QueuedPackets, b.p2QueuedPackets) &&
    Object.is(a.p2TransportFailures, b.p2TransportFailures) &&
    Object.is(a.p2TransportFailureDelta, b.p2TransportFailureDelta) &&
    Object.is(a.p2QueueWriteFailures, b.p2QueueWriteFailures) &&
    Object.is(a.p2QueueFailureDelta, b.p2QueueFailureDelta) &&
    Object.is(a.micUplinkStatus, b.micUplinkStatus)
  );
}

function stateColor(state: string): string {
  if (state === 'sweet' || state === 'monitor') return 'var(--signal)';
  if (state === 'clip' || state === 'hot') return 'var(--tx)';
  if (state === 'under') return 'var(--power)';
  return 'var(--fg-2)';
}

function actionColor(tone: string, fallback: string): string {
  if (tone === 'protect') return 'var(--tx)';
  if (tone === 'reduce') return 'var(--power)';
  if (tone === 'raise') return 'var(--accent)';
  return fallback;
}

function metricColor(status: string): string {
  if (status === 'met') return 'var(--signal)';
  if (status === 'bad') return 'var(--tx)';
  if (status === 'warn') return 'var(--power)';
  return 'var(--fg-3)';
}

function metricBg(status: string): string {
  if (status === 'met') return 'rgba(67, 181, 129, 0.12)';
  if (status === 'bad') return 'rgba(230, 58, 43, 0.12)';
  if (status === 'warn') return 'rgba(245, 166, 35, 0.12)';
  return 'var(--bg-1)';
}

type TxFidelityAdvisorProps = {
  targetSpectralDensity?: number;
};

export function TxFidelityAdvisor(props: TxFidelityAdvisorProps) {
  const { targetSpectralDensity } = props;
  const moxOn = useTxStore((s) => s.moxOn);
  const tunOn = useTxStore((s) => s.tunOn);
  const txMonitorEnabled = useTxStore((s) => s.txMonitorEnabled);
  const micDbfs = useTxStore((s) => s.micDbfs);
  const wdspMicPk = useTxStore((s) => s.wdspMicPk);
  const micAv = useTxStore((s) => s.micAv);
  const lvlrGr = useTxStore((s) => s.lvlrGr);
  const cfcGr = useTxStore((s) => s.cfcGr);
  const compPk = useTxStore((s) => s.compPk);
  const compAv = useTxStore((s) => s.compAv);
  const alcGr = useTxStore((s) => s.alcGr);
  const outPk = useTxStore((s) => s.outPk);
  const outAv = useTxStore((s) => s.outAv);
  const swr = useTxStore((s) => s.swr);
  const psEnabled = useTxStore((s) => s.psEnabled);
  const psCorrecting = useTxStore((s) => s.psCorrecting);
  const psFeedbackLevel = useTxStore((s) => s.psFeedbackLevel);
  const psCalState = useTxStore((s) => s.psCalState);
  const psCalibrationStalled = useTxStore((s) => s.psCalibrationStalled);
  const audioSuiteMode = useAudioSuiteStore((s) => s.processingMode);
  const audioSuiteMasterBypassed = useAudioSuiteStore((s) => s.masterBypassed);
  const vstEngineActive = useAudioSuiteStore((s) => s.vstEngineActive);
  const loadMasterBypassFromServer = useAudioSuiteStore((s) => s.loadMasterBypassFromServer);
  const loadProcessingModeFromServer = useAudioSuiteStore((s) => s.loadProcessingModeFromServer);
  const [chainMeters, setChainMeters] = useState<ChainMeterSnapshot>(EMPTY_CHAIN_METERS);
  const [txHealth, setTxHealth] = useState<TxHealthSnapshot>(EMPTY_TX_HEALTH);
  const shouldPollChainMeters = !tunOn && (moxOn || txMonitorEnabled);
  const shouldPollTxHealth = !tunOn && (moxOn || txMonitorEnabled || audioSuiteMode === 'vst' || vstEngineActive);

  useEffect(() => {
    void loadMasterBypassFromServer();
    void loadProcessingModeFromServer();
  }, [loadMasterBypassFromServer, loadProcessingModeFromServer]);

  useEffect(() => {
    if (!shouldPollChainMeters || typeof fetch !== 'function') {
      setChainMeters((prev) => (sameChainMeters(prev, EMPTY_CHAIN_METERS) ? prev : EMPTY_CHAIN_METERS));
      return;
    }

    return startEfficientPolling(
      async (signal) => {
        const res = await fetch('/api/tx-audio-suite/chain/meters', { signal });
        if (res.ok) {
          const body = (await res.json()) as ChainMetersDto;
          const next = normalizeChainMeters(body);
          setChainMeters((prev) => (sameChainMeters(prev, next) ? prev : next));
        }
      },
      {
        intervalMs: CHAIN_METER_POLL_MS,
        hiddenIntervalMs: HIDDEN_POLL_MS,
        onError: () => {
          /* transient meter read failure; keep the last chain reading */
        },
      },
    );
  }, [shouldPollChainMeters]);

  useEffect(() => {
    if (!shouldPollTxHealth) {
      setTxHealth((prev) => (prev === EMPTY_TX_HEALTH ? prev : EMPTY_TX_HEALTH));
      return;
    }

    return startEfficientPolling(
      async (signal) => {
        const diag = await fetchTxDiagnostics(signal);
        setTxHealth((prev) => {
          const next = normalizeTxHealth(diag, prev);
          return sameTxHealth(prev, next) ? prev : next;
        });
      },
      {
        intervalMs: TX_DIAG_POLL_MS,
        hiddenIntervalMs: HIDDEN_POLL_MS,
        onError: () => {
          /* transient diagnostic read failure; keep the previous health state */
        },
      },
    );
  }, [shouldPollTxHealth]);

  const snapshot = {
    moxOn,
    tunOn,
    txMonitorEnabled,
    micDbfs,
    wdspMicPk,
    micAv,
    lvlrGr,
    cfcGr,
    compPk,
    compAv,
    alcGr,
    outPk,
    outAv,
    swr,
    psEnabled,
    psCorrecting,
    psFeedbackLevel,
    psCalState,
    psCalibrationStalled,
    targetSpectralDensity,
    audioSuiteInputDbfs: chainMeters.inputDbfs ?? undefined,
    audioSuiteOutputDbfs: chainMeters.outputDbfs ?? undefined,
    audioSuiteMode,
    audioSuiteBypassed: audioSuiteMasterBypassed,
    vstEngineActive,
    vstDegradedDelta: txHealth.vstDegradedDelta,
    ingestDroppedFrameDelta: txHealth.ingestDroppedFrameDelta,
    p2QueuedPackets: txHealth.p2QueuedPackets,
    p2TransportFailureDelta: txHealth.p2TransportFailureDelta,
    p2QueueFailureDelta: txHealth.p2QueueFailureDelta,
    micUplinkStatus: txHealth.micUplinkStatus,
  };
  const analysis = analyzeTxFidelity(snapshot);
  const color = stateColor(analysis.state);
  const nextColor = actionColor(analysis.actionTone, color);
  const score = analysis.score > 0 ? `${analysis.score}` : '--';

  return (
    <section
      aria-label="TX fidelity advisor"
      title={analysis.detail}
      style={{
        display: 'grid',
        gridTemplateColumns: 'minmax(0, 1fr)',
        gap: 7,
        alignItems: 'start',
        padding: '9px 10px 10px',
        border: '1px solid var(--line-strong)',
        borderRadius: 'var(--r-lg)',
        background: 'linear-gradient(180deg, var(--bg-2), var(--panel-bot))',
        boxShadow: `inset 0 1px 0 var(--panel-hl-top), inset 3px 0 0 ${color}`,
        minWidth: 0,
      }}
    >
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'minmax(0, 1fr) auto',
          gridTemplateAreas: "'status score' 'detail detail' 'next next'",
          gap: '4px 8px',
          alignItems: 'center',
          minWidth: 0,
        }}
      >
        <div
          style={{
            gridArea: 'status',
            display: 'flex',
            gap: 6,
            alignItems: 'center',
            flexWrap: 'wrap',
            minWidth: 0,
            rowGap: 2,
          }}
        >
          <span
            style={{
              color: 'var(--fg-0)',
              fontWeight: 900,
              fontSize: 12,
              flex: '0 1 auto',
              lineHeight: 1.1,
              minWidth: 0,
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
          >
            {analysis.label}
          </span>
          <span
            className="label-xs"
            style={{
              flexShrink: 0,
              padding: '1px 5px',
              border: `1px solid ${color}`,
              borderRadius: 'var(--r-sm)',
              color,
              background: 'var(--bg-1)',
              fontWeight: 900,
            }}
          >
            FIDELITY
          </span>
        </div>
        <div
          className="mono"
          style={{
            gridArea: 'score',
            minWidth: 42,
            height: 22,
            display: 'grid',
            placeItems: 'center',
            boxSizing: 'border-box',
            padding: '0 7px',
            border: `1px solid ${color}`,
            borderRadius: 'var(--r-md)',
            color,
            background: 'var(--bg-1)',
            fontSize: 11,
            fontWeight: 900,
          }}
        >
          {score}
        </div>
        <div
          style={{
            gridArea: 'detail',
            color: 'var(--fg-2)',
            fontSize: 11,
            lineHeight: 1.25,
            minHeight: '2.5em',
            maxHeight: '2.5em',
            overflow: 'hidden',
            overflowWrap: 'break-word',
            whiteSpace: 'normal',
          }}
        >
          {analysis.detail}
        </div>
        <div
          className="mono"
          title={`NEXT ${analysis.recommendation}`}
          style={{
            gridArea: 'next',
            boxSizing: 'border-box',
            padding: '4px 6px',
            border: '1px solid var(--line)',
            borderRadius: 'var(--r-md)',
            background: 'var(--bg-1)',
            color: nextColor,
            fontSize: 10,
            fontWeight: 900,
            lineHeight: 1.2,
            minHeight: 'calc(2.4em + 8px)',
            maxHeight: 'calc(2.4em + 8px)',
            overflow: 'hidden',
            overflowWrap: 'break-word',
            whiteSpace: 'normal',
          }}
        >
          NEXT {analysis.recommendation}
        </div>
      </div>
      <div
        className="mono"
        style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(auto-fit, minmax(104px, 1fr))',
          gap: 5,
          color: 'var(--fg-2)',
          fontSize: 10,
          lineHeight: 1.25,
          minWidth: 0,
        }}
      >
        {analysis.tuningMetrics.map((m) => {
          const mColor = metricColor(m.status);
          return (
            <span
              key={m.id}
              data-testid={`tx-fidelity-metric-${m.id}`}
              data-status={m.status}
              aria-label={`${m.label} ${m.value}. Target ${m.target}. ${m.detail}`}
              title={`${m.label} target ${m.target}: ${m.detail}`}
              style={{
                minWidth: 0,
                boxSizing: 'border-box',
                display: 'flex',
                justifyContent: 'space-between',
                gap: 5,
                alignItems: 'center',
                whiteSpace: 'nowrap',
                overflow: 'hidden',
                padding: '3px 5px',
                border: `1px solid ${mColor}`,
                borderRadius: 'var(--r-sm)',
                background: metricBg(m.status),
                color: mColor,
              }}
            >
              <span style={{ fontWeight: 900, overflow: 'hidden', textOverflow: 'ellipsis' }}>{m.label}</span>
              <span style={{ color: mColor, overflow: 'hidden', textOverflow: 'ellipsis' }}>{m.value}</span>
            </span>
          );
        })}
      </div>
    </section>
  );
}
