// SPDX-License-Identifier: GPL-2.0-or-later
//
// Dockable TX fidelity panel. The analyzer itself lives in
// components/TxFidelityAdvisor so the same surface can be reused later in
// compact/mobile contexts without depending on workspace chrome.

import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';

import {
  fetchTxDiagnostics,
  fetchTxFidelityPolicy,
  saveTxFidelityPolicy,
  setCfcConfig,
  setDrive,
  setLevelerMaxGain,
  setMicGain,
  setTxFilter,
  setTxLeveling,
  setTxPhaseRotator,
  type RadioStateDto,
  type RxMode,
  type TxDiagnosticsDto,
  type TxLevelingConfigDto,
  type TxPhaseRotatorConfigDto,
} from '../../api/client';
import {
  levelingChanged,
  phaseRotatorChanged,
  recommendTxAutoTune,
  scoreTxPhaseRotatorCandidate,
  summarizeTxAutoTuneSamples,
  type TxAutoTunePlan,
  type TxAutoTuneSample,
  type TxAutoTuneSettings,
} from '../../audio/tx-auto-tune';
import { AudioChainMeters } from '../../components/AudioChainMeters';
import { TxAudioProfileBar } from '../../components/TxAudioProfileBar';
import { TxFidelityAdvisor } from '../../components/TxFidelityAdvisor';
import { useAudioSuiteStore } from '../../state/audio-suite-store';
import { useConnectionStore } from '../../state/connection-store';
import { useTxStore } from '../../state/tx-store';

const AUTO_TUNE_PASS_SAMPLE_MS = 4_000;
const AUTO_TUNE_MAX_PASSES = 4;
const AUTO_TUNE_POLL_MS = 250;
const AUTO_TUNE_SETTLE_MS = 300;
const AUTO_TUNE_CHAIN_DBFS_FLOOR = -119.5;
const AUTO_TUNE_PHASE_SWEEP_MS = 2_250;
const AUTO_TUNE_PHASE_SETTLE_MS = 180;

type AutoTunePhase = 'idle' | 'sampling' | 'applying' | 'applied' | 'error';

type AutoTuneChainMeters = {
  outputDbfs: number | null;
};

type AutoTuneCounters = {
  vstDegradedBlocks: number | null;
  ingestDroppedFrames: number | null;
  txBlocks: number | null;
  p2TransportFailures: number | null;
  p2QueueFailures: number | null;
};

function controlInputStyle(): CSSProperties {
  return {
    width: '100%',
    minWidth: 0,
    height: 26,
    boxSizing: 'border-box',
    border: '1px solid var(--line)',
    borderRadius: 4,
    background: 'var(--bg-0)',
    color: 'var(--fg-0)',
    fontSize: 11,
    fontWeight: 800,
    padding: '0 6px',
  };
}

function finiteDbfs(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) && v > AUTO_TUNE_CHAIN_DBFS_FLOOR ? v : null;
}

function finiteCount(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) && v >= 0 ? v : null;
}

function finitePositive(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) && v > 0 ? v : null;
}

function counterDelta(prev: number | null, next: number | null): number | null {
  if (next === null) return null;
  if (prev === null) return 0;
  return Math.max(0, next - prev);
}

function sleep(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) {
      reject(new Error('Auto tune canceled'));
      return;
    }
    const timer = setTimeout(resolve, ms);
    signal.addEventListener(
      'abort',
      () => {
        clearTimeout(timer);
        reject(new Error('Auto tune canceled'));
      },
      { once: true },
    );
  });
}

async function fetchAutoTuneChainMeters(signal: AbortSignal): Promise<AutoTuneChainMeters> {
  try {
    const res = await fetch('/api/tx-audio-suite/chain/meters', { signal });
    if (!res.ok) return { outputDbfs: null };
    const body = (await res.json()) as {
      outputDb?: unknown;
      outputDbfs?: unknown;
    };
    return {
      outputDbfs: finiteDbfs(body.outputDbfs ?? body.outputDb),
    };
  } catch {
    return { outputDbfs: null };
  }
}

function autoTuneSampleFromDiagnostics(
  diag: TxDiagnosticsDto,
  chain: AutoTuneChainMeters,
  prev: AutoTuneCounters,
): { sample: TxAutoTuneSample; counters: AutoTuneCounters } {
  const tx = useTxStore.getState();
  const degradedBlocks = finiteCount(diag.vstEngine?.degradedBlocks);
  const droppedFrames = finiteCount(diag.ingest.droppedFrames);
  const txBlocks = finiteCount(diag.ingest.totalTxBlocks);
  const p2TransportFailures = finiteCount(diag.protocol2?.sendFailures);
  const p2QueueFailures = finiteCount(diag.protocol2?.queueWriteFailures);
  const counters = {
    vstDegradedBlocks: degradedBlocks,
    ingestDroppedFrames: droppedFrames,
    txBlocks,
    p2TransportFailures,
    p2QueueFailures,
  };

  return {
    counters,
    sample: {
      micPkDbfs: diag.stage.micPkDbfs ?? finiteDbfs(tx.wdspMicPk) ?? finiteDbfs(tx.micDbfs),
      outPkDbfs: diag.stage.outPkDbfs ?? finiteDbfs(tx.outPk),
      outAvDbfs: diag.stage.outAvDbfs ?? finiteDbfs(tx.outAv),
      compPkDbfs: diag.stage.compPkDbfs ?? finiteDbfs(tx.compPk),
      compAvDbfs: diag.stage.compAvDbfs ?? finiteDbfs(tx.compAv),
      audioSuiteOutputDbfs: chain.outputDbfs,
      alcGrDb: Number.isFinite(diag.stage.alcGrDb) ? diag.stage.alcGrDb : tx.alcGr,
      lvlrGrDb: Number.isFinite(diag.stage.lvlrGrDb) ? diag.stage.lvlrGrDb : tx.lvlrGr,
      cfcGrDb: Number.isFinite(diag.stage.cfcGrDb) ? diag.stage.cfcGrDb : tx.cfcGr,
      swr: Number.isFinite(tx.swr) && tx.swr > 0 ? tx.swr : 1,
      psFeedbackLevel: finitePositive(tx.psFeedbackLevel),
      vstDegradedDelta: counterDelta(prev.vstDegradedBlocks, degradedBlocks),
      ingestDroppedFrameDelta: counterDelta(prev.ingestDroppedFrames, droppedFrames),
      txBlockDelta: counterDelta(prev.txBlocks, txBlocks),
      p2QueuedPackets: finiteCount(diag.protocol2?.queuedPackets),
      p2TransportFailureDelta: counterDelta(prev.p2TransportFailures, p2TransportFailures),
      p2QueueFailureDelta: counterDelta(prev.p2QueueFailures, p2QueueFailures),
    },
  };
}

