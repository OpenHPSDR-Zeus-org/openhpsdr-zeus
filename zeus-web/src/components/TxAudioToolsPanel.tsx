// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useEffect, useMemo } from 'react';
import { CfcSettingsPanel } from './CfcSettingsPanel';
import { DownloadAudioSuiteButton } from './DownloadAudioSuiteButton';
import { TxFidelityPanel } from '../layout/panels/TxFidelityPanel';
import { usePluginPanels } from '../plugins/runtime/usePluginPanels';
import type { RegisteredPluginPanel } from '../plugins/runtime/pluginRuntime';
import { useAudioSuiteStore } from '../state/audio-suite-store';

// ---------------------------------------------------------------
// Audio-chain plugin slot — installed plugins whose manifest declares
// `ui.panels[].slot === "tx-audio-tools.chain"` are owned by the
// Audio Suite floating window (Phase 2 of issue #332). The chain
// flow strip here is a read-only one-glance view; the "Audio Suite"
// button opens the floating window where plugins can be reordered
// (drag-and-drop tiles), previewed through the operator's headphones,
// and tuned via their per-plugin panels stacked vertically.
//
// CFC stays below — it's WDSP-driven, ships in Zeus core (not as a
// plugin), so it's not part of the reorderable chain.
// ---------------------------------------------------------------
const CHAIN_SLOT = 'tx-audio-tools.chain';

