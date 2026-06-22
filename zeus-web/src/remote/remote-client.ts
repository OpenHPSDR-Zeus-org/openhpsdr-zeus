// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Remote-access bootstrap. When the SPA is opened at `…/?remote=<CALLSIGN>` it
// connects to the operator's radio over WebRTC through the Cloudflare broker
// instead of the local websocket, then feeds the unlocked binary radio frames
// through the exact same dispatch path the local /ws client uses
// (dispatchServerFrame) — so panadapter / waterfall / meters / audio render
// identically, just sourced over WebRTC.
//
// Scope: full native control. RX display + audio + meters stream over the frames
// channel, and the read-write `/api/*` tunnel (api-tunnel.ts) carries the SPA's
// control REST to the radio's loopback Kestrel — VFO/mode/band/filter/AGC/drive/
// MOX/TUN, exactly as the desktop app does. The server gates the burn-zone
// (PureSignal) + secrets and dead-man un-keys a dropped session. Voice-mic uplink
// is the next phase (mic PCM stays WS-only for now). Deny-by-default holds:
// nothing flows until connectViaBroker's SPAKE2+ password handshake unlocks.

import { connectViaBroker, type RemoteConnection } from './connect';
import { installApiTunnel, setApiChannel } from './api-tunnel';
import {
  dispatchServerFrame,
  sendAudioStreamRequest,
  sendDisplayStreamRequest,
  setRemoteControlSender,
} from '../realtime/ws-client';

/** Parse `?remote=<CALLSIGN>` from the current URL; '' / absent → not remote. */
export function getRemoteCallsign(): string | null {
  try {
    const cs = new URLSearchParams(window.location.search).get('remote');
    const trimmed = cs?.trim();
    return trimmed ? trimmed.toUpperCase() : null;
  } catch {
    return null;
  }
}

/** True when the SPA should run as a remote (WebRTC) monitor rather than a local client. */
export function isRemoteMode(): boolean {
  return getRemoteCallsign() !== null;
}

// Install the read-write /api/* fetch shim at module load — BEFORE the app's
// mount effects fire their `/api/state` etc. requests. In remote mode there is
// no same-origin backend, so those requests must tunnel; the shim queues them
// until the session unlocks and setApiChannel() flushes the queue. No-op outside
// remote mode (the local /ws client uses the real same-origin backend).
if (isRemoteMode()) {
  installApiTunnel();
}

/**
 * Connect to the operator's radio via the broker, unlock with the supplied
 * password, then route the unlocked frame stream into the stores and request
 * the RX display + audio streams over the control DataChannel.
 *
 * Resolves with the live connection once unlocked; rejects with a human-readable
 * Error (incorrect password, radio offline, broker unreachable) the gate UI can
 * surface for retry. No frame flows before this resolves.
 */
export async function startRemoteClient(
  callsign: string,
  password: string,
): Promise<RemoteConnection> {
  const conn = await connectViaBroker({
    callsign,
    password,
    onFrame: (data) => dispatchServerFrame(data),
  });

  // Hand the read-only API tunnel its live "api" channel so queued + future
  // same-origin `/api/*` GETs flow to the radio's loopback Kestrel. The session
  // is unlocked by the time connectViaBroker resolves (deny-by-default holds).
  setApiChannel(conn.api);

  // Route the 2-byte stream-request control frames (0x21/0x22) over the WebRTC
  // control channel instead of the (absent) local websocket. Drop the override
  // and tear the session down if the peer connection dies.
  setRemoteControlSender((bytes) => {
    try {
      conn.control.send(new Uint8Array(bytes));
    } catch {
      /* channel closed underneath us — onclose/onconnectionstatechange clean up */
    }
  });

  conn.pc.addEventListener('connectionstatechange', () => {
    const s = conn.pc.connectionState;
    if (s === 'closed' || s === 'failed' || s === 'disconnected') {
      setRemoteControlSender(null);
      // Clear the tunnel channel and fail pending API requests so the UI gets a
      // network-style rejection rather than hanging on a dead session.
      setApiChannel(null);
    }
  });

  // Ask the radio to start the RX display + audio streams. The server bumps its
  // global display/audio gates on these (RemoteWebRtcSession → hub), which is
  // what actually opens the panadapter frame fan-out for this session.
  sendDisplayStreamRequest(true);
  sendAudioStreamRequest(true);

  return conn;
}
