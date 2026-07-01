// SPDX-License-Identifier: GPL-2.0-or-later
//
// Thetis-style RF filter matrix. Server-authoritative; the frontend only loads
// and saves normalized rows from /api/radio/rf-filters.

import { create } from 'zustand';

export interface RfFilterRange {
  key: string;
  label: string;
  startHz: number;
  endHz: number;
  forceBypass: boolean;
}

export interface RfFilterProfile {
  key: string;
  label: string;
  rxFilters: RfFilterRange[];
  txFilters: RfFilterRange[];
}

export interface RfFilterActive {
  profileKey: string;
  profileLabel: string;
  rx1Hz: number;
  rx2Hz: number;
  txHz: number;
  txActive: boolean;
  rx1Key: string;
  rx1Label: string;
  rx2Key: string;
  rx2Label: string;
  txKey: string;
  txLabel: string;
  reason: string;
}

export interface RfFilterSettings {
  supported: boolean;
  boardFamily: string;
  activeProfileKey: string;
  customMatrixEnabled: boolean;
  rxBypassAll: boolean;
  rxBypassOnTx: boolean;
  rxBypassOnPureSignal: boolean;
  profiles: RfFilterProfile[];
  active: RfFilterActive;
  warnings: string[];
}

const EMPTY_ACTIVE: RfFilterActive = {
  profileKey: 'anan-7000',
  profileLabel: 'ANAN-7000 / Saturn BPF',
  rx1Hz: 0,
  rx2Hz: 0,
  txHz: 0,
  txActive: false,
  rx1Key: 'auto',
  rx1Label: 'Auto',
  rx2Key: 'auto',
  rx2Label: 'Auto',
  txKey: 'auto',
  txLabel: 'Auto',
  reason: '',
};

const DEFAULT_SETTINGS: RfFilterSettings = {
  supported: false,
  boardFamily: '',
  activeProfileKey: 'anan-7000',
  customMatrixEnabled: false,
  rxBypassAll: false,
  rxBypassOnTx: false,
  rxBypassOnPureSignal: false,
  profiles: [],
  active: EMPTY_ACTIVE,
  warnings: [],
};

function parseRange(raw: unknown): RfFilterRange {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    key: typeof r.key === 'string' ? r.key : '',
    label: typeof r.label === 'string' ? r.label : '',
    startHz: typeof r.startHz === 'number' ? r.startHz : 0,
    endHz: typeof r.endHz === 'number' ? r.endHz : 0,
    forceBypass: typeof r.forceBypass === 'boolean' ? r.forceBypass : false,
  };
}

function parseProfile(raw: unknown): RfFilterProfile {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    key: typeof r.key === 'string' ? r.key : '',
    label: typeof r.label === 'string' ? r.label : '',
    rxFilters: Array.isArray(r.rxFilters) ? r.rxFilters.map(parseRange) : [],
    txFilters: Array.isArray(r.txFilters) ? r.txFilters.map(parseRange) : [],
  };
}

function parseActive(raw: unknown): RfFilterActive {
  if (!raw || typeof raw !== 'object') return EMPTY_ACTIVE;
  const r = raw as Record<string, unknown>;
  return {
    profileKey: typeof r.profileKey === 'string' ? r.profileKey : EMPTY_ACTIVE.profileKey,
    profileLabel: typeof r.profileLabel === 'string' ? r.profileLabel : EMPTY_ACTIVE.profileLabel,
    rx1Hz: typeof r.rx1Hz === 'number' ? r.rx1Hz : 0,
    rx2Hz: typeof r.rx2Hz === 'number' ? r.rx2Hz : 0,
    txHz: typeof r.txHz === 'number' ? r.txHz : 0,
    txActive: typeof r.txActive === 'boolean' ? r.txActive : false,
    rx1Key: typeof r.rx1Key === 'string' ? r.rx1Key : 'auto',
    rx1Label: typeof r.rx1Label === 'string' ? r.rx1Label : 'Auto',
    rx2Key: typeof r.rx2Key === 'string' ? r.rx2Key : 'auto',
    rx2Label: typeof r.rx2Label === 'string' ? r.rx2Label : 'Auto',
    txKey: typeof r.txKey === 'string' ? r.txKey : 'auto',
    txLabel: typeof r.txLabel === 'string' ? r.txLabel : 'Auto',
    reason: typeof r.reason === 'string' ? r.reason : '',
  };
}