// ---------------------------------------------------------------
// Master signal-flow strip — one-glance read of which chain blocks
// are installed and active, drawn in Zeus tokens (brass-plate rail
// matching v3 Lifted Dark). Uninstalled blocks render dim; CFC is
// always on (WDSP-driven, can't be uninstalled).
// ---------------------------------------------------------------
function ChainFlow({ chainPanels }: { chainPanels: RegisteredPluginPanel[] }) {
  const v1Slots: Array<{ id: string; title: string; installed: boolean }> = useMemo(() => {
    const installedIds = new Set(chainPanels.map((p) => p.pluginId));
    return [
      { id: 'eq',      title: 'EQ',      installed: installedIds.has('com.openhpsdr.zeus.samples.eq') },
      { id: 'comp',    title: 'COMP',    installed: installedIds.has('com.openhpsdr.zeus.samples.compressor') },
      { id: 'exciter', title: 'EXCITER', installed: installedIds.has('com.openhpsdr.zeus.samples.exciter') },
      { id: 'bass',    title: 'BASS',    installed: installedIds.has('com.openhpsdr.zeus.samples.bass') },
      { id: 'reverb',  title: 'REVERB',  installed: installedIds.has('com.openhpsdr.zeus.samples.reverb') },
      { id: 'cfc',     title: 'CFC',     installed: true }, // WDSP-driven, always present
    ];
  }, [chainPanels]);

  const masterBypassed = useAudioSuiteStore((s) => s.masterBypassed);
  const processingMode = useAudioSuiteStore((s) => s.processingMode);
  const chainOrder = useAudioSuiteStore((s) => s.chainOrder);
  const loadMasterBypassFromServer = useAudioSuiteStore(
    (s) => s.loadMasterBypassFromServer,
  );
  const loadProcessingModeFromServer = useAudioSuiteStore(
    (s) => s.loadProcessingModeFromServer,
  );

  // On the VST route the native v1 blocks don't run — the out-of-process
  // engine hosts the operator's VST3 plugins instead. Show that chain (in
  // signal order) so the strip reflects what's actually processing.
  const vstMode = processingMode === 'vst';
  const vstSlots = useMemo(() => {
    if (!vstMode) return [];
    const orderIndex = new Map(chainOrder.map((id, i) => [id, i] as const));
    return chainPanels
      .filter((p) => p.editorBacked === true && orderIndex.has(p.pluginId))
      .sort((a, b) => orderIndex.get(a.pluginId)! - orderIndex.get(b.pluginId)!)
      .map((p) => ({
        id: p.pluginId,
        title: (p.title || p.pluginId.split('.').pop() || 'VST').toUpperCase(),
        installed: true,
      }));
  }, [vstMode, chainPanels, chainOrder]);

  const slots = vstMode ? vstSlots : v1Slots;

  // Pull the master-bypass state from the server once on mount so a
  // fresh browser session reflects the persisted operator preference.
  // Subsequent broadcasts (0x1F) keep us in sync without polling.
  // Processing mode (Native/VST) is likewise server-authoritative.
  useEffect(() => {
    loadMasterBypassFromServer();
    loadProcessingModeFromServer();
  }, [loadMasterBypassFromServer, loadProcessingModeFromServer]);

  return (
    <div
      role="presentation"
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: '8px 12px',
        padding: '8px 12px',
        background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
        border: '1px solid var(--line)',
        borderRadius: 6,
        boxShadow: 'inset 0 2px 0 var(--power), inset 0 3px 8px rgba(255, 201, 58, 0.08)',
        fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
        fontSize: 11,
        letterSpacing: 1.2,
        textTransform: 'uppercase',
        color: 'var(--fg-2)',
        flexWrap: 'wrap',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
        <ProcessingModeButton />
        <MasterBypassButton />
      </div>

      <div style={{ display: 'flex', alignItems: 'center', gap: 6, flex: '0 1 auto', flexWrap: 'wrap', minWidth: 0 }}>
        <span style={{ marginRight: 4, color: 'var(--fg-1)', fontWeight: 600, flex: '0 0 auto' }}>
          {vstMode ? 'VST chain' : 'TX chain'}
        </span>
        {vstMode && slots.length === 0 && (
          <span style={{ color: 'var(--fg-3)', fontSize: 10, fontWeight: 500, textTransform: 'none', letterSpacing: 0 }}>
            No VST3 plugins in the chain — open the Audio Suite to scan and add some.
          </span>
        )}
        {slots.map((slot, i) => {
          // CFC is downstream in WDSP and unaffected by master bypass —
          // never dim it. Plugin slots dim to 45% when bypassed to mirror
          // the per-plugin bypass visual convention (operator sees the
          // chain is inert).
          const dimForBypass = slot.id !== 'cfc' && masterBypassed;
          return (
            <span key={slot.id} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
              {i > 0 && (
                <span aria-hidden style={{ color: 'var(--fg-3)', fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)' }}>›</span>
              )}
              <span
                style={{
                  padding: '2px 8px',
                  borderRadius: 3,
                  background: slot.installed ? 'var(--bg-2)' : 'var(--bg-1)',
                  border: '1px solid ' + (slot.installed ? 'var(--accent)' : 'var(--line)'),
                  color: slot.installed ? 'var(--fg-0)' : 'var(--fg-3)',
                  opacity: dimForBypass ? 0.45 : (slot.installed ? 1 : 0.5),
                  fontSize: 10,
                  fontWeight: 500,
                  transition: 'opacity 120ms ease-out',
                }}
                title={
                  dimForBypass
                    ? 'Master bypass engaged — this stage is inert. Click BYPASS to engage the chain.'
                    : slot.installed
                    ? 'Installed and active'
                    : 'Not installed — click Download Audio Suite or Settings → Plugins → Install from URL'
                }
              >
                {slot.title}
              </span>
            </span>
          );
        })}
      </div>

      <div style={{ display: 'flex', alignItems: 'center', gap: 8, flex: '0 0 auto', flexWrap: 'nowrap' }}>
        <AudioSuiteOpenButton />
        {!vstMode && <DownloadAudioSuiteButton />}
      </div>
    </div>
  );
}

// Master-bypass toggle for the whole plugin chain. One click instead of
// six per-plugin bypass clicks. Sits at the head of the brass rail so
// it's the first thing the operator's eye lands on. Visual convention
// matches the per-plugin bypass (feedback_audio_plugin_bypass_convention):
//   engaged   (bypassed) → --tx background, white text, "BYPASS" label
//   released (chain hot) → --bg-2 background, --fg-2 text, "BYPASS" label
function MasterBypassButton() {
  const masterBypassed = useAudioSuiteStore((s) => s.masterBypassed);
  const setMasterBypassed = useAudioSuiteStore((s) => s.setMasterBypassed);
  return (
    <button
      type="button"
      onClick={() => setMasterBypassed(!masterBypassed)}
      aria-pressed={masterBypassed}
      title={
        masterBypassed
          ? 'Master bypass ENGAGED — entire plugin chain (NoiseGate / EQ / Comp / Exciter / Bass / Reverb) is inert; mic passes through untouched. CFC is unaffected. Click to engage the chain.'
          : 'Plugin chain ACTIVE — all installed plugins are processing your mic. Click to disengage the whole chain in one shot (per-plugin bypass states are preserved).'
      }
      style={{
        padding: '4px 12px',
        borderRadius: 4,
        border: '1px solid ' + (masterBypassed ? 'var(--tx)' : 'var(--accent)'),
        background: masterBypassed ? 'var(--tx)' : 'var(--bg-2)',
        color: masterBypassed ? '#fff' : 'var(--fg-2)',
        cursor: 'pointer',
        fontSize: 10,
        fontWeight: 700,
        letterSpacing: 1.2,
        textTransform: 'uppercase',
        fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
        minWidth: 72,
        transition: 'background 120ms ease-out, color 120ms ease-out, border-color 120ms ease-out',
      }}
    >
      Bypass
    </button>
  );
}

// Native ↔ VST processing-route selector. Native (default) = Brian's
// in-process Audio Suite chain. VST = the operator's VST3 plugins hosted in
// the out-of-process VSTHost engine (crash-isolated from the radio). The two
// are mutually exclusive. PROVISIONAL placement/labels — the maintainer owns
// the final Audio Suite UX; this is a functional control so the route can be
// driven and tested. Palette uses tokens only (--accent for the active VST
// route, --tx for a "VST selected but engine not live" warning).
function ProcessingModeButton() {
  const mode = useAudioSuiteStore((s) => s.processingMode);
  const engineAvailable = useAudioSuiteStore((s) => s.vstEngineAvailable);
  const engineActive = useAudioSuiteStore((s) => s.vstEngineActive);
  const setProcessingMode = useAudioSuiteStore((s) => s.setProcessingMode);

  const isVst = mode === 'vst';
  const vstWarn = isVst && !engineActive; // selected but engine not routing

  const border = isVst ? (vstWarn ? 'var(--tx)' : 'var(--accent)') : 'var(--line)';
  const background = isVst && !vstWarn ? 'var(--accent)' : 'var(--bg-2)';
  const color = isVst ? (vstWarn ? 'var(--tx)' : '#fff') : 'var(--fg-2)';

  const title = !isVst
    ? 'Processing route: NATIVE — the in-process Audio Suite chain (default). Click to route TX mic audio through the out-of-process VST engine instead.'
    : engineActive
    ? 'Processing route: VST — TX mic audio runs through the out-of-process VST engine (your Audio Suite VST3 plugins, loaded at default settings). Click to return to the native chain.'
    : engineAvailable
    ? 'VST route selected, but the engine is not routing yet (starting up, or it failed to come up). TX audio passes through clean meanwhile. Click to return to the native chain.'
    : 'VST route selected, but no VST engine is installed — install VSTHost (github.com/KlayaR/VSTHost). TX audio passes through clean. Click to return to the native chain.';

  return (
    <button
      type="button"
      onClick={() => setProcessingMode(isVst ? 'native' : 'vst')}
      aria-pressed={isVst}
      title={title}
      style={{
        padding: '4px 12px',
        borderRadius: 4,
        border: '1px solid ' + border,
        background,
        color,
        cursor: 'pointer',
        fontSize: 10,
        fontWeight: 700,
        letterSpacing: 1.2,
        whiteSpace: 'nowrap',
        minWidth: 72,
        transition: 'background 120ms ease-out, color 120ms ease-out, border-color 120ms ease-out',
      }}
    >
      {isVst ? 'VST' : 'Native'}
    </button>
  );
}

// Opens the Audio Suite floating window. Disabled (visually dimmed) when
// no chain plugins are installed — there's nothing to show, and the
// Download Audio Suite button to its right is the right action.
function AudioSuiteOpenButton() {
  const open = useAudioSuiteStore((s) => s.open);
  return (
    <button
      type="button"
      onClick={open}
      style={{
        padding: '4px 12px',
        borderRadius: 4,
        border: '1px solid var(--accent)',
        background: 'var(--bg-2)',
        color: 'var(--fg-0)',
        cursor: 'pointer',
        fontSize: 10,
        fontWeight: 600,
        letterSpacing: 1,
        textTransform: 'uppercase',
        fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
      }}
      title="Open the Audio Suite window to reorder, preview, and tune chain plugins"
    >
      Audio Suite
    </button>
  );
}

// ---------------------------------------------------------------
// TxAudioToolsPanel — chain-flow strip + CFC.
//
// Audio-chain plugins (issue #332, Phase 2 onward) live in the Audio
// Suite floating window — click the "Audio Suite" button in the chain
// strip header to open it. The strip here is purely informational:
// one tile per known v1/v2 block showing whether it's installed, so
// the operator can see at a glance what's loaded without opening the
// window. CFC stays inline — it's WDSP-driven, ships in Zeus core,
// and isn't part of the reorderable plugin chain.
// ---------------------------------------------------------------
export function TxAudioToolsPanel() {
  const allPanels = usePluginPanels();
  const chainPanels = useMemo(
    () => allPanels.filter((p) => p.slot === CHAIN_SLOT),
    [allPanels],
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <ChainFlow chainPanels={chainPanels} />

      <div className="ps-card">
        <h4>
          TX Fidelity Policy
          <span className="ps-card-hint">station profile / target density / apply chain</span>
        </h4>
        <TxFidelityPanel />
      </div>

      {/* CFC — WDSP-driven, always available, always last in the chain. */}
      <CfcSettingsPanel />
    </div>
  );
}
