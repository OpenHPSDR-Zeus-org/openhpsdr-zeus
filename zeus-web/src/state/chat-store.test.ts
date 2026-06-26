// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { beforeEach, describe, expect, it } from 'vitest';
import { useChatStore, PUBLIC_ROOM } from './chat-store';

// Per-room store: messages live in messagesByRoom keyed by room id, the active
// tab gates unread counting, and friends/rooms mirror relay frames.
const RESET = {
  enabled: false,
  connected: false,
  callsign: null,
  relayUrl: null,
  relayError: null,
  isAdmin: false,
  freqPublic: true,
  roster: [],
  rooms: [{ id: PUBLIC_ROOM, name: 'Public', kind: 'public' as const, members: [] }],
  activeRoom: PUBLIC_ROOM,
  messagesByRoom: {},
  unreadByRoom: {},
  acceptedFriends: [],
  incomingRequests: [],
  outgoingRequests: [],
};

function msg(id: string, ts: number, text = id, from = 'N9WAR', room = PUBLIC_ROOM) {
  return { id, ts, text, from, room };
}

const lobby = () => useChatStore.getState().messagesByRoom[PUBLIC_ROOM] ?? [];

describe('chat-store ingest (0x35 live frames)', () => {
  beforeEach(() => {
    useChatStore.setState(RESET);
  });

  it('status frame updates connection + capability fields', () => {
    useChatStore.getState().ingest({
      kind: 'status',
      status: {
        enabled: true, connected: true, callsign: 'N9WAR',
        relayUrl: 'wss://x/chat', error: null, isAdmin: true, freqPublic: false,
      },
    });
    const s = useChatStore.getState();
    expect(s.enabled).toBe(true);
    expect(s.connected).toBe(true);
    expect(s.callsign).toBe('N9WAR');
    expect(s.isAdmin).toBe(true);
    expect(s.freqPublic).toBe(false);
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

  it('message frames append to their room, de-dupe by id, and stay time-ordered', () => {
    const ing = useChatStore.getState().ingest;
    ing({ kind: 'message', message: msg('b', 200) });
    ing({ kind: 'message', message: msg('a', 100) });
    ing({ kind: 'message', message: msg('b', 200) }); // duplicate id — ignored
    expect(lobby().map((x) => x.id)).toEqual(['a', 'b']);
  });

  it('preserves a valid image attachment and drops a malformed one', () => {
    const ing = useChatStore.getState().ingest;
    const dataUrl = 'data:image/jpeg;base64,/9j/AA==';
    ing({
      kind: 'message',
      message: {
        id: 'img1',
        ts: 100,
        from: 'N9WAR',
        text: 'look',
        room: PUBLIC_ROOM,
        attachment: { kind: 'image', mime: 'image/jpeg', dataUrl, width: 800, height: 600 },
      },
    });
    // A non-image data URL must not survive normalization.
    ing({
      kind: 'message',
      message: {
        id: 'img2',
        ts: 200,
        from: 'N9WAR',
        text: 'nope',
        room: PUBLIC_ROOM,
        attachment: { kind: 'image', mime: 'application/pdf', dataUrl: 'data:application/pdf;base64,AAAA' },
      },
    });
    const msgs = lobby();
    expect(msgs.find((m) => m.id === 'img1')?.attachment?.dataUrl).toBe(dataUrl);
    expect(msgs.find((m) => m.id === 'img1')?.attachment?.width).toBe(800);
    expect(msgs.find((m) => m.id === 'img2')?.attachment).toBeNull();
  });

  it('routes messages to the correct room and counts unread for inactive rooms', () => {
    const ing = useChatStore.getState().ingest;
    ing({ kind: 'message', message: msg('g1', 100, 'hi', 'W1ABC', 'g123') });
    expect(useChatStore.getState().messagesByRoom['g123']?.map((m) => m.id)).toEqual(['g1']);
    // active room is lobby, so a g123 message bumps its unread badge.
    expect(useChatStore.getState().unreadByRoom['g123']).toBe(1);
    expect(useChatStore.getState().unreadByRoom[PUBLIC_ROOM] ?? 0).toBe(0);
  });

  it('history frame merges into the named room without duplicates', () => {
    const ing = useChatStore.getState().ingest;
    ing({ kind: 'message', message: msg('a', 100) });
    ing({ kind: 'history', room: PUBLIC_ROOM, messages: [msg('a', 100), msg('c', 300), msg('b', 200)] });
    expect(lobby().map((x) => x.id)).toEqual(['a', 'b', 'c']);
  });

  it('caps retained messages per room at 500, keeping the newest', () => {
    const many = Array.from({ length: 600 }, (_, i) => msg(`m${i}`, i + 1));
    useChatStore.getState().ingest({ kind: 'history', room: PUBLIC_ROOM, messages: many });
    const m = lobby();
    expect(m).toHaveLength(500);
    expect(m[0]?.id).toBe('m100'); // oldest 100 dropped
    expect(m[m.length - 1]?.id).toBe('m599');
  });

  it('friends frame mirrors the consent graph', () => {
    useChatStore.getState().ingest({
      kind: 'friends',
      friends: { accepted: ['W1ABC'], incoming: ['K9XYZ'], outgoing: ['N0DEF'] },
    });
    const s = useChatStore.getState();
    expect(s.acceptedFriends).toEqual(['W1ABC']);
    expect(s.incomingRequests).toEqual(['K9XYZ']);
    expect(s.outgoingRequests).toEqual(['N0DEF']);
  });

  it('rooms frame replaces the visible room list', () => {
    useChatStore.getState().ingest({
      kind: 'rooms',
      rooms: [
        { id: PUBLIC_ROOM, name: 'Public', kind: 'public', members: [] },
        { id: 'g1', name: 'Net Control', kind: 'group', members: ['N9WAR'] },
      ],
    });
    const ids = useChatStore.getState().rooms.map((r) => r.id);
    expect(ids).toContain('g1');
    expect(useChatStore.getState().rooms.find((r) => r.id === 'g1')?.kind).toBe('group');
  });

  it('ignores malformed or unknown envelopes without throwing', () => {
    const ing = useChatStore.getState().ingest;
    expect(() => ing({ kind: 'nope' } as never)).not.toThrow();
    expect(() => ing(null as never)).not.toThrow();
    expect(lobby()).toHaveLength(0);
  });
});
