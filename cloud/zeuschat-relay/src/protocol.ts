// ZeusChat relay wire protocol.
//
// This is the authoritative definition of the JSON messages exchanged over the
// WebSocket between a Zeus backend (the "chat node") and the relay Durable
// Object. The C# side (Zeus.Contracts / ChatService) mirrors these shapes.
//
// Transport: text WebSocket frames, one JSON object per frame, discriminated by
// the `t` (type) field.
//
// Connection auth happens at the HTTP upgrade (before any frame), via headers:
//   Authorization: Bearer <RELAY_SHARED_SECRET>   (if the relay sets a secret)
//   X-QRZ-Session:  <live QRZ session key>         (when QRZ_VERIFY != "off")
//   X-QRZ-Callsign: <operator's own callsign>      (validated against QRZ)
// The relay validates the QRZ session, then locks the verified callsign for the
// connection. `hello` thereafter carries presence; its `callsign` is only used
// as a fallback in local dev (QRZ_VERIFY=off), where header auth is unavailable.

/** Operator presence/activity state. */
export type PresenceStatus = 'rx' | 'tx' | 'away';

/** A connected operator as seen by everyone in the room. */
export interface Operator {
  /** Uppercased callsign (QRZ-verified on the backend that asserted it). */
  callsign: string;
  /** Maidenhead grid square, if known. */
  grid?: string;
  /** Current VFO frequency in Hz. */
  freq?: number;
  /** Current mode (e.g. "USB", "CW"). */
  mode?: string;
  /** Activity state. */
  status?: PresenceStatus;
  /** Epoch ms the operator connected. */
  since: number;
}

/** Messages sent by a backend chat node TO the relay. */
export type ClientToRelay =
  // First frame after connect: declares identity + initial presence.
  | {
      t: 'hello';
      callsign: string;
      grid?: string;
      freq?: number;
      mode?: string;
      status?: PresenceStatus;
      /** Client identifier, e.g. "zeus/0.9.0". Informational. */
      client?: string;
    }
  // Live presence update (frequency / mode / status changed).
  | { t: 'presence'; freq?: number; mode?: string; status?: PresenceStatus }
  // Outgoing chat message.
  | { t: 'msg'; text: string; room?: string }
  // Keepalive. The relay auto-responds with {t:"pong"} without waking (see
  // setWebSocketAutoResponse in chat-room.ts) — send this EXACT string:
  // '{"t":"ping"}'.
  | { t: 'ping' };

/** Messages sent by the relay TO a backend chat node. */
export type RelayToClient =
  // Acknowledges hello: echoes the caller's resolved operator + current roster.
  | { t: 'welcome'; self: Operator; roster: Operator[] }
  // Roster changed (someone joined/left or updated presence).
  | { t: 'roster'; roster: Operator[] }
  // A chat message (including the sender's own, for echo/ordering).
  | { t: 'msg'; id: string; from: string; text: string; ts: number; room: string }
  // Protocol/validation error. Non-fatal unless the socket is also closed.
  | { t: 'error'; code: string; message: string }
  // Keepalive response.
  | { t: 'pong' };

/** Max accepted chat message length (characters); longer is truncated. */
export const MAX_MESSAGE_LEN = 2000;

/** The single room used in P0. Band-derived rooms arrive in P3. */
export const DEFAULT_ROOM = 'lobby';