async function collectTxAutoTuneSamples(
  signal: AbortSignal,
  onProgress: (progress: number) => void,
  durationMs = AUTO_TUNE_PASS_SAMPLE_MS,
): Promise<TxAutoTuneSample[]> {
  const samples: TxAutoTuneSample[] = [];
  const started = Date.now();
  let counters: AutoTuneCounters = {
    vstDegradedBlocks: null,
    ingestDroppedFrames: null,
    txBlocks: null,
    p2TransportFailures: null,
    p2QueueFailures: null,
  };

  while (!signal.aborted) {
    const [diag, chain] = await Promise.all([
      fetchTxDiagnostics(signal),
      fetchAutoTuneChainMeters(signal),
    ]);
    const next = autoTuneSampleFromDiagnostics(diag, chain, counters);
    counters = next.counters;
    samples.push(next.sample);
    const elapsed = Date.now() - started;
    onProgress(Math.min(1, elapsed / durationMs));
    if (elapsed >= durationMs) break;
    await sleep(Math.min(AUTO_TUNE_POLL_MS, durationMs - elapsed), signal);
  }

  return samples;
}

function cfcConfigChanged(a: TxAutoTuneSettings['cfcConfig'], b: TxAutoTuneSettings['cfcConfig']): boolean {
  if (
    a.enabled !== b.enabled ||
    a.postEqEnabled !== b.postEqEnabled ||
    a.preCompDb !== b.preCompDb ||
    a.prePeqDb !== b.prePeqDb ||
    a.bands.length !== b.bands.length
  ) {
    return true;
  }
  return a.bands.some((band, i) => {
    const other = b.bands[i];
    return !other ||
      band.freqHz !== other.freqHz ||
      band.compLevelDb !== other.compLevelDb ||
      band.postGainDb !== other.postGainDb;
  });
}

type PhaseSweepResult = {
  config: TxPhaseRotatorConfigDto;
  action: string | null;
  samples: TxAutoTuneSample[];
};

function clampPhaseCorner(v: number): number {
  return Math.max(20, Math.min(2000, Math.round(v)));
}

function clampPhaseStages(v: number): number {
  return Math.max(1, Math.min(16, Math.round(v)));
}

function normalizedPhaseRotator(cfg: TxPhaseRotatorConfigDto): TxPhaseRotatorConfigDto {
  return {
    enabled: cfg.enabled,
    cornerHz: clampPhaseCorner(cfg.cornerHz),
    stages: clampPhaseStages(cfg.stages),
    reverse: cfg.reverse,
  };
}

function phaseRotatorLabel(cfg: TxPhaseRotatorConfigDto): string {
  return cfg.enabled ? `${cfg.cornerHz} Hz / ${cfg.stages} stages` : 'off';
}

function pushUniquePhaseCandidate(
  list: TxPhaseRotatorConfigDto[],
  cfg: TxPhaseRotatorConfigDto,
): void {
  const next = normalizedPhaseRotator(cfg);
  if (!list.some((x) => !phaseRotatorChanged(x, next))) list.push(next);
}

function buildPhaseRotatorCandidates(
  current: TxPhaseRotatorConfigDto,
  mode: RxMode,
  txFilterLowHz: number,
  txFilterHighHz: number,
  targetSpectralDensity: number,
): TxPhaseRotatorConfigDto[] {
  const candidates: TxPhaseRotatorConfigDto[] = [];
  const cur = normalizedPhaseRotator(current);
  const reverse = cur.reverse;
  pushUniquePhaseCandidate(candidates, cur);
  if (cur.enabled) {
    pushUniquePhaseCandidate(candidates, { ...cur, enabled: false });
  }

  const abs = filterSignedToAbs(mode, txFilterLowHz, txFilterHighHz);
  const passLow = Math.max(20, Math.min(abs.lowAbs || 80, abs.highAbs || 3000));
  const passHigh = Math.max(passLow + 200, abs.highAbs || 3000);
  const width = Math.max(240, passHigh - passLow);
  const target = Math.max(0, Math.min(100, Math.round(targetSpectralDensity)));
  const baseStages = target >= 95 ? 10 : target >= 80 ? 8 : target >= 60 ? 6 : 4;
  const fractions = target >= 90
    ? [0.04, 0.075, 0.12, 0.19, 0.3]
    : [0.035, 0.07, 0.115, 0.18, 0.27];

  for (const fraction of fractions) {
    pushUniquePhaseCandidate(candidates, {
      enabled: true,
      cornerHz: clampPhaseCorner(passLow + width * fraction),
      stages: baseStages,
      reverse,
    });
  }

  const centerCorner = clampPhaseCorner(passLow + width * 0.12);
  pushUniquePhaseCandidate(candidates, {
    enabled: true,
    cornerHz: centerCorner,
    stages: clampPhaseStages(baseStages - 2),
    reverse,
  });
  pushUniquePhaseCandidate(candidates, {
    enabled: true,
    cornerHz: centerCorner,
    stages: clampPhaseStages(baseStages + 2),
    reverse,
  });
  if (cur.enabled) {
    pushUniquePhaseCandidate(candidates, {
      enabled: true,
      cornerHz: clampPhaseCorner(cur.cornerHz * 0.78),
      stages: cur.stages,
      reverse,
    });
    pushUniquePhaseCandidate(candidates, {
      enabled: true,
      cornerHz: clampPhaseCorner(cur.cornerHz * 1.28),
      stages: cur.stages,
      reverse,
    });
  }

  return candidates.slice(0, 9);
}

