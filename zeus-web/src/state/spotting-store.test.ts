// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it, vi } from 'vitest';

// Mock the REST layer BEFORE the store imports it — the store fires a one-shot
// status probe on module load. Both uploaders default OFF.
vi.mock('../api/spotting', () => ({
  getSpottingStatus: vi.fn(async () => ({
    pskReporterEnabled: false,
    wsprnetEnabled: false,
    callsign: '',
    grid: '',
    identityResolved: false,
  })),
  postSpottingConfig: vi.fn(
    async (cfg: {
      pskReporterEnabled: boolean;
      wsprnetEnabled: boolean;
      callsign: string;
      grid: string;
    }) => ({
      ...cfg,
      identityResolved: cfg.callsign !== '' && cfg.grid !== '',
    }),
  ),
}));

import { useSpottingStore } from './spotting-store';
import * as api from '../api/spotting';

describe('spotting-store', () => {
  it('defaults both uploaders to disabled egress (opt-in)', () => {
    const { config } = useSpottingStore.getState();
    expect(config.pskReporterEnabled).toBe(false);
    expect(config.wsprnetEnabled).toBe(false);
  });

  it('never auto-POSTs config on load (no silent enable)', () => {
    expect(api.postSpottingConfig).not.toHaveBeenCalled();
  });

  it('saveConfig pushes the operator choice to the backend', async () => {
    const cfg = {
      pskReporterEnabled: true,
      wsprnetEnabled: false,
      callsign: 'K1ABC',
      grid: 'FN42',
    };
    const status = await useSpottingStore.getState().saveConfig(cfg);
    expect(api.postSpottingConfig).toHaveBeenCalledWith(cfg);
    expect(status.pskReporterEnabled).toBe(true);
    expect(status.identityResolved).toBe(true);
    expect(useSpottingStore.getState().config.pskReporterEnabled).toBe(true);
    expect(useSpottingStore.getState().config.callsign).toBe('K1ABC');
  });
});
