// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
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
import { CalibrationPanel } from './CalibrationPanel';
import { DisplayPanel } from './DisplayPanel';
import { QrzSettingsPanel } from './QrzSettingsPanel';
import { RadioOptionsPanel } from './RadioOptionsPanel';
import { RotatorSettingsPanel } from './RotatorSettingsPanel';
import { ServerUrlPanel } from './ServerUrlPanel';
import { TciSettingsPanel } from './TciSettingsPanel';
import { RadioSelector } from './RadioSelector';
import { usePaStore } from '../state/pa-store';
import { useRadioStore } from '../state/radio-store';
import { useEasterEggStore } from '../state/easter-egg-store';
import { PsSettingsPanel } from './PsSettingsPanel';
import { TxAudioToolsPanel } from './TxAudioToolsPanel';
import { DspSettingsPanel } from './DspSettingsPanel';
import { PluginsPanel } from '../plugins/components/PluginsPanel';
import { HamClockSettingsPanel } from './HamClockSettingsPanel';
import { SpotsSettingsPanel } from './SpotsSettingsPanel';
import { UpdatesPanel } from './UpdatesPanel';
import { HardwareDiagnosticsPanel } from './HardwareDiagnosticsPanel';

export type SettingsTabId =
  | 'pa'
  | 'hardware'
  | 'ps'
  | 'tx-audio'
  | 'dsp'
  | 'bandplan'
  | 'qrz'
  | 'rotator'
  | 'tci'
  | 'display'
  | 'plugins'
  | 'hamclock'
  | 'spots'
  | 'server'
  | 'radio'
  | 'calibration'
  | 'updates'
  | 'about';

const TABS: ReadonlyArray<{ id: SettingsTabId; label: string }> = [
  { id: 'pa', label: 'PA SETTINGS' },
  { id: 'hardware', label: 'HARDWARE' },
  { id: 'ps', label: 'PURESIGNAL' },
  { id: 'tx-audio', label: 'AUDIO TOOLS' },
  { id: 'dsp', label: 'DSP' },
  { id: 'bandplan', label: 'BAND PLAN' },
  { id: 'qrz', label: 'QRZ' },
  { id: 'rotator', label: 'ROTATOR' },
  { id: 'tci', label: 'TCI' },
  { id: 'display', label: 'DISPLAY' },
  { id: 'plugins', label: 'PLUGINS' },
  { id: 'hamclock', label: 'HAMCLOCK' },
  { id: 'spots', label: 'SPOTS' },
  { id: 'server', label: 'SERVER' },
  { id: 'radio', label: 'RADIO' },
  { id: 'calibration', label: 'CALIBRATION' },
  { id: 'updates', label: 'UPDATES' },
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
  // RADIO tab is board-option-only. Hidden unless the active capability
  // fingerprint advertises an option surface that writes a real firmware
  // field (HL2 Band Volts or ANAN-G2 ADC dither/random).
  const hasHl2OptionalToggles = useRadioStore(
    (s) => s.capabilities.hasHl2OptionalToggles,
  );
  const supportsG2AdcOptions = useRadioStore(
    (s) => s.capabilities.supportsG2AdcOptions,
  );
  const hasRadioOptions = hasHl2OptionalToggles || supportsG2AdcOptions;
  // HARDWARE is a hidden diagnostics folder — unlocked only via the header
  // brand-mark easter egg (see easter-egg-store). It re-locks on every launch,
  // so a fresh session never lists it.
  const hardwareUnlocked = useEasterEggStore((s) => s.hardwareUnlocked);
  const visibleTabs = useMemo(
    () =>
      TABS.filter(
        (t) =>
          (t.id !== 'radio' || hasRadioOptions) &&
          (t.id !== 'hardware' || hardwareUnlocked),
      ),
    [hasRadioOptions, hardwareUnlocked],
  );

  // If the operator was sitting on the RADIO tab and the board changed
  // (re-discovery now reports no radio options), bounce them back to PA so they
  // don't get stuck on an empty tabpanel.
  useEffect(() => {
    if (active === 'radio' && !hasRadioOptions) {
      setActive('pa');
    }
  }, [active, hasRadioOptions]);

  // Effective tab for rendering. If the stored `active` tab isn't currently
  // visible — a hidden HARDWARE folder, or RADIO opened via initialTab without
  // board options — fall back to PA so the operator never lands on a hidden
  // tabpanel. Deriving this (rather than bouncing in an effect) avoids racing
  // the initialTab effect, which could otherwise re-select a hidden tab.
  const activeTab = visibleTabs.some((t) => t.id === active) ? active : 'pa';

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
            const isActive = t.id === activeTab;
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
          {activeTab === 'pa' && <PaSettingsPanel />}
          {activeTab === 'hardware' && hardwareUnlocked && <HardwareDiagnosticsPanel />}
          {activeTab === 'ps' && <PsSettingsPanel />}
          {activeTab === 'tx-audio' && <TxAudioToolsPanel />}
          {activeTab === 'dsp' && <DspSettingsPanel />}
          {activeTab === 'bandplan' && <BandPlanEditor />}
          {activeTab === 'qrz' && <QrzSettingsPanel />}
          {activeTab === 'rotator' && <RotatorSettingsPanel />}
          {activeTab === 'tci' && <TciSettingsPanel />}
          {activeTab === 'display' && <DisplayPanel />}
          {activeTab === 'plugins' && <PluginsPanel />}
          {activeTab === 'hamclock' && <HamClockSettingsPanel />}
          {activeTab === 'spots' && <SpotsSettingsPanel />}
          {activeTab === 'server' && <ServerUrlPanel />}
          {activeTab === 'radio' && hasRadioOptions && <RadioOptionsPanel />}
          {activeTab === 'calibration' && <CalibrationPanel />}
          {activeTab === 'updates' && <UpdatesPanel />}
          {activeTab === 'about' && <AboutPanel />}
        </div>
      </div>

      {activeTab === 'pa' && (
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