async function sweepTxPhaseRotator(params: {
  signal: AbortSignal;
  current: TxPhaseRotatorConfigDto;
  baseSamples: TxAutoTuneSample[];
  mode: RxMode;
  txFilterLowHz: number;
  txFilterHighHz: number;
  targetSpectralDensity: number;
  applyState: (state: RadioStateDto) => void;
  setLocal: (cfg: TxPhaseRotatorConfigDto) => void;
  onMessage: (message: string) => void;
}): Promise<PhaseSweepResult | null> {
  const original = normalizedPhaseRotator(params.current);
  const candidates = buildPhaseRotatorCandidates(
    original,
    params.mode,
    params.txFilterLowHz,
    params.txFilterHighHz,
    params.targetSpectralDensity,
  );
  if (candidates.length <= 1) return null;

  const baseStats = summarizeTxAutoTuneSamples(params.baseSamples);
  const baseScore = scoreTxPhaseRotatorCandidate(baseStats, params.targetSpectralDensity);
  if (!baseScore.usable) return null;

  const results: Array<{
    config: TxPhaseRotatorConfigDto;
    samples: TxAutoTuneSample[];
    score: number;
    usable: boolean;
  }> = [
    {
      config: original,
      samples: params.baseSamples,
      score: baseScore.score + 0.25,
      usable: baseScore.usable,
    },
  ];

  let applied = original;
  try {
    for (let i = 0; i < candidates.length; i++) {
      const candidate = candidates[i]!;
      if (!phaseRotatorChanged(candidate, original)) continue;
      params.onMessage(`Balancing phase ${i + 1}/${candidates.length}`);
      params.setLocal(candidate);
      const state = await setTxPhaseRotator(candidate, params.signal);
      params.applyState(state);
      applied = candidate;
      await sleep(AUTO_TUNE_PHASE_SETTLE_MS, params.signal);
      const samples = await collectTxAutoTuneSamples(
        params.signal,
        () => {},
        AUTO_TUNE_PHASE_SWEEP_MS,
      );
      const stats = summarizeTxAutoTuneSamples(samples);
      const scored = scoreTxPhaseRotatorCandidate(stats, params.targetSpectralDensity);
      const simplicityPenalty = candidate.enabled ? candidate.stages * 0.04 : 0;
      results.push({
        config: candidate,
        samples,
        score: scored.score - simplicityPenalty,
        usable: scored.usable,
      });
    }

    const usable = results.filter((r) => r.usable && Number.isFinite(r.score));
    if (usable.length === 0) return null;
    usable.sort((a, b) => b.score - a.score);
    const best = usable[0]!;
    const originalResult = results[0]!;
    const improvement = best.score - originalResult.score;
    const selected =
      phaseRotatorChanged(best.config, original) && improvement >= 1.0
        ? best
        : originalResult;

    if (phaseRotatorChanged(applied, selected.config)) {
      params.setLocal(selected.config);
      const state = await setTxPhaseRotator(selected.config);
      params.applyState(state);
      applied = selected.config;
    }

    if (!phaseRotatorChanged(selected.config, original)) {
      return { config: selected.config, action: null, samples: selected.samples };
    }
    return {
      config: selected.config,
      action: `phase rotator ${phaseRotatorLabel(selected.config)} measured best`,
      samples: selected.samples,
    };
  } catch (err) {
    if (phaseRotatorChanged(applied, original)) {
      try {
        params.setLocal(original);
        const state = await setTxPhaseRotator(original);
        params.applyState(state);
      } catch {
        /* Preserve the original error. */
      }
    }
    throw err;
  }
}

function autoTuneMessage(plan: TxAutoTunePlan): string {
  if (plan.actions.length === 0) return plan.summary;
  return `${plan.summary}: ${plan.actions.join(', ')}`;
}

function autoTuneRunMessage(
  plan: TxAutoTunePlan,
  passCount: number,
  appliedActions: ReadonlyArray<string>,
): string {
  if (appliedActions.length === 0 || plan.blockers.length > 0 || plan.changed) {
    return passCount > 1 ? `${autoTuneMessage(plan)} / ${passCount} passes` : autoTuneMessage(plan);
  }
  return `Auto tune locked after ${passCount} pass${passCount === 1 ? '' : 'es'}: ${appliedActions.length} bounded change${appliedActions.length === 1 ? '' : 's'}`;
}

function controlLabelStyle(): CSSProperties {
  return {
    display: 'grid',
    gap: 3,
    minWidth: 0,
    color: 'var(--fg-2)',
    fontSize: 9,
    fontWeight: 800,
    letterSpacing: '0.08em',
    textTransform: 'uppercase',
  };
}

