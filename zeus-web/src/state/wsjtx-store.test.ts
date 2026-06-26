// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it, vi } from 'vitest';

// Mock the REST layer BEFORE the store imports it — the store fires a one-shot
// status probe on module load.
vi.mock('../api/wsjtx', () => ({
  getWsjtxStatus: vi.fn(async () => ({
    enabled: false,
    host: '127.0.0.1',
    port: 2237,
    instanceId: 'WSJT-X',
  })),
  postWsjtxConfig: vi.fn(async (cfg: { enabled: boolean; host: string; port: number }) => ({
    ...cfg,
    instanceId: 'WSJT-X',
  })),
}));

import { useWsjtxStore } from './wsjtx-store';
import * as api from '../api/wsjtx';

describe('wsjtx-store', () => {
  it('defaults to disabled egress (opt-in)', () => {
    expect(useWsjtxStore.getState().config.enabled).toBe(false);
  });

  it('never auto-POSTs config on load (no silent enable)', () => {
    expect(api.postWsjtxConfig).not.toHaveBeenCalled();
  });

  it('saveConfig pushes the operator choice to the backend', async () => {
    const cfg = { enabled: true, host: '127.0.0.1', port: 2237 };
    const status = await useWsjtxStore.getState().saveConfig(cfg);
    expect(api.postWsjtxConfig).toHaveBeenCalledWith(cfg);
    expect(status.enabled).toBe(true);
    expect(useWsjtxStore.getState().config.enabled).toBe(true);
  });
});
