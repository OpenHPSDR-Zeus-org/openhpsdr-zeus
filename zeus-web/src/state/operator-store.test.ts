// SPDX-License-Identifier: GPL-2.0-or-later
import { beforeEach, describe, expect, it, vi } from 'vitest';

// Mock the REST layer BEFORE the store imports it — the store fires a one-shot
// hydrate on module load. Default = unset identity (no override, no QRZ).
vi.mock('../api/operator', () => ({
  getOperator: vi.fn(async () => ({
    callsign: '',
    grid: '',
    resolvedCallsign: '',
    resolvedGrid: '',
    callsignFromQrz: false,
    gridFromQrz: false,
    identityResolved: false,
  })),
  postOperator: vi.fn(async (id: { callsign: string; grid: string }) => ({
    callsign: id.callsign,
    grid: id.grid,
    // Empty override falls back to a QRZ home value in this fake.
    resolvedCallsign: id.callsign || 'W1QRZ',
    resolvedGrid: id.grid || 'FN31',
    callsignFromQrz: id.callsign === '',
    gridFromQrz: id.grid === '',
    identityResolved: true,
  })),
}));

import { useOperatorStore } from './operator-store';
import * as api from '../api/operator';

describe('operator-store (server-authoritative)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useOperatorStore.setState({
      call: '',
      grid: '',
      resolvedCall: '',
      resolvedGrid: '',
      callFromQrz: false,
      gridFromQrz: false,
      hydrated: false,
    });
  });

  it('does not use localStorage as the system of record (server-backed)', () => {
    // The old port-scoped localStorage path is gone — identity round-trips the
    // server. Setting a call must POST, not write a localStorage key.
    useOperatorStore.getState().setCall('k1abc');
    expect(api.postOperator).toHaveBeenCalled();
  });

  it('hydrate maps the resolved identity + QRZ-fallback flags', async () => {
    vi.mocked(api.getOperator).mockResolvedValueOnce({
      callsign: '',
      grid: '',
      resolvedCallsign: 'W1QRZ',
      resolvedGrid: 'FN31',
      callsignFromQrz: true,
      gridFromQrz: true,
      identityResolved: true,
    });
    await useOperatorStore.getState().hydrate();
    const s = useOperatorStore.getState();
    expect(s.call).toBe(''); // no override
    expect(s.resolvedCall).toBe('W1QRZ'); // from QRZ
    expect(s.callFromQrz).toBe(true);
    expect(s.gridFromQrz).toBe(true);
    expect(s.hydrated).toBe(true);
  });

  it('ungates TX once a resolved call exists (the canCall condition)', async () => {
    // canCall in the TX control is `resolvedCall.trim().length > 0`.
    expect(useOperatorStore.getState().resolvedCall.trim().length > 0).toBe(false);
    await useOperatorStore.getState().save({ call: 'K1ABC', grid: 'FN42' });
    expect(useOperatorStore.getState().resolvedCall.trim().length > 0).toBe(true);
  });

  it('setCall is optimistic locally and persists upper-cased/trimmed to the server', async () => {
    useOperatorStore.getState().setCall('  k1abc ');
    expect(useOperatorStore.getState().call).toBe('K1ABC'); // optimistic, normalized
    await Promise.resolve();
    await Promise.resolve();
    expect(api.postOperator).toHaveBeenCalledWith({ callsign: 'K1ABC', grid: '' });
  });

  it('save round-trips the server response into resolved values', async () => {
    await useOperatorStore.getState().save({ call: 'K1ABC', grid: 'FN42' });
    const s = useOperatorStore.getState();
    expect(s.call).toBe('K1ABC');
    expect(s.resolvedCall).toBe('K1ABC');
    expect(s.callFromQrz).toBe(false);
  });

  it('never throws when the server is unreachable (offline edit)', async () => {
    vi.mocked(api.getOperator).mockRejectedValueOnce(new Error('offline'));
    await expect(useOperatorStore.getState().hydrate()).resolves.toBeUndefined();
    expect(useOperatorStore.getState().hydrated).toBe(true);
  });
});
