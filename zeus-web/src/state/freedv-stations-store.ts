// SPDX-License-Identifier: GPL-2.0-or-later
//
// freedv-stations-store — client state for the FreeDV Stations panel.
// Polls GET /api/freedv/stations (served by the backend FreeDvReporterService),
// holds the live station list and implements click-to-tune by driving the Zeus
// VFO (setVfo + setMode + setFreeDvConfig) over the native radio connection.

import { create } from 'zustand';
import {
  fetchFreeDvStations,
  getFreeDvReporterSettings,
  setFreeDvReporterSettings,
  freeDvStationQsy,
  setMode,
  setVfo,
  setFreeDvConfig,
  type FreeDvStationDto,
  type FreeDvReporterSettings,
  type FreeDvSubmode,
} from '../api/client';
import { useConnectionStore } from './connection-store';
import { freqHzToBand } from './spots-store';

export { freqHzToBand };
export type { FreeDvReporterSettings };

interface FreeDvStationsState {
  stations: FreeDvStationDto[];
  connectionState: string;
  loading: boolean;
  error: string | null;
  lastUpdated: number | null;

  /** True when Zeus is connected in "report" role (on the public map). */
  reporting: boolean;
  /** Operator's own session id while reporting (highlights own row). */
  mySid: string | null;

  /** Report-mode settings (opt-in toggle + callsign/grid/message). */
  reporterSettings: FreeDvReporterSettings | null;
  /** Transient feedback from the last report-settings save / QSY. */
  reporterError: string | null;
  reporterSaving: boolean;

  /** Free-text filter (callsign / grid / mode / band). */
  query: string;

  /** Transient feedback from the last tune attempt. */
  tuneError: string | null;

  setQuery: (query: string) => void;
  loadStations: () => Promise<void>;
  tuneToStation: (station: FreeDvStationDto) => Promise<void>;

  loadReporterSettings: () => Promise<void>;
  saveReporterSettings: (settings: FreeDvReporterSettings) => Promise<void>;
  requestQsy: (sid: string) => Promise<void>;
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

export const useFreeDvStationsStore = create<FreeDvStationsState>()((set, get) => ({
  stations: [],
  connectionState: 'Disconnected',
  loading: false,
  error: null,
  lastUpdated: null,
  reporting: false,
  mySid: null,
  reporterSettings: null,
  reporterError: null,
  reporterSaving: false,
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
        reporting: resp.reporting,
        mySid: resp.mySid,
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

  loadReporterSettings: async () => {
    try {
      const settings = await getFreeDvReporterSettings();
      set({ reporterSettings: settings, reporterError: null });
    } catch (err) {
      set({
        reporterError: err instanceof Error ? err.message : 'Failed to load report settings',
      });
    }
  },

  saveReporterSettings: async (settings) => {
    set({ reporterSaving: true, reporterError: null });
    try {
      const saved = await setFreeDvReporterSettings(settings);
      set({ reporterSettings: saved, reporterSaving: false });
      // Reconnect takes a moment server-side; refresh the roster + reporting flag.
      void get().loadStations();
    } catch (err) {
      set({
        reporterError: err instanceof Error ? err.message : 'Failed to save report settings',
        reporterSaving: false,
      });
    }
  },

  requestQsy: async (sid) => {
    set({ reporterError: null });
    try {
      await freeDvStationQsy(sid);
    } catch (err) {
      set({ reporterError: err instanceof Error ? err.message : 'QSY request failed' });
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
