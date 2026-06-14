// SPDX-License-Identifier: GPL-2.0-or-later
//
// spots-store — client state for the POTA/SOTA Spots panel. Polls
// GET /api/spots/activations (served by the backend ActivationSpotsService),
// holds the merged list + operator settings, and implements click-to-tune by
// driving the Zeus VFO (setVfo + setMode) over the native radio connection.
//
// Inspired by POTACAT (github.com/Waffleslop/POTACAT): same public feeds, but
// the tune target is the connected Zeus radio over /api/vfo + /api/mode instead
// of Hamlib rigctld.

import { create } from 'zustand';
import {
  fetchActivationSpots,
  fetchSpotsSettings,
  updateSpotsSettings,
  setMode,
  setVfo,
  SPOTS_SETTINGS_DEFAULTS,
  type ActivationSpotDto,
  type RxMode,
  type SpotsSettings,
} from '../api/client';
import { useConnectionStore } from './connection-store';

export type SpotSourceFilter = 'ALL' | 'POTA' | 'SOTA';

interface SpotsState {
  spots: ActivationSpotDto[];
  loading: boolean;
  error: string | null;
  lastUpdated: number | null;

  // Operator settings (persisted server-side). `settingsLoaded` gates the panel
  // from acting on defaults before the real config arrives.
  settings: SpotsSettings;
  settingsLoaded: boolean;
  savingSettings: boolean;

  // Transient feedback from the last tune attempt (e.g. blocked because no
  // radio is connected). The panel surfaces it briefly.
  tuneError: string | null;

  // View filters (panel-local, not persisted).
  source: SpotSourceFilter;
  query: string;

  setSource: (source: SpotSourceFilter) => void;
  setQuery: (query: string) => void;

  loadSpots: () => Promise<void>;
  loadSettings: () => Promise<void>;
  saveSettings: (settings: SpotsSettings) => Promise<void>;
  tuneToSpot: (spot: ActivationSpotDto) => Promise<void>;
}

/** Map a raw POTA/SOTA mode string to a Zeus RxMode, choosing the sideband by
 *  the usual HF convention (LSB below 10 MHz, USB at/above). Digital → DIGU
 *  (FT8/FT4/JS8/etc are USB-side); RTTY → DIGL. CW uses the operator's
 *  configured sideband (default CWU). Unknown modes fall back to SSB. */
export function spotModeToRxMode(
  mode: string,
  freqHz: number,
  cwSideband: 'CWU' | 'CWL' = 'CWU',
): RxMode {
  const m = (mode || '').toUpperCase().trim();
  const ssb: RxMode = freqHz < 10_000_000 ? 'LSB' : 'USB';

  if (m === 'USB' || m === 'LSB') return m;
  if (m === 'SSB' || m === 'PHONE' || m === 'VOICE') return ssb;
  if (m === 'CW' || m === 'CWU' || m === 'CWL') return cwSideband;
  if (m === 'AM' || m === 'SAM') return 'AM';
  if (m === 'FM' || m === 'NFM' || m === 'FMN') return 'FM';
  if (m === 'RTTY') return 'DIGL';
  // Everything else (FT8, FT4, JS8, JT65, DATA, PSK, MFSK, OLIVIA, …) is a
  // USB-side data mode.
  if (m.length > 0) return 'DIGU';
  return ssb;
}

export function spotMatchesFilters(
  spot: ActivationSpotDto,
  source: SpotSourceFilter,
  query: string,
): boolean {
  if (source !== 'ALL' && spot.source !== source) return false;
  const q = query.trim().toUpperCase();
  if (q.length === 0) return true;
  return (
    spot.activator.toUpperCase().includes(q) ||
    spot.reference.toUpperCase().includes(q) ||
    (spot.name ?? '').toUpperCase().includes(q) ||
    (spot.location ?? '').toUpperCase().includes(q) ||
    spot.mode.toUpperCase().includes(q)
  );
}

export const useSpotsStore = create<SpotsState>()((set, get) => ({
  spots: [],
  loading: false,
  error: null,
  lastUpdated: null,
  settings: SPOTS_SETTINGS_DEFAULTS,
  settingsLoaded: false,
  savingSettings: false,
  tuneError: null,
  source: 'ALL',
  query: '',

  setSource: (source) => set({ source }),
  setQuery: (query) => set({ query }),

  loadSpots: async () => {
    set({ loading: true });
    try {
      const spots = await fetchActivationSpots();
      set({ spots, error: null, loading: false, lastUpdated: Date.now() });
    } catch (err) {
      set({
        error: err instanceof Error ? err.message : 'Failed to load spots',
        loading: false,
      });
    }
  },

  loadSettings: async () => {
    try {
      const settings = await fetchSpotsSettings();
      set({ settings, settingsLoaded: true });
    } catch {
      // Fall back to defaults but mark loaded so the UI isn't stuck.
      set({ settingsLoaded: true });
    }
  },

  saveSettings: async (settings) => {
    set({ savingSettings: true });
    try {
      const saved = await updateSpotsSettings(settings);
      set({ settings: saved, savingSettings: false });
    } catch {
      // Keep the optimistic value locally so the form doesn't jump back.
      set({ settings, savingSettings: false });
    }
  },

  tuneToSpot: async (spot) => {
    const { settings } = get();
    if (settings.tuneOnlyWhenConnected && useConnectionStore.getState().status !== 'Connected') {
      set({ tuneError: 'No radio connected — connect first, or disable “tune only when connected”.' });
      return;
    }
    set({ tuneError: null });
    try {
      await setVfo(spot.freqHz);
      if (settings.setModeOnTune) {
        await setMode(spotModeToRxMode(spot.mode, spot.freqHz, settings.cwSideband));
      }
    } catch (err) {
      set({ tuneError: err instanceof Error ? err.message : 'Tune failed' });
    }
  },
}));
