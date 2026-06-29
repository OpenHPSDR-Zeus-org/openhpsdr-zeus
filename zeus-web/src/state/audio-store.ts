// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Global (per-radio, NOT per-band) TX-audio SOURCE (external-ports plan, §2/§11).
// Mirrors /api/radio/audio, which is server-authoritative: the backend reads
// AudioSettingsStore, CLAMPS the source against the connected board, and pushes
// the resolved selection to the live radio through PushAudioFrontEnd, so the
// frontend never clobbers the server on connect — it only loads + PUTs operator
// edits. The GET response carries per-board source-availability gates so the
// single-select picker offers only the sources the board has (Host is always
// available).
//
// This replaces the prior four-bool model (lineIn/balancedInput peers); line-in
// and balanced are now distinct `source` enum values.

import { create } from 'zustand';

// Must match Zeus.Contracts.TxAudioSource (string round-trips via STJ enum
// names — the wire carries the member name, e.g. "RadioMic").
export type TxAudioSource = 'Host' | 'RadioMic' | 'RadioLineIn' | 'RadioBalancedXlr';

const SOURCES: readonly TxAudioSource[] = ['Host', 'RadioMic', 'RadioLineIn', 'RadioBalancedXlr'];

export interface AudioFrontEnd {
  // Per-board source-availability gates (read-only / server-set).
  hasOnboardCodec: boolean;
  hermesLite2MicFrontEnd: boolean;
  hasRadioLineIn: boolean;
  hasBalancedXlr: boolean;
  hasMicBias: boolean;
  // The resolved (board-clamped) source + its params.
  source: TxAudioSource;
  micBoost: boolean;
  micBias: boolean;
  lineInGain: number;
}

const DEFAULT_AUDIO: AudioFrontEnd = {
  hasOnboardCodec: false,
  hermesLite2MicFrontEnd: false,
  hasRadioLineIn: false,
  hasBalancedXlr: false,
  hasMicBias: false,
  source: 'Host',
  micBoost: false,
  micBias: false,
  lineInGain: 0,
};

function parseBool(v: unknown, fallback: boolean): boolean {
  return typeof v === 'boolean' ? v : fallback;
}

function parseSource(v: unknown): TxAudioSource {
  // Tolerate the numeric enum form (0..3) as well as the STJ member-name form.
  if (typeof v === 'string' && (SOURCES as readonly string[]).includes(v)) {
    return v as TxAudioSource;
  }
  if (typeof v === 'number' && Number.isInteger(v) && v >= 0 && v < SOURCES.length) {
    return SOURCES[v] ?? 'Host';
  }
  return 'Host';
}

function parse(raw: unknown): AudioFrontEnd {
  if (!raw || typeof raw !== 'object') return DEFAULT_AUDIO;
  const r = raw as Record<string, unknown>;
  const gain = typeof r.lineInGain === 'number' ? r.lineInGain : 0;
  return {
    hasOnboardCodec: parseBool(r.hasOnboardCodec, false),
    hermesLite2MicFrontEnd: parseBool(r.hermesLite2MicFrontEnd, false),
    hasRadioLineIn: parseBool(r.hasRadioLineIn, false),
    hasBalancedXlr: parseBool(r.hasBalancedXlr, false),
    hasMicBias: parseBool(r.hasMicBias, false),
    source: parseSource(r.source),
    micBoost: parseBool(r.micBoost, false),
    micBias: parseBool(r.micBias, false),
    lineInGain: Math.min(31, Math.max(0, Math.round(gain))),
  };
}

// The mutable subset an operator can PUT (the gates are read-only / server-set).
export type AudioFrontEndEdit = Pick<
  AudioFrontEnd,
  'source' | 'micBoost' | 'micBias' | 'lineInGain'
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
    // store. The PUT returns the canonical settings (incl. board-clamped source
    // + clamped gain) which we adopt so the local view stays in lockstep with
    // the server.
    const prev = get().settings;
    const edit: AudioFrontEndEdit = {
      source: patch.source ?? prev.source,
      micBoost: patch.micBoost ?? prev.micBoost,
      micBias: patch.micBias ?? prev.micBias,
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
