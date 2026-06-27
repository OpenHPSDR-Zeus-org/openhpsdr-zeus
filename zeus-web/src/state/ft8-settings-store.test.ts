// SPDX-License-Identifier: GPL-2.0-or-later
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Ft8Settings } from '../api/ft8-settings';

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
}));

// Mock the REST layer BEFORE the store imports it (one-shot hydrate on load).
vi.mock('../api/ft8-settings', () => ({
  FT8_SETTINGS_DEFAULTS: DEFAULTS,
  getFt8Settings: vi.fn(async () => DEFAULTS),
  postFt8Settings: vi.fn(async (s: Ft8Settings) => s),
}));

import { useFt8SettingsStore } from './ft8-settings-store';
import { useFt8Store } from './ft8-store';
import * as api from '../api/ft8-settings';

describe('ft8-settings-store (server-backed prefs)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useFt8SettingsStore.setState({ settings: DEFAULTS, hydrated: false });
    useFt8Store.setState({ enabled: false, passes: 3, receiver: -1, protocol: 'FT8' });
  });

  it('defaults match the contract defaults (no felt change)', () => {
    expect(useFt8SettingsStore.getState().settings).toEqual(DEFAULTS);
  });

  it('never auto-POSTs on load (opt-in writes only)', () => {
    expect(api.postFt8Settings).not.toHaveBeenCalled();
  });

  it('update merges a partial and persists the whole record', async () => {
    await useFt8SettingsStore.getState().update({ autoLog: false, showOnlyCq: true });
    const s = useFt8SettingsStore.getState().settings;
    expect(s.autoLog).toBe(false);
    expect(s.showOnlyCq).toBe(true);
    expect(s.autoSequence).toBe(true); // untouched
    expect(api.postFt8Settings).toHaveBeenCalledWith(
      expect.objectContaining({ autoLog: false, showOnlyCq: true }),
    );
  });

  it('hydrate seeds the live decoder pass count from the persisted depth', async () => {
    vi.mocked(api.getFt8Settings).mockResolvedValueOnce({ ...DEFAULTS, decodePasses: 4 });
    await useFt8SettingsStore.getState().hydrate();
    expect(useFt8SettingsStore.getState().settings.decodePasses).toBe(4);
    expect(useFt8Store.getState().passes).toBe(4); // decode-depth wiring
  });

  it('optimistic update applies immediately even before the server responds', () => {
    void useFt8SettingsStore.getState().update({ cqMessage: 'CQ TEST' });
    expect(useFt8SettingsStore.getState().settings.cqMessage).toBe('CQ TEST');
  });
});