function AudioSuitePreviewToggle() {
  const previewSupported = useAudioSuiteStore((s) => s.previewSupported);
  const previewEnabled = useAudioSuiteStore((s) => s.previewEnabled);
  const setPreviewEnabled = useAudioSuiteStore((s) => s.setPreviewEnabled);
  const loadPreviewState = useAudioSuiteStore((s) => s.loadPreviewState);
  const openAudioSuite = useAudioSuiteStore((s) => s.open);

  useEffect(() => {
    void loadPreviewState();
  }, [loadPreviewState]);

  return (
    <section
      aria-label="Audio Suite preview"
      style={{
        display: 'grid',
        gridTemplateColumns: 'minmax(0, 1fr) auto auto',
        alignItems: 'center',
        gap: 8,
        minWidth: 0,
        padding: '6px 8px',
        border: '1px solid var(--line)',
        borderRadius: 5,
        background: 'var(--bg-2)',
      }}
    >
      <span
        className="label-xs"
        style={{
          minWidth: 0,
          color: previewEnabled ? 'var(--tx)' : 'var(--fg-2)',
          fontWeight: 900,
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
        }}
      >
        Preview
      </span>
      <button
        type="button"
        aria-label="Open Audio Suite"
        title="Open the Audio Suite window to reorder, preview, and tune chain plugins"
        onClick={() => openAudioSuite()}
        style={{
          height: 24,
          border: '1px solid var(--accent)',
          borderRadius: 4,
          background: 'var(--bg-1)',
          color: 'var(--fg-0)',
          cursor: 'pointer',
          fontSize: 10,
          fontWeight: 900,
          padding: '0 9px',
          textTransform: 'uppercase',
          whiteSpace: 'nowrap',
        }}
      >
        Open Suite
      </button>
      <button
        type="button"
        aria-pressed={previewEnabled}
        aria-label={previewEnabled ? 'Preview on' : 'Preview off'}
        disabled={!previewSupported}
        title={
          previewSupported
            ? previewEnabled
              ? 'Preview is ON - processed TX audio is mixed into your RX playback'
              : 'Preview is OFF - click to hear the full TX chain in your headphones'
            : 'Preview is unavailable in this host mode'
        }
        onClick={() => void setPreviewEnabled(!previewEnabled)}
        style={{
          minWidth: 46,
          height: 24,
          border: '1px solid ' + (previewEnabled ? 'var(--tx)' : 'var(--line)'),
          borderRadius: 4,
          background: previewEnabled ? 'var(--tx)' : 'var(--bg-1)',
          color: previewEnabled ? '#fff' : 'var(--fg-0)',
          cursor: previewSupported ? 'pointer' : 'not-allowed',
          fontSize: 10,
          fontWeight: 900,
          opacity: previewSupported ? 1 : 0.5,
          padding: '0 9px',
          textTransform: 'uppercase',
        }}
      >
        {previewEnabled ? 'ON' : 'OFF'}
      </button>
    </section>
  );
}

