// SPDX-License-Identifier: GPL-2.0-or-later
//
// UI state for the DeepCW decoder panel. Unlike the old classic decoder
// (which streamed one character at a time), the neural model decodes the
// whole rolling window each pass, so `text` is the latest full-window
// transcript — replaced, not appended.

import { create } from 'zustand';

export type DeepCwState = 'idle' | 'listening' | 'held';

export type DeepCwStore = {
  state: DeepCwState;
  /** Continuous decoded transcript — appended as new audio is decoded, not
   *  replaced each pass (the model re-decodes an overlapping window, so the
   *  hook stitches only the newly-revealed tail). Capped to maxChars. */
  text: string;
  maxChars: number;
  modelLoaded: boolean;
  loadError: string | null;
  isDecoding: boolean;
  /** Seconds of audio the model decodes each pass (5–20; engine bounds). */
  windowSeconds: number;
  /** Bumps whenever the transcript is reset (CLEAR), so the stitching hook
   *  re-anchors instead of appending against a stale previous window. */
  anchorSeq: number;

  setEnabled: (enabled: boolean) => void;
  toggleHold: () => void;
  clear: () => void;
  /** Append the newly-decoded tail to the transcript (no-op while held). */
  appendDecoded: (chunk: string) => void;
  setModelLoaded: (loaded: boolean) => void;
  setLoadError: (err: string | null) => void;
  setDecoding: (decoding: boolean) => void;
  setWindowSeconds: (seconds: number) => void;
};

export const DECODE_WINDOW_OPTIONS = [6, 10, 20] as const;

export const useDeepCwStore = create<DeepCwStore>((set) => ({
  state: 'idle',
  text: '',
  maxChars: 4000,
  modelLoaded: false,
  loadError: null,
  isDecoding: false,
  windowSeconds: 10,
  anchorSeq: 0,

  setEnabled: (enabled) =>
    set((s) => ({
      state: s.state === 'held' && enabled ? 'held' : enabled ? 'listening' : 'idle',
    })),

  toggleHold: () =>
    set((s) => ({
      state: s.state === 'listening' ? 'held' : s.state === 'held' ? 'listening' : s.state,
    })),

  clear: () => set((s) => ({ text: '', anchorSeq: s.anchorSeq + 1 })),

  // Hold freezes the displayed transcript: ignore new decodes while held.
  appendDecoded: (chunk) =>
    set((s) => (s.state === 'held' || chunk === '' ? {} : { text: (s.text + chunk).slice(-s.maxChars) })),

  setModelLoaded: (loaded) => set({ modelLoaded: loaded }),
  setLoadError: (err) => set({ loadError: err }),
  setDecoding: (decoding) => set({ isDecoding: decoding }),
  setWindowSeconds: (seconds) => set({ windowSeconds: seconds }),
}));
