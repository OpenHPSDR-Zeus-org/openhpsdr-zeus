// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Per-operator config for the analog S-meter tile. Persisted to localStorage
// under `zeus-analog-meter` so dial preferences (which scales/ticks are
// shown, dBm visibility, ballistics) survive a reload.

import { create } from 'zustand';
import { persist } from 'zustand/middleware';

export type AnalogMeterMode = 'rx' | 'tx';

export interface AnalogMeterConfig {
  scaleS: boolean;
  scalePo: boolean;
  scaleSwr: boolean;
  showDbm: boolean;
  /** SWR threshold above which the readout switches to --tx. */
  swrAlarm: number;
  /** Needle ballistics (seconds). */
  attack: number;
  decay: number;
  /** Pre-ballistic moving-average window, in samples (loop runs at rAF rate). */
  avg: number;
  peakHold: boolean;
  /** When true, an image of Zeus fades in over the S-meter face as the
   *  signal approaches S9+20, with a blue flicker glow. Pure visual flair —
   *  no protocol/DSP impact. Off by default. */
  zeusMode: boolean;
}

export interface AnalogMeterState extends AnalogMeterConfig {
  setScale: (id: 's' | 'po' | 'swr', on: boolean) => void;
  setShowDbm: (on: boolean) => void;
  setSwrAlarm: (r: number) => void;
  setAttack: (s: number) => void;
  setDecay: (s: number) => void;
  setAvg: (n: number) => void;
  setPeakHold: (on: boolean) => void;
  setZeusMode: (on: boolean) => void;
  resetBallistics: () => void;
}

export const ANALOG_METER_DEFAULTS: AnalogMeterConfig = {
  scaleS: true,
  scalePo: true,
  scaleSwr: true,
  showDbm: true,
  swrAlarm: 3.0,
  attack: 0.05,
  decay: 0.6,
  avg: 6,
  peakHold: true,
  zeusMode: false,
};

export const useAnalogMeterStore = create<AnalogMeterState>()(
  persist(
    (set) => ({
      ...ANALOG_METER_DEFAULTS,
      setScale: (id, on) =>
        set((s) => {
          if (id === 's') return { ...s, scaleS: on };
          if (id === 'po') return { ...s, scalePo: on };
          return { ...s, scaleSwr: on };
        }),
      setShowDbm: (on) => set({ showDbm: on }),
      setSwrAlarm: (r) => set({ swrAlarm: Math.max(1.5, Math.min(5, r)) }),
      setAttack: (s) => set({ attack: Math.max(0.005, Math.min(0.5, s)) }),
      setDecay: (s) => set({ decay: Math.max(0.05, Math.min(2, s)) }),
      setAvg: (n) => set({ avg: Math.max(1, Math.min(64, Math.round(n))) }),
      setPeakHold: (on) => set({ peakHold: on }),
      setZeusMode: (on) => set({ zeusMode: on }),
      resetBallistics: () =>
        set({
          attack: ANALOG_METER_DEFAULTS.attack,
          decay: ANALOG_METER_DEFAULTS.decay,
          avg: ANALOG_METER_DEFAULTS.avg,
          peakHold: ANALOG_METER_DEFAULTS.peakHold,
        }),
    }),
    { name: 'zeus-analog-meter' },
  ),
);
