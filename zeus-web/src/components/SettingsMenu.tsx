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
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.

import { useEffect, useMemo, useState } from 'react';
import { PaSettingsPanel } from './PaSettingsPanel';
import { BandPlanEditor } from './bandplan/BandPlanEditor';
import { AboutPanel } from './AboutPanel';
import { DisplayPanel } from './DisplayPanel';
import { QrzSettingsPanel } from './QrzSettingsPanel';
import { RadioOptionsPanel } from './RadioOptionsPanel';
import { RotatorSettingsPanel } from './RotatorSettingsPanel';
import { ServerUrlPanel } from './ServerUrlPanel';
import { TciSettingsPanel } from './TciSettingsPanel';
import { RadioSelector } from './RadioSelector';
import { usePaStore } from '../state/pa-store';
import { useRadioStore } from '../state/radio-store';
import { PsSettingsPanel } from './PsSettingsPanel';
import { TxAudioToolsPanel } from './TxAudioToolsPanel';

export type SettingsTabId =
  | 'pa'
  | 'ps'
  | 'tx-audio'
  | 'bandplan'
  | 'qrz'
  | 'rotator'
  | 'tci'
  | 'display'
  | 'server'
  | 'radio'
  | 'about';

const TABS: ReadonlyArray<{ id: SettingsTabId; label: string }> = [
  { id: 'pa', label: 'PA SETTINGS' },
  { id: 'ps', label: 'PURESIGNAL' },
  { id: 'tx-audio', label: 'TX AUDIO TOOLS' },
  { id: 'bandplan', label: 'BAND PLAN' },
  { id: 'qrz', label: 'QRZ' },
  { id: 'rotator', label: 'ROTATOR' },
  { id: 'tci', label: 'TCI' },
  { id: 'display', label: 'DISPLAY' },
  { id: 'server', label: 'SERVER' },
  { id: 'radio', label: 'RADIO' },
  { id: 'about', label: 'ABOUT' },
];

type Props = {
  initialTab?: SettingsTabId;
  onClose: () => void;
};

// SettingsView — settings is a workspace-replacing view, not a popover. The
// parent (App) renders it in the same grid cell as FlexWorkspace whenever
// layout-store.settingsViewOpen is true. Clicking any layout tab in the
// LeftLayoutBar returns to the workspace (setActiveLayout clears the flag).
export function SettingsView({ initialTab, onClose }: Props) {
  const [active, setActive] = useState<SettingsTabId>(initialTab ?? 'pa');
  const savePa = usePaStore((s) => s.save);
  const loadPa = usePaStore((s) => s.load);
  const paInflight = usePaStore((s) => s.inflight);
  // RADIO tab is HL2-only. Hidden on every other board (the backend always
  // answers /api/radio/hl2-options 200, but operators on Hermes / ANAN /
  // Orion / G2 have no use for a one-checkbox tab that does nothing for
  // them — so we gate visibility on the per-board capability flag).
  const hasHl2OptionalToggles = useRadioStore(
    (s) => s.capabilities.hasHl2OptionalToggles,
  );
  const visibleTabs = useMemo(
    () => TABS.filter((t) => t.id !== 'radio' || hasHl2OptionalToggles),
    [hasHl2OptionalToggles],
  );

  // If the operator was sitting on the RADIO tab and the board changed
  // (re-discovery now reports a non-HL2), bounce them back to PA so they
  // don't get stuck on an empty tabpanel.
  useEffect(() => {
    if (active === 'radio' && !hasHl2OptionalToggles) {
      setActive('pa');
    }
  }, [active, hasHl2OptionalToggles]);

  useEffect(() => {
    if (initialTab) setActive(initialTab);
  }, [initialTab]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const handleApply = async () => {
    await savePa();
    onClose();
  };
  const handleCancel = async () => {
    // Discard any in-memory edits by re-fetching the server's canonical state.
    await loadPa();
    onClose();
  };

  return (
    <div className="settings-view" role="region" aria-label="Settings">
      <div className="settings-view-header">
        <h2 className="settings-view-title" id="settings-title">
          Settings
        </h2>
        <button
          type="button"
          onClick={onClose}
          aria-label="Close settings"
          className="settings-view-close"
          title="Close (Esc)"
        >
          ×
        </button>
      </div>

      <RadioSelector />

      <div className="settings-view-body">
        <nav
          role="tablist"
          aria-label="Settings sections"
          className="settings-view-tabs"
        >
          {visibleTabs.map((t) => {
            const isActive = t.id === active;
            return (
              <button
                key={t.id}
                type="button"
                role="tab"
                aria-selected={isActive}
                onClick={() => setActive(t.id)}
                className={`settings-view-tab${isActive ? ' active' : ''}`}
              >
                {t.label}
              </button>
            );
          })}
        </nav>

        <div role="tabpanel" className="settings-view-panel">
          {active === 'pa' && <PaSettingsPanel />}
          {active === 'ps' && <PsSettingsPanel />}
          {active === 'tx-audio' && <TxAudioToolsPanel />}
          {active === 'bandplan' && <BandPlanEditor />}
          {active === 'qrz' && <QrzSettingsPanel />}
          {active === 'rotator' && <RotatorSettingsPanel />}
          {active === 'tci' && <TciSettingsPanel />}
          {active === 'display' && <DisplayPanel />}
          {active === 'server' && <ServerUrlPanel />}
          {active === 'radio' && hasHl2OptionalToggles && <RadioOptionsPanel />}
          {active === 'about' && <AboutPanel />}
        </div>
      </div>

      {active === 'pa' && (
        <div className="settings-view-footer">
          <button type="button" className="btn sm" onClick={handleCancel} disabled={paInflight}>
            CANCEL
          </button>
          <button
            type="button"
            className="btn sm active"
            onClick={handleApply}
            disabled={paInflight}
          >
            {paInflight ? 'SAVING…' : 'APPLY'}
          </button>
        </div>
      )}
    </div>
  );
}
