// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Radio-side speaker output (codec-equipped radios, both protocols). Mirrors
// /api/radio/speaker-output: when ON, the backend sends demodulated RX audio
// to the radio's onboard codec so its speaker / headphone / line-out jacks
// play the receive audio. Under Protocol 1 that travels in the EP2 frame's
// L/R slots; under Protocol 2 it travels as UDP packets to port 1028 (issue
// #1122). `available` is true while any codec-equipped radio is connected
// (HL2 has no codec and is excluded); the toggle is otherwise inert. Default
// is OFF (opt-in) so an operator who already hears RX audio host-side isn't
// surprised by doubled audio.

import { create } from 'zustand';

export interface RadioSpeakerOutput {
  enabled: boolean;
  available: boolean;
}

const DEFAULT: RadioSpeakerOutput = { enabled: false, available: false };

function parseBool(v: unknown, fallback: boolean): boolean {
  return typeof v === 'boolean' ? v : fallback;
}

function parse(raw: unknown): RadioSpeakerOutput {
  if (!raw || typeof raw !== 'object') return DEFAULT;
  const r = raw as Record<string, unknown>;
  return {
    enabled: parseBool(r.enabled, false),
    available: parseBool(r.available, false),
  };
}

export async function fetchRadioSpeakerOutput(
  signal?: AbortSignal,
): Promise<RadioSpeakerOutput> {
  const res = await fetch('/api/radio/speaker-output', { signal });
  if (!res.ok) throw new Error(`GET /api/radio/speaker-output → ${res.status}`);
  return parse(await res.json());
}

export async function updateRadioSpeakerOutput(
  enabled: boolean,
  signal?: AbortSignal,
): Promise<RadioSpeakerOutput> {
  const res = await fetch('/api/radio/speaker-output', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ enabled }),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/radio/speaker-output → ${res.status}`);
  return parse(await res.json());
}

type RadioSpeakerStore = {
  settings: RadioSpeakerOutput;
  loaded: boolean;
  inflight: boolean;
  error: string | null;
  load: () => Promise<void>;
  setEnabled: (enabled: boolean) => Promise<void>;
};

export const useRadioSpeakerStore = create<RadioSpeakerStore>((set, get) => ({
  settings: DEFAULT,
  loaded: false,
  inflight: false,
  error: null,

  load: async () => {
    set({ inflight: true, error: null });
    try {
      const s = await fetchRadioSpeakerOutput();
      set({ settings: s, loaded: true, inflight: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },

  setEnabled: async (enabled) => {
    // Optimistic local update, rollback on error — same idiom as the audio
    // store. The PUT returns the canonical {enabled, available}.
    const prev = get().settings;
    set({ settings: { ...prev, enabled }, inflight: true, error: null });
    try {
      const s = await updateRadioSpeakerOutput(enabled);
      set({ settings: s, inflight: false });
    } catch (err) {
      set({
        settings: prev,
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },
}));
