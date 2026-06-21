// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { createContext, useEffect, type ReactNode } from 'react';
import { useBandPlanStore } from '../state/bandPlan';
import type { BandRegion, BandSegment, RxMode } from '../api/bands';

export type BandPlanContextValue = {
  regions: BandRegion[];
  currentRegionId: string;
  segments: BandSegment[];
  txGuardIgnore: boolean;
  inBand: (freqHz: number, mode: RxMode) => boolean;
  getSegment: (freqHz: number) => BandSegment | null;
};

const BandPlanContext = createContext<BandPlanContextValue | null>(null);

export function BandPlanProvider({ children }: { children: ReactNode }) {
  const refresh = useBandPlanStore((s) => s.refresh);
  const regions = useBandPlanStore((s) => s.regions);
  const currentRegionId = useBandPlanStore((s) => s.currentRegionId);
  const segments = useBandPlanStore((s) => s.segments);
  const txGuardIgnore = useBandPlanStore((s) => s.txGuardIgnore);
  const inBandFn = useBandPlanStore((s) => s.inBand);
  const getSegmentFn = useBandPlanStore((s) => s.getSegment);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  return (
    <BandPlanContext.Provider
      value={{ regions, currentRegionId, segments, txGuardIgnore, inBand: inBandFn, getSegment: getSegmentFn }}
    >
      {children}
    </BandPlanContext.Provider>
  );
}
