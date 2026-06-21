// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { afterEach, describe, expect, it, vi } from 'vitest';

import { BOARD_LABELS, fetchRadioSelection, type BoardKind } from './radio';

describe('BOARD_LABELS', () => {
  it('has a label for every BoardKind value Zeus recognises', () => {
    // If a new BoardKind is added without a corresponding label, the
    // RadioSelector dropdown renders `undefined` and crashes the option
    // list. This test catches that at compile-time-equivalent runtime.
    const expected: BoardKind[] = [
      'Auto',
      'Metis',
      'Hermes',
      'HermesII',
      'Angelia',
      'Orion',
      'HermesLite2',
      'OrionMkII',
      'HermesC10',
      'Unknown',
    ];
    for (const k of expected) {
      expect(BOARD_LABELS[k]).toBeTruthy();
    }
    // BOARD_LABELS keys must be the same set as the expected list — no
    // stragglers from an old enum (Griffin / Atlas) hanging around.
    expect(new Set(Object.keys(BOARD_LABELS))).toEqual(new Set(expected));
  });
});

describe('fetchRadioSelection legacy-name coercion', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('coerces the legacy P1 "Griffin" value to "HermesII"', async () => {
    // Pre-#218 Phase 4 the P1 enum used Griffin; old persisted state or
    // a stale client could surface that name. normalizeBoard maps it to
    // the unified HermesII so the dropdown renders correctly.
    vi.stubGlobal('fetch', () =>
      Promise.resolve(
        new Response(
          JSON.stringify({
            preferred: 'Griffin',
            connected: 'HermesII',
            effective: 'HermesII',
            overrideDetection: false,
          }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
      ),
    );

    const sel = await fetchRadioSelection();
    expect(sel.preferred).toBe('HermesII');
    expect(sel.connected).toBe('HermesII');
  });

  it('coerces the legacy P2 "Atlas" value to "Metis"', async () => {
    vi.stubGlobal('fetch', () =>
      Promise.resolve(
        new Response(
          JSON.stringify({
            preferred: 'Auto',
            connected: 'Atlas',
            effective: 'Metis',
            overrideDetection: false,
          }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
      ),
    );

    const sel = await fetchRadioSelection();
    expect(sel.connected).toBe('Metis');
    expect(sel.effective).toBe('Metis');
  });

  it('falls back to Unknown for an unrecognised value', async () => {
    vi.stubGlobal('fetch', () =>
      Promise.resolve(
        new Response(
          JSON.stringify({
            preferred: 'SomethingFutureWeDontKnow',
            connected: 'Unknown',
            effective: 'Unknown',
            overrideDetection: false,
          }),
          { status: 200, headers: { 'content-type': 'application/json' } },
        ),
      ),
    );

    const sel = await fetchRadioSelection();
    expect(sel.preferred).toBe('Unknown');
  });
});
