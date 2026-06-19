// SPDX-License-Identifier: GPL-2.0-or-later
//
// Dockable TX fidelity panel. The analyzer itself lives in
// components/TxFidelityAdvisor so the same surface can be reused later in
// compact/mobile contexts without depending on workspace chrome.

import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';

import {
  fetchTxDiagnostics,
  fetchTxFidelityPolicy,
  fetchTxStationProfiles,
  resetTxStationProfile,
  saveTxFidelityPolicy,
  saveTxStationProfile,
  setCfcConfig,
  setDrive,
  setLevelerMaxGain,
  setMicGain,
  type TxDiagnosticsDto,
} from '../../api/client';
import { applyTxStationProfile } from '../../audio/apply-tx-station-profile';
import {
  recommendTxAutoTune,
  type TxAutoTunePlan,
  type TxAutoTuneSample,
  type TxAutoTuneSettings,
} from '../../audio/tx-auto-tune';
import {
  formatTxStationProfileSummary,
  getTxStationProfile,
  isTxStationProfileId,
  mergeTxStationProfileOverrides,
  sanitizeTxStationProfile,
  STUDIO_SSB_PROFILE,
  TX_STATION_PROFILES,
  txStationProfileToDto,
  type TxStationProfile,
  type TxStationProfileId,
} from '../../audio/tx-station-profile';
import { AudioChainMeters } from '../../components/AudioChainMeters';
import { TxFidelityAdvisor } from '../../components/TxFidelityAdvisor';
import { useAudioSuiteStore, type AudioProfileSummary } from '../../state/audio-suite-store';
import { useConnectionStore } from '../../state/connection-store';
import { useTxStore } from '../../state/tx-store';

type ApplyPhase =
  | 'idle'
  | 'pending'
  | 'applying'
  | 'applied'
  | 'saving'
  | 'saved'
  | 'resetting'
  | 'error';

type ActivationReason = 'selected' | 'chain' | 'saved' | 'reset';

const AUTO_TUNE_SAMPLE_MS = 15_000;
const AUTO_TUNE_POLL_MS = 250;
const AUTO_TUNE_CHAIN_DBFS_FLOOR = -119.5;

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
    onProgress(Math.min(1, elapsed / AUTO_TUNE_SAMPLE_MS));
    if (elapsed >= AUTO_TUNE_SAMPLE_MS) break;
    await sleep(Math.min(AUTO_TUNE_POLL_MS, AUTO_TUNE_SAMPLE_MS - elapsed), signal);
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

