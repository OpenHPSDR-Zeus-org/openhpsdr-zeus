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

// Operator-to-operator chat state. Hydrated via REST on panel mount and kept
// live by the 0x35 MSG_TYPE_CHAT_EVENT push frames decoded in
// realtime/ws-client.ts, which dispatch their parsed envelope into ingest().
// The store is global so the WS can drive it whether or not the ChatPanel is
// mounted.
//
// Channels: the public lobby ("lobby"), admin-created groups ("g…"), and DMs
// ("dm:LO:HI"). Messages are kept per-room; the active tab selects which thread
// the panel shows. Friendships, frequency visibility, rooms, and admin status
// are all relay-authoritative (mirrored from frames), never invented locally.

import { create } from 'zustand';
import {
  chatStatus,
  chatSetEnabled,
  chatSend,
  chatDm,
  chatMessages,
  chatRoster,
  chatRooms,
  chatRequestHistory,
  chatFriends,
  chatFriendRequest,
  chatFriendAccept,
  chatFriendDeny,
  chatFriendRemove,
  chatSetFreqVisibility,
  chatCreateRoom,
  chatDeleteRoom,
  chatAddMember,
  chatRemoveMember,
  chatBan,
  chatUnban,
  normalizeStatus,
  normalizeOperator,
  normalizeMessage,
  normalizeFriends,
  normalizeRoom,
  type ChatOperator,
  type ChatMessage,
  type ChatStatus,
  type ChatRoom,
  type ChatFriends,
} from '../api/chat';
import { ApiError } from '../api/client';
import { warnOnce } from '../util/logger';

const HISTORY_LIMIT = 200;
export const PUBLIC_ROOM = 'lobby';

/** Cap retained messages per room so a long-lived session can't grow unbounded. */
const MAX_MESSAGES = 500;

/** Canonical DM room id for two callsigns (order-independent, uppercased). */
export function dmRoomId(a: string, b: string): string {
  const [lo, hi] = [a.toUpperCase(), b.toUpperCase()].sort();
  return `dm:${lo}:${hi}`;
}

/** The other party in a "dm:LO:HI" id, relative to `me`. */
export function dmOther(roomId: string, me: string | null): string | null {
  if (!roomId.startsWith('dm:')) return null;
  const [, a, b] = roomId.split(':');
  const meUp = (me ?? '').toUpperCase();
  if (a && a !== meUp) return a;
  if (b && b !== meUp) return b;
  return a ?? b ?? null;
}

// The JSON envelope carried by each 0x35 frame. Discriminated on `kind`.
export type ChatEnvelope =
  | { kind: 'status'; status: unknown }
  | { kind: 'roster'; roster: unknown }
  | { kind: 'message'; message: unknown }
  | { kind: 'history'; room?: unknown; messages: unknown }
  | { kind: 'friends'; friends: unknown }
  | { kind: 'rooms'; rooms: unknown }
  | { kind: 'banned'; message?: unknown };

export type ChatStoreState = {
  enabled: boolean;
  connected: boolean;
  callsign: string | null;
  relayUrl: string | null;
  relayError: string | null;
  isAdmin: boolean;
  freqPublic: boolean;
  roster: ChatOperator[];

  // Channels + per-room message threads + which tab is active.
  rooms: ChatRoom[];
  activeRoom: string;
  messagesByRoom: Record<string, ChatMessage[]>;
  unreadByRoom: Record<string, number>;

  // Friend graph (consent gate for seeing freq).
  acceptedFriends: string[];
  incomingRequests: string[];
  outgoingRequests: string[];

  refreshStatus: () => Promise<void>;
  setEnabled: (enabled: boolean) => Promise<void>;
  send: (text: string) => Promise<boolean>;
  loadHistory: () => Promise<void>;
  loadRoster: () => Promise<void>;
  loadRooms: () => Promise<void>;
  loadFriends: () => Promise<void>;
  setActiveRoom: (room: string) => void;
  openDm: (callsign: string) => void;
  requestRoomHistory: (room: string) => Promise<void>;
  setFreqVisibility: (isPublic: boolean) => Promise<void>;
  requestFriend: (callsign: string) => Promise<void>;
  acceptFriend: (callsign: string) => Promise<void>;
  denyFriend: (callsign: string) => Promise<void>;
  removeFriend: (callsign: string) => Promise<void>;
  // Admin / moderation.
  createRoom: (name: string) => Promise<void>;
  deleteRoom: (room: string) => Promise<void>;
  addMember: (room: string, callsign: string) => Promise<void>;
  removeMember: (room: string, callsign: string) => Promise<void>;
  ban: (callsign: string) => Promise<void>;
  unban: (callsign: string) => Promise<void>;
  ingest: (envelope: ChatEnvelope) => void;
};