function TxFidelityAutoTune({ targetSpectralDensity }: { targetSpectralDensity: number }) {
  const status = useConnectionStore((s) => s.status);
  const applyState = useConnectionStore((s) => s.applyState);
  const setTxLevelingLocal = useConnectionStore((s) => s.setTxLeveling);
  const setTxPhaseRotatorLocal = useConnectionStore((s) => s.setTxPhaseRotator);
  const hydrateTxFromState = useTxStore((s) => s.hydrateFromState);
  const tunOn = useTxStore((s) => s.tunOn);
  const setMicGainDb = useTxStore((s) => s.setMicGainDb);
  const setLevelerMaxGainDb = useTxStore((s) => s.setLevelerMaxGainDb);
  const setDrivePercent = useTxStore((s) => s.setDrivePercent);
  const setCfcConfigLocal = useTxStore((s) => s.setCfcConfig);
  const loadPreviewState = useAudioSuiteStore((s) => s.loadPreviewState);
  const setPreviewEnabled = useAudioSuiteStore((s) => s.setPreviewEnabled);
  const [phase, setPhase] = useState<AutoTunePhase>('idle');
  const [message, setMessage] = useState('Ready / live tune');
  const [progress, setProgress] = useState(0);
  const [lastPlan, setLastPlan] = useState<TxAutoTunePlan | null>(null);
  const [lastRunActions, setLastRunActions] = useState<string[]>([]);
  const abortRef = useRef<AbortController | null>(null);

  useEffect(() => {
    void loadPreviewState();
  }, [loadPreviewState]);

  useEffect(() => {
    return () => {
      abortRef.current?.abort();
    };
  }, []);

  const applyPlan = useCallback(
    async (plan: TxAutoTunePlan, signal: AbortSignal) => {
      const before = useTxStore.getState();
      if (plan.settings.micGainDb !== before.micGainDb) {
        const mic = await setMicGain(plan.settings.micGainDb, signal);
        setMicGainDb(mic.micGainDb);
      }
      if (plan.settings.levelerMaxGainDb !== before.levelerMaxGainDb) {
        const leveler = await setLevelerMaxGain(plan.settings.levelerMaxGainDb, signal);
        setLevelerMaxGainDb(leveler.levelerMaxGainDb);
      }
      if (cfcConfigChanged(before.cfcConfig, plan.settings.cfcConfig)) {
        const state = await setCfcConfig(plan.settings.cfcConfig, signal);
        applyState(state);
        hydrateTxFromState(state);
        setCfcConfigLocal(state.cfc);
      }
      const beforeLeveling = useConnectionStore.getState().txLeveling;
      if (levelingChanged(beforeLeveling, plan.settings.txLeveling)) {
        setTxLevelingLocal(plan.settings.txLeveling);
        const state = await setTxLeveling(plan.settings.txLeveling, signal);
        applyState(state);
        hydrateTxFromState(state);
      }
      const beforePhaseRotator = useConnectionStore.getState().txPhaseRotator;
      if (phaseRotatorChanged(beforePhaseRotator, plan.settings.txPhaseRotator)) {
        setTxPhaseRotatorLocal(plan.settings.txPhaseRotator);
        const state = await setTxPhaseRotator(plan.settings.txPhaseRotator, signal);
        applyState(state);
      }
      if (plan.settings.drivePercent !== before.drivePercent) {
        const drive = await setDrive(plan.settings.drivePercent, signal);
        setDrivePercent(drive.drivePercent);
      }
    },
    [
      applyState,
      hydrateTxFromState,
      setCfcConfigLocal,
      setDrivePercent,
      setLevelerMaxGainDb,
      setMicGainDb,
      setTxLevelingLocal,
      setTxPhaseRotatorLocal,
    ],
  );

  const runAutoTune = useCallback(async () => {
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;
    setLastPlan(null);
    setLastRunActions([]);
    setProgress(0);
    // True only when Auto Tune itself armed the monitor for this run, so the
    // finally block can restore blocked/error runs without disturbing a
    // Preview the operator turned on themselves.
    let startedSilentPreview = false;
    let leaveStartedPreviewOn = false;

    try {
      if (status !== 'Connected') {
        setPhase('error');
        setMessage('Connect radio first');
        return;
      }
      if (useTxStore.getState().tunOn) {
        setPhase('error');
        setMessage('Use voice MOX or Preview');
        return;
      }

      let tx = useTxStore.getState();
      if (!tx.moxOn && !tx.txMonitorEnabled) {
        let audio = useAudioSuiteStore.getState();
        if (!audio.previewSupported) {
          await loadPreviewState();
          audio = useAudioSuiteStore.getState();
        }
        if (!audio.previewSupported) {
          setPhase('error');
          setMessage('Enable Preview or key MOX');
          return;
        }
        // Meter-only: the chain runs so the stage meters animate and we can
        // sample, but the demodulated monitor stays silent — the operator does
        // not hear the preview while Auto Tune works in the background.
        await setPreviewEnabled(true, true);
        startedSilentPreview = true;
        await sleep(250, controller.signal);
        tx = useTxStore.getState();
        if (!tx.moxOn && !tx.txMonitorEnabled) {
          setPhase('error');
          setMessage('Preview did not start');
          return;
        }
      }

      const appliedActions: string[] = [];
      let finalPlan: TxAutoTunePlan | null = null;
      let passCount = 0;
      let phaseSweepCompleted = false;

      for (let pass = 1; pass <= AUTO_TUNE_MAX_PASSES; pass++) {
        passCount = pass;
        setPhase('sampling');
        setMessage(`Tuning ${pass}/${AUTO_TUNE_MAX_PASSES} / talk`);
        const samples = await collectTxAutoTuneSamples(
          controller.signal,
          (p) => {
            const runProgress = (pass - 1 + p) / AUTO_TUNE_MAX_PASSES;
            setProgress(Math.min(0.98, runProgress));
            const remaining = Math.max(0, Math.ceil((1 - p) * (AUTO_TUNE_PASS_SAMPLE_MS / 1000)));
            setMessage(
              remaining > 0
                ? `Tuning ${pass}/${AUTO_TUNE_MAX_PASSES} / ${remaining}s`
                : `Analyzing ${pass}/${AUTO_TUNE_MAX_PASSES}`,
            );
          },
          AUTO_TUNE_PASS_SAMPLE_MS,
        );

        let recommendationSamples = samples;
        let phaseAction: string | null = null;
        if (!phaseSweepCompleted) {
          phaseSweepCompleted = true;
          const connBeforeSweep = useConnectionStore.getState();
          const phaseSweep = await sweepTxPhaseRotator({
            signal: controller.signal,
            current: connBeforeSweep.txPhaseRotator,
            baseSamples: samples,
            mode: connBeforeSweep.mode,
            txFilterLowHz: connBeforeSweep.txFilterLowHz,
            txFilterHighHz: connBeforeSweep.txFilterHighHz,
            targetSpectralDensity,
            applyState,
            setLocal: setTxPhaseRotatorLocal,
            onMessage: setMessage,
          });
          if (phaseSweep?.action) {
            recommendationSamples = phaseSweep.samples;
            phaseAction = phaseSweep.action;
          }
        }

        setMessage(`Analyzing ${pass}/${AUTO_TUNE_MAX_PASSES}`);
        tx = useTxStore.getState();
        const conn = useConnectionStore.getState();
        const audio = useAudioSuiteStore.getState();
        let plan = recommendTxAutoTune(
          {
            micGainDb: tx.micGainDb,
            levelerMaxGainDb: tx.levelerMaxGainDb,
            drivePercent: tx.drivePercent,
            cfcConfig: tx.cfcConfig,
            txLeveling: conn.txLeveling,
            txPhaseRotator: conn.txPhaseRotator,
            targetSpectralDensity,
            keyed: tx.moxOn,
            audioSuiteActive: !audio.masterBypassed && (tx.moxOn || tx.txMonitorEnabled),
            vstActive: audio.processingMode === 'vst' || audio.vstEngineActive,
          },
          recommendationSamples,
        );
        if (phaseAction) {
          const actions = [phaseAction, ...plan.actions];
          plan = {
            ...plan,
            settings: { ...plan.settings, txPhaseRotator: conn.txPhaseRotator },
            actions,
            changed: true,
            summary: `Auto tune applied ${actions.length} bounded change${actions.length === 1 ? '' : 's'}`,
          };
        }
        finalPlan = plan;
        setLastPlan(plan);

        if (plan.changed) {
          setPhase('applying');
          setMessage(`Applying ${pass}/${AUTO_TUNE_MAX_PASSES}`);
          await applyPlan(plan, controller.signal);
          appliedActions.push(...plan.actions);
        }

        if (!plan.changed || plan.blockers.length > 0 || pass >= AUTO_TUNE_MAX_PASSES) break;

        setMessage(`Settling ${pass}/${AUTO_TUNE_MAX_PASSES}`);
        await sleep(AUTO_TUNE_SETTLE_MS, controller.signal);
      }

      if (!finalPlan) {
        throw new Error('Auto tune did not collect samples');
      }
      setLastRunActions(appliedActions);
      leaveStartedPreviewOn = startedSilentPreview && finalPlan.blockers.length === 0;

      setProgress(1);
      setPhase('applied');
      setMessage(
        leaveStartedPreviewOn
          ? `${autoTuneRunMessage(finalPlan, passCount, appliedActions)} / Preview metering stays on`
          : autoTuneRunMessage(finalPlan, passCount, appliedActions),
      );
    } catch (err) {
      if (controller.signal.aborted) return;
      setPhase('error');
      setMessage(err instanceof Error ? err.message : 'Auto tune failed');
    } finally {
      // Keep a successful Auto Tune's meter-only Preview alive so the fidelity
      // gauges can prove the post-tune state. Blocked/error runs restore the
      // prior off-air state.
      if (startedSilentPreview && !leaveStartedPreviewOn) {
        void setPreviewEnabled(false);
      }
      if (abortRef.current === controller) {
        abortRef.current = null;
      }
    }
  }, [
    applyPlan,
    applyState,
    loadPreviewState,
    setPreviewEnabled,
    setTxPhaseRotatorLocal,
    status,
    targetSpectralDensity,
  ]);

  const busy = phase === 'sampling' || phase === 'applying';
  const color =
    phase === 'applied'
      ? 'var(--signal)'
      : phase === 'error'
        ? 'var(--tx)'
        : busy
          ? 'var(--accent)'
          : 'var(--fg-2)';
  const disabled = busy || status !== 'Connected' || tunOn;
  const detailTitle = lastPlan
    ? [
        lastPlan.summary,
        lastRunActions.length ? `Run actions: ${lastRunActions.join(', ')}` : '',
        lastPlan.actions.length ? `Last pass actions: ${lastPlan.actions.join(', ')}` : '',
        lastPlan.blockers.length ? `Blocked: ${lastPlan.blockers.join(', ')}` : '',
      ]
        .filter(Boolean)
        .join(' / ')
    : message;

  return (
    <section
      aria-label="TX fidelity auto tune"
      title={detailTitle}
      style={{
        display: 'grid',
        gap: 6,
        minWidth: 0,
        padding: '7px 8px',
        border: '1px solid var(--line)',
        borderRadius: 5,
        background: 'var(--bg-2)',
      }}
    >
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'minmax(0, 1fr) auto',
          gap: 8,
          alignItems: 'center',
          minWidth: 0,
        }}
      >
        <div style={{ display: 'grid', gap: 2, minWidth: 0 }}>
          <span className="label-xs" style={{ color: 'var(--fg-2)', fontWeight: 900 }}>
            Auto Tune
          </span>
          <span
            className="mono"
            style={{
              minWidth: 0,
              color,
              fontSize: 10,
              fontWeight: 800,
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              whiteSpace: 'nowrap',
            }}
          >
            {message}
          </span>
        </div>
        <button
          type="button"
          aria-label="Auto tune TX fidelity"
          disabled={disabled}
          onClick={() => void runAutoTune()}
          style={{
            minWidth: 74,
            height: 26,
            border: `1px solid ${busy ? 'var(--accent)' : 'var(--line)'}`,
            borderRadius: 4,
            background: busy ? 'var(--accent-soft)' : 'var(--bg-1)',
            color: busy ? 'var(--accent)' : 'var(--fg-0)',
            cursor: disabled ? 'not-allowed' : 'pointer',
            fontSize: 10,
            fontWeight: 900,
            opacity: disabled && !busy ? 0.55 : 1,
            padding: '0 9px',
            textTransform: 'uppercase',
          }}
        >
          {phase === 'applying' ? 'Apply' : phase === 'sampling' ? 'Sampling' : 'Auto'}
        </button>
      </div>
      {busy && (
        <div
          role="progressbar"
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={Math.round(progress * 100)}
          style={{
            height: 4,
            overflow: 'hidden',
            borderRadius: 3,
            background: 'var(--bg-0)',
          }}
        >
          <div
            style={{
              width: `${Math.round(progress * 100)}%`,
              height: '100%',
              background: 'var(--accent)',
              transition: 'width 120ms linear',
            }}
          />
        </div>
      )}
    </section>
  );
}