function parse(raw: unknown): RfFilterSettings {
  if (!raw || typeof raw !== 'object') return DEFAULT_SETTINGS;
  const r = raw as Record<string, unknown>;
  return {
    supported: typeof r.supported === 'boolean' ? r.supported : false,
    boardFamily: typeof r.boardFamily === 'string' ? r.boardFamily : '',
    activeProfileKey:
      typeof r.activeProfileKey === 'string' ? r.activeProfileKey : 'anan-7000',
    customMatrixEnabled:
      typeof r.customMatrixEnabled === 'boolean' ? r.customMatrixEnabled : false,
    rxBypassAll: typeof r.rxBypassAll === 'boolean' ? r.rxBypassAll : false,
    rxBypassOnTx: typeof r.rxBypassOnTx === 'boolean' ? r.rxBypassOnTx : false,
    rxBypassOnPureSignal:
      typeof r.rxBypassOnPureSignal === 'boolean' ? r.rxBypassOnPureSignal : false,
    profiles: Array.isArray(r.profiles) ? r.profiles.map(parseProfile) : [],
    active: parseActive(r.active),
    warnings: Array.isArray(r.warnings)
      ? r.warnings.filter((w): w is string => typeof w === 'string')
      : [],
  };
}

async function fetchRfFilters(): Promise<RfFilterSettings> {
  const res = await fetch('/api/radio/rf-filters');
  if (!res.ok) throw new Error(`GET /api/radio/rf-filters -> ${res.status}`);
  return parse(await res.json());
}

async function putRfFilters(settings: RfFilterSettings): Promise<RfFilterSettings> {
  const res = await fetch('/api/radio/rf-filters', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      customMatrixEnabled: settings.customMatrixEnabled,
      rxBypassAll: settings.rxBypassAll,
      rxBypassOnTx: settings.rxBypassOnTx,
      rxBypassOnPureSignal: settings.rxBypassOnPureSignal,
      profiles: settings.profiles,
    }),
  });
  if (!res.ok) throw new Error(`PUT /api/radio/rf-filters -> ${res.status}`);
  return parse(await res.json());
}

async function resetRfFilters(): Promise<RfFilterSettings> {
  const res = await fetch('/api/radio/rf-filters/reset', { method: 'POST' });
  if (!res.ok) throw new Error(`POST /api/radio/rf-filters/reset -> ${res.status}`);
  return parse(await res.json());
}

type RfFilterStore = {
  settings: RfFilterSettings;
  loaded: boolean;
  inflight: boolean;
  error: string | null;
  load: () => Promise<void>;
  save: (next: RfFilterSettings) => Promise<void>;
  reset: () => Promise<void>;
};

export const useRfFilterStore = create<RfFilterStore>((set, get) => ({
  settings: DEFAULT_SETTINGS,
  loaded: false,
  inflight: false,
  error: null,

  load: async () => {
    set({ inflight: true, error: null });
    try {
      const settings = await fetchRfFilters();
      set({ settings, loaded: true, inflight: false });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : String(err), inflight: false });
    }
  },

  save: async (next) => {
    const prev = get().settings;
    set({ settings: next, inflight: true, error: null });
    try {
      const settings = await putRfFilters(next);
      set({ settings, loaded: true, inflight: false });
    } catch (err) {
      set({
        settings: prev,
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },

  reset: async () => {
    set({ inflight: true, error: null });
    try {
      const settings = await resetRfFilters();
      set({ settings, loaded: true, inflight: false });
    } catch (err) {
      set({ error: err instanceof Error ? err.message : String(err), inflight: false });
    }
  },
}));
