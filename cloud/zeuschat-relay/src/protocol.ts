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

/** Operator presence/activity state. `away` means idle in Zeus clients. */
export type PresenceStatus = 'rx' | 'tx' | 'away';

/** The kind of a channel. "public" is the single all-operators lobby. */
export type RoomKind = 'public' | 'group' | 'dm';

/** The well-known id of the public (all-operators) room. */
export const PUBLIC_ROOM = 'lobby';

/** A connected operator as seen by everyone in the room. */
export interface Operator {
  /** Uppercased callsign (QRZ-verified on the backend that asserted it). */
  callsign: string;
  /** Maidenhead grid square, if known. */
  grid?: string;
  /** Current VFO frequency in Hz. Omitted unless the viewer may see it. */
  freq?: number;
  /** Current mode (e.g. "USB", "CW"). */
  mode?: string;
  /** Activity state. */
  status?: PresenceStatus;
  /** Epoch ms the operator connected. */
  since: number;
  /** True when this operator is a relay moderator. Lets clients paint admin
   *  callsigns distinctly (gold). Omitted (falsy) for ordinary operators. */
  admin?: boolean;
}

/** A chat channel the operator can see: the public lobby, a group, or a DM. */
export interface RoomInfo {
  /** Stable id: "lobby", a "g…" group id, or "dm:LO:HI". */
  id: string;
  /** Display name (group name; for DMs the relay sends the member list). */
  name: string;
  kind: RoomKind;
  /** Member callsigns (empty for the public room). For a DM, the two parties. */
  members: string[];
}

/**
 * An inline media attachment carried with a message. Photos are sent "like a
 * text message": the bytes ride inside the message as a base64 data URL rather
 * than via out-of-band blob storage. The Zeus web client downscales/compresses
 * photos and records voice at a low Opus bitrate so `dataUrl` stays under
 * MAX_ATTACHMENT_DATAURL_LEN — the whole message is stored in a single
 * Durable-Object value (128 KiB cap), so it must fit with headroom. `kind` is
 * "image" or "audio"; unknown kinds are ignored by clients.
 */
export interface Attachment {
  kind: string;
  /** MIME type, e.g. "image/jpeg" or "audio/webm". Must match the kind family. */
  mime: string;
  /** Base64 data URL, e.g. "data:image/jpeg;base64,…" or "data:audio/webm;base64,…". */
  dataUrl: string;
  /** Original filename, if known. */
  name?: string;
  /** Pixel dimensions, if known. */
  width?: number;
  height?: number;
  /** Decoded byte size, if known. */
  size?: number;
}

/** A stored/relayed chat message. */
export interface Msg {
  id: string;
  from: string;
  text: string;
  /** Epoch ms. */
  ts: number;
  /** Room id this message belongs to. */
  room: string;
  /** Optional inline photo (absent for the common text-only case). */
  attachment?: Attachment;
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
      /** Whether this operator's frequency may be shared at all (eye toggle).
       *  false hides it from everyone, friends included. Default true. */
      freqPublic?: boolean;
      /** Client identifier, e.g. "zeus/0.9.0". Informational. */
      client?: string;
    }
  // Live presence update (frequency / mode / status / freq-visibility changed).
  | { t: 'presence'; freq?: number; mode?: string; status?: PresenceStatus; freqPublic?: boolean }
  // Outgoing chat message to a room (defaults to the public lobby). `text` may
  // be empty when an `attachment` (inline photo) is present.
  | { t: 'msg'; text: string; room?: string; attachment?: Attachment }
  // Send a direct message to another operator (creates the DM room on demand).
  | { t: 'dm'; to: string; text: string; attachment?: Attachment }
  // Request recent history for a room the operator can see.
  | { t: 'history'; room: string }
  // --- friends (consent graph) ---------------------------------------------
  | { t: 'friend_req'; to: string }
  | { t: 'friend_accept'; from: string }
  | { t: 'friend_deny'; from: string }
  | { t: 'friend_remove'; callsign: string }
  // --- admin / moderation (ignored unless the sender is an admin) -----------
  | { t: 'admin_create_room'; name: string }
  | { t: 'admin_delete_room'; room: string }
  | { t: 'admin_add_member'; room: string; callsign: string }
  | { t: 'admin_remove_member'; room: string; callsign: string }
  | { t: 'admin_ban'; callsign: string }
  | { t: 'admin_unban'; callsign: string }
  // Wipe a room's stored history (defaults to the public lobby).
  | { t: 'admin_clear_room'; room?: string }
  // Push a one-off global announcement to every connected operator.
  | { t: 'admin_broadcast'; text: string }
  // Ask the relay for the current ban list (relay replies with a `bans` frame).
  | { t: 'admin_list_bans' }
  // Toggle the admin "see all frequencies" override for THIS connection: while
  // on, the operator's roster carries every connected operator's frequency,
  // regardless of friendship or the owner's eye toggle. Ignored unless admin.
  | { t: 'admin_see_all'; on: boolean }
  // Keepalive. The relay auto-responds with {t:"pong"} without waking (see
  // setWebSocketAutoResponse in chat-room.ts) — send this EXACT string:
  // '{"t":"ping"}'.
  | { t: 'ping' };