// Live TX shaping number boxes. These edit LIVE state directly (server-
// authoritative, persisted) and are TRANSIENT relative to any saved profile —
// committing a value never mutates a tx_audio_profiles row. Each box uses the
// controlled-input draft pattern proven in TxFilterPanel: a local string draft
// edited freely (so the field can be cleared/retyped), committed on blur/Enter,
// and resynced from the live store value (mode flip, server reconcile, profile
// apply). Parse + clamp happen at commit, not per keystroke.
function clampNumber(v: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, v));
}

function isSymmetricMode(mode: RxMode): boolean {
  return mode === 'AM' || mode === 'SAM' || mode === 'DSB' || mode === 'FM';
}

// Positive magnitudes the operator types; the server re-signs per mode family
// (RadioService.SignedFilterForMode). Mirrors TxFilterPanel's helpers.
function filterSignedToAbs(mode: RxMode, low: number, high: number): { lowAbs: number; highAbs: number } {
  if (isSymmetricMode(mode)) {
    return { lowAbs: 0, highAbs: Math.max(Math.abs(low), Math.abs(high)) };
  }
  const lo = Math.min(Math.abs(low), Math.abs(high));
  const hi = Math.max(Math.abs(low), Math.abs(high));
  return { lowAbs: lo, highAbs: hi };
}

function filterAbsToSigned(mode: RxMode, lowAbs: number, highAbs: number): { low: number; high: number } {
  const lo = clampNumber(Math.round(lowAbs), 0, 10000);
  const hi = clampNumber(Math.round(highAbs), 0, 10000);
  const [lCap, hCap] = lo <= hi ? [lo, hi] : [hi, lo];
  switch (mode) {
    case 'USB':
    case 'DIGU':
    case 'CWU':
      return { low: lCap, high: hCap };
    case 'LSB':
    case 'DIGL':
    case 'CWL':
      return { low: -hCap, high: -lCap };
    default:
      return { low: -hCap, high: hCap };
  }
}

type NumberBoxProps = {
  label: string;
  ariaLabel: string;
  value: number;
  min: number;
  max: number;
  step: number;
  parse: 'int' | 'float';
  disabled?: boolean;
  onCommit: (v: number) => void;
};

// A single controlled number box. `value` is the live store value; the draft
// resyncs whenever it changes so a freshly applied profile (or server
// reconcile) shows the new value instead of stale draft text.
function NumberBox({ label, ariaLabel, value, min, max, parse, disabled, onCommit }: NumberBoxProps) {
  const [draft, setDraft] = useState<string>(String(value));
  useEffect(() => {
    setDraft(String(value));
  }, [value]);

  const commit = useCallback(() => {
    const parsed = parse === 'int' ? Number.parseInt(draft, 10) : Number.parseFloat(draft);
    if (!Number.isFinite(parsed)) {
      setDraft(String(value));
      return;
    }
    const next = clampNumber(parsed, min, max);
    setDraft(String(next));
    if (next !== value) onCommit(next);
  }, [draft, parse, min, max, value, onCommit]);

  return (
    <label style={controlLabelStyle()}>
      {label}
      <input
        aria-label={ariaLabel}
        // Text + inputMode rather than type="number": a numeric input nukes
        // partial/intermediate states ("-", "1.", "-2.") to "" mid-typing — the
        // source of the janky feel, especially for the negative mic-gain range.
        // We keep the draft as free text, accept only a well-formed signed
        // decimal, and parse/clamp once on commit (blur / Enter).
        type="text"
        inputMode={parse === 'int' && min >= 0 ? 'numeric' : 'decimal'}
        autoComplete="off"
        value={draft}
        disabled={disabled}
        onChange={(e) => {
          const raw = e.currentTarget.value;
          if (raw === '' || /^-?\d*\.?\d*$/.test(raw)) setDraft(raw);
        }}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === 'Enter') e.currentTarget.blur();
        }}
        style={{ ...controlInputStyle(), opacity: disabled ? 0.55 : 1 }}
      />
    </label>
  );
}

