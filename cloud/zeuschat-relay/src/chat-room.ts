import { DurableObject } from 'cloudflare:workers';
import type { Env } from './types';
import {
  type ClientToRelay,
  type RelayToClient,
  type Operator,
  type PresenceStatus,
  MAX_MESSAGE_LEN,
  DEFAULT_ROOM,
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
  since: number;
  // Per-connection message rate-limit window (fixed window).
  rlWindowStart?: number;
  rlCount?: number;
}

/** Max messages a single connection may send per RL_WINDOW_MS. */
const MSG_RATE_LIMIT = 6;
const RL_WINDOW_MS = 5000;

/**
 * A single chat room. Each connected Zeus backend holds one WebSocket here.
 * Uses the WebSocket Hibernation API so the DO can evict from memory while
 * sockets stay open — presence/roster is reconstructed from socket attachments.
 */
export class ChatRoom extends DurableObject<Env> {
  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
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
    // The Worker entry verifies the QRZ login and forwards the authoritative
    // callsign here. When present it is locked in (hello cannot change it);
    // when absent (local dev with QRZ_VERIFY=off) the callsign comes from hello.
    const verified = (request.headers.get('X-Operator-Callsign') ?? '').trim().toUpperCase();
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

    switch (msg.t) {
      case 'hello': {
        // Prefer the verified callsign locked in at connect; fall back to the
        // hello-provided one only when the relay didn't verify one (local dev).
        const callsign = att.callsign || (msg.callsign ?? '').trim().toUpperCase();
        if (!callsign) {
          this.send(ws, { t: 'error', code: 'no_callsign', message: 'callsign required' });
          return;
        }
        const next: Attachment = {
          callsign,
          grid: msg.grid,
          freq: msg.freq,
          mode: msg.mode,
          status: msg.status ?? 'rx',
          since: att.since || Date.now(),
        };
        ws.serializeAttachment(next);
        this.send(ws, { t: 'welcome', self: toOperator(next), roster: this.roster() });
        this.broadcastRoster();
        return;
      }

      case 'presence': {
        if (!att.callsign) return; // ignore until identified
        const next: Attachment = {
          ...att,
          freq: msg.freq ?? att.freq,
          mode: msg.mode ?? att.mode,
          status: msg.status ?? att.status,
        };
        ws.serializeAttachment(next);
        this.broadcastRoster();
        return;
      }

      case 'msg': {
        if (!att.callsign) {
          this.send(ws, { t: 'error', code: 'no_hello', message: 'send hello first' });
          return;
        }
        const text = (msg.text ?? '').slice(0, MAX_MESSAGE_LEN);
        if (!text.trim()) return;

        // Per-connection fixed-window message rate limit.
        const now = Date.now();
        const fresh = now - (att.rlWindowStart ?? 0) > RL_WINDOW_MS;
        const count = fresh ? 0 : att.rlCount ?? 0;
        if (count >= MSG_RATE_LIMIT) {
          this.send(ws, { t: 'error', code: 'rate_limited', message: 'Slow down — too many messages' });
          return;
        }
        ws.serializeAttachment({
          ...att,
          rlWindowStart: fresh ? now : att.rlWindowStart,
          rlCount: count + 1,
        });

        this.broadcast({
          t: 'msg',
          id: crypto.randomUUID(),
          from: att.callsign,
          text,
          ts: now,
          room: msg.room ?? DEFAULT_ROOM,
        });
        return;
      }

      case 'ping': {
        // Normally handled by auto-response; respond anyway if it reaches here.
        this.send(ws, { t: 'pong' });
        return;
      }
    }
  }

  override async webSocketClose(
    ws: WebSocket,
    code: number,
    reason: string,
    _wasClean: boolean,
  ): Promise<void> {
    try {
      ws.close(code, reason);
    } catch {
      // already closing
    }
    this.broadcastRoster();
  }

  override async webSocketError(_ws: WebSocket, _error: unknown): Promise<void> {
    this.broadcastRoster();
  }

  // --- helpers ---------------------------------------------------------------

  private send(ws: WebSocket, msg: RelayToClient): void {
    try {
      ws.send(JSON.stringify(msg));
    } catch {
      // socket gone
    }
  }

  private broadcast(msg: RelayToClient): void {
    const payload = JSON.stringify(msg);
    for (const ws of this.ctx.getWebSockets()) {
      try {
        ws.send(payload);
      } catch {
        // skip dead sockets
      }
    }
  }

  private roster(): Operator[] {
    const seen = new Set<string>();
    const out: Operator[] = [];
    for (const ws of this.ctx.getWebSockets()) {
      const att = ws.deserializeAttachment() as Attachment | null;
      if (!att || !att.callsign) continue;
      if (seen.has(att.callsign)) continue; // de-dup multi-tab/same call
      seen.add(att.callsign);
      out.push(toOperator(att));
    }
    out.sort((a, b) => a.callsign.localeCompare(b.callsign));
    return out;
  }

  private broadcastRoster(): void {
    this.broadcast({ t: 'roster', roster: this.roster() });
  }
}

function toOperator(a: Attachment): Operator {
  return {
    callsign: a.callsign,
    grid: a.grid,
    freq: a.freq,
    mode: a.mode,
    status: a.status,
    since: a.since,
  };
}
