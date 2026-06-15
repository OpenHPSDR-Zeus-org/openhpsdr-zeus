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

export type SpotSourceFilter = 'ALL' | 'POTA' | 'SOTA' | 'DX';

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

export type SpotModeGroup = 'CW' | 'PHONE' | 'DIGITAL' | 'FM' | 'AM';

/** Collapse a raw POTA/SOTA mode string into one of the coarse mode groups used
 *  by the mode filter. Anything unrecognised but non-empty is treated as a data
 *  mode (DIGITAL) since the modern POTA/SOTA mode zoo is overwhelmingly data. */
export function spotModeGroup(mode: string): SpotModeGroup {
  const m = (mode || '').toUpperCase().trim();
  if (m === 'CW') return 'CW';
  if (m === 'SSB' || m === 'USB' || m === 'LSB' || m === 'PHONE' || m === 'VOICE') return 'PHONE';
  if (m === 'FM' || m === 'NFM' || m === 'FMN') return 'FM';
  if (m === 'AM' || m === 'SAM') return 'AM';
  return 'DIGITAL';
}

/** All amateur band keys we classify, widest → narrowest wavelength, each with
 *  its inclusive Hz edges. Used by both the band filter and the panel's band
 *  chip column. Anything outside every range is "out of band" (key null). */
export const SPOT_BANDS: ReadonlyArray<{ key: string; loHz: number; hiHz: number }> = [
  { key: '160m', loHz: 1_800_000, hiHz: 2_000_000 },
  { key: '80m', loHz: 3_500_000, hiHz: 4_000_000 },
  { key: '60m', loHz: 5_250_000, hiHz: 5_450_000 },
  { key: '40m', loHz: 7_000_000, hiHz: 7_300_000 },
  { key: '30m', loHz: 10_100_000, hiHz: 10_150_000 },
  { key: '20m', loHz: 14_000_000, hiHz: 14_350_000 },
  { key: '17m', loHz: 18_068_000, hiHz: 18_168_000 },
  { key: '15m', loHz: 21_000_000, hiHz: 21_450_000 },
  { key: '12m', loHz: 24_890_000, hiHz: 24_990_000 },
  { key: '10m', loHz: 28_000_000, hiHz: 29_700_000 },
  { key: '6m', loHz: 50_000_000, hiHz: 54_000_000 },
  { key: '2m', loHz: 144_000_000, hiHz: 148_000_000 },
  { key: '70cm', loHz: 420_000_000, hiHz: 450_000_000 },
];

/** The band key for a frequency, or null if it lands outside every amateur band. */
export function freqHzToBand(hz: number): string | null {
  for (const b of SPOT_BANDS) {
    if (hz >= b.loHz && hz <= b.hiHz) return b.key;
  }
  return null;
}

const QRT_RE = /\b(qrt|qsy|going\s*qrt|closing|shutting\s*down|pack(?:ed|ing)?\s*up|done\s*for)\b/i;

/** True if the spot's comment marks the activator as QRT / closing down. */
export function spotIsQrt(spot: ActivationSpotDto): boolean {
  return QRT_RE.test(spot.comments ?? '');
}

/** Age of a spot in seconds (feeds emit UTC with no offset), or null if unparseable. */
export function spotAgeSeconds(spot: ActivationSpotDto, nowMs: number = Date.now()): number | null {
  const t = Date.parse(`${spot.spotTime}Z`);
  if (Number.isNaN(t)) return null;
  return Math.max(0, Math.round((nowMs - t) / 1000));
}

/** True if the activator is on the operator's watchlist (entries are upper-cased). */
export function isWatchedCall(activator: string, watchlist: readonly string[]): boolean {
  if (watchlist.length === 0) return false;
  return watchlist.includes(activator.trim().toUpperCase());
}

/** True if the activator already appears in the local logbook (worked-before). */
export function spotIsWorked(spot: ActivationSpotDto, workedCalls: ReadonlySet<string>): boolean {
  return workedCalls.has(spot.activator.trim().toUpperCase());
}

/** Quick text/source view filter (panel chips + search box). */
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

/** Apply the persisted operator settings (band / mode / QRT / age / dedup) to a
 *  raw spot list. Source & free-text filtering stay panel-local; this is the
 *  global gate the operator configured in Settings → Spots. The input is assumed
 *  newest-first (the server sorts that way), which makes dedup keep the latest. */
export function applySpotSettingsFilters(
  spots: ActivationSpotDto[],
  settings: SpotsSettings,
  nowMs: number = Date.now(),
  workedCalls?: ReadonlySet<string>,
): ActivationSpotDto[] {
  // Compare case-insensitively so a hand-crafted POST ('20M' vs '20m') still
  // matches the canonical lowercase band keys / uppercase mode-group keys.
  const bands = settings.bands.map((b) => b.toUpperCase());
  const modes = settings.modes.map((m) => m.toUpperCase());
  const maxAgeSecs = settings.maxAgeMinutes > 0 ? settings.maxAgeMinutes * 60 : 0;
  const hideWorked = settings.hideWorked && workedCalls !== undefined && workedCalls.size > 0;

  let out = spots.filter((s) => {
    if (settings.hideQrt && spotIsQrt(s)) return false;
    if (hideWorked && spotIsWorked(s, workedCalls)) return false;
    if (bands.length > 0) {
      const b = freqHzToBand(s.freqHz);
      if (b === null || !bands.includes(b.toUpperCase())) return false;
    }
    if (modes.length > 0 && !modes.includes(spotModeGroup(s.mode))) return false;
    if (maxAgeSecs > 0) {
      const age = spotAgeSeconds(s, nowMs);
      if (age !== null && age > maxAgeSecs) return false;
    }
    return true;
  });

  if (settings.latestPerActivator) {
    const seen = new Set<string>();
    out = out.filter((s) => {
      const key = `${s.source}|${s.activator}`;
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });
  }

  return out;
}