// Live TX shaping controls — mic / leveler / decay / low / high — committing
// through the existing live Set* endpoints. Transient vs the stored profile.
function TxLiveShapingControls() {
  const status = useConnectionStore((s) => s.status);
  const mode = useConnectionStore((s) => s.mode);
  const applyState = useConnectionStore((s) => s.applyState);
  const setTxLevelingLocal = useConnectionStore((s) => s.setTxLeveling);
  const setTxPhaseRotatorLocal = useConnectionStore((s) => s.setTxPhaseRotator);
  const txLeveling = useConnectionStore((s) => s.txLeveling);
  const txPhaseRotator = useConnectionStore((s) => s.txPhaseRotator);
  const txFilterLowHz = useConnectionStore((s) => s.txFilterLowHz);
  const txFilterHighHz = useConnectionStore((s) => s.txFilterHighHz);
  const micGainDb = useTxStore((s) => s.micGainDb);
  const levelerMaxGainDb = useTxStore((s) => s.levelerMaxGainDb);
  const hydrateTxFromState = useTxStore((s) => s.hydrateFromState);
  const setMicGainDb = useTxStore((s) => s.setMicGainDb);
  const setLevelerMaxGainDb = useTxStore((s) => s.setLevelerMaxGainDb);
  const [error, setError] = useState<string | null>(null);

  const disabled = status !== 'Connected';
  const filterAbs = filterSignedToAbs(mode, txFilterLowHz, txFilterHighHz);
  const lowDisabled = disabled || isSymmetricMode(mode);

  const commitMic = useCallback(
    (db: number) => {
      void setMicGain(db)
        .then((r) => setMicGainDb(r.micGainDb))
        .catch((err) => setError(err instanceof Error ? err.message : 'Mic set failed'));
    },
    [setMicGainDb],
  );

  const commitLeveler = useCallback(
    (db: number) => {
      void setLevelerMaxGain(db)
        .then((r) => setLevelerMaxGainDb(r.levelerMaxGainDb))
        .catch((err) => setError(err instanceof Error ? err.message : 'Leveler set failed'));
    },
    [setLevelerMaxGainDb],
  );

  const commitLeveling = useCallback(
    (patch: Partial<TxLevelingConfigDto>) => {
      const next = { ...txLeveling, ...patch };
      setTxLevelingLocal(next);
      void setTxLeveling(next)
        .then((state) => {
          applyState(state);
          hydrateTxFromState(state);
        })
        .catch((err) => setError(err instanceof Error ? err.message : 'Leveling set failed'));
    },
    [txLeveling, setTxLevelingLocal, applyState, hydrateTxFromState],
  );

  const commitPhaseRotator = useCallback(
    (patch: Partial<TxPhaseRotatorConfigDto>) => {
      const next = { ...txPhaseRotator, ...patch };
      setTxPhaseRotatorLocal(next);
      void setTxPhaseRotator(next)
        .then(applyState)
        .catch((err) => setError(err instanceof Error ? err.message : 'Phase rotator set failed'));
    },
    [txPhaseRotator, setTxPhaseRotatorLocal, applyState],
  );

  const commitFilter = useCallback(
    (lowAbs: number, highAbs: number) => {
      const { low, high } = filterAbsToSigned(mode, lowAbs, highAbs);
      if (low === txFilterLowHz && high === txFilterHighHz) return;
      useConnectionStore.setState({ txFilterLowHz: low, txFilterHighHz: high });
      void setTxFilter(low, high)
        .then(applyState)
        .catch((err) => setError(err instanceof Error ? err.message : 'Filter set failed'));
    },
    [mode, txFilterLowHz, txFilterHighHz, applyState],
  );

  return (
    <section
      aria-label="TX live shaping"
      style={{
        display: 'grid',
        gap: 7,
        padding: '8px 10px',
        minWidth: 0,
        maxWidth: '100%',
        boxSizing: 'border-box',
        border: '1px solid var(--line)',
        borderRadius: 6,
        background: 'var(--bg-2)',
      }}
    >
      <span
        className="label-xs"
        style={{
          color: 'var(--fg-2)',
          fontSize: 9,
          fontWeight: 900,
          letterSpacing: '0.08em',
          textTransform: 'uppercase',
        }}
      >
        Live TX Shaping
      </span>
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
          gap: 7,
          alignItems: 'end',
          minWidth: 0,
        }}
      >
        <NumberBox
          label="Mic"
          ariaLabel="TX mic gain"
          value={micGainDb}
          min={-40}
          max={10}
          step={1}
          parse="int"
          disabled={disabled}
          onCommit={commitMic}
        />
        <NumberBox
          label="Leveler"
          ariaLabel="TX leveler max gain"
          value={levelerMaxGainDb}
          min={0}
          max={20}
          step={0.5}
          parse="float"
          disabled={disabled}
          onCommit={commitLeveler}
        />
        <NumberBox
          label="Decay"
          ariaLabel="TX leveler decay"
          value={txLeveling.levelerDecayMs}
          min={1}
          max={5000}
          step={5}
          parse="int"
          disabled={disabled}
          onCommit={(v) => commitLeveling({ levelerDecayMs: v })}
        />
        <div />
        <NumberBox
          label="Low"
          ariaLabel="TX filter low cut"
          value={filterAbs.lowAbs}
          min={0}
          max={10000}
          step={10}
          parse="int"
          disabled={lowDisabled}
          onCommit={(v) => commitFilter(v, filterAbs.highAbs)}
        />
        <NumberBox
          label="High"
          ariaLabel="TX filter high cut"
          value={filterAbs.highAbs}
          min={0}
          max={10000}
          step={50}
          parse="int"
          disabled={disabled}
          onCommit={(v) => commitFilter(filterAbs.lowAbs, v)}
        />
        <label style={{ ...controlLabelStyle(), minWidth: 0 }}>
          Phase
          <input
            aria-label="TX phase rotator enabled"
            type="checkbox"
            checked={txPhaseRotator.enabled}
            disabled={disabled}
            onChange={(e) => commitPhaseRotator({ enabled: e.currentTarget.checked })}
            style={{ width: 16, height: 16 }}
          />
        </label>
        <label style={{ ...controlLabelStyle(), minWidth: 0 }}>
          Reverse
          <input
            aria-label="TX phase reverse"
            type="checkbox"
            checked={txPhaseRotator.reverse}
            disabled={disabled}
            onChange={(e) => commitPhaseRotator({ reverse: e.currentTarget.checked })}
            style={{ width: 16, height: 16 }}
          />
        </label>
        <NumberBox
          label="Corner"
          ariaLabel="TX phase rotator corner"
          value={txPhaseRotator.cornerHz}
          min={20}
          max={2000}
          step={5}
          parse="int"
          disabled={disabled || !txPhaseRotator.enabled}
          onCommit={(v) => commitPhaseRotator({ cornerHz: v })}
        />
        <NumberBox
          label="Stages"
          ariaLabel="TX phase rotator stages"
          value={txPhaseRotator.stages}
          min={1}
          max={16}
          step={1}
          parse="int"
          disabled={disabled || !txPhaseRotator.enabled}
          onCommit={(v) => commitPhaseRotator({ stages: v })}
        />
      </div>
      {error && (
        <div
          className="mono"
          role="alert"
          style={{ color: 'var(--tx)', fontSize: 10, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
          title={error}
        >
          {error}
        </div>
      )}
    </section>
  );
}


