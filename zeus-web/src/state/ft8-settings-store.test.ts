// SPDX-License-Identifier: GPL-2.0-or-later
//
// ft8-settings-store — PER-MODE server-backed prefs. FT8/FT4/WSPR each keep an
// independent record; a change to one must never bleed into another, the active
// mode's decode depth seeds the live decoder, and writes are opt-in (never an
// auto-POST on load).
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { DigitalMode, Ft8Settings } from '../api/ft8-settings';

// vi.hoisted so the mock factory (also hoisted) can reference the defaults.
const DEFAULTS = vi.hoisted<Ft8Settings>(() => ({
  autoSequence: true,
  callFirst: false,
  holdTxFreq: false,
  disableTxAfter73: true,
  defaultTxSlot: 0,
  defaultTxOffsetHz: 1500,
  rr73InsteadOfRrr: true,
  skipGrid: false,
  callerMaxRetries: 0,
  cqMessage: 'CQ',
  cqDxMessage: 'CQ DX',
  freeTextMacro: '',
  decodePasses: 3,
  showOnlyCq: false,
  hideWorkedBefore: false,
  autoLog: true,
  promptBeforeLog: false,
  clearDxAfterLog: true,
  reportToComment: false,
  wfDbMin: -140,
  wfDbMax: -50,
  palette: 'blue',
  rbw: 'auto',
  smoothing: 0,
  zoom: 1.0,
  spanHz: 3000,
}));

// Mock the REST layer BEFORE the store imports it (one-shot hydrate on load).
// postFt8Settings echoes back the settings it was handed (2nd arg).
vi.mock('../api/ft8-settings', () => ({
  DIGITAL_MODES: ['FT8', 'FT4', 'WSPR'] as const,
  FT8_SETTINGS_DEFAULTS: DEFAULTS,
  getFt8Settings: vi.fn(async () => DEFAULTS),
  postFt8Settings: vi.fn(async (_mode: DigitalMode, s: Ft8Settings) => s),
}));

import { useFt8SettingsStore } from './ft8-settings-store';
import { useFt8Store } from './ft8-store';
import * as api from '../api/ft8-settings';

const FRESH = () => ({
  byMode: { FT8: DEFAULTS, FT4: DEFAULTS, WSPR: DEFAULTS },
  hydrated: { FT8: false, FT4: false, WSPR: false },
});

describe('ft8-settings-store (per-mode server-backed prefs)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useFt8SettingsStore.setState(FRESH());
    useFt8Store.setState({ enabled: false, passes: 3, receiver: -1, protocol: 'FT8' });
  });

  it('defaults match the contract defaults for every mode (no felt change)', () => {
    const { byMode } = useFt8SettingsStore.getState();
    expect(byMode.FT8).toEqual(DEFAULTS);
    expect(byMode.FT4).toEqual(DEFAULTS);
    expect(byMode.WSPR).toEqual(DEFAULTS);
  });

  it('exposes the per-mode display defaults (waterfall block)', () => {
    const s = useFt8SettingsStore.getState().byMode.FT8;
    expect(s.wfDbMin).toBe(-140);
    expect(s.wfDbMax).toBe(-50);
    expect(s.palette).toBe('blue');
    expect(s.rbw).toBe('auto');
    expect(s.smoothing).toBe(0);
    expect(s.zoom).toBe(1.0);
    expect(s.spanHz).toBe(3000);
  });

  it('never auto-POSTs on load (opt-in writes only)', () => {
    expect(api.postFt8Settings).not.toHaveBeenCalled();
  });

  it('update merges a partial, persists the whole record, and targets the mode', async () => {
    await useFt8SettingsStore.getState().update('FT4', { autoLog: false, showOnlyCq: true });
    const s = useFt8SettingsStore.getState().byMode.FT4;
    expect(s.autoLog).toBe(false);
    expect(s.showOnlyCq).toBe(true);
    expect(s.autoSequence).toBe(true); // untouched
    expect(api.postFt8Settings).toHaveBeenCalledWith(
      'FT4',
      expect.objectContaining({ autoLog: false, showOnlyCq: true }),
    );
  });

  it('per-mode independence: editing FT8 does not change FT4 or WSPR', async () => {
    await useFt8SettingsStore.getState().update('FT8', { cqMessage: 'CQ TEST' });
    const { byMode } = useFt8SettingsStore.getState();
    expect(byMode.FT8.cqMessage).toBe('CQ TEST');
    expect(byMode.FT4.cqMessage).toBe('CQ'); // independent
    expect(byMode.WSPR.cqMessage).toBe('CQ'); // independent
  });

  it('hydrate(mode) loads that mode and seeds the live decoder when it is active', async () => {
    vi.mocked(api.getFt8Settings).mockResolvedValueOnce({ ...DEFAULTS, decodePasses: 4 });
    await useFt8SettingsStore.getState().hydrate('FT8');
    expect(useFt8SettingsStore.getState().byMode.FT8.decodePasses).toBe(4);
    expect(useFt8Store.getState().passes).toBe(4); // active mode → engine seeded
    expect(useFt8SettingsStore.getState().hydrated.FT8).toBe(true);
  });

  it('hydrate of a NON-active mode does not restart the live decoder', async () => {
    // Engaged protocol is FT8; hydrating FT4 at depth 4 must not touch the engine.
    vi.mocked(api.getFt8Settings).mockResolvedValueOnce({ ...DEFAULTS, decodePasses: 4 });
    await useFt8SettingsStore.getState().hydrate('FT4');
    expect(useFt8SettingsStore.getState().byMode.FT4.decodePasses).toBe(4);
    expect(useFt8Store.getState().passes).toBe(3); // unchanged
  });

  it('hydrating WSPR never restarts the FT8/FT4 decoder (WSPR has no shared-engine pass)', async () => {
    // Engaged protocol is FT8 at depth 3; WSPR is a separate decoder, so even a
    // depth-4 WSPR row must not touch the live FT8/FT4 engine (exercises the
    // seedEngineDepthIfActive WSPR early-return).
    vi.mocked(api.getFt8Settings).mockResolvedValueOnce({ ...DEFAULTS, decodePasses: 4 });
    await useFt8SettingsStore.getState().hydrate('WSPR');
    expect(useFt8SettingsStore.getState().byMode.WSPR.decodePasses).toBe(4);
    expect(useFt8Store.getState().passes).toBe(3); // unchanged
  });

  it('optimistic update applies immediately even before the server responds', () => {
    void useFt8SettingsStore.getState().update('WSPR', { spanHz: 2000 });
    expect(useFt8SettingsStore.getState().byMode.WSPR.spanHz).toBe(2000);
  });

  it('a failed persist keeps the optimistic value (never throws into the UI)', async () => {
    vi.mocked(api.postFt8Settings).mockRejectedValueOnce(new Error('offline'));
    await useFt8SettingsStore.getState().update('FT8', { autoLog: false });
    expect(useFt8SettingsStore.getState().byMode.FT8.autoLog).toBe(false);
  });
});