// --- Watch alerts -----------------------------------------------------------
// Keys of watched spots we've already alerted on, deduped by activator +
// kHz-rounded frequency so a re-post on the same frequency doesn't re-fire,
// but a move to a new band does. Keys that age out of the feed are pruned so a
// later re-appearance alerts again. Module-level (survives store churn).
const alertedSpotKeys = new Set<string>();

function watchAlertKey(s: ActivationSpotDto): string {
  return `${s.activator.toUpperCase()}|${Math.round(s.freqHz / 1000)}`;
}

/** Ask the browser for notification permission (idempotent, best-effort). Call
 *  this from a user gesture — e.g. when the operator enables alerts. */
export async function requestSpotNotificationPermission(): Promise<boolean> {
  try {
    if (typeof Notification === 'undefined') return false;
    if (Notification.permission === 'granted') return true;
    if (Notification.permission === 'denied') return false;
    return (await Notification.requestPermission()) === 'granted';
  } catch {
    return false;
  }
}

let alertAudioCtx: AudioContext | null = null;

/** A short two-tone chirp via WebAudio — no asset dependency. Fails silently if
 *  the AudioContext is unavailable or suspended (e.g. no prior user gesture). */
function playWatchBeep(): void {
  try {
    const Ctor =
      (window as typeof window & { webkitAudioContext?: typeof AudioContext }).AudioContext ??
      (window as typeof window & { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!Ctor) return;
    alertAudioCtx ??= new Ctor();
    const ctx = alertAudioCtx;
    const now = ctx.currentTime;
    const gain = ctx.createGain();
    gain.gain.setValueAtTime(0.0001, now);
    gain.gain.exponentialRampToValueAtTime(0.18, now + 0.02);
    gain.gain.exponentialRampToValueAtTime(0.0001, now + 0.35);
    gain.connect(ctx.destination);
    const osc = ctx.createOscillator();
    osc.type = 'sine';
    osc.frequency.setValueAtTime(880, now);
    osc.frequency.setValueAtTime(1320, now + 0.12);
    osc.connect(gain);
    osc.start(now);
    osc.stop(now + 0.36);
  } catch {
    /* audio blocked — silent */
  }
}

/** Fire desktop + sound alerts for any freshly-spotted watchlist activators.
 *  Respects hideQrt and maxAge so stale / closing spots don't nag. */
function processWatchAlerts(spots: ActivationSpotDto[], settings: SpotsSettings, nowMs: number): void {
  if (!settings.alertsEnabled || settings.watchlist.length === 0) return;
  const watch = new Set(settings.watchlist); // already upper-cased
  const maxAgeSecs = settings.maxAgeMinutes > 0 ? settings.maxAgeMinutes * 60 : 0;

  const currentKeys = new Set<string>();
  const fresh: ActivationSpotDto[] = [];
  for (const s of spots) {
    if (!watch.has(s.activator.toUpperCase())) continue;
    if (settings.hideQrt && spotIsQrt(s)) continue;
    if (maxAgeSecs > 0) {
      const age = spotAgeSeconds(s, nowMs);
      if (age !== null && age > maxAgeSecs) continue;
    }
    const k = watchAlertKey(s);
    currentKeys.add(k);
    if (!alertedSpotKeys.has(k)) fresh.push(s);
  }

  // Prune keys no longer present so a future re-spot re-alerts.
  for (const k of [...alertedSpotKeys]) if (!currentKeys.has(k)) alertedSpotKeys.delete(k);

  if (fresh.length === 0) return;
  for (const s of fresh) alertedSpotKeys.add(watchAlertKey(s));

  // One notification summarising the batch (avoids a popup storm), then the cue.
  try {
    if (typeof Notification !== 'undefined' && Notification.permission === 'granted') {
      const lead = fresh[0]!;
      const title =
        fresh.length === 1
          ? `★ ${lead.activator} spotted`
          : `★ ${fresh.length} watched calls spotted`;
      const body =
        fresh.length === 1
          ? `${(lead.freqHz / 1_000_000).toFixed(3)} MHz ${lead.mode || ''}${lead.reference ? ` · ${lead.reference}` : ''}`.trim()
          : fresh.map((s) => s.activator).join(', ');
      new Notification(title, { body, tag: 'zeus-spot-watch' });
    }
  } catch {
    /* notifications unavailable — sound still plays */
  }
  if (settings.alertSound) playWatchBeep();
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
      const now = Date.now();
      set({ spots, error: null, loading: false, lastUpdated: now });
      // Raise watch alerts for any freshly-spotted watchlist calls.
      processWatchAlerts(spots, get().settings, now);
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
      const group = spotModeGroup(spot.mode);
      const offset =
        group === 'CW' ? settings.cwTuneOffsetHz
        : group === 'DIGITAL' ? settings.digiTuneOffsetHz
        : 0;
      await setVfo(spot.freqHz + offset);
      if (settings.setModeOnTune) {
        await setMode(spotModeToRxMode(spot.mode, spot.freqHz, settings.cwSideband));
      }
    } catch (err) {
      set({ tuneError: err instanceof Error ? err.message : 'Tune failed' });
    }
  },
}));
