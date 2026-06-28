// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  getSupportAvailability,
  getSupportStatus,
  setSupportAvailability,
  approveSupportRequest,
} from './support';

function mockFetchOnce(body: unknown, ok = true, status = 200) {
  const res = {
    ok,
    status,
    statusText: ok ? 'OK' : 'Error',
    json: async () => body,
  } as Response;
  return vi.spyOn(globalThis, 'fetch').mockResolvedValue(res);
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('getSupportAvailability', () => {
  it('coerces a well-formed response', async () => {
    mockFetchOnce({ available: true, autoShareCrashes: true });
    const s = await getSupportAvailability();
    expect(s).toEqual({ available: true, autoShareCrashes: true });
  });

  it('defaults missing flags to false', async () => {
    mockFetchOnce({});
    const s = await getSupportAvailability();
    expect(s).toEqual({ available: false, autoShareCrashes: false });
  });
});

describe('setSupportAvailability', () => {
  it('PUTs the body and returns the new state', async () => {
    const spy = mockFetchOnce({ available: true, autoShareCrashes: false });
    const s = await setSupportAvailability({ available: true, autoShareCrashes: false });
    expect(s).toEqual({ available: true, autoShareCrashes: false });
    const call = spy.mock.calls[0];
    expect(call?.[1]?.method).toBe('PUT');
  });
});

describe('getSupportStatus', () => {
  it('normalizes pending requests and drops malformed entries', async () => {
    mockFetchOnce({
      available: true,
      autoShareCrashes: false,
      activeSessions: 2,
      pending: [
        {
          requestId: 'r1',
          adminCallsign: 'KB2UKA',
          createdAt: '2026-06-27T12:00:00Z',
          expiresAt: '2026-06-27T12:01:30Z',
        },
        { adminCallsign: 'no-id' }, // dropped (no requestId)
        42, // dropped (not an object)
      ],
    });
    const s = await getSupportStatus();
    expect(s.available).toBe(true);
    expect(s.activeSessions).toBe(2);
    expect(s.pending).toHaveLength(1);
    expect(s.pending[0]?.requestId).toBe('r1');
    expect(s.pending[0]?.adminCallsign).toBe('KB2UKA');
  });

  it('falls back to empty defaults on garbage', async () => {
    mockFetchOnce({});
    const s = await getSupportStatus();
    expect(s).toEqual({
      available: false,
      autoShareCrashes: false,
      pending: [],
      activeSessions: 0,
    });
  });
});

describe('approveSupportRequest', () => {
  it('returns true on a 200', async () => {
    mockFetchOnce({ ok: true });
    expect(await approveSupportRequest('r1')).toBe(true);
  });

  it('returns false on a 404', async () => {
    mockFetchOnce({ ok: false }, false, 404);
    expect(await approveSupportRequest('ghost')).toBe(false);
  });
});