export function TxFidelityPanel() {
  const policyTouchedRef = useRef(false);
  // The fidelity-policy profileId pointer is retained for compatibility (the
  // server seeds the 'studio-ssb' default); the unified TX Audio Profile dropdown
  // owns profile selection now, so the panel only tracks the spectral-density
  // target here.
  const policyProfileIdRef = useRef<string>('studio-ssb');
  const [targetSpectralDensity, setTargetSpectralDensity] = useState(55);

  useEffect(() => {
    let active = true;
    fetchTxFidelityPolicy()
      .then((policy) => {
        if (!active || policyTouchedRef.current) return;
        policyProfileIdRef.current = policy.profileId || 'studio-ssb';
        setTargetSpectralDensity(policy.targetSpectralDensity);
      })
      .catch(() => {
        /* Keep the built-in default when the policy is unavailable. */
      });
    return () => {
      active = false;
    };
  }, []);

  const updateSpectralDensity = useCallback((spectralDensity: number) => {
    policyTouchedRef.current = true;
    const clamped = Math.max(0, Math.min(100, Math.round(spectralDensity)));
    setTargetSpectralDensity(clamped);
    void saveTxFidelityPolicy({
      profileId: policyProfileIdRef.current,
      targetSpectralDensity: clamped,
    }).catch(() => {
      /* The advisor stays usable even if the preference write fails. */
    });
  }, []);

  return (
    <div
      style={{
        height: '100%',
        boxSizing: 'border-box',
        display: 'grid',
        alignContent: 'start',
        gap: 8,
        minHeight: 0,
        overflowY: 'auto',
        overflowX: 'hidden',
        padding: 10,
        background: 'var(--bg-1)',
      }}
    >
      <TxFidelityAdvisor targetSpectralDensity={targetSpectralDensity} />
      <TxFidelityAutoTune targetSpectralDensity={targetSpectralDensity} />
      <AudioSuitePreviewToggle />
      <AudioChainMeters compact title="Audio Suite Chain" />
      <TxAudioProfileBar compact />
      <TxSpectralDensityControl
        targetSpectralDensity={targetSpectralDensity}
        onChange={updateSpectralDensity}
      />
      <TxLiveShapingControls />
    </div>
  );
}

// Spectral-density target slider — persisted via /api/tx/fidelity-policy (kept).
// Standalone from the profile dropdown; the saved density of an applied profile
// reaches here through the apply path's policy write.
function TxSpectralDensityControl({
  targetSpectralDensity,
  onChange,
}: {
  targetSpectralDensity: number;
  onChange: (density: number) => void;
}) {
  return (
    <section
      aria-label="TX spectral density"
      style={{
        display: 'grid',
        gap: 6,
        padding: '8px 10px',
        minWidth: 0,
        boxSizing: 'border-box',
        border: '1px solid var(--line)',
        borderRadius: 6,
        background: 'var(--bg-2)',
      }}
    >
      <label style={{ ...controlLabelStyle(), minWidth: 0 }}>
        Density
        <span style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
          <input
            aria-label="TX spectral density"
            type="range"
            min={0}
            max={100}
            step={1}
            value={targetSpectralDensity}
            onChange={(e) => onChange(Number(e.target.value))}
            style={{ flex: 1, minWidth: 0 }}
          />
          <button
            type="button"
            onClick={() => onChange(100)}
            title="Max spectral density"
            style={{
              height: 24,
              border: '1px solid var(--line)',
              borderRadius: 4,
              background: targetSpectralDensity >= 100 ? 'var(--accent)' : 'var(--bg-1)',
              color: targetSpectralDensity >= 100 ? 'var(--fg-0)' : 'var(--fg-0)',
              fontSize: 10,
              fontWeight: 900,
              padding: '0 7px',
            }}
          >
            Max
          </button>
          <span className="mono" style={{ width: 24, textAlign: 'right', color: 'var(--fg-0)' }}>
            {targetSpectralDensity}
          </span>
        </span>
      </label>
    </section>
  );
}