function applyStatus(s: ChatStatus): Partial<ChatStoreState> {
  return {
    enabled: s.enabled,
    connected: s.connected,
    callsign: s.callsign,
    relayUrl: s.relayUrl,
    relayError: s.error,
    isAdmin: s.isAdmin,
    freqPublic: s.freqPublic,
  };
}

function applyFriends(f: ChatFriends): Partial<ChatStoreState> {
  return {
    acceptedFriends: f.accepted,
    incomingRequests: f.incoming,
    outgoingRequests: f.outgoing,
  };
}

/** Append a message, de-duping by id and keeping chronological order. */
function appendMessage(existing: ChatMessage[], msg: ChatMessage): ChatMessage[] {
  if (msg.id && existing.some((m) => m.id === msg.id)) return existing;
  const next = [...existing, msg];
  next.sort((a, b) => a.ts - b.ts);
  return next.length > MAX_MESSAGES ? next.slice(next.length - MAX_MESSAGES) : next;
}

function mergeHistory(existing: ChatMessage[], incoming: ChatMessage[]): ChatMessage[] {
  const byId = new Map<string, ChatMessage>();
  for (const m of existing) if (m.id) byId.set(m.id, m);
  for (const m of incoming) if (m.id) byId.set(m.id, m);
  const merged = [...byId.values()];
  merged.sort((a, b) => a.ts - b.ts);
  return merged.length > MAX_MESSAGES ? merged.slice(merged.length - MAX_MESSAGES) : merged;
}

function errMsg(err: unknown): string {
  return err instanceof ApiError ? err.message : String(err);
}