function autoTuneMessage(plan: TxAutoTunePlan): string {
  if (plan.actions.length === 0) return plan.summary;
  return `${plan.summary}: ${plan.actions.join(', ')}`;
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

function miniMenuOptionStyle(active: boolean): CSSProperties {
  return {
    width: '100%',
    minWidth: 0,
    border: '1px solid ' + (active ? 'var(--accent)' : 'transparent'),
    borderRadius: 3,
    background: active ? 'var(--accent-soft)' : 'transparent',
    color: active ? 'var(--fg-0)' : 'var(--fg-1)',
    cursor: 'pointer',
    fontSize: 10,
    fontWeight: 900,
    overflow: 'hidden',
    padding: '6px 8px',
    textAlign: 'left',
    textOverflow: 'ellipsis',
    textTransform: 'uppercase',
    whiteSpace: 'nowrap',
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
  const hydrateTxFromState = useTxStore((s) => s.hydrateFromState);
  const tunOn = useTxStore((s) => s.tunOn);
  const setMicGainDb = useTxStore((s) => s.setMicGainDb);
  const setLevelerMaxGainDb = useTxStore((s) => s.setLevelerMaxGainDb);
  const setDrivePercent = useTxStore((s) => s.setDrivePercent);
  const setCfcConfigLocal = useTxStore((s) => s.setCfcConfig);
  const loadPreviewState = useAudioSuiteStore((s) => s.loadPreviewState);
  const setPreviewEnabled = useAudioSuiteStore((s) => s.setPreviewEnabled);
  const [phase, setPhase] = useState<AutoTunePhase>('idle');
  const [message, setMessage] = useState('Ready / 15s sample');
  const [progress, setProgress] = useState(0);
  const [lastPlan, setLastPlan] = useState<TxAutoTunePlan | null>(null);
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
    ],
  );

  const runAutoTune = useCallback(async () => {
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;
    setLastPlan(null);
    setProgress(0);
    // True only when Auto Tune itself armed the monitor for this run, so the
    // finally block restores the off-air state and never disturbs a Preview the
    // operator turned on themselves.
    let startedSilentPreview = false;

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

      setPhase('sampling');
      setMessage('Talk now / 15s');
      const samples = await collectTxAutoTuneSamples(controller.signal, (p) => {
        setProgress(p);
        const remaining = Math.max(0, Math.ceil((1 - p) * (AUTO_TUNE_SAMPLE_MS / 1000)));
        setMessage(remaining > 0 ? `Talk now / ${remaining}s` : 'Analyzing...');
      });

      tx = useTxStore.getState();
      const audio = useAudioSuiteStore.getState();
      const plan = recommendTxAutoTune(
        {
          micGainDb: tx.micGainDb,
          levelerMaxGainDb: tx.levelerMaxGainDb,
          drivePercent: tx.drivePercent,
          cfcConfig: tx.cfcConfig,
          targetSpectralDensity,
          keyed: tx.moxOn,
          audioSuiteActive: !audio.masterBypassed && (tx.moxOn || tx.txMonitorEnabled),
          vstActive: audio.processingMode === 'vst' || audio.vstEngineActive,
        },
        samples,
      );
      setLastPlan(plan);

      if (plan.changed) {
        setPhase('applying');
        setMessage('Applying...');
        await applyPlan(plan, controller.signal);
      }

      setProgress(1);
      setPhase('applied');
      setMessage(autoTuneMessage(plan));
    } catch (err) {
      if (controller.signal.aborted) return;
      setPhase('error');
      setMessage(err instanceof Error ? err.message : 'Auto tune failed');
    } finally {
      // Tear down the silent monitor we armed for this run so the radio returns
      // to its off-air state — leaving it on would keep the chain metering (and
      // hold the mic uplink) after Auto Tune is done.
      if (startedSilentPreview) {
        void setPreviewEnabled(false);
      }
      if (abortRef.current === controller) {
        abortRef.current = null;
      }
    }
  }, [applyPlan, loadPreviewState, setPreviewEnabled, status, targetSpectralDensity]);

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
        lastPlan.actions.length ? `Actions: ${lastPlan.actions.join(', ')}` : '',
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

function profileDefaults(): TxStationProfile[] {
  return mergeTxStationProfileOverrides([]);
}

function chooseSuggestedAudioProfileName(
  profile: TxStationProfile,
  audioProfiles: ReadonlyArray<AudioProfileSummary>,
): string {
  const names = audioProfiles
    .filter((audioProfile) => audioProfile.processingMode === profile.audioSuiteRoute)
    .map((audioProfile) => audioProfile.name.trim())
    .filter(Boolean);
  if (names.length === 0 || profile.audioSuiteRoute !== 'vst' || profile.audioSuiteProfileName?.trim()) {
    return '';
  }

  const keywordsByProfile: Record<TxStationProfileId, string[]> = {
    'studio-ssb': ['studio', 'ssb', 'broadcast'],
    essb: ['essb', 'broadcast', 'wide', 'studio'],
    dx: ['dx', 'punch', 'contest', 'pileup'],
  };
  const keywords = keywordsByProfile[profile.id];
  const lowered = names.map((name) => name.toLowerCase());
  for (const keyword of keywords) {
    const match = lowered.findIndex((name) => name.includes(keyword));
    if (match >= 0) return names[match]!;
  }

  return '';
}

function activationPendingMessage(profile: TxStationProfile, reason: ActivationReason): string {
  const action =
    reason === 'saved'
      ? 'saved'
      : reason === 'reset'
        ? 'reset'
        : reason === 'chain'
          ? 'chain selected'
          : 'selected';
  return `${profile.label} ${action} / connect to apply`;
}

