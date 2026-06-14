// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Global (per-radio, NOT per-band) audio front-end (external-ports plan,
// Phase 4). Mirrors /api/radio/audio, which is server-authoritative: the
// backend reads AudioSettingsStore and pushes the selection to the live radio
// through PushAudioFrontEnd, so the frontend never clobbers the server on
// connect — it only loads + PUTs operator edits.
//
// The GET response carries the capability gates (hasOnboardCodec for the
// Hermes-class mic-boost / line-in controls; hermesLite2MicFrontEnd for the
// HL2 mic_trs(balanced) / mic_bias / line-in-gain controls) so the panel can
// render the right controls.

import { create } from 'zustand';

export interface AudioFrontEnd {
  hasOnboardCodec: boolean;
  hermesLite2MicFrontEnd: boolean;
  lineIn: boolean;
  micBoost: boolean;
  micBias: boolean;
  balancedInput: boolean;
  lineInGain: number;
}

const DEFAULT_AUDIO: AudioFrontEnd = {
  hasOnboardCodec: false,
  hermesLite2MicFrontEnd: false,
  lineIn: false,
  micBoost: false,
  micBias: false,
  balancedInput: false,
  lineInGain: 0,
};

function parseBool(v: unknown, fallback: boolean): boolean {
  return typeof v === 'boolean' ? v : fallback;
}

function parse(raw: unknown): AudioFrontEnd {
  if (!raw || typeof raw !== 'object') return DEFAULT_AUDIO;
  const r = raw as Record<string, unknown>;
  const gain = typeof r.lineInGain === 'number' ? r.lineInGain : 0;
  return {
    hasOnboardCodec: parseBool(r.hasOnboardCodec, false),
    hermesLite2MicFrontEnd: parseBool(r.hermesLite2MicFrontEnd, false),
    lineIn: parseBool(r.lineIn, false),
    micBoost: parseBool(r.micBoost, false),
    micBias: parseBool(r.micBias, false),
    balancedInput: parseBool(r.balancedInput, false),
    lineInGain: Math.min(31, Math.max(0, Math.round(gain))),
  };
}

// The mutable subset an operator can PUT (the gates are read-only / server-set).
export type AudioFrontEndEdit = Pick<
  AudioFrontEnd,
  'lineIn' | 'micBoost' | 'micBias' | 'balancedInput' | 'lineInGain'
>;

export async function fetchAudioFrontEnd(
  signal?: AbortSignal,
): Promise<AudioFrontEnd> {
  const res = await fetch('/api/radio/audio', { signal });
  if (!res.ok) throw new Error(`GET /api/radio/audio → ${res.status}`);
  return parse(await res.json());
}

export async function updateAudioFrontEnd(
  edit: AudioFrontEndEdit,
  signal?: AbortSignal,
): Promise<AudioFrontEnd> {
  const res = await fetch('/api/radio/audio', {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(edit),
    signal,
  });
  if (!res.ok) throw new Error(`PUT /api/radio/audio → ${res.status}`);
  return parse(await res.json());
}

type AudioStore = {
  settings: AudioFrontEnd;
  loaded: boolean;
  inflight: boolean;
  error: string | null;
  load: () => Promise<void>;
  update: (patch: Partial<AudioFrontEndEdit>) => Promise<void>;
};

export const useAudioStore = create<AudioStore>((set, get) => ({
  settings: DEFAULT_AUDIO,
  loaded: false,
  inflight: false,
  error: null,

  load: async () => {
    set({ inflight: true, error: null });
    try {
      const s = await fetchAudioFrontEnd();
      set({ settings: s, loaded: true, inflight: false });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : String(err),
        inflight: false,
      });
    }
  },

  update: async (patch) => {
    // Optimistic local update, rollback on error — same idiom as the antenna
    // store. The PUT returns the canonical settings (incl. clamped gain) which
    // we adopt so the local view stays in lockstep with the server.
    const prev = get().settings;
    const edit: AudioFrontEndEdit = {
      lineIn: patch.lineIn ?? prev.lineIn,
      micBoost: patch.micBoost ?? prev.micBoost,
      micBias: patch.micBias ?? prev.micBias,
      balancedInput: patch.balancedInput ?? prev.balancedInput,
      lineInGain: patch.lineInGain ?? prev.lineInGain,
    };
    set({ settings: { ...prev, ...edit }, inflight: true, error: null });
    try {
      const s = await updateAudioFrontEnd(edit);
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
