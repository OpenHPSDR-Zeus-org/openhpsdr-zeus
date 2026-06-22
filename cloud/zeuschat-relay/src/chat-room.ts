import { DurableObject } from 'cloudflare:workers';
import type { Env } from './types';
import {
  type ClientToRelay,
  type RelayToClient,
  type Operator,
  type RoomInfo,
  type RoomKind,
  type Msg,
  type PresenceStatus,
  MAX_MESSAGE_LEN,
  PUBLIC_ROOM,
  PUBLIC_RETENTION_MS,
  PRIVATE_RETENTION_MS,
  PRUNE_INTERVAL_MS,
  HISTORY_LIMIT,
  dmRoomId,
} from './protocol';

/**
 * Per-connection state, stored on the WebSocket via serializeAttachment so it
 * survives Durable Object hibernation. Keep it small — it is persisted by the
 * runtime for every accepted socket.
 */
interface Attachment {
  callsign: string;
  grid?: string;
  freq?: number;
  mode?: string;
  status?: PresenceStatus;
  /** Whether this operator's freq may ever be shared (eye toggle). Default true. */
  freqPublic?: boolean;
  since: number;
  // Per-connection message rate-limit window (fixed window).
  rlWindowStart?: number;
  rlCount?: number;
}

/** Persisted metadata for a non-public room (group or DM). */
interface RoomMeta {
  id: string;
  name: string;
  kind: RoomKind;
  createdBy?: string;
  ts: number;
}

/** Max messages a single connection may send per RL_WINDOW_MS. */
const MSG_RATE_LIMIT = 6;
const RL_WINDOW_MS = 5000;

function norm(callsign: string): string {
  return callsign.trim().toUpperCase();
}

/**
 * The single shared chat room DO (idFromName("lobby")) — it actually owns the
 * whole network: the public roster, the friend consent graph, admin-created
 * private rooms, DMs, bans, and persisted message history. Uses the WebSocket
 * Hibernation API so it can evict from memory while sockets stay open;
 * graph/room/ban state is persisted in DO storage and lazily rehydrated.
 *
 * Frequency is private: an operator's freq is only included in another
 * operator's roster when (a) they are mutual friends AND (b) the owner has not
 * hidden their frequency via the eye toggle (freqPublic=false).
 */
