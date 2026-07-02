// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// "Download VST Engine" — the new-operator one-click path to working VST
// processing, modelled on DownloadAudioSuiteButton. The out-of-process VST
// engine (upstream KlayaR/VSTHost, run headless) is fetched from its latest
// release and staged by the server; it is never bundled (GPLv3 isolation — see
// Zeus.Server.Hosting/VstEngineInstaller.cs). One click downloads, installs,
// and then CONFIGURES so the operator can use it immediately. Visible only while
// no engine is present (or an install is in flight / failed); it disappears once
// the engine is ready.
//
// The `route` prop decides the configure step: 'tx' switches the TX processing
// mode to VST (classic single-click TX flow); 'rx' leaves TX alone and just
// refreshes the shared engine's RX view, so an operator installing purely for
// RX VST doesn't get their TX route silently switched.

import { useAudioSuiteStore } from '../state/audio-suite-store';

type StageState = 'pending' | 'active' | 'ok' | 'error';

type InstallPhase =
  | 'idle'
  | 'downloading'
  | 'extracting'
  | 'staging'
  | 'configuring'
  | 'done'
  | 'failed';

// The three operator-meaningful stages, in order. Each maps to one or more of
// the server/client phases reported through the store.
const STAGES: ReadonlyArray<{ key: string; label: string; phases: InstallPhase[] }> = [
  { key: 'download', label: 'Download engine', phases: ['downloading'] },
  { key: 'install', label: 'Install engine', phases: ['extracting', 'staging'] },
  { key: 'configure', label: 'Configure VST mode', phases: ['configuring'] },
];

const PHASE_ORDER: Record<InstallPhase, number> = {
  idle: -1,
  downloading: 0,
  extracting: 1,
  staging: 1,
  configuring: 2,
  done: 3,
  failed: 99,
};

function stageState(stageIndex: number, phase: InstallPhase): StageState {
  if (phase === 'done') return 'ok';
  if (phase === 'failed') {
    // The active stage at failure time is the first not-yet-completed one.
    return 'pending';
  }
  const current = PHASE_ORDER[phase];
  const stagePeak = Math.max(...STAGES[stageIndex]!.phases.map((p) => PHASE_ORDER[p]));
  if (current > stagePeak) return 'ok';
  if (current >= Math.min(...STAGES[stageIndex]!.phases.map((p) => PHASE_ORDER[p]))) return 'active';
  return 'pending';
}

function dot(state: StageState): { color: string; glow: string; symbol: string } {
  switch (state) {
    case 'active': return { color: 'var(--accent)', glow: 'rgba(74, 158, 255, 0.55)', symbol: '…' };
    case 'ok':     return { color: 'var(--accent)', glow: 'rgba(74, 158, 255, 0.55)', symbol: '✓' };
    case 'error':  return { color: 'var(--tx)',     glow: 'rgba(230, 58, 43, 0.55)',  symbol: '!' };
    case 'pending':
    default:       return { color: 'var(--fg-3)',   glow: 'transparent',              symbol: '·' };
  }
}

export function DownloadVstEngineButton({
  route = 'tx',
}: {
  route?: 'tx' | 'rx';
} = {}) {
  // Both TX and RX check the same shared engine on disk (VstEngineController
  // .FindEngineExe), so their availability flags reflect the same file. Pick
  // the route-specific flag so RX doesn't wait on a TX processing-mode fetch
  // it doesn't otherwise trigger.
  const engineAvailable = useAudioSuiteStore((s) =>
    route === 'rx' ? s.rxVstEngineAvailable : s.vstEngineAvailable,
  );
  const install = useAudioSuiteStore((s) => s.vstEngineInstall);
  const installVstEngine = useAudioSuiteStore((s) => s.installVstEngine);

  const phase = install.phase as InstallPhase;
  const busy =
    phase === 'downloading' ||
    phase === 'extracting' ||
    phase === 'staging' ||
    phase === 'configuring';
  const failed = phase === 'failed';
  const done = phase === 'done';

  // Hide once the engine is installed and we're not mid-/just-finished an
  // install — there's nothing to download. Stays visible through the "ready"
  // confirmation and any failure (so the operator can retry).
  if (engineAvailable && !busy && !failed && !done) return null;

  const label = busy
    ? `Installing… ${install.percent}%`
    : failed
      ? 'Retry VST Engine'
      : done
        ? 'VST Engine Ready'
        : 'Download VST Engine';

  const tooltip =
    route === 'rx'
      ? 'Download and install the out-of-process VST engine so RX audio can run through VST plugins (installs once; shared with TX)'
      : 'Download, install, and enable the out-of-process VST engine so TX audio can run through VST plugins';

  const showPanel = busy || failed || done;

  return (
    <>
      <button
        type="button"
        className="btn sm active"
        disabled={busy || done}
        onClick={() => {
          if (!busy && !done) {
            void installVstEngine('/api/tx-audio-suite/vst-engine/install', route);
          }
        }}
        title={tooltip}
        style={{ whiteSpace: 'nowrap' }}
      >
        {label}
      </button>

      {showPanel && (
        <div
          role="status"
          aria-live="polite"
          style={{
            marginTop: 8,
            padding: '10px 12px',
            background: 'var(--bg-1)',
            border: '1px solid var(--line)',
            borderRadius: 6,
            fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
            fontSize: 11,
            color: 'var(--fg-1)',
            display: 'flex',
            flexDirection: 'column',
            gap: 6,
            width: '100%',
          }}
        >
          <div
            style={{
              display: 'flex',
              justifyContent: 'space-between',
              alignItems: 'baseline',
              color: 'var(--fg-2)',
              letterSpacing: 0.8,
              textTransform: 'uppercase',
            }}
          >
            <span>VST engine install</span>
            <span style={{ fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)' }}>
              {done ? 'ready' : failed ? 'failed' : `${install.percent}%`}
            </span>
          </div>

          {STAGES.map((stage, i) => {
            const st = failed ? ('error' as StageState) : stageState(i, phase);
            const d = dot(st);
            return (
              <div
                key={stage.key}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: 8,
                  fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                }}
              >
                <span
                  aria-hidden
                  style={{
                    width: 16,
                    height: 16,
                    borderRadius: 3,
                    display: 'inline-flex',
                    alignItems: 'center',
                    justifyContent: 'center',
                    background: 'var(--bg-2)',
                    color: d.color,
                    border: '1px solid var(--line)',
                    boxShadow: d.glow !== 'transparent' ? `0 0 6px ${d.glow}` : 'none',
                    fontSize: 11,
                    lineHeight: 1,
                    fontWeight: 600,
                  }}
                >
                  {d.symbol}
                </span>
                <span style={{ flex: 1, color: 'var(--fg-0)' }}>{stage.label}</span>
              </div>
            );
          })}

          {install.message && (
            <div style={{ color: failed ? 'var(--tx)' : 'var(--fg-2)', fontSize: 10 }}>
              {install.message}
            </div>
          )}
        </div>
      )}
    </>
  );
}
