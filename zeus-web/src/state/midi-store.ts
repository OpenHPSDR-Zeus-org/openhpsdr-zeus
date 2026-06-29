// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { create } from 'zustand';
import {
  EMPTY_BINDINGS,
  getMidiCommands,
  getMidiConfig,
  getMidiStatus,
  putMidiConfig,
  startMidiLearn,
  stopMidiLearn,
  type MidiBindings,
  type MidiCommandInfo,
  type MidiConfig,
  type MidiLearnFrame,
  type MidiMapping,
  type MidiStatus,
  type StreamDeckMapping,
} from '../api/midi';

// The backend (MidiConfigStore on disk) is the source of truth for MIDI runtime
// config. The panel initialises from /api/midi/config once it arrives. Do NOT
// seed from localStorage or auto-PUT on load — same lesson as the CAT/TCI
// stores (a localStorage/disk drift produced a phantom "changed" warning).
const DEFAULT_CONFIG: MidiConfig = {
  enabled: false,
  bindings: EMPTY_BINDINGS,
};

export type MidiStoreState = {
  config: MidiConfig;
  status: MidiStatus | null;
  commands: MidiCommandInfo[];
  // The most recent Learn-mode control event pushed over the hub, so the panel
  // can highlight the live control and bind it. Cleared when learn stops.
  lastLearn: MidiLearnFrame | null;

  refreshStatus: () => Promise<void>;
  refreshCommands: () => Promise<void>;
  refreshConfig: () => Promise<void>;
  setEnabled: (enabled: boolean) => Promise<void>;
  setBindings: (bindings: MidiBindings) => Promise<void>;
  upsertMapping: (mapping: MidiMapping) => Promise<void>;
  removeMapping: (deviceName: string, controlId: string) => Promise<void>;
  upsertStreamDeckMapping: (mapping: StreamDeckMapping) => Promise<void>;
  removeStreamDeckMapping: (serial: string, buttonIndex: number) => Promise<void>;
  startLearn: () => Promise<void>;
  stopLearn: () => Promise<void>;
  ingestLearn: (frame: MidiLearnFrame) => void;
};

export const useMidiStore = create<MidiStoreState>((set, get) => ({
  config: DEFAULT_CONFIG,
  status: null,
  commands: [],
  lastLearn: null,

  refreshStatus: async () => {
    try {
      const status = await getMidiStatus();
      set({ status });
    } catch {
      /* transient — next poll recovers */
    }
  },

  refreshCommands: async () => {
    try {
      const commands = await getMidiCommands();
      set({ commands });
    } catch {
      /* transient */
    }
  },

  refreshConfig: async () => {
    try {
      const config = await getMidiConfig();
      set({ config });
    } catch {
      /* transient */
    }
  },

  setEnabled: async (enabled) => {
    const next: MidiConfig = { ...get().config, enabled };
    const status = await putMidiConfig(next);
    set({ config: next, status });
  },

  setBindings: async (bindings) => {
    const next: MidiConfig = { ...get().config, bindings };
    const status = await putMidiConfig(next);
    set({ config: next, status });
  },

  upsertMapping: async (mapping) => {
    const cur = get().config.bindings;
    const mappings = cur.mappings.filter(
      (m) => !(m.deviceName === mapping.deviceName && m.controlId === mapping.controlId),
    );
    mappings.push(mapping);
    await get().setBindings({ ...cur, mappings });
  },

  removeMapping: async (deviceName, controlId) => {
    const cur = get().config.bindings;
    const mappings = cur.mappings.filter((m) => !(m.deviceName === deviceName && m.controlId === controlId));
    await get().setBindings({ ...cur, mappings });
  },

  upsertStreamDeckMapping: async (mapping) => {
    const cur = get().config.bindings;
    const streamDeckMappings = cur.streamDeckMappings.filter(
      (m) => !(m.serial === mapping.serial && m.buttonIndex === mapping.buttonIndex),
    );
    streamDeckMappings.push(mapping);
    await get().setBindings({ ...cur, streamDeckMappings });
  },

  removeStreamDeckMapping: async (serial, buttonIndex) => {
    const cur = get().config.bindings;
    const streamDeckMappings = cur.streamDeckMappings.filter(
      (m) => !(m.serial === serial && m.buttonIndex === buttonIndex),
    );
    await get().setBindings({ ...cur, streamDeckMappings });
  },

  startLearn: async () => {
    const status = await startMidiLearn();
    set({ status, lastLearn: null });
  },

  stopLearn: async () => {
    const status = await stopMidiLearn();
    set({ status, lastLearn: null });
  },

  ingestLearn: (frame) => set({ lastLearn: frame }),
}));
