// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

// REST client for the MIDI controller + Stream Deck subsystem (issue #18).
// Mirrors api/cat.ts. Enum values arrive as PascalCase strings (the backend's
// JsonStringEnumConverter); property names are camelCase.

import { ApiError } from './client';

export type MidiControlType = 'Button' | 'KnobOrSlider' | 'Wheel';

export type MidiCommandInfo = {
  command: string;
  label: string;
  controlType: MidiControlType;
  isToggle: boolean;
  supported: boolean;
};

export type MidiDevice = { name: string; connected: boolean };

export type StreamDeckDevice = {
  name: string;
  serial: string;
  buttonCount: number;
  connected: boolean;
};

export type MidiMapping = {
  deviceName: string;
  controlId: string;
  controlType: MidiControlType;
  command: string;
  min: number;
  max: number;
  toggle: boolean;
};

export type StreamDeckMapping = {
  serial: string;
  buttonIndex: number;
  command: string;
};

export type MidiBindings = {
  version: number;
  mappings: MidiMapping[];
  streamDeckMappings: StreamDeckMapping[];
};

export type MidiConfig = {
  enabled: boolean;
  bindings: MidiBindings;
};

export type MidiStatus = {
  enabled: boolean;
  midiEngineAvailable: boolean;
  streamDeckEngineAvailable: boolean;
  midiDevices: MidiDevice[];
  streamDeckDevices: StreamDeckDevice[];
  learning: boolean;
};

export type MidiLearnFrame = {
  deviceName: string;
  controlId: string;
  controlType: MidiControlType;
  value: number;
  delta: number;
};

export const EMPTY_BINDINGS: MidiBindings = {
  version: 1,
  mappings: [],
  streamDeckMappings: [],
};

async function jsonFetch<T>(input: RequestInfo, init: RequestInit | undefined, parse: (raw: unknown) => T): Promise<T> {
  const res = await fetch(input, init);
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = (await res.json()) as unknown;
      if (body && typeof body === 'object' && 'error' in body && typeof (body as { error: unknown }).error === 'string') {
        message = (body as { error: string }).error;
      }
    } catch {
      /* non-JSON */
    }
    throw new ApiError(res.status, message);
  }
  return parse((await res.json()) as unknown);
}

function asArray(v: unknown): unknown[] {
  return Array.isArray(v) ? v : [];
}

function normalizeBindings(raw: unknown): MidiBindings {
  const r = (raw ?? {}) as Record<string, unknown>;
  const mappings = asArray(r.mappings).map((m) => {
    const mm = (m ?? {}) as Record<string, unknown>;
    return {
      deviceName: typeof mm.deviceName === 'string' ? mm.deviceName : '',
      controlId: typeof mm.controlId === 'string' ? mm.controlId : '',
      controlType: (mm.controlType as MidiControlType) ?? 'Button',
      command: typeof mm.command === 'string' ? mm.command : '',
      min: typeof mm.min === 'number' ? mm.min : 0,
      max: typeof mm.max === 'number' ? mm.max : 127,
      toggle: Boolean(mm.toggle),
    } satisfies MidiMapping;
  });
  const streamDeckMappings = asArray(r.streamDeckMappings).map((m) => {
    const mm = (m ?? {}) as Record<string, unknown>;
    return {
      serial: typeof mm.serial === 'string' ? mm.serial : '',
      buttonIndex: typeof mm.buttonIndex === 'number' ? mm.buttonIndex : 0,
      command: typeof mm.command === 'string' ? mm.command : '',
    } satisfies StreamDeckMapping;
  });
  return {
    version: typeof r.version === 'number' ? r.version : 1,
    mappings,
    streamDeckMappings,
  };
}

function normalizeStatus(raw: unknown): MidiStatus {
  const r = (raw ?? {}) as Record<string, unknown>;
  const midiDevices = asArray(r.midiDevices).map((d) => {
    const dd = (d ?? {}) as Record<string, unknown>;
    return { name: typeof dd.name === 'string' ? dd.name : '', connected: Boolean(dd.connected) };
  });
  const streamDeckDevices = asArray(r.streamDeckDevices).map((d) => {
    const dd = (d ?? {}) as Record<string, unknown>;
    return {
      name: typeof dd.name === 'string' ? dd.name : '',
      serial: typeof dd.serial === 'string' ? dd.serial : '',
      buttonCount: typeof dd.buttonCount === 'number' ? dd.buttonCount : 0,
      connected: Boolean(dd.connected),
    };
  });
  return {
    enabled: Boolean(r.enabled),
    midiEngineAvailable: Boolean(r.midiEngineAvailable),
    streamDeckEngineAvailable: Boolean(r.streamDeckEngineAvailable),
    midiDevices,
    streamDeckDevices,
    learning: Boolean(r.learning),
  };
}

function normalizeConfig(raw: unknown): MidiConfig {
  const r = (raw ?? {}) as Record<string, unknown>;
  return { enabled: Boolean(r.enabled), bindings: normalizeBindings(r.bindings) };
}

function normalizeCommands(raw: unknown): MidiCommandInfo[] {
  return asArray(raw).map((c) => {
    const cc = (c ?? {}) as Record<string, unknown>;
    return {
      command: typeof cc.command === 'string' ? cc.command : '',
      label: typeof cc.label === 'string' ? cc.label : '',
      controlType: (cc.controlType as MidiControlType) ?? 'Button',
      isToggle: Boolean(cc.isToggle),
      supported: Boolean(cc.supported),
    } satisfies MidiCommandInfo;
  });
}

export function getMidiStatus(signal?: AbortSignal): Promise<MidiStatus> {
  return jsonFetch('/api/midi/status', { signal }, normalizeStatus);
}

export function getMidiConfig(signal?: AbortSignal): Promise<MidiConfig> {
  return jsonFetch('/api/midi/config', { signal }, normalizeConfig);
}

export function putMidiConfig(cfg: MidiConfig, signal?: AbortSignal): Promise<MidiStatus> {
  return jsonFetch(
    '/api/midi/config',
    {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(cfg),
      signal,
    },
    normalizeStatus,
  );
}

export function getMidiCommands(signal?: AbortSignal): Promise<MidiCommandInfo[]> {
  return jsonFetch('/api/midi/commands', { signal }, normalizeCommands);
}

export function startMidiLearn(signal?: AbortSignal): Promise<MidiStatus> {
  return jsonFetch('/api/midi/learn/start', { method: 'POST', signal }, normalizeStatus);
}

export function stopMidiLearn(signal?: AbortSignal): Promise<MidiStatus> {
  return jsonFetch('/api/midi/learn/stop', { method: 'POST', signal }, normalizeStatus);
}