function activationSuccessMessage(profile: TxStationProfile, reason: ActivationReason): string {
  const action =
    reason === 'saved'
      ? 'saved and active'
      : reason === 'reset'
        ? 'reset and active'
        : 'active';
  return `${profile.label} ${action} / ${formatTxStationProfileSummary(profile)}`;
}

type TxStationProfilesProps = {
  selectedProfileId: TxStationProfileId;
  onPolicyChange?: (profileId: TxStationProfileId, spectralDensity: number) => void;
  onTargetSpectralDensityChange?: (density: number) => void;
};

function TxStationProfiles({
  selectedProfileId,
  onPolicyChange,
  onTargetSpectralDensityChange,
}: TxStationProfilesProps) {
  const status = useConnectionStore((s) => s.status);
  const mode = useConnectionStore((s) => s.mode);
  const applyState = useConnectionStore((s) => s.applyState);
  const hydrateTxFromState = useTxStore((s) => s.hydrateFromState);
  const setMicGainDb = useTxStore((s) => s.setMicGainDb);
  const setLevelerMaxGainDb = useTxStore((s) => s.setLevelerMaxGainDb);
  const setCfcConfigLocal = useTxStore((s) => s.setCfcConfig);
  const loadAudioProfiles = useAudioSuiteStore((s) => s.loadProfiles);
  const applyAudioProfile = useAudioSuiteStore((s) => s.applyProfile);
  const setAudioProcessingMode = useAudioSuiteStore((s) => s.setProcessingMode);
  const setAudioMasterBypassed = useAudioSuiteStore((s) => s.setMasterBypassed);
  const audioProfiles = useAudioSuiteStore((s) => s.profiles);
  const [profiles, setProfiles] = useState<TxStationProfile[]>(profileDefaults);
  const [phase, setPhase] = useState<ApplyPhase>('idle');
  const [message, setMessage] = useState(formatTxStationProfileSummary(STUDIO_SSB_PROFILE));
  const [profileMenuOpen, setProfileMenuOpen] = useState(false);
  const [audioProfileMenuOpen, setAudioProfileMenuOpen] = useState(false);
  const pendingActivationRef = useRef<{
    profileId: TxStationProfileId;
    reason: ActivationReason;
  } | null>(null);
  const activationSeqRef = useRef(0);

  const selectedProfile = getTxStationProfile(selectedProfileId, profiles);
  const busy = phase === 'applying' || phase === 'saving' || phase === 'resetting';
  const profileSummary = formatTxStationProfileSummary(selectedProfile);
  const displayedMessage = phase === 'idle' ? profileSummary : message;

  useEffect(() => {
    onTargetSpectralDensityChange?.(selectedProfile.spectralDensity);
  }, [onTargetSpectralDensityChange, selectedProfile.id, selectedProfile.spectralDensity]);

  useEffect(() => {
    let active = true;
    fetchTxStationProfiles()
      .then((overrides) => {
        if (!active) return;
        setProfiles(mergeTxStationProfileOverrides(overrides));
      })
      .catch((err) => {
        if (!active) return;
        setPhase('error');
        setMessage(err instanceof Error ? err.message : 'Profile load failed');
      });
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    void loadAudioProfiles();
  }, [loadAudioProfiles]);

  useEffect(() => {
    if (phase === 'idle') {
      setMessage(formatTxStationProfileSummary(getTxStationProfile(selectedProfileId, profiles)));
    }
  }, [phase, profiles, selectedProfileId]);

  const applyStationProfile = useCallback(
    async (profile: TxStationProfile, reason: ActivationReason) => {
      const seq = ++activationSeqRef.current;

      if (status !== 'Connected') {
        pendingActivationRef.current = { profileId: profile.id, reason };
        setPhase('pending');
        setMessage(activationPendingMessage(profile, reason));
        return;
      }

      setPhase('applying');
      setMessage('Applying...');
      try {
        await applyTxStationProfile(profile, {
          mode,
          applyState,
          hydrateTxFromState,
          setMicGainDb,
          setLevelerMaxGainDb,
          setCfcConfigLocal,
          setAudioProcessingMode,
          setAudioMasterBypassed,
          applyAudioProfile,
        });

        if (seq !== activationSeqRef.current) return;
        setPhase('applied');
        setMessage(activationSuccessMessage(profile, reason));
      } catch (err) {
        if (seq !== activationSeqRef.current) return;
        setPhase('error');
        setMessage(err instanceof Error ? err.message : 'Apply failed');
      }
    },
    [
      applyAudioProfile,
      applyState,
      hydrateTxFromState,
      mode,
      setCfcConfigLocal,
      setAudioMasterBypassed,
      setAudioProcessingMode,
      setLevelerMaxGainDb,
      setMicGainDb,
      status,
    ],
  );

  const activateStationProfile = useCallback(
    (profile: TxStationProfile, reason: ActivationReason) => {
      pendingActivationRef.current = null;
      void applyStationProfile(profile, reason);
    },
    [applyStationProfile],
  );

  useEffect(() => {
    if (status !== 'Connected') return;
    const pending = pendingActivationRef.current;
    if (!pending) return;
    pendingActivationRef.current = null;
    const profile = getTxStationProfile(pending.profileId, profiles);
    void applyStationProfile(profile, pending.reason);
  }, [applyStationProfile, profiles, status]);

  function selectProfile(profileId: string) {
    const profile = getTxStationProfile(profileId, profiles);
    onPolicyChange?.(profile.id, profile.spectralDensity);
    setProfileMenuOpen(false);
    setAudioProfileMenuOpen(false);
    activateStationProfile(profile, 'selected');
  }

  function selectAudioProfile(name: string) {
    const savedProfile = audioProfiles.find((profile) => profile.name === name);
    const next = {
      ...selectedProfile,
      audioSuiteProfileName: name,
      ...(savedProfile
        ? {
            audioSuiteRoute: savedProfile.processingMode,
            audioSuiteBypassed: savedProfile.masterBypass,
          }
        : {}),
    };
    updateSelectedProfile(next);
    setAudioProfileMenuOpen(false);
    activateStationProfile(next, 'chain');
  }

  function bindSuggestedAudioProfile(name: string) {
    const savedProfile = audioProfiles.find((profile) => profile.name === name);
    const next = {
      ...selectedProfile,
      audioSuiteRoute: savedProfile?.processingMode ?? 'vst',
      audioSuiteBypassed: savedProfile?.masterBypass ?? false,
      audioSuiteProfileName: name,
    };
    updateSelectedProfile(next);
    setAudioProfileMenuOpen(false);
    activateStationProfile(next, 'chain');
  }

  function updateSelectedProfile(next: TxStationProfile) {
    const fallback = getTxStationProfile(next.id, TX_STATION_PROFILES);
    const sanitized = sanitizeTxStationProfile(txStationProfileToDto(next), fallback);
    setProfiles((prev) => prev.map((profile) => (profile.id === sanitized.id ? sanitized : profile)));
    setPhase('idle');
    setMessage(formatTxStationProfileSummary(sanitized));
  }

  function patchSelectedProfile(patch: Partial<TxStationProfile>) {
    updateSelectedProfile({ ...selectedProfile, ...patch });
  }

  function patchLeveling(patch: Partial<TxStationProfile['txLeveling']>) {
    patchSelectedProfile({
      txLeveling: {
        ...selectedProfile.txLeveling,
        ...patch,
      },
    });
  }

  async function saveSelectedProfile() {
    setPhase('saving');
    setMessage('Saving...');
    try {
      const saved = await saveTxStationProfile(txStationProfileToDto(selectedProfile));
      const fallback = getTxStationProfile(saved.id, TX_STATION_PROFILES);
      const sanitized = sanitizeTxStationProfile(saved, fallback);
      setProfiles((prev) => prev.map((profile) => (profile.id === sanitized.id ? sanitized : profile)));
      if (sanitized.id === selectedProfileId) {
        onPolicyChange?.(sanitized.id, sanitized.spectralDensity);
      }
      activateStationProfile(sanitized, 'saved');
    } catch (err) {
      setPhase('error');
      setMessage(err instanceof Error ? err.message : 'Save failed');
    }
  }

  async function resetSelectedProfile() {
    setPhase('resetting');
    setMessage('Resetting...');
    try {
      await resetTxStationProfile(selectedProfile.id);
      const defaults = profileDefaults();
      const restored = getTxStationProfile(selectedProfile.id, defaults);
      setProfiles((prev) => prev.map((profile) => (profile.id === restored.id ? restored : profile)));
      onPolicyChange?.(restored.id, restored.spectralDensity);
      activateStationProfile(restored, 'reset');
    } catch (err) {
      setPhase('error');
      setMessage(err instanceof Error ? err.message : 'Reset failed');
    }
  }

  const audioProfileNames = audioProfiles.map((profile) => profile.name);
  const selectedAudioProfileName = selectedProfile.audioSuiteProfileName?.trim() ?? '';
  const selectedAudioProfileMissing =
    selectedAudioProfileName.length > 0 &&
    !audioProfileNames.includes(selectedAudioProfileName);
  const selectedAudioProfile = selectedAudioProfileName
    ? audioProfiles.find((profile) => profile.name === selectedAudioProfileName)
    : undefined;
  const suggestedAudioProfileName = chooseSuggestedAudioProfileName(
    selectedProfile,
    audioProfiles,
  );
  const audioProfileNote = selectedAudioProfile
    ? `${selectedAudioProfile.processingMode === 'vst' ? 'VST' : 'Native'} route / ${
        selectedAudioProfile.masterBypass ? 'rack bypass' : 'rack hot'
      } saved in chain profile`
    : 'No chain profile selected; current Audio Suite chain stays active';

  return (
    <section
      aria-label="TX station profile"
      style={{
        display: 'grid',
        gap: 7,
        padding: '8px 10px',
        minWidth: 0,
        maxWidth: '100%',
        boxSizing: 'border-box',
        border: '1px solid var(--line)',
        borderRadius: 6,
        background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
      }}
    >
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'minmax(0, 1fr)',
          gap: 7,
          alignItems: 'end',
          minWidth: 0,
        }}
      >
        <div style={controlLabelStyle()}>
          Station Profile
          <div style={{ position: 'relative', minWidth: 0 }}>
            <button
              type="button"
              aria-label="TX station profile"
              aria-haspopup="listbox"
              aria-expanded={profileMenuOpen}
              disabled={busy}
              title={selectedProfile.applyTitle}
              onClick={() => setProfileMenuOpen((open) => !open)}
              style={{
                display: 'grid',
                gridTemplateColumns: 'minmax(0, 1fr) auto',
                alignItems: 'center',
                gap: 6,
                width: '100%',
                minWidth: 0,
                height: 28,
                boxSizing: 'border-box',
                border: '1px solid var(--accent)',
                borderRadius: 4,
                background: 'var(--bg-1)',
                color: 'var(--fg-0)',
                cursor: busy ? 'not-allowed' : 'pointer',
                fontSize: 11,
                fontWeight: 900,
                padding: '0 8px',
                textTransform: 'uppercase',
              }}
            >
              <span
                className="mono"
                style={{ minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
              >
                {selectedProfile.label}
              </span>
              <span aria-hidden style={{ color: 'var(--fg-2)', fontSize: 10 }}>
                {profileMenuOpen ? '^' : 'v'}
              </span>
            </button>
            {profileMenuOpen && (
              <div
                role="listbox"
                aria-label="TX station profile options"
                style={{
                  position: 'absolute',
                  zIndex: 20,
                  top: 31,
                  left: 0,
                  right: 0,
                  display: 'grid',
                  gap: 2,
                  padding: 4,
                  border: '1px solid var(--accent)',
                  borderRadius: 4,
                  background: 'var(--bg-0)',
                  boxShadow: '0 10px 20px rgba(0, 0, 0, 0.45)',
                }}
              >
                {profiles.map((profile) => {
                  const active = profile.id === selectedProfile.id;
                  return (
                    <button
                      key={profile.id}
                      type="button"
                      role="option"
                      aria-selected={active}
                      onClick={() => selectProfile(profile.id)}
                      style={miniMenuOptionStyle(active)}
                    >
                      {profile.label}
                    </button>
                  );
                })}
              </div>
            )}
          </div>
        </div>
      </div>

      <div
        className="mono"
        style={{
          minWidth: 0,
          color: phase === 'error' ? 'var(--tx)' : 'var(--fg-2)',
          fontSize: 10,
          overflow: 'hidden',
          textOverflow: 'ellipsis',
          whiteSpace: 'nowrap',
        }}
        title={displayedMessage}
      >
        {displayedMessage}
      </div>

      <details
        style={{
          minWidth: 0,
          border: '1px solid var(--line)',
          borderRadius: 4,
          background: 'var(--bg-2)',
        }}
      >
        <summary
          className="label-xs"
          style={{
            color: 'var(--fg-2)',
            cursor: 'pointer',
            fontWeight: 800,
            listStyle: 'none',
            padding: '6px 8px',
          }}
        >
          Edit Profile
        </summary>
        <div style={{ display: 'grid', gap: 7, minWidth: 0, padding: '0 8px 8px' }}>
          <div
            style={{
              display: 'grid',
              gap: 7,
              minWidth: 0,
              padding: '7px 8px',
              border: '1px solid var(--line)',
              borderRadius: 4,
              background: 'var(--bg-1)',
            }}
          >
            <label style={controlLabelStyle()}>
              Chain Profile
              <div style={{ position: 'relative', minWidth: 0 }}>
                <button
                  type="button"
                  aria-label="TX profile audio suite profile"
                  aria-haspopup="listbox"
                  aria-expanded={audioProfileMenuOpen}
                  disabled={busy}
                  onClick={() => setAudioProfileMenuOpen((open) => !open)}
                  style={{
                    display: 'grid',
                    gridTemplateColumns: 'minmax(0, 1fr) auto',
                    alignItems: 'center',
                    gap: 6,
                    width: '100%',
                    minWidth: 0,
                    height: 26,
                    boxSizing: 'border-box',
                    border: '1px solid var(--line)',
                    borderRadius: 4,
                    background: 'var(--bg-0)',
                    color: 'var(--fg-0)',
                    cursor: busy ? 'not-allowed' : 'pointer',
                    fontSize: 10,
                    fontWeight: 800,
                    padding: '0 7px',
                  }}
                >
                  <span
                    className="mono"
                    style={{
                      minWidth: 0,
                      overflow: 'hidden',
                      textOverflow: 'ellipsis',
                      whiteSpace: 'nowrap',
                    }}
                  >
                    {selectedAudioProfileName || 'Use current chain'}
                  </span>
                  <span aria-hidden style={{ color: 'var(--fg-2)', fontSize: 10 }}>
                    {audioProfileMenuOpen ? '^' : 'v'}
                  </span>
                </button>
                {audioProfileMenuOpen && (
                  <div
                    role="listbox"
                    aria-label="TX profile audio suite profile options"
                    style={{
                      position: 'absolute',
                      zIndex: 21,
                      top: 29,
                      left: 0,
                      right: 0,
                      display: 'grid',
                      gap: 2,
                      maxHeight: 140,
                      overflowY: 'auto',
                      padding: 4,
                      border: '1px solid var(--accent)',
                      borderRadius: 4,
                      background: 'var(--bg-0)',
                      boxShadow: '0 10px 20px rgba(0, 0, 0, 0.45)',
                    }}
                  >
                    <button
                      type="button"
                      role="option"
                      aria-selected={!selectedAudioProfileName}
                      onClick={() => selectAudioProfile('')}
                      style={miniMenuOptionStyle(!selectedAudioProfileName)}
                    >
                      Use current chain
                    </button>
                    {selectedAudioProfileMissing && (
                      <button
                        type="button"
                        role="option"
                        aria-selected
                        onClick={() => selectAudioProfile(selectedAudioProfileName)}
                        style={miniMenuOptionStyle(true)}
                      >
                        {selectedAudioProfileName}
                      </button>
                    )}
                    {audioProfiles.map((profile) => (
                      <button
                        key={profile.name}
                        type="button"
                        role="option"
                        aria-selected={profile.name === selectedAudioProfileName}
                        onClick={() => selectAudioProfile(profile.name)}
                        style={miniMenuOptionStyle(profile.name === selectedAudioProfileName)}
                      >
                        {profile.name}
                      </button>
                    ))}
                  </div>
                )}
              </div>
            </label>

            {suggestedAudioProfileName && (
              <div
                style={{
                  display: 'grid',
                  gridTemplateColumns: 'minmax(0, 1fr) auto',
                  gap: 6,
                  alignItems: 'center',
                  minWidth: 0,
                  padding: '5px 6px',
                  border: '1px solid var(--accent-soft)',
                  borderRadius: 4,
                  background: 'var(--bg-2)',
                }}
              >
                <span
                  className="mono"
                  style={{
                    minWidth: 0,
                    color: 'var(--fg-2)',
                    fontSize: 9.5,
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                    whiteSpace: 'nowrap',
                  }}
                  title={`Suggested Audio Suite chain: ${suggestedAudioProfileName}`}
                >
                  Suggested chain: {suggestedAudioProfileName}
                </span>
                <button
                  type="button"
                  disabled={busy}
                  onClick={() => bindSuggestedAudioProfile(suggestedAudioProfileName)}
                  style={{
                    height: 23,
                    border: '1px solid var(--accent)',
                    borderRadius: 4,
                    background: 'var(--bg-1)',
                    color: 'var(--fg-0)',
                    cursor: busy ? 'not-allowed' : 'pointer',
                    fontSize: 10,
                    fontWeight: 900,
                    padding: '0 8px',
                    textTransform: 'uppercase',
                  }}
                >
                  Bind
                </button>
              </div>
            )}

            <div
              className="mono"
              style={{
                color: selectedAudioProfileMissing ? 'var(--power)' : 'var(--fg-3)',
                fontSize: 9.5,
                overflow: 'hidden',
                textOverflow: 'ellipsis',
                whiteSpace: 'nowrap',
              }}
              title={
                selectedAudioProfileMissing
                  ? `Audio Suite profile "${selectedAudioProfileName}" is not currently saved.`
                  : audioProfileNote
              }
            >
              {selectedAudioProfileMissing
                ? `Missing chain profile: ${selectedAudioProfileName}`
                : audioProfileNote}
            </div>
          </div>

          <div
            style={{
              display: 'grid',
              gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
              gap: 7,
              alignItems: 'end',
              minWidth: 0,
            }}
          >
            <label style={{ ...controlLabelStyle(), gridColumn: '1 / -1', minWidth: 0 }}>
              Density
              <span style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
                <input
                  aria-label="TX spectral density"
                  type="range"
                  min={0}
                  max={100}
                  step={1}
                  value={selectedProfile.spectralDensity}
                  onChange={(e) => patchSelectedProfile({ spectralDensity: Number(e.target.value) })}
                  style={{ flex: 1, minWidth: 0 }}
                />
                <button
                  type="button"
                  onClick={() => patchSelectedProfile({ spectralDensity: 100 })}
                  title="Max spectral density"
                  style={{
                    height: 24,
                    border: '1px solid var(--line)',
                    borderRadius: 4,
                    background: selectedProfile.spectralDensity >= 100 ? 'var(--accent)' : 'var(--bg-2)',
                    color: selectedProfile.spectralDensity >= 100 ? '#fff' : 'var(--fg-0)',
                    fontSize: 10,
                    fontWeight: 900,
                    padding: '0 7px',
                  }}
                >
                  Max
                </button>
                <span className="mono" style={{ width: 24, textAlign: 'right', color: 'var(--fg-0)' }}>
                  {selectedProfile.spectralDensity}
                </span>
              </span>
            </label>

            <label style={controlLabelStyle()}>
              Mic
              <input
                aria-label="TX profile mic gain"
                type="number"
                step={0.5}
                min={-40}
                max={10}
                value={selectedProfile.micGainDb}
                onChange={(e) => patchSelectedProfile({ micGainDb: Number(e.target.value) })}
                style={controlInputStyle()}
              />
            </label>
            <label style={controlLabelStyle()}>
              Leveler
              <input
                aria-label="TX profile leveler max gain"
                type="number"
                step={0.5}
                min={0}
                max={20}
                value={selectedProfile.levelerMaxGainDb}
                onChange={(e) => patchSelectedProfile({ levelerMaxGainDb: Number(e.target.value) })}
                style={controlInputStyle()}
              />
            </label>
            <label style={controlLabelStyle()}>
              Decay
              <input
                aria-label="TX profile leveler decay"
                type="number"
                step={5}
                min={1}
                max={5000}
                value={selectedProfile.txLeveling.levelerDecayMs}
                onChange={(e) => patchLeveling({ levelerDecayMs: Number(e.target.value) })}
                style={controlInputStyle()}
              />
            </label>
            <label style={controlLabelStyle()}>
              Low
              <input
                aria-label="TX profile low cut"
                type="number"
                step={10}
                min={20}
                max={600}
                value={selectedProfile.lowCutHz}
                onChange={(e) => patchSelectedProfile({ lowCutHz: Number(e.target.value) })}
                style={controlInputStyle()}
              />
            </label>
            <label style={controlLabelStyle()}>
              High
              <input
                aria-label="TX profile high cut"
                type="number"
                step={50}
                min={1500}
                max={6000}
                value={selectedProfile.highCutHz}
                onChange={(e) => patchSelectedProfile({ highCutHz: Number(e.target.value) })}
                style={controlInputStyle()}
              />
            </label>
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 6 }}>
            <button
              type="button"
              disabled={busy}
              onClick={saveSelectedProfile}
              style={{
                border: '1px solid var(--line)',
                borderRadius: 4,
                background: phase === 'saved' ? 'var(--signal)' : 'var(--bg-2)',
                color: phase === 'saved' ? '#fff' : 'var(--fg-0)',
                cursor: busy ? 'not-allowed' : 'pointer',
                fontSize: 10,
                fontWeight: 800,
                padding: '5px 7px',
                textTransform: 'uppercase',
              }}
            >
              {phase === 'saving' ? 'Saving' : 'Save'}
            </button>
            <button
              type="button"
              disabled={busy}
              onClick={resetSelectedProfile}
              style={{
                border: '1px solid var(--line)',
                borderRadius: 4,
                background: 'var(--bg-2)',
                color: 'var(--fg-1)',
                cursor: busy ? 'not-allowed' : 'pointer',
                fontSize: 10,
                fontWeight: 800,
                padding: '5px 7px',
                textTransform: 'uppercase',
              }}
            >
              {phase === 'resetting' ? 'Resetting' : 'Reset'}
            </button>
          </div>
        </div>
      </details>
    </section>
  );
}

