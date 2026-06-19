// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { beforeEach, describe, expect, it } from 'vitest';
import { useChatStore } from './chat-store';

const RESET = {
  enabled: false,
  connected: false,
  callsign: null,
  relayUrl: null,
  relayError: null,
  roster: [],
  messages: [],
};

function msg(id: string, ts: number, text = id, from = 'N9WAR', room = 'lobby') {
  return { id, ts, text, from, room };
}

describe('chat-store ingest (0x35 live frames)', () => {
  beforeEach(() => {
    useChatStore.setState(RESET);
  });

  it('status frame updates connection fields', () => {
    useChatStore.getState().ingest({
      kind: 'status',
      status: { enabled: true, connected: true, callsign: 'N9WAR', relayUrl: 'wss://x/chat', error: null },
    });
    const s = useChatStore.getState();
    expect(s.enabled).toBe(true);
    expect(s.connected).toBe(true);
    expect(s.callsign).toBe('N9WAR');
    expect(s.relayUrl).toBe('wss://x/chat');
    expect(s.relayError).toBeNull();
  });

  it('roster frame replaces the roster with normalized operators', () => {
    useChatStore.getState().ingest({
      kind: 'roster',
      roster: [
        { callsign: 'N9WAR', grid: 'EL96eo', freqHz: 14_200_000, mode: 'USB', status: 'rx', since: 1 },
        { callsign: 'W1ABC', freqHz: 7_074_000, status: 'tx', since: 2 },
      ],
    });
    const r = useChatStore.getState().roster;
    expect(r).toHaveLength(2);
    expect(r[0]?.callsign).toBe('N9WAR');
    expect(r[0]?.freqHz).toBe(14_200_000);
    expect(r[1]?.status).toBe('tx');
    expect(r[1]?.grid).toBeNull();
  });

  it('message frames append, de-dupe by id, and stay time-ordered', () => {
    const ing = useChatStore.getState().ingest;
    ing({ kind: 'message', message: msg('b', 200) });
    ing({ kind: 'message', message: msg('a', 100) });
    ing({ kind: 'message', message: msg('b', 200) }); // duplicate id — ignored
    const m = useChatStore.getState().messages;
    expect(m.map((x) => x.id)).toEqual(['a', 'b']);
  });

  it('history frame merges with existing messages without duplicates', () => {
    const ing = useChatStore.getState().ingest;
    ing({ kind: 'message', message: msg('a', 100) });
    ing({ kind: 'history', messages: [msg('a', 100), msg('c', 300), msg('b', 200)] });
    const m = useChatStore.getState().messages;
    expect(m.map((x) => x.id)).toEqual(['a', 'b', 'c']);
  });

  it('caps retained messages at 500, keeping the newest', () => {
    const many = Array.from({ length: 600 }, (_, i) => msg(`m${i}`, i + 1));
    useChatStore.getState().ingest({ kind: 'history', messages: many });
    const m = useChatStore.getState().messages;
    expect(m).toHaveLength(500);
    expect(m[0]?.id).toBe('m100'); // oldest 100 dropped
    expect(m[m.length - 1]?.id).toBe('m599');
  });

  it('ignores malformed or unknown envelopes without throwing', () => {
    const ing = useChatStore.getState().ingest;
    expect(() => ing({ kind: 'nope' } as never)).not.toThrow();
    expect(() => ing(null as never)).not.toThrow();
    expect(useChatStore.getState().messages).toHaveLength(0);
  });
});
