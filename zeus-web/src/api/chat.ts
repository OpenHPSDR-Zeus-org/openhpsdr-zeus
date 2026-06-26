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

// Operator-to-operator chat REST surface. Live push frames arrive over the
// existing WS (type byte 0x35, see realtime/ws-client.ts); these endpoints
// cover status, enable/disable, send, and the initial history/roster hydrate.

import { ApiError } from './client';

export type ChatOperatorStatus = 'rx' | 'tx' | 'away';

export type ChatOperator = {
  callsign: string;
  grid: string | null;
  freqHz: number | null;
  mode: string | null;
  status: ChatOperatorStatus | null;
  /** Epoch ms — when this operator joined / was last seen. */
  since: number;
};

/**
 * An inline media attachment carried with a message. Photos are sent "like a
 * text message": the bytes ride inside the message as a base64 data URL. The
 * client downscales/compresses before sending (see util/chat-image.ts) so the
 * encoded size stays within the relay's per-message storage cap.
 */
export type ChatAttachment = {
  /** Only "image" today; unknown kinds are ignored when rendering. */
  kind: string;
  /** MIME type, e.g. "image/jpeg". */
  mime: string;
  /** Base64 data URL — usable directly as an <img> src. */
  dataUrl: string;
  /** Original filename, if known. */
  name: string | null;
  width: number | null;
  height: number | null;
  /** Decoded byte size, if known. */
  size: number | null;
};

/** Max length of an attachment data URL (chars). Mirrors the C#/relay caps. */
export const MAX_ATTACHMENT_DATAURL_LEN = 120_000;

export type ChatMessage = {
  id: string;
  from: string;
  text: string;
  /** Epoch ms. */
  ts: number;
  room: string;
  /** Optional inline photo; null for plain text messages. */
  attachment: ChatAttachment | null;
};

export type ChatStatus = {
  enabled: boolean;
  connected: boolean;
  callsign: string | null;
  relayUrl: string | null;
  error: string | null;
  /** Whether this operator is a relay moderator (can create rooms / ban). */
  isAdmin: boolean;
  /** Whether this operator's frequency is shared at all (eye toggle). */
  freqPublic: boolean;
};

export type ChatRoomKind = 'public' | 'group' | 'dm';

export type ChatRoom = {
  id: string;
  name: string;
  kind: ChatRoomKind;
  members: string[];
};

export type ChatFriends = {
  /** Mutual friends — their frequency is visible to you. */
  accepted: string[];
  /** Requests awaiting your accept/deny. */
  incoming: string[];
  /** Requests you've sent that are still pending. */
  outgoing: string[];
};

function toStr(v: unknown): string | null {
  return typeof v === 'string' && v.length > 0 ? v : null;
}

function toNum(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) ? v : null;
}

function toStatus(v: unknown): ChatOperatorStatus | null {
  return v === 'rx' || v === 'tx' || v === 'away' ? v : null;
}

export function normalizeStatus(raw: unknown): ChatStatus {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    enabled: Boolean(r.enabled),
    connected: Boolean(r.connected),
    callsign: toStr(r.callsign),
    relayUrl: toStr(r.relayUrl),
    error: toStr(r.error),
    isAdmin: Boolean(r.isAdmin),
    freqPublic: r.freqPublic !== false, // default visible
  };
}

export function normalizeRoom(raw: unknown): ChatRoom {
  const r = (raw ?? {}) as Record<string, unknown>;
  const kind = r.kind === 'public' || r.kind === 'dm' ? r.kind : 'group';
  return {
    id: typeof r.id === 'string' ? r.id : '',
    name: typeof r.name === 'string' ? r.name : '',
    kind,
    members: toCallList(r.members),
  };
}

export function normalizeOperator(raw: unknown): ChatOperator {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    callsign: typeof r.callsign === 'string' ? r.callsign : '',
    grid: toStr(r.grid),
    freqHz: toNum(r.freqHz),
    mode: toStr(r.mode),
    status: toStatus(r.status),
    since: toNum(r.since) ?? 0,
  };
}