export function TxFidelityPanel() {
  const policyTouchedRef = useRef(false);
  const [selectedProfileId, setSelectedProfileId] = useState<TxStationProfileId>(
    STUDIO_SSB_PROFILE.id,
  );
  const [targetSpectralDensity, setTargetSpectralDensity] = useState(
    STUDIO_SSB_PROFILE.spectralDensity,
  );

  useEffect(() => {
    let active = true;
    fetchTxFidelityPolicy()
      .then((policy) => {
        if (!active || policyTouchedRef.current) return;
        const profileId = isTxStationProfileId(policy.profileId)
          ? policy.profileId
          : STUDIO_SSB_PROFILE.id;
        setSelectedProfileId(profileId);
        setTargetSpectralDensity(policy.targetSpectralDensity);
      })
      .catch(() => {
        /* Keep the built-in Studio SSB default when the policy is unavailable. */
      });
    return () => {
      active = false;
    };
  }, []);

  const updatePolicy = useCallback((profileId: TxStationProfileId, spectralDensity: number) => {
    policyTouchedRef.current = true;
    setSelectedProfileId(profileId);
    setTargetSpectralDensity(spectralDensity);
    void saveTxFidelityPolicy({
      profileId,
      targetSpectralDensity: spectralDensity,
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
      <TxStationProfiles
        selectedProfileId={selectedProfileId}
        onPolicyChange={updatePolicy}
        onTargetSpectralDensityChange={setTargetSpectralDensity}
      />
    </div>
  );
}
