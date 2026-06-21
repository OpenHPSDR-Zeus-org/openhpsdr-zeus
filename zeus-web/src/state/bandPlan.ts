// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { create } from 'zustand';
import {
  fetchCurrent,
  fetchRegions,
  setCurrentRegion,
  savePlan,
  resetPlan,
  setTxGuardIgnore,
  inBand,
  binarySearchSegment,
  type BandRegion,
  type BandSegment,
  type RxMode,
} from '../api/bands';

export type BandPlanState = {
  regions: BandRegion[];
  currentRegionId: string;
  segments: BandSegment[];
  txGuardIgnore: boolean;
  loading: boolean;
  error: string | null;

  refresh: () => Promise<void>;
  changeRegion: (regionId: string) => Promise<void>;
  saveOverride: (regionId: string, segments: BandSegment[]) => Promise<void>;
  resetOverride: (regionId: string) => Promise<void>;
  setGuardIgnore: (ignore: boolean) => Promise<void>;

  inBand: (freqHz: number, mode: RxMode) => boolean;
  getSegment: (freqHz: number) => BandSegment | null;
};

export const useBandPlanStore = create<BandPlanState>()((set, get) => ({
  regions: [],
  currentRegionId: 'IARU_R1',
  segments: [],
  txGuardIgnore: false,
  loading: false,
  error: null,

  refresh: async () => {
    set({ loading: true, error: null });
    try {
      const [current, regions] = await Promise.all([fetchCurrent(), fetchRegions()]);
      set({
        regions,
        currentRegionId: current.regionId,
        segments: current.segments,
        txGuardIgnore: current.txGuardIgnore,
        loading: false,
      });
    } catch (e) {
      set({ loading: false, error: String(e) });
    }
  },

  changeRegion: async (regionId) => {
    await setCurrentRegion(regionId);
    await get().refresh();
  },

  saveOverride: async (regionId, segments) => {
    await savePlan(regionId, segments);
    await get().refresh();
  },

  resetOverride: async (regionId) => {
    await resetPlan(regionId);
    await get().refresh();
  },

  setGuardIgnore: async (ignore) => {
    await setTxGuardIgnore(ignore);
    set({ txGuardIgnore: ignore });
  },

  inBand: (freqHz, mode) => inBand(get().segments, freqHz, mode),
  getSegment: (freqHz) => binarySearchSegment(get().segments, freqHz),
}));
