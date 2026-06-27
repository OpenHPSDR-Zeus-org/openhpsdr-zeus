// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it, vi } from 'vitest';

// Mock the REST layer BEFORE the store imports it — the store fires a one-shot
// status probe on module load.
vi.mock('../api/wsjtx', () => ({
  WSJTX_DEFAULT_PORT: 2237,
  WSJTX_DEFAULT_GROUP: '224.0.0.73',
  WSJTX_DEFAULT_TTL: 1,
  WSJTX_DEFAULT_INSTANCE: 'WSJT-X',
  getWsjtxStatus: vi.fn(async () => ({
    enabled: false,
    host: '127.0.0.1',
    port: 2237,
    instanceId: 'WSJT-X',
    transport: 'unicast',
    multicastGroup: '224.0.0.73',
    multicastTtl: 1,
    sendQsoLogged: false,
    sendLiveDecodes: false,
  })),
  postWsjtxConfig: vi.fn(async (cfg: Record<string, unknown>) => ({ ...cfg })),
}));

import { useWsjtxStore } from './wsjtx-store';
import type { WsjtxConfig } from '../api/wsjtx';
import * as api from '../api/wsjtx';

const FULL_CONFIG: WsjtxConfig = {
  enabled: true,
  host: '127.0.0.1',
  port: 2237,
  instanceId: 'WSJT-X',
  transport: 'multicast',
  multicastGroup: '224.0.0.73',
  multicastTtl: 1,
  sendQsoLogged: true,
  sendLiveDecodes: true,
};

describe('wsjtx-store', () => {
  it('defaults to disabled egress (opt-in)', () => {
    expect(useWsjtxStore.getState().config.enabled).toBe(false);
  });

  it('never auto-POSTs config on load (no silent enable)', () => {
    expect(api.postWsjtxConfig).not.toHaveBeenCalled();
  });

  it('saveConfig pushes the full operator choice (incl. transport/multicast) to the backend', async () => {
    const status = await useWsjtxStore.getState().saveConfig(FULL_CONFIG);
    expect(api.postWsjtxConfig).toHaveBeenCalledWith(FULL_CONFIG);
    expect(status.enabled).toBe(true);
    expect(status.transport).toBe('multicast');
    expect(useWsjtxStore.getState().config.enabled).toBe(true);
    expect(useWsjtxStore.getState().config.sendLiveDecodes).toBe(true);
  });
});