function toCallList(v: unknown): string[] {
  return Array.isArray(v)
    ? v.filter((c): c is string => typeof c === 'string' && c.length > 0).map((c) => c.toUpperCase())
    : [];
}

export function normalizeFriends(raw: unknown): ChatFriends {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    accepted: toCallList(r.accepted),
    incoming: toCallList(r.incoming),
    outgoing: toCallList(r.outgoing),
  };
}

/**
 * Parse an optional attachment. Returns null unless it is a well-formed image
 * data URL within the size cap — a malformed/oversized attachment degrades the
 * message to text-only rather than rendering a broken image.
 */
export function normalizeAttachment(raw: unknown): ChatAttachment | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  const mime = typeof r.mime === 'string' ? r.mime : '';
  const dataUrl = typeof r.dataUrl === 'string' ? r.dataUrl : '';
  if (!mime.startsWith('image/')) return null;
  if (!dataUrl.startsWith('data:image/')) return null;
  if (dataUrl.length > MAX_ATTACHMENT_DATAURL_LEN) return null;
  return {
    kind: 'image',
    mime,
    dataUrl,
    name: toStr(r.name),
    width: toNum(r.width),
    height: toNum(r.height),
    size: toNum(r.size),
  };
}

export function normalizeMessage(raw: unknown): ChatMessage {
  const r = (raw ?? {}) as Record<string, unknown>;
  return {
    id: typeof r.id === 'string' ? r.id : '',
    from: typeof r.from === 'string' ? r.from : '',
    text: typeof r.text === 'string' ? r.text : '',
    ts: toNum(r.ts) ?? 0,
    room: typeof r.room === 'string' ? r.room : '',
    attachment: normalizeAttachment(r.attachment),
  };
}

async function jsonFetch<T>(
  input: RequestInfo,
  init: RequestInit | undefined,
  parse: (raw: unknown) => T,
): Promise<T> {
  const res = await fetch(input, init);
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    try {
      const body = (await res.json()) as unknown;
      if (
        body &&
        typeof body === 'object' &&
        'error' in body &&
        typeof (body as { error: unknown }).error === 'string'
      ) {
        message = (body as { error: string }).error;
      }
    } catch {
      /* non-JSON body — keep status text */
    }
    throw new ApiError(res.status, message);
  }
  return parse((await res.json()) as unknown);
}

export function chatStatus(signal?: AbortSignal): Promise<ChatStatus> {
  return jsonFetch('/api/chat/status', { signal }, normalizeStatus);
}

export function chatSetEnabled(enabled: boolean, signal?: AbortSignal): Promise<ChatStatus> {
  return jsonFetch(
    '/api/chat/enable',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ enabled }),
      signal,
    },
    normalizeStatus,
  );
}

/**
 * Heartbeat telling the backend whether this client currently has the Chat
 * panel displayed. Presence (the relay connection) is gated on this, so an
 * enabled operator who isn't showing the panel stays off everyone's roster.
 */
export function chatSetVisible(visible: boolean, signal?: AbortSignal): Promise<ChatStatus> {
  return jsonFetch(
    '/api/chat/visible',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ visible }),
      signal,
    },
    normalizeStatus,
  );
}

export function chatSend(
  text: string,
  room?: string,
  attachment?: ChatAttachment | null,
  signal?: AbortSignal,
): Promise<{ ok: boolean }> {
  const body: Record<string, unknown> = { text };
  if (room) body.room = room;
  if (attachment) body.attachment = attachment;
  return jsonFetch(
    '/api/chat/send',
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
      signal,
    },
    (raw) => ({ ok: Boolean((raw as { ok?: unknown } | null)?.ok) }),
  );
}

export function chatMessages(limit: number, signal?: AbortSignal): Promise<ChatMessage[]> {
  return jsonFetch(
    `/api/chat/messages?limit=${encodeURIComponent(limit)}`,
    { signal },
    (raw) => {
      const arr = (raw as { messages?: unknown } | null)?.messages;
      return Array.isArray(arr) ? arr.map(normalizeMessage) : [];
    },
  );
}

