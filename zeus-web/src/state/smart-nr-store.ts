// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus - OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { create } from 'zustand';
import type { NrConfigDto } from '../api/client';

export type SmartNrAutomationMode = 'manual' | 'suggest' | 'auto';

export type SmartNrStatus = {
  atUtc: string | null;
  profile: string;
  reason: string;
  heldByRxChain?: boolean;
  rxChainLabel?: string;
  rxChainRecommendation?: string;
  rxChainTone?: 'neutral' | 'optimize' | 'protect';
  rxChainScore?: number;
  maxSnrDb: number;
  occupancyPct: number;
  coherentOccupancyPct: number;
  impulsivePct: number;
  peakCount: number;
  coherentPeakCount: number;
  pending: boolean;
  applied: boolean;
  nr: NrConfigDto | null;
};

export type SmartNrSettings = {
  automationMode: SmartNrAutomationMode;
  aggressiveness: number;
  autoBlankerEnabled: boolean;
  autoNotchEnabled: boolean;
  maxBlankerThreshold: number;
  dwellSamples: number;
};

export type SmartNrState = SmartNrSettings & {
  status: SmartNrStatus | null;
  setAutomationMode: (mode: SmartNrAutomationMode) => void;
  setSettings: (patch: Partial<SmartNrSettings>) => void;
  setStatus: (status: SmartNrStatus | null) => void;
  resetSettings: () => void;
};

const STORAGE_KEY = 'zeus.smartNr';

const DEFAULT_SETTINGS: SmartNrSettings = {
  automationMode: 'manual',
  aggressiveness: 55,
  autoBlankerEnabled: true,
  autoNotchEnabled: true,
  maxBlankerThreshold: 16,
  dwellSamples: 5,
};

function clampFinite(v: unknown, min: number, max: number, fallback: number): number {
  return typeof v === 'number' && Number.isFinite(v) ? Math.max(min, Math.min(max, v)) : fallback;
}

function isAutomationMode(v: unknown): v is SmartNrAutomationMode {
  return v === 'manual' || v === 'suggest' || v === 'auto';
}

function normalizeSettings(raw: Partial<SmartNrSettings> = {}): SmartNrSettings {
  return {
    automationMode: isAutomationMode(raw.automationMode) ? raw.automationMode : DEFAULT_SETTINGS.automationMode,
    aggressiveness: clampFinite(raw.aggressiveness, 0, 100, DEFAULT_SETTINGS.aggressiveness),
    autoBlankerEnabled: raw.autoBlankerEnabled !== false,
    autoNotchEnabled: raw.autoNotchEnabled !== false,
    maxBlankerThreshold: clampFinite(raw.maxBlankerThreshold, 8, 30, DEFAULT_SETTINGS.maxBlankerThreshold),
    dwellSamples: Math.round(clampFinite(raw.dwellSamples, 3, 8, DEFAULT_SETTINGS.dwellSamples)),
  };
}

function readPersisted(): SmartNrSettings {
  try {
    if (typeof localStorage === 'undefined') return DEFAULT_SETTINGS;
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return DEFAULT_SETTINGS;
    return normalizeSettings(JSON.parse(raw) as Partial<SmartNrSettings>);
  } catch {
    return DEFAULT_SETTINGS;
  }
}

function persist(settings: SmartNrSettings): void {
  try {
    if (typeof localStorage === 'undefined') return;
    localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
  } catch {
    // localStorage may be unavailable in private/webview contexts.
  }
}

const persisted = readPersisted();

function pickSettings(state: SmartNrState): SmartNrSettings {
  return {
    automationMode: state.automationMode,
    aggressiveness: state.aggressiveness,
    autoBlankerEnabled: state.autoBlankerEnabled,
    autoNotchEnabled: state.autoNotchEnabled,
    maxBlankerThreshold: state.maxBlankerThreshold,
    dwellSamples: state.dwellSamples,
  };
}

export const useSmartNrStore = create<SmartNrState>((set, get) => ({
  ...persisted,
  status: null,
  setAutomationMode: (automationMode) => {
    const next = normalizeSettings({ ...pickSettings(get()), automationMode });
    set({ ...next, status: automationMode === 'manual' ? null : get().status });
    persist(next);
  },
  setSettings: (patch) => {
    const next = normalizeSettings({ ...pickSettings(get()), ...patch });
    set(next);
    persist(next);
  },
  setStatus: (status) => set({ status }),
  resetSettings: () => {
    set({ ...DEFAULT_SETTINGS, status: null });
    persist(DEFAULT_SETTINGS);
  },
}));
