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

import { useMemo } from 'react';
import { CfcSettingsPanel } from './CfcSettingsPanel';
import { DownloadAudioSuiteButton } from './DownloadAudioSuiteButton';
import { usePluginPanels } from '../plugins/runtime/usePluginPanels';
import type { RegisteredPluginPanel } from '../plugins/runtime/pluginRuntime';

// ---------------------------------------------------------------
// Audio-chain plugin slot — installed plugins whose manifest declares
// `ui.panels[].slot === "tx-audio-tools.chain"` render here, in chain
// order, above the always-on CFC section. The fixed v1 chain order is
// per issue #332 Phase 0 (KB2UKA-locked 2026-05-17):
//
//   EQ → Compressor → Exciter → Bass enhancer → Reverb → CFC
//
// Plugins not in this list (e.g. third-party chain blocks added later)
// render after the known v1 blocks, sorted by panel id for determinism.
// ---------------------------------------------------------------
const CHAIN_SLOT = 'tx-audio-tools.chain';

const V1_CHAIN_ORDER: ReadonlyArray<string> = [
  'com.openhpsdr.zeus.samples.eq',
  'com.openhpsdr.zeus.samples.compressor',
  'com.openhpsdr.zeus.samples.exciter',
  'com.openhpsdr.zeus.samples.bass',
  'com.openhpsdr.zeus.samples.reverb',
];

function chainPosition(pluginId: string): number {
  const idx = V1_CHAIN_ORDER.indexOf(pluginId);
  // Unknown plugins (third-party, future blocks) sort after the known v1
  // set. Using +Infinity puts them at the bottom; the secondary sort by
  // panel id then keeps them deterministic relative to each other.
  return idx === -1 ? Number.POSITIVE_INFINITY : idx;
}

// Sort registered panels into the fixed v1 chain order.
function sortChainPanels(panels: RegisteredPluginPanel[]): RegisteredPluginPanel[] {
  return [...panels].sort((a, b) => {
    const da = chainPosition(a.pluginId);
    const db = chainPosition(b.pluginId);
    if (da !== db) return da - db;
    return a.panelId.localeCompare(b.panelId);
  });
}

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

  return (
    <div
      role="presentation"
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: 6,
        padding: '8px 12px',
        background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
        border: '1px solid var(--line-1)',
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
      <span style={{ marginRight: 4, color: 'var(--fg-1)', fontWeight: 500 }}>TX chain</span>
      {v1Slots.map((slot, i) => (
        <span key={slot.id} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
          {i > 0 && (
            <span aria-hidden style={{ color: 'var(--fg-3)', fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)' }}>›</span>
          )}
          <span
            style={{
              padding: '2px 8px',
              borderRadius: 3,
              background: slot.installed ? 'var(--bg-2)' : 'var(--bg-1)',
              border: '1px solid ' + (slot.installed ? 'var(--accent)' : 'var(--line-1)'),
              color: slot.installed ? 'var(--fg-0)' : 'var(--fg-3)',
              opacity: slot.installed ? 1 : 0.5,
              fontSize: 10,
              fontWeight: 500,
            }}
            title={slot.installed ? 'Installed and active' : 'Not installed — click Download Audio Suite or Settings → Plugins → Install from URL'}
          >
            {slot.title}
          </span>
        </span>
      ))}
      <DownloadAudioSuiteButton />
    </div>
  );
}

// ---------------------------------------------------------------
// TxAudioToolsPanel — chain plugins above, CFC below.
//
// Audio-chain plugins (issue #332) declare panel slot
// `tx-audio-tools.chain`; we filter `usePluginPanels()` to that slot,
// sort by the fixed v1 chain order, and render each plugin's component
// inline. CFC stays at the bottom — it's WDSP-driven and ships in Zeus
// core, not as a plugin, so it doesn't move.
// ---------------------------------------------------------------
export function TxAudioToolsPanel() {
  const allPanels = usePluginPanels();
  const chainPanels = useMemo(
    () => sortChainPanels(allPanels.filter((p) => p.slot === CHAIN_SLOT)),
    [allPanels],
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <ChainFlow chainPanels={chainPanels} />

      {/* Chain plugins in v1 order. Empty until at least one is installed. */}
      {chainPanels.map((panel) => {
        const Component = panel.component;
        return (
          <div key={`${panel.pluginId}::${panel.panelId}`} data-plugin-id={panel.pluginId}>
            <Component />
          </div>
        );
      })}

      {/* CFC — WDSP-driven, always available, always last in the chain. */}
      <CfcSettingsPanel />
    </div>
  );
}