/** Messages sent by the relay TO a backend chat node. */
export type RelayToClient =
  // Acknowledges hello: the caller's resolved operator, the public roster, and
  // whether the caller is a moderator.
  | { t: 'welcome'; self: Operator; roster: Operator[]; isAdmin: boolean }
  // Public-room roster changed (someone joined/left or updated presence).
  | { t: 'roster'; roster: Operator[] }
  // The set of rooms this operator can see (public + their groups + their DMs).
  | { t: 'rooms'; rooms: RoomInfo[] }
  // Recent history for a room, in response to a history request (or pushed on
  // join). Ordered oldest→newest.
  | { t: 'history'; room: string; messages: Msg[] }
  // A chat message in a room the operator is a member of (including own echo).
  | { t: 'msg'; id: string; from: string; text: string; ts: number; room: string; attachment?: Attachment }
  // A room's history was wiped by an admin — clients should drop their scrollback.
  | { t: 'cleared'; room: string }
  // A one-off global announcement from an admin, shown to everyone regardless of
  // which room they're viewing.
  | { t: 'notice'; from: string; text: string; ts: number }
  // The operator's friend graph (see ChatFriendsDto on the C# side).
  | { t: 'friends'; accepted: string[]; incoming: string[]; outgoing: string[] }
  // The operator has been banned/kicked; the socket is about to close.
  | { t: 'banned'; message: string }
  // The current ban list, sent to admins on hello, on every ban/unban, and in
  // response to `admin_list_bans`. Non-admins never receive this.
  | { t: 'bans'; bans: string[] }
  // Protocol/validation error. Non-fatal unless the socket is also closed.
  | { t: 'error'; code: string; message: string }
  // Keepalive response.
  | { t: 'pong' };

/** Max accepted chat message length (characters); longer is truncated. */
export const MAX_MESSAGE_LEN = 2000;

/**
 * Max accepted length of an attachment's base64 data URL (characters). Sized to
 * leave headroom under the Durable-Object 128 KiB per-value storage cap once the
 * surrounding message JSON is added. An attachment over this is dropped (the
 * message still delivers as text). Mirrors ChatAttachment.MaxDataUrlLength on
 * the C# side; keep the two in sync.
 */
export const MAX_ATTACHMENT_DATAURL_LEN = 120_000;

/** The single room used in P0. Kept as an alias of PUBLIC_ROOM. */
export const DEFAULT_ROOM = PUBLIC_ROOM;

/** Retention windows (ms): the public room prunes hourly, private rooms/DMs daily. */
export const PUBLIC_RETENTION_MS = 60 * 60 * 1000;
export const PRIVATE_RETENTION_MS = 24 * 60 * 60 * 1000;

/** How often the prune alarm fires. */
export const PRUNE_INTERVAL_MS = 60 * 60 * 1000;

/** Max messages returned in a history reply. */
export const HISTORY_LIMIT = 100;

/** Build the canonical DM room id for two callsigns (order-independent). */
export function dmRoomId(a: string, b: string): string {
  const [lo, hi] = [a.toUpperCase(), b.toUpperCase()].sort();
  return `dm:${lo}:${hi}`;
}
