// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Remote-access RX-monitoring bootstrap (Phase A). When the SPA is opened at
// `…/?remote=<CALLSIGN>` it connects to the operator's radio over WebRTC through
// the Cloudflare broker instead of the local websocket, then feeds the unlocked
// binary radio frames through the exact same dispatch path the local /ws client
// uses (dispatchServerFrame) — so panadapter / waterfall / meters / audio render
// identically, just sourced over WebRTC.
//
// Scope is RX MONITORING ONLY: display + audio + meters. There is no TX, mic
// uplink, or tuning/control RPC in this phase. Deny-by-default holds: nothing
// flows until connectViaBroker's SPAKE2+ password handshake unlocks.

import { connectViaBroker, type RemoteConnection } from './connect';
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
    }
  });

  // Ask the radio to start the RX display + audio streams. The server bumps its
  // global display/audio gates on these (RemoteWebRtcSession → hub), which is
  // what actually opens the panadapter frame fan-out for this session.
  sendDisplayStreamRequest(true);
  sendAudioStreamRequest(true);

  return conn;
}
