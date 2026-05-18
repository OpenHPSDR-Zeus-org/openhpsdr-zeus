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
import { useAudioSuiteStore } from '../state/audio-suite-store';

// ---------------------------------------------------------------
// Audio-chain plugin slot — installed plugins whose manifest declares
// `ui.panels[].slot === "tx-audio-tools.chain"` are owned by the
// Audio Suite floating window (Phase 2 of issue #332). The chain
// flow strip here is a read-only one-glance view; the "Audio Suite"
// button opens the floating window where plugins can be reordered
// (drag-and-drop tiles), auditioned through the operator's headphones,
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
      <AudioSuiteOpenButton />
      <DownloadAudioSuiteButton />
    </div>
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
        marginLeft: 'auto',
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
      title="Open the Audio Suite window to reorder, audition, and tune chain plugins"
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

      {/* CFC — WDSP-driven, always available, always last in the chain. */}
      <CfcSettingsPanel />
    </div>
  );
}