export function chatRoster(signal?: AbortSignal): Promise<ChatOperator[]> {
  return jsonFetch('/api/chat/roster', { signal }, (raw) => {
    const arr = (raw as { operators?: unknown } | null)?.operators;
    return Array.isArray(arr) ? arr.map(normalizeOperator) : [];
  });
}

export function chatFriends(signal?: AbortSignal): Promise<ChatFriends> {
  return jsonFetch('/api/chat/friends', { signal }, normalizeFriends);
}

/** request / accept / deny / remove — all POST { callsign } and return { ok }. */
function friendAction(
  path: 'request' | 'accept' | 'deny' | 'remove',
  callsign: string,
  signal?: AbortSignal,
): Promise<{ ok: boolean }> {
  return jsonFetch(
    `/api/chat/friends/${path}`,
    {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ callsign }),
      signal,
    },
    (raw) => ({ ok: Boolean((raw as { ok?: unknown } | null)?.ok) }),
  );
}

export const chatFriendRequest = (callsign: string, signal?: AbortSignal) =>
  friendAction('request', callsign, signal);
export const chatFriendAccept = (callsign: string, signal?: AbortSignal) =>
  friendAction('accept', callsign, signal);
export const chatFriendDeny = (callsign: string, signal?: AbortSignal) =>
  friendAction('deny', callsign, signal);
export const chatFriendRemove = (callsign: string, signal?: AbortSignal) =>
  friendAction('remove', callsign, signal);

// ── Rooms / DMs / history / moderation / eye toggle ───────────────────────

function postOk(path: string, body: unknown, signal?: AbortSignal): Promise<{ ok: boolean }> {
  return jsonFetch(
    path,
    { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body), signal },
    (raw) => ({ ok: Boolean((raw as { ok?: unknown } | null)?.ok) }),
  );
}

export function chatRooms(signal?: AbortSignal): Promise<ChatRoom[]> {
  return jsonFetch('/api/chat/rooms', { signal }, (raw) => {
    const arr = (raw as { rooms?: unknown } | null)?.rooms;
    return Array.isArray(arr) ? arr.map(normalizeRoom) : [];
  });
}

export const chatDm = (
  to: string,
  text: string,
  attachment?: ChatAttachment | null,
  signal?: AbortSignal,
) => postOk('/api/chat/dm', attachment ? { to, text, attachment } : { to, text }, signal);

export const chatRequestHistory = (room: string, signal?: AbortSignal) =>
  postOk('/api/chat/history', { room }, signal);

export const chatSetFreqVisibility = (isPublic: boolean, signal?: AbortSignal) =>
  postOk('/api/chat/freq-visibility', { public: isPublic }, signal);

export const chatCreateRoom = (name: string, signal?: AbortSignal) =>
  postOk('/api/chat/admin/room/create', { name }, signal);
export const chatDeleteRoom = (room: string, signal?: AbortSignal) =>
  postOk('/api/chat/admin/room/delete', { room }, signal);
export const chatAddMember = (room: string, callsign: string, signal?: AbortSignal) =>
  postOk('/api/chat/admin/room/add', { room, callsign }, signal);
export const chatRemoveMember = (room: string, callsign: string, signal?: AbortSignal) =>
  postOk('/api/chat/admin/room/remove', { room, callsign }, signal);
export const chatBan = (callsign: string, signal?: AbortSignal) =>
  postOk('/api/chat/admin/ban', { callsign }, signal);
export const chatUnban = (callsign: string, signal?: AbortSignal) =>
  postOk('/api/chat/admin/unban', { callsign }, signal);
/** Admin: wipe a room's history (defaults to the public lobby). */
export const chatClearRoom = (room?: string, signal?: AbortSignal) =>
  postOk('/api/chat/admin/clear', room ? { room } : {}, signal);
/** Admin: broadcast a one-off global announcement to every connected operator. */
export const chatBroadcast = (text: string, signal?: AbortSignal) =>
  postOk('/api/chat/admin/broadcast', { text }, signal);
/** Admin: request the current ban list (relay replies with a `bans` push frame). */
export const chatListBans = (signal?: AbortSignal) =>
  postOk('/api/chat/admin/bans', {}, signal);
