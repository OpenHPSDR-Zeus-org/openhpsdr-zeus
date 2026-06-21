// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { create } from 'zustand';
import { fetchSpaceWeather, type SpaceWeatherSnapshot } from '../api/spacewx';

interface SpaceWxState {
  data: SpaceWeatherSnapshot | null;
  loading: boolean;
  /** Last successful/attempted fetch (epoch ms), for "updated N min ago". */
  lastFetch: number | null;
  load: (signal?: AbortSignal) => Promise<void>;
}

// Shared store so multiple panels (or future consumers) reuse one snapshot.
// The backend already caches the upstream N0NBH call for 5 min, so frequent
// load() calls are cheap.
export const useSpaceWxStore = create<SpaceWxState>((set) => ({
  data: null,
  loading: false,
  lastFetch: null,

  load: async (signal) => {
    set({ loading: true });
    try {
      const data = await fetchSpaceWeather(signal);
      set({ data, loading: false, lastFetch: Date.now() });
    } catch {
      // Aborted or network error — keep prior data, drop the spinner.
      set({ loading: false });
    }
  },
}));
