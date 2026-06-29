// SPDX-License-Identifier: GPL-2.0-or-later
import { describe, expect, it, vi } from 'vitest';
import type { DxClusterStatus } from '../api/dxcluster';

// Mock the REST layer BEFORE the store imports it — the store fires a one-shot
// status probe on module load. The client defaults OFF (no network egress).
const disabledStatus: DxClusterStatus = {
  enabled: false,
  host: '',
  port: 7373,
  callsign: '',
  hasPassword: false,
  loginCommands: '',
  autoConnect: false,
  state: 'Disconnected',
  spotsReceived: 0,
  lastSpotCallsign: null,
  error: null,
};

vi.mock('../api/dxcluster', () => ({
  getDxClusterStatus: vi.fn(async () => disabledStatus),
  putDxClusterConfig: vi.fn(
    async (cfg: { enabled: boolean; host: string; port: number; callsign: string }) => ({
      ...disabledStatus,
      enabled: cfg.enabled,
      host: cfg.host,
      port: cfg.port,
      callsign: cfg.callsign,
    }),
  ),
  connectDxCluster: vi.fn(async () => ({ ...disabledStatus, enabled: true, state: 'Connecting' })),
  disconnectDxCluster: vi.fn(async () => ({ ...disabledStatus, state: 'Disconnected' })),
}));

import { useDxClusterStore } from './dxcluster-store';
import * as api from '../api/dxcluster';

describe('dxcluster-store', () => {
  it('defaults to disabled (opt-in egress)', () => {
    const { config } = useDxClusterStore.getState();
    expect(config.enabled).toBe(false);
    expect(config.port).toBe(7373);
  });

  it('never auto-PUTs config on load (no silent connect)', () => {
    expect(api.putDxClusterConfig).not.toHaveBeenCalled();
    expect(api.connectDxCluster).not.toHaveBeenCalled();
  });

  it('saveConfig pushes the operator choice to the backend', async () => {
    const cfg = {
      enabled: true,
      host: 'dxc.example.org',
      port: 7300,
      callsign: 'K1ABC',
      password: 'secret',
      loginCommands: 'set/filter on',
      autoConnect: true,
    };
    const status = await useDxClusterStore.getState().saveConfig(cfg);
    expect(api.putDxClusterConfig).toHaveBeenCalledWith(cfg);
    expect(status.enabled).toBe(true);
    expect(status.host).toBe('dxc.example.org');
    expect(useDxClusterStore.getState().config.callsign).toBe('K1ABC');
    // The password is retained locally (the API never echoes it back).
    expect(useDxClusterStore.getState().config.password).toBe('secret');
  });

  it('connect/disconnect update the live status', async () => {
    const c = await useDxClusterStore.getState().connect();
    expect(api.connectDxCluster).toHaveBeenCalled();
    expect(c.state).toBe('Connecting');

    const d = await useDxClusterStore.getState().disconnect();
    expect(api.disconnectDxCluster).toHaveBeenCalled();
    expect(d.state).toBe('Disconnected');
  });
});
