// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { create } from 'zustand';

export type SpotData = {
  callsign: string;
  mode: string;
  freqHz: number;
  argb: number;
  comment?: string;
};

type SpotStore = {
  spots: SpotData[];
  setSpots: (spots: SpotData[]) => void;
};

export const useSpotStore = create<SpotStore>()((set) => ({
  spots: [],
  setSpots: (spots) => set({ spots }),
}));
