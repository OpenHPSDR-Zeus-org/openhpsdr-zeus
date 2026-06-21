// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Unit tests for the remote-mode URL parsing — the ?remote=<CALLSIGN> gate that
// decides whether the SPA runs as a local /ws client or a WebRTC RX monitor.

import { afterEach, describe, expect, it } from 'vitest';
import { getRemoteCallsign, isRemoteMode } from './remote-client';

function setSearch(search: string): void {
  // jsdom honours replaceState for location.search without a real navigation.
  window.history.replaceState(null, '', `/${search}`);
}

describe('remote-client URL parsing', () => {
  afterEach(() => setSearch(''));

  it('returns null when ?remote is absent', () => {
    setSearch('?foo=bar');
    expect(getRemoteCallsign()).toBeNull();
    expect(isRemoteMode()).toBe(false);
  });

  it('returns null for an empty ?remote', () => {
    setSearch('?remote=');
    expect(getRemoteCallsign()).toBeNull();
    expect(isRemoteMode()).toBe(false);
  });

  it('returns the upper-cased, trimmed callsign', () => {
    setSearch('?remote=n9war');
    expect(getRemoteCallsign()).toBe('N9WAR');
    expect(isRemoteMode()).toBe(true);
  });

  it('treats a whitespace-only ?remote as not-remote', () => {
    setSearch('?remote=%20%20');
    expect(getRemoteCallsign()).toBeNull();
    expect(isRemoteMode()).toBe(false);
  });
});
