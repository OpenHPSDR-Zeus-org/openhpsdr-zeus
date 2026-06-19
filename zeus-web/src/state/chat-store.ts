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

// Operator-to-operator chat state. Hydrated via REST on panel mount
// (refreshStatus / loadHistory / loadRoster) and kept live thereafter by the
// 0x35 MSG_TYPE_CHAT_EVENT push frames decoded in realtime/ws-client.ts, which
// dispatch their parsed envelope straight into ingest(). The store is global
// so the WS can drive it whether or not the ChatPanel is mounted.

import { create } from 'zustand';
import {
  chatStatus,
  chatSetEnabled,
  chatSend,
  chatMessages,
  chatRoster,
  normalizeStatus,
  normalizeOperator,
  normalizeMessage,
  type ChatOperator,
  type ChatMessage,
  type ChatStatus,
} from '../api/chat';
import { ApiError } from '../api/client';
import { warnOnce } from '../util/logger';

const HISTORY_LIMIT = 200;

/** Cap retained messages so a long-lived session can't grow unbounded. */
const MAX_MESSAGES = 500;

// The JSON envelope carried by each 0x35 frame. Discriminated on `kind`.
export type ChatEnvelope =
  | { kind: 'status'; status: unknown }
  | { kind: 'roster'; roster: unknown }
  | { kind: 'message'; message: unknown }
  | { kind: 'history'; messages: unknown };

export type ChatStoreState = {
  enabled: boolean;
  connected: boolean;
  callsign: string | null;
  relayUrl: string | null;
  relayError: string | null;
  roster: ChatOperator[];
  messages: ChatMessage[];

  refreshStatus: () => Promise<void>;
  setEnabled: (enabled: boolean) => Promise<void>;
  send: (text: string) => Promise<boolean>;
  loadHistory: () => Promise<void>;
  loadRoster: () => Promise<void>;
  ingest: (envelope: ChatEnvelope) => void;
};

function applyStatus(s: ChatStatus): Partial<ChatStoreState> {
  return {
    enabled: s.enabled,
    connected: s.connected,
    callsign: s.callsign,
    relayUrl: s.relayUrl,
    relayError: s.error,
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

export const useChatStore = create<ChatStoreState>((set) => ({
  enabled: false,
  connected: false,
  callsign: null,
  relayUrl: null,
  relayError: null,
  roster: [],
  messages: [],

  refreshStatus: async () => {
    try {
      const status = await chatStatus();
      set(applyStatus(status));
    } catch {
      // Status is a read-only sanity probe — leave prior state on failure.
    }
  },

  setEnabled: async (enabled) => {
    try {
      const status = await chatSetEnabled(enabled);
      set(applyStatus(status));
    } catch (err) {
      const msg = err instanceof ApiError ? err.message : String(err);
      set({ relayError: msg });
    }
  },

  send: async (text) => {
    const trimmed = text.trim();
    if (!trimmed) return false;
    try {
      const { ok } = await chatSend(trimmed);
      return ok;
    } catch (err) {
      const msg = err instanceof ApiError ? err.message : String(err);
      set({ relayError: msg });
      return false;
    }
  },

  loadHistory: async () => {
    try {
      const messages = await chatMessages(HISTORY_LIMIT);
      set((s) => ({ messages: mergeHistory(s.messages, messages) }));
    } catch {
      // History is best-effort; live frames will backfill.
    }
  },

  loadRoster: async () => {
    try {
      const roster = await chatRoster();
      set({ roster });
    } catch {
      // Roster is best-effort; a live roster frame will replace it.
    }
  },

  ingest: (envelope) => {
    if (!envelope || typeof envelope !== 'object') return;
    switch (envelope.kind) {
      case 'status':
        set(applyStatus(normalizeStatus(envelope.status)));
        return;
      case 'roster':
        set({
          roster: Array.isArray(envelope.roster)
            ? envelope.roster.map(normalizeOperator)
            : [],
        });
        return;
      case 'message': {
        const msg = normalizeMessage(envelope.message);
        set((s) => ({ messages: appendMessage(s.messages, msg) }));
        return;
      }
      case 'history': {
        const incoming = Array.isArray(envelope.messages)
          ? envelope.messages.map(normalizeMessage)
          : [];
        set((s) => ({ messages: mergeHistory(s.messages, incoming) }));
        return;
      }
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
export type { ChatOperator, ChatMessage, ChatStatus } from '../api/chat';