export const useChatStore = create<ChatStoreState>((set, get) => ({
  enabled: false,
  connected: false,
  callsign: null,
  relayUrl: null,
  relayError: null,
  isAdmin: false,
  freqPublic: true,
  roster: [],
  rooms: [{ id: PUBLIC_ROOM, name: 'Public', kind: 'public', members: [] }],
  activeRoom: PUBLIC_ROOM,
  messagesByRoom: {},
  unreadByRoom: {},
  acceptedFriends: [],
  incomingRequests: [],
  outgoingRequests: [],

  refreshStatus: async () => {
    try {
      set(applyStatus(await chatStatus()));
    } catch {
      // Status is a read-only sanity probe — leave prior state on failure.
    }
  },

  setEnabled: async (enabled) => {
    try {
      set(applyStatus(await chatSetEnabled(enabled)));
    } catch (err) {
      set({ relayError: errMsg(err) });
    }
  },

  send: async (text) => {
    const trimmed = text.trim();
    if (!trimmed) return false;
    const { activeRoom, callsign } = get();
    try {
      if (activeRoom.startsWith('dm:')) {
        const other = dmOther(activeRoom, callsign);
        if (!other) return false;
        await chatDm(other, trimmed);
      } else {
        await chatSend(trimmed, activeRoom);
      }
      return true;
    } catch (err) {
      set({ relayError: errMsg(err) });
      return false;
    }
  },

  loadHistory: async () => {
    try {
      const messages = await chatMessages(HISTORY_LIMIT);
      set((s) => ({
        messagesByRoom: { ...s.messagesByRoom, [PUBLIC_ROOM]: mergeHistory(s.messagesByRoom[PUBLIC_ROOM] ?? [], messages) },
      }));
    } catch {
      // History is best-effort; live frames will backfill.
    }
  },

  loadRoster: async () => {
    try {
      set({ roster: await chatRoster() });
    } catch {
      // Roster is best-effort; a live roster frame will replace it.
    }
  },

  loadRooms: async () => {
    try {
      const rooms = await chatRooms();
      if (rooms.length) set({ rooms });
    } catch {
      // Best-effort; a live rooms frame will replace it.
    }
  },

  loadFriends: async () => {
    try {
      set(applyFriends(await chatFriends()));
    } catch {
      // Best-effort; a live friends frame will replace it.
    }
  },

  setActiveRoom: (room) => {
    set((s) => ({ activeRoom: room, unreadByRoom: { ...s.unreadByRoom, [room]: 0 } }));
    if (get().messagesByRoom[room] === undefined) void get().requestRoomHistory(room);
  },

  openDm: (callsign) => {
    const me = get().callsign ?? '';
    const id = dmRoomId(callsign, me || callsign);
    set((s) => {
      const exists = s.rooms.some((r) => r.id === id);
      const rooms = exists
        ? s.rooms
        : [...s.rooms, { id, name: callsign.toUpperCase(), kind: 'dm' as const, members: [me, callsign.toUpperCase()].filter(Boolean) }];
      return { rooms, activeRoom: id, unreadByRoom: { ...s.unreadByRoom, [id]: 0 } };
    });
    if (get().messagesByRoom[id] === undefined) void get().requestRoomHistory(id);
  },

  requestRoomHistory: async (room) => {
    if (room === PUBLIC_ROOM) {
      await get().loadHistory();
      return;
    }
    try {
      await chatRequestHistory(room); // relay pushes a history frame back
    } catch {
      // Best-effort; the tab simply has no scrollback yet.
    }
  },

  setFreqVisibility: async (isPublic) => {
    set({ freqPublic: isPublic }); // optimistic; status frame confirms
    try {
      await chatSetFreqVisibility(isPublic);
    } catch (err) {
      set({ relayError: errMsg(err) });
      void get().refreshStatus();
    }
  },

  // Friend actions are fire-and-forget: the relay echoes the resulting graph
  // back as a 0x35 friends frame, which ingest() applies as the source of truth.
  requestFriend: async (callsign) => {
    try { await chatFriendRequest(callsign); } catch (err) { set({ relayError: errMsg(err) }); }
  },
  acceptFriend: async (callsign) => {
    try { await chatFriendAccept(callsign); } catch (err) { set({ relayError: errMsg(err) }); }
  },
  denyFriend: async (callsign) => {
    try { await chatFriendDeny(callsign); } catch (err) { set({ relayError: errMsg(err) }); }
  },
  removeFriend: async (callsign) => {
    try { await chatFriendRemove(callsign); } catch (err) { set({ relayError: errMsg(err) }); }
  },

  createRoom: async (name) => {
    try { await chatCreateRoom(name); } catch (err) { set({ relayError: errMsg(err) }); }
  },
  deleteRoom: async (room) => {
    try { await chatDeleteRoom(room); } catch (err) { set({ relayError: errMsg(err) }); }
  },
  addMember: async (room, callsign) => {
    try { await chatAddMember(room, callsign); } catch (err) { set({ relayError: errMsg(err) }); }
  },
  removeMember: async (room, callsign) => {
    try { await chatRemoveMember(room, callsign); } catch (err) { set({ relayError: errMsg(err) }); }
  },
  ban: async (callsign) => {
    try { await chatBan(callsign); } catch (err) { set({ relayError: errMsg(err) }); }
  },
  unban: async (callsign) => {
    try { await chatUnban(callsign); } catch (err) { set({ relayError: errMsg(err) }); }
  },

  ingest: (envelope) => {
    if (!envelope || typeof envelope !== 'object') return;
    switch (envelope.kind) {
      case 'status':
        set(applyStatus(normalizeStatus(envelope.status)));
        return;
      case 'roster':
        set({ roster: Array.isArray(envelope.roster) ? envelope.roster.map(normalizeOperator) : [] });
        return;
      case 'message': {
        const msg = normalizeMessage(envelope.message);
        const room = msg.room || PUBLIC_ROOM;
        set((s) => {
          const next = appendMessage(s.messagesByRoom[room] ?? [], msg);
          const own = !!s.callsign && msg.from.toUpperCase() === s.callsign.toUpperCase();
          const bump = s.activeRoom !== room && !own;
          return {
            messagesByRoom: { ...s.messagesByRoom, [room]: next },
            unreadByRoom: bump
              ? { ...s.unreadByRoom, [room]: (s.unreadByRoom[room] ?? 0) + 1 }
              : s.unreadByRoom,
          };
        });
        return;
      }
      case 'history': {
        const room = typeof envelope.room === 'string' ? envelope.room : PUBLIC_ROOM;
        const incoming = Array.isArray(envelope.messages) ? envelope.messages.map(normalizeMessage) : [];
        set((s) => ({
          messagesByRoom: { ...s.messagesByRoom, [room]: mergeHistory(s.messagesByRoom[room] ?? [], incoming) },
        }));
        return;
      }
      case 'friends':
        set(applyFriends(normalizeFriends(envelope.friends)));
        return;
      case 'rooms': {
        const incoming = Array.isArray(envelope.rooms) ? envelope.rooms.map(normalizeRoom) : [];
        set((s) => {
          // Preserve any local placeholder DM tabs the relay hasn't echoed yet.
          const ids = new Set(incoming.map((r) => r.id));
          const placeholders = s.rooms.filter((r) => r.kind === 'dm' && !ids.has(r.id));
          const rooms = [...incoming, ...placeholders];
          // If the active tab vanished (room deleted / membership removed), fall back to public.
          const activeRoom = rooms.some((r) => r.id === s.activeRoom) ? s.activeRoom : PUBLIC_ROOM;
          return { rooms, activeRoom };
        });
        return;
      }
      case 'banned':
        set({ relayError: typeof envelope.message === 'string' ? envelope.message : 'You have been banned from ZeusChat.' });
        return;
      default:
        warnOnce('chat-ingest-unknown-kind', `unknown chat envelope kind: ${String((envelope as { kind?: unknown }).kind)}`);
    }
  },
}));

// Convenience for callers that only need the current callsign synchronously.
export function ownCallsign(): string | null {
  return useChatStore.getState().callsign;
}

// Re-export the wire types so panels can import them from one place.
export type { ChatOperator, ChatMessage, ChatStatus, ChatRoom } from '../api/chat';
