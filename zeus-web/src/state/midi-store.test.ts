// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { MidiStatus } from '../api/midi';

vi.mock('../api/midi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/midi')>();
  return {
    ...actual, // keep EMPTY_BINDINGS and types
    getMidiStatus: vi.fn(),
    getMidiConfig: vi.fn(),
    getMidiCommands: vi.fn(),
    putMidiConfig: vi.fn(),
    startMidiLearn: vi.fn(),
    stopMidiLearn: vi.fn(),
  };
});

import {
  EMPTY_BINDINGS,
  getMidiCommands,
  putMidiConfig,
  startMidiLearn,
  stopMidiLearn,
} from '../api/midi';
import { useMidiStore } from './midi-store';

const mockCommands = vi.mocked(getMidiCommands);
const mockPut = vi.mocked(putMidiConfig);
const mockStart = vi.mocked(startMidiLearn);
const mockStop = vi.mocked(stopMidiLearn);

function status(over: Partial<MidiStatus> = {}): MidiStatus {
  return {
    enabled: false,
    midiEngineAvailable: true,
    streamDeckEngineAvailable: false,
    midiDevices: [],
    streamDeckDevices: [],
    learning: false,
    ...over,
  };
}

beforeEach(() => {
  // jsdom localStorage stub (vitest local has none — CI-only failures otherwise).
  const mem: Record<string, string> = {};
  vi.stubGlobal('localStorage', {
    getItem: (k: string) => (k in mem ? mem[k] : null),
    setItem: (k: string, v: string) => {
      mem[k] = v;
    },
    removeItem: (k: string) => {
      delete mem[k];
    },
    clear: () => {
      for (const k of Object.keys(mem)) delete mem[k];
    },
  });
  vi.clearAllMocks();
  useMidiStore.setState({
    config: { enabled: false, bindings: EMPTY_BINDINGS },
    status: null,
    commands: [],
    lastLearn: null,
  });
  mockPut.mockResolvedValue(status());
});

describe('midi-store', () => {
  it('refreshCommands populates the catalogue', async () => {
    mockCommands.mockResolvedValue([
      { command: 'Band40m', label: 'Band 40m', controlType: 'Button', isToggle: false, supported: true },
    ]);
    await useMidiStore.getState().refreshCommands();
    expect(useMidiStore.getState().commands).toHaveLength(1);
    expect(useMidiStore.getState().commands[0]?.command).toBe('Band40m');
  });

  it('setEnabled PUTs the enable flag and stores returned status', async () => {
    mockPut.mockResolvedValue(status({ enabled: true }));
    await useMidiStore.getState().setEnabled(true);
    expect(mockPut).toHaveBeenCalledWith(expect.objectContaining({ enabled: true }));
    expect(useMidiStore.getState().config.enabled).toBe(true);
    expect(useMidiStore.getState().status?.enabled).toBe(true);
  });

  it('upsertMapping adds then replaces a binding by (device, controlId)', async () => {
    await useMidiStore.getState().upsertMapping({
      deviceName: 'DJ',
      controlId: 'cc:0:7',
      controlType: 'KnobOrSlider',
      command: 'SetAfGain',
      min: 0,
      max: 127,
      toggle: false,
    });
    expect(useMidiStore.getState().config.bindings.mappings).toHaveLength(1);

    // Same control id → replace, not duplicate.
    await useMidiStore.getState().upsertMapping({
      deviceName: 'DJ',
      controlId: 'cc:0:7',
      controlType: 'KnobOrSlider',
      command: 'MicGain',
      min: 0,
      max: 127,
      toggle: false,
    });
    const mappings = useMidiStore.getState().config.bindings.mappings;
    expect(mappings).toHaveLength(1);
    expect(mappings[0]?.command).toBe('MicGain');
  });

  it('removeMapping drops the binding', async () => {
    await useMidiStore.getState().upsertMapping({
      deviceName: 'DJ',
      controlId: 'note:0:60',
      controlType: 'Button',
      command: 'MoxOnOff',
      min: 0,
      max: 127,
      toggle: true,
    });
    await useMidiStore.getState().removeMapping('DJ', 'note:0:60');
    expect(useMidiStore.getState().config.bindings.mappings).toHaveLength(0);
  });

  it('ingestLearn records the last control event', () => {
    useMidiStore.getState().ingestLearn({
      deviceName: 'DJ',
      controlId: 'cc:0:9',
      controlType: 'KnobOrSlider',
      value: 64,
      delta: 0,
    });
    expect(useMidiStore.getState().lastLearn?.controlId).toBe('cc:0:9');
  });

  it('startLearn/stopLearn flip status and clear lastLearn', async () => {
    mockStart.mockResolvedValue(status({ learning: true }));
    mockStop.mockResolvedValue(status({ learning: false }));
    useMidiStore.getState().ingestLearn({
      deviceName: 'DJ', controlId: 'cc:0:1', controlType: 'Wheel', value: 0, delta: 1,
    });
    await useMidiStore.getState().startLearn();
    expect(useMidiStore.getState().status?.learning).toBe(true);
    expect(useMidiStore.getState().lastLearn).toBeNull();
    await useMidiStore.getState().stopLearn();
    expect(useMidiStore.getState().status?.learning).toBe(false);
  });
});
