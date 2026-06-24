// SPDX-License-Identifier: GPL-2.0-or-later
//
// freedv-stations-store — client state for the FreeDV Stations panel.
// Polls GET /api/freedv/stations (served by the backend FreeDvReporterService),
// holds the live station list and implements click-to-tune by driving the Zeus
// VFO (setVfo + setMode + setFreeDvConfig) over the native radio connection.

import { create } from 'zustand';
import {
  fetchFreeDvStations,
  setMode,
  setVfo,
  setFreeDvConfig,
  type FreeDvStationDto,
  type FreeDvSubmode,
} from '../api/client';
import { useConnectionStore } from './connection-store';
import { freqHzToBand } from './spots-store';

export { freqHzToBand };

interface FreeDvStationsState {
  stations: FreeDvStationDto[];
  connectionState: string;
  loading: boolean;
  error: string | null;
  lastUpdated: number | null;

  /** Free-text filter (callsign / grid / mode / band). */
  query: string;

  /** Transient feedback from the last tune attempt. */
  tuneError: string | null;

  setQuery: (query: string) => void;
  loadStations: () => Promise<void>;
  tuneToStation: (station: FreeDvStationDto) => Promise<void>;
}

/** Map a FreeDV Reporter mode string to a FreeDvSubmode enum value.
 *  Returns null when the mapping is unknown — callers skip the submode call. */
function reporterModeToSubmode(mode: string): FreeDvSubmode | null {
  switch (mode.trim().toUpperCase()) {
    case '1600':
      return 'Mode1600';
    case '700C':
      return 'Mode700C';
    case '700D':
      return 'Mode700D';
    case '700E':
      return 'Mode700E';
    case '800XA':
      return 'Mode800XA';
    case 'RADEV1':
    case 'RADE':
      return 'RadeV1';
    default:
      return null;
  }
}

/** True when `station` matches the free-text `query` (callsign / grid / mode / band). */
export function stationMatchesQuery(station: FreeDvStationDto, query: string): boolean {
  const q = query.trim().toUpperCase();
  if (q.length === 0) return true;
  const band = freqHzToBand(station.freqHz);
  return (
    station.callsign.toUpperCase().includes(q) ||
    (station.gridSquare ?? '').toUpperCase().includes(q) ||
    station.mode.toUpperCase().includes(q) ||
    (band !== null && band.toUpperCase() === q) ||
    (band !== null && band.toUpperCase().replace('M', '').startsWith(q.replace('M', '')))
  );
}

export const useFreeDvStationsStore = create<FreeDvStationsState>()((set) => ({
  stations: [],
  connectionState: 'Disconnected',
  loading: false,
  error: null,
  lastUpdated: null,
  query: '',
  tuneError: null,

  setQuery: (query) => set({ query }),

  loadStations: async () => {
    set({ loading: true });
    try {
      const resp = await fetchFreeDvStations();
      set({
        stations: resp.stations,
        connectionState: resp.connectionState,
        error: null,
        loading: false,
        lastUpdated: Date.now(),
      });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Failed to load FreeDV stations',
        loading: false,
      });
    }
  },

  tuneToStation: async (station) => {
    if (useConnectionStore.getState().status !== 'Connected') {
      set({ tuneError: 'No radio connected — connect first.' });
      return;
    }
    set({ tuneError: null });
    try {
      await setVfo(station.freqHz);
      await setMode('FREEDV');
      const submode = reporterModeToSubmode(station.mode);
      if (submode !== null) {
        await setFreeDvConfig({ submode });
      }
    } catch (err) {
      set({ tuneError: err instanceof Error ? err.message : 'Tune failed' });
    }
  },
}));