export class ChatRoom extends DurableObject<Env> {
  // Friend graph (callsign -> related callsigns).
  private friends = new Map<string, Set<string>>(); // accepted, mutual
  private incoming = new Map<string, Set<string>>(); // to -> froms awaiting decision
  private outgoing = new Map<string, Set<string>>(); // from -> tos still pending
  // Rooms (groups + DMs; the public lobby is implicit, never stored).
  private rooms = new Map<string, RoomMeta>(); // roomId -> meta
  private members = new Map<string, Set<string>>(); // roomId -> member callsigns
  private userRooms = new Map<string, Set<string>>(); // callsign -> roomIds
  private bans = new Set<string>();
  private admins: Set<string>;
  private loaded = false;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.admins = new Set(
      (env.ADMINS ?? 'N9WAR,KB2UKA')
        .split(',')
        .map((s) => s.trim().toUpperCase())
        .filter(Boolean),
    );
    // Auto-answer keepalives without waking the DO. Backends must send the
    // exact request string for this to match.
    this.ctx.setWebSocketAutoResponse(
      new WebSocketRequestResponsePair(
        JSON.stringify({ t: 'ping' }),
        JSON.stringify({ t: 'pong' }),
      ),
    );
  }

  /** Upgrade an incoming request to a hibernatable WebSocket. */
  override async fetch(request: Request): Promise<Response> {
    const pair = new WebSocketPair();
    const client = pair[0];
    const server = pair[1];

    this.ctx.acceptWebSocket(server);
    const verified = norm(request.headers.get('X-Operator-Callsign') ?? '');
    const initial: Attachment = { callsign: verified, since: Date.now() };
    server.serializeAttachment(initial);

    return new Response(null, { status: 101, webSocket: client });
  }

  override async webSocketMessage(ws: WebSocket, raw: string | ArrayBuffer): Promise<void> {
    let msg: ClientToRelay;
    try {
      const text = typeof raw === 'string' ? raw : new TextDecoder().decode(raw);
      msg = JSON.parse(text) as ClientToRelay;
    } catch {
      this.send(ws, { t: 'error', code: 'bad_json', message: 'invalid JSON' });
      return;
    }

    const att = (ws.deserializeAttachment() as Attachment | null) ?? {
      callsign: '',
      since: Date.now(),
    };

    await this.ensureLoaded();
    const me = att.callsign;

    switch (msg.t) {
      case 'hello': {
        const callsign = me || norm(msg.callsign ?? '');
        if (!callsign) {
          this.send(ws, { t: 'error', code: 'no_callsign', message: 'callsign required' });
          return;
        }
        if (this.bans.has(callsign)) {
          this.send(ws, { t: 'banned', message: 'You have been banned from ZeusChat.' });
          try { ws.close(4403, 'banned'); } catch { /* already closing */ }
          return;
        }
        const next: Attachment = {
          callsign,
          grid: msg.grid,
          freq: msg.freq,
          mode: msg.mode,
          status: msg.status ?? 'rx',
          freqPublic: msg.freqPublic,
          since: att.since || Date.now(),
        };
        ws.serializeAttachment(next);
        this.send(ws, {
          t: 'welcome',
          self: toOperator(next),
          roster: this.rosterFor(callsign),
          isAdmin: this.admins.has(callsign),
        });
        this.broadcastRoster();
        this.notify(callsign); // friend graph
        this.send(ws, { t: 'rooms', rooms: this.roomsFor(callsign) });
        await this.sendHistory(ws, callsign, PUBLIC_ROOM); // instant public scrollback
        return;
      }

      case 'presence': {
        if (!me) return;
        const next: Attachment = {
          ...att,
          freq: msg.freq ?? att.freq,
          mode: msg.mode ?? att.mode,
          status: msg.status ?? att.status,
          freqPublic: msg.freqPublic ?? att.freqPublic,
        };
        ws.serializeAttachment(next);
        this.broadcastRoster();
        return;
      }

      case 'msg': {
        if (!me) {
          this.send(ws, { t: 'error', code: 'no_hello', message: 'send hello first' });
          return;
        }
        const room = msg.room ?? PUBLIC_ROOM;
        if (!this.canAccess(me, room)) {
          this.send(ws, { t: 'error', code: 'no_access', message: 'not a member of that room' });
          return;
        }
        const text = (msg.text ?? '').slice(0, MAX_MESSAGE_LEN);
        if (!text.trim()) return;
        if (!this.checkRate(ws, att)) return;
        await this.postMessage(room, me, text);
        return;
      }

      case 'dm': {
        if (!me) return;
        const to = norm(msg.to ?? '');
        if (!to || to === me || this.bans.has(to)) return;
        const text = (msg.text ?? '').slice(0, MAX_MESSAGE_LEN);
        if (!text.trim()) return;
        if (!this.checkRate(ws, att)) return;
        const room = await this.ensureDm(me, to);
        await this.postMessage(room, me, text);
        return;
      }

      case 'history': {
        if (!me) return;
        await this.sendHistory(ws, me, msg.room);
        return;
      }

      case 'friend_req':
        if (me) await this.friendRequest(me, norm(msg.to ?? ''));
        return;
      case 'friend_accept':
        if (me) await this.friendAccept(me, norm(msg.from ?? ''));
        return;
      case 'friend_deny':
        if (me) await this.friendDeny(me, norm(msg.from ?? ''));
        return;
      case 'friend_remove':
        if (me) await this.friendRemove(me, norm(msg.callsign ?? ''));
        return;

      case 'admin_create_room':
        if (this.isAdmin(me)) await this.createRoom(me, (msg.name ?? '').trim());
        return;
      case 'admin_delete_room':
        if (this.isAdmin(me)) await this.deleteRoom(me, msg.room ?? '');
        return;
      case 'admin_add_member':
        if (this.isAdmin(me)) await this.addMember(msg.room ?? '', norm(msg.callsign ?? ''));
        return;
      case 'admin_remove_member':
        if (this.isAdmin(me)) await this.removeMember(msg.room ?? '', norm(msg.callsign ?? ''));
        return;
      case 'admin_ban':
        if (this.isAdmin(me)) await this.banUser(me, norm(msg.callsign ?? ''));
        return;
      case 'admin_unban':
        if (this.isAdmin(me)) await this.unbanUser(norm(msg.callsign ?? ''));
        return;

      case 'ping':
        this.send(ws, { t: 'pong' });
        return;
    }
  }

  override async webSocketClose(ws: WebSocket, code: number, reason: string): Promise<void> {
    try { ws.close(code, reason); } catch { /* already closing */ }
    // A close can wake a hibernated DO with an empty in-memory graph; rehydrate
    // before broadcasting or rosterFor() would strip every friend's freq.
    await this.ensureLoaded();
    this.broadcastRoster();
  }

  override async webSocketError(): Promise<void> {
    // Same hibernation guard as webSocketClose — never broadcast an unloaded graph.
    await this.ensureLoaded();
    this.broadcastRoster();
  }

  /** Scheduled retention sweep: public room hourly, private rooms/DMs daily. */
  override async alarm(): Promise<void> {
    const now = Date.now();
    const all = await this.ctx.storage.list<Msg>({ prefix: 'm:' });
    const dead: string[] = [];
    for (const [key, m] of all) {
      const cutoff = m.room === PUBLIC_ROOM ? PUBLIC_RETENTION_MS : PRIVATE_RETENTION_MS;
      if (now - m.ts > cutoff) dead.push(key);
    }
    if (dead.length) await this.ctx.storage.delete(dead);
    await this.ctx.storage.setAlarm(now + PRUNE_INTERVAL_MS);
  }

  // --- persistence load ------------------------------------------------------

  private async ensureLoaded(): Promise<void> {
    if (this.loaded) return;
    const fr = await this.ctx.storage.list<number>({ prefix: 'fr:' });
    for (const key of fr.keys()) {
      const [, a, b] = key.split(':');
      if (a && b) addTo(this.friends, a, b);
    }
    const req = await this.ctx.storage.list<number>({ prefix: 'req:' });
    for (const key of req.keys()) {
      const [, from, to] = key.split(':');
      if (from && to) {
        addTo(this.outgoing, from, to);
        addTo(this.incoming, to, from);
      }
    }
    const rooms = await this.ctx.storage.list<RoomMeta>({ prefix: 'room:' });
    for (const meta of rooms.values()) this.rooms.set(meta.id, meta);
    const um = await this.ctx.storage.list<number>({ prefix: 'um:' });
    for (const key of um.keys()) {
      // um:{CALL}:{ROOMID} — ROOMID may itself contain ':' (dm:LO:HI).
      const rest = key.slice(3);
      const sep = rest.indexOf(':');
      if (sep < 0) continue;
      const call = rest.slice(0, sep);
      const roomId = rest.slice(sep + 1);
      addTo(this.userRooms, call, roomId);
      addTo(this.members, roomId, call);
    }
    const bans = await this.ctx.storage.list<number>({ prefix: 'ban:' });
    for (const key of bans.keys()) this.bans.add(key.slice(4));

    if ((await this.ctx.storage.getAlarm()) === null) {
      await this.ctx.storage.setAlarm(Date.now() + PRUNE_INTERVAL_MS);
    }
    this.loaded = true;
  }

  // --- messages --------------------------------------------------------------

  private async postMessage(room: string, from: string, text: string): Promise<void> {
    const msg: Msg = { id: crypto.randomUUID(), from, text, ts: Date.now(), room };
    const key = `m:${room}:${String(msg.ts).padStart(15, '0')}:${crypto.randomUUID().slice(0, 8)}`;
    await this.ctx.storage.put(key, msg);
    this.deliverToRoom(room, { t: 'msg', ...msg });
  }

  private deliverToRoom(room: string, frame: RelayToClient): void {
    if (room === PUBLIC_ROOM) {
      this.broadcast(frame);
      return;
    }
    const members = this.members.get(room);
    if (!members) return;
    for (const ws of this.ctx.getWebSockets()) {
      const att = ws.deserializeAttachment() as Attachment | null;
      if (att?.callsign && members.has(att.callsign)) this.send(ws, frame);
    }
  }

  private async sendHistory(ws: WebSocket, viewer: string, room: string): Promise<void> {
    if (!this.canAccess(viewer, room)) return;
    const all = await this.ctx.storage.list<Msg>({ prefix: `m:${room}:` });
    const messages = [...all.values()].slice(-HISTORY_LIMIT);
    this.send(ws, { t: 'history', room, messages });
  }

  // --- rooms / DMs -----------------------------------------------------------

  private canAccess(callsign: string, room: string): boolean {
    if (room === PUBLIC_ROOM) return true;
    return this.members.get(room)?.has(callsign) ?? false;
  }

  private roomsFor(callsign: string): RoomInfo[] {
    const out: RoomInfo[] = [{ id: PUBLIC_ROOM, name: 'Public', kind: 'public', members: [] }];
    for (const roomId of this.userRooms.get(callsign) ?? []) {
      const meta = this.rooms.get(roomId);
      if (!meta) continue;
      out.push({
        id: meta.id,
        name: meta.name,
        kind: meta.kind,
        members: [...(this.members.get(roomId) ?? [])].sort(),
      });
    }
    return out;
  }

  private pushRooms(callsign: string): void {
    const rooms = this.roomsFor(callsign);
    for (const ws of this.ctx.getWebSockets()) {
      const att = ws.deserializeAttachment() as Attachment | null;
      if (att?.callsign === callsign) this.send(ws, { t: 'rooms', rooms });
    }
  }

  private async ensureDm(a: string, b: string): Promise<string> {
    const id = dmRoomId(a, b);
    if (this.rooms.has(id)) return id;
    const meta: RoomMeta = { id, name: '', kind: 'dm', ts: Date.now() };
    this.rooms.set(id, meta);
    await this.ctx.storage.put(`room:${id}`, meta);
    const lo = norm(a);
    const hi = norm(b);
    for (const call of [lo, hi]) {
      addTo(this.members, id, call);
      addTo(this.userRooms, call, id);
      await this.ctx.storage.put(`rm:${id}:${call}`, Date.now());
      await this.ctx.storage.put(`um:${call}:${id}`, Date.now());
    }
    this.pushRooms(lo);
    this.pushRooms(hi);
    return id;
  }

  private async createRoom(admin: string, name: string): Promise<void> {
    if (!name) return;
    const id = `g${crypto.randomUUID().replace(/-/g, '').slice(0, 10)}`;
    const meta: RoomMeta = { id, name, kind: 'group', createdBy: admin, ts: Date.now() };
    this.rooms.set(id, meta);
    await this.ctx.storage.put(`room:${id}`, meta);
    await this.addMember(id, admin); // creator joins their own room
  }

  private async addMember(room: string, callsign: string): Promise<void> {
    const meta = this.rooms.get(room);
    if (!meta || meta.kind !== 'group' || !callsign) return;
    if (this.members.get(room)?.has(callsign)) return;
    addTo(this.members, room, callsign);
    addTo(this.userRooms, callsign, room);
    await this.ctx.storage.put(`rm:${room}:${callsign}`, Date.now());
    await this.ctx.storage.put(`um:${callsign}:${room}`, Date.now());
    this.pushRooms(callsign);
  }

  private async removeMember(room: string, callsign: string): Promise<void> {
    const meta = this.rooms.get(room);
    if (!meta || meta.kind !== 'group' || !callsign) return;
    if (!this.members.get(room)?.has(callsign)) return;
    removeFrom(this.members, room, callsign);
    removeFrom(this.userRooms, callsign, room);
    await this.ctx.storage.delete(`rm:${room}:${callsign}`);
    await this.ctx.storage.delete(`um:${callsign}:${room}`);
    this.pushRooms(callsign); // tab disappears for them
  }

  private async deleteRoom(admin: string, room: string): Promise<void> {
    const meta = this.rooms.get(room);
    if (!meta || meta.kind !== 'group') return;
    const exMembers = [...(this.members.get(room) ?? [])];
    for (const call of exMembers) {
      removeFrom(this.userRooms, call, room);
      await this.ctx.storage.delete(`rm:${room}:${call}`);
      await this.ctx.storage.delete(`um:${call}:${room}`);
    }
    this.members.delete(room);
    this.rooms.delete(room);
    await this.ctx.storage.delete(`room:${room}`);
    const msgKeys = [...(await this.ctx.storage.list({ prefix: `m:${room}:` })).keys()];
    if (msgKeys.length) await this.ctx.storage.delete(msgKeys);
    for (const call of exMembers) this.pushRooms(call);
  }

  // --- bans ------------------------------------------------------------------

  private isAdmin(callsign: string): boolean {
    return !!callsign && this.admins.has(callsign);
  }

  private async banUser(admin: string, callsign: string): Promise<void> {
    if (!callsign || this.admins.has(callsign)) return; // never ban an admin
    this.bans.add(callsign);
    await this.ctx.storage.put(`ban:${callsign}`, Date.now());
    // Kick any live sockets for the banned operator.
    for (const ws of this.ctx.getWebSockets()) {
      const att = ws.deserializeAttachment() as Attachment | null;
      if (att?.callsign === callsign) {
        this.send(ws, { t: 'banned', message: `You were banned by ${admin}.` });
        try { ws.close(4403, 'banned'); } catch { /* already closing */ }
      }
    }
    this.broadcastRoster();
  }

  private async unbanUser(callsign: string): Promise<void> {
    if (!callsign || !this.bans.has(callsign)) return;
    this.bans.delete(callsign);
    await this.ctx.storage.delete(`ban:${callsign}`);
  }

  // --- friendship graph ------------------------------------------------------

  private async friendRequest(from: string, to: string): Promise<void> {
    if (!to || to === from) return;
    if (this.friends.get(from)?.has(to)) {
      this.notify(from);
      return;
    }
    if (this.incoming.get(from)?.has(to)) {
      await this.friendAccept(from, to); // mutual → auto-accept
      return;
    }
    if (this.outgoing.get(from)?.has(to)) {
      this.notify(from);
      return;
    }
    await this.ctx.storage.put(`req:${from}:${to}`, Date.now());
    addTo(this.outgoing, from, to);
    addTo(this.incoming, to, from);
    this.notify(from);
    this.notify(to);
  }

  private async friendAccept(by: string, from: string): Promise<void> {
    if (!from) return;
    if (!this.incoming.get(by)?.has(from)) {
      if (this.friends.get(by)?.has(from)) this.notify(by);
      return;
    }
    await this.ctx.storage.delete(`req:${from}:${by}`);
    removeFrom(this.incoming, by, from);
    removeFrom(this.outgoing, from, by);
    await this.ctx.storage.put(`fr:${by}:${from}`, Date.now());
    await this.ctx.storage.put(`fr:${from}:${by}`, Date.now());
    addTo(this.friends, by, from);
    addTo(this.friends, from, by);
    this.notify(by);
    this.notify(from);
    this.broadcastRoster();
  }

  private async friendDeny(by: string, from: string): Promise<void> {
    if (!from || !this.incoming.get(by)?.has(from)) return;
    await this.ctx.storage.delete(`req:${from}:${by}`);
    removeFrom(this.incoming, by, from);
    removeFrom(this.outgoing, from, by);
    this.notify(by);
    this.notify(from);
  }

  private async friendRemove(by: string, other: string): Promise<void> {
    if (!other) return;
    let changed = false;
    let unfriended = false;
    if (this.friends.get(by)?.has(other)) {
      await this.ctx.storage.delete(`fr:${by}:${other}`);
      await this.ctx.storage.delete(`fr:${other}:${by}`);
      removeFrom(this.friends, by, other);
      removeFrom(this.friends, other, by);
      changed = true;
      unfriended = true;
    }
    if (this.outgoing.get(by)?.has(other)) {
      await this.ctx.storage.delete(`req:${by}:${other}`);
      removeFrom(this.outgoing, by, other);
      removeFrom(this.incoming, other, by);
      changed = true;
    }
    if (this.incoming.get(by)?.has(other)) {
      await this.ctx.storage.delete(`req:${other}:${by}`);
      removeFrom(this.incoming, by, other);
      removeFrom(this.outgoing, other, by);
      changed = true;
    }
    if (!changed) return;
    this.notify(by);
    this.notify(other);
    if (unfriended) this.broadcastRoster();
  }

  private friendsSnapshot(callsign: string): RelayToClient {
    return {
      t: 'friends',
      accepted: [...(this.friends.get(callsign) ?? [])].sort(),
      incoming: [...(this.incoming.get(callsign) ?? [])].sort(),
      outgoing: [...(this.outgoing.get(callsign) ?? [])].sort(),
    };
  }

  private notify(callsign: string): void {
    if (!callsign) return;
    const snap = this.friendsSnapshot(callsign);
    for (const ws of this.ctx.getWebSockets()) {
      const att = ws.deserializeAttachment() as Attachment | null;
      if (att?.callsign === callsign) this.send(ws, snap);
    }
  }

  // --- helpers ---------------------------------------------------------------

  private checkRate(ws: WebSocket, att: Attachment): boolean {
    const now = Date.now();
    const fresh = now - (att.rlWindowStart ?? 0) > RL_WINDOW_MS;
    const count = fresh ? 0 : att.rlCount ?? 0;
    if (count >= MSG_RATE_LIMIT) {
      this.send(ws, { t: 'error', code: 'rate_limited', message: 'Slow down — too many messages' });
      return false;
    }
    ws.serializeAttachment({ ...att, rlWindowStart: fresh ? now : att.rlWindowStart, rlCount: count + 1 });
    return true;
  }

  private send(ws: WebSocket, msg: RelayToClient): void {
    try { ws.send(JSON.stringify(msg)); } catch { /* socket gone */ }
  }

  private broadcast(msg: RelayToClient): void {
    const payload = JSON.stringify(msg);
    for (const ws of this.ctx.getWebSockets()) {
      try { ws.send(payload); } catch { /* skip dead sockets */ }
    }
  }

  /** Public roster as seen by `viewer`: freq only for friends whose eye is open. */
  private rosterFor(viewer: string): Operator[] {
    const fset = this.friends.get(viewer);
    const seen = new Set<string>();
    const out: Operator[] = [];
    for (const ws of this.ctx.getWebSockets()) {
      const att = ws.deserializeAttachment() as Attachment | null;
      if (!att || !att.callsign) continue;
      if (seen.has(att.callsign)) continue;
      seen.add(att.callsign);
      const canSeeFreq =
        att.callsign === viewer || (att.freqPublic !== false && (fset?.has(att.callsign) ?? false));
      out.push(toOperator(att, canSeeFreq));
    }
    out.sort((a, b) => a.callsign.localeCompare(b.callsign));
    return out;
  }

  private broadcastRoster(): void {
    for (const ws of this.ctx.getWebSockets()) {
      const att = ws.deserializeAttachment() as Attachment | null;
      if (!att) continue;
      this.send(ws, { t: 'roster', roster: this.rosterFor(att.callsign) });
    }
  }
}

function addTo(map: Map<string, Set<string>>, key: string, value: string): void {
  let set = map.get(key);
  if (!set) {
    set = new Set();
    map.set(key, set);
  }
  set.add(value);
}

function removeFrom(map: Map<string, Set<string>>, key: string, value: string): void {
  const set = map.get(key);
  if (!set) return;
  set.delete(value);
  if (set.size === 0) map.delete(key);
}

function toOperator(a: Attachment, includeFreq = true): Operator {
  return {
    callsign: a.callsign,
    grid: a.grid,
    freq: includeFreq ? a.freq : undefined,
    mode: a.mode,
    status: a.status,
    since: a.since,
  };
}
