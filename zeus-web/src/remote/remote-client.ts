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
// Scope: full native control + voice TX. RX display + audio + meters stream over
// the frames channel; the read-write `/api/*` tunnel (api-tunnel.ts) carries the
// SPA's control REST to the radio's loopback Kestrel — VFO/mode/band/filter/AGC/
// drive/MOX/TUN, exactly as the desktop app does; and a MOX-gated sendonly Opus
// audio track carries the operator's mic for voice TX (see connect.ts /
// RemoteMicAudioPipeline on the radio). The server gates the burn-zone
// (PureSignal) + secrets and dead-man un-keys a dropped session. Deny-by-default
// holds: nothing flows until connectViaBroker's SPAKE2+ password handshake unlocks.

import { connectViaBroker, type RemoteConnection } from './connect';
import { installApiTunnel, setApiChannel } from './api-tunnel';
import { useTxStore } from '../state/tx-store';
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

// Hidden <audio> sink for the radio's RX audio when it arrives on the WebRTC
// media track (Opus-RX host path). The browser owns decode + jitter buffer +
// PLC; we just attach the stream and let it play. One element, reused across
// reconnects.
let rxAudioEl: HTMLAudioElement | null = null;

function playRemoteRxAudioTrack(stream: MediaStream): void {
  if (typeof document === 'undefined') return;
  if (!rxAudioEl) {
    rxAudioEl = document.createElement('audio');
    rxAudioEl.autoplay = true;
    // Not added to the DOM tree — an offscreen element still plays audio.
  }
  rxAudioEl.srcObject = stream;
  void rxAudioEl.play().catch((e) => {
    console.warn('[remote] RX audio track autoplay blocked:', e);
  });
}

function stopRemoteRxAudioTrack(): void {
  if (!rxAudioEl) return;
  rxAudioEl.pause();
  rxAudioEl.srcObject = null;
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

  // Hand the read-write API tunnel its live "api" channel so queued + future
  // same-origin `/api/*` requests (reads AND control writes) flow to the radio's
  // loopback Kestrel. The session is unlocked by the time connectViaBroker
  // resolves (deny-by-default holds).
  setApiChannel(conn.api);

  // State-of-the-art RX audio: when the radio host has the Opus-RX path enabled it
  // streams RX audio back on the WebRTC audio track instead of as PCM over the
  // data channel. Play that track directly so we inherit the browser's native
  // adaptive jitter buffer + packet-loss concealment (lowest latency, robust to
  // internet loss). If the host doesn't enable it, no inbound track arrives and
  // RX audio keeps flowing through the existing PCM/WebAudio path — nothing here
  // fires. The unlock click is a user gesture, so autoplay is permitted.
  conn.pc.addEventListener('track', (ev) => {
    if (ev.track.kind !== 'audio') return;
    playRemoteRxAudioTrack(ev.streams[0] ?? new MediaStream([ev.track]));
  });

  // Voice-mic uplink: stream the operator's mic to the radio only while keyed
  // (MOX). The first key lazily prompts for mic permission; thereafter it's an
  // instant enable/disable. CW/RTTY/digital don't key MOX so the mic stays off.
  // A denied/absent mic leaves TX keying fully working, just without voice audio.
  const unsubMic = useTxStore.subscribe((s, prev) => {
    if (s.moxOn === prev.moxOn) return;
    void conn.setMicEnabled(s.moxOn).catch((e) => {
      console.warn('[remote] voice mic uplink unavailable:', e);
    });
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
      // Clear the tunnel channel and fail pending API requests so the UI gets a
      // network-style rejection rather than hanging on a dead session.
      setApiChannel(null);
      // Stop driving the mic from MOX and release the capture (conn.close also
      // stops the tracks; this unhooks the store subscription).
      unsubMic();
      // Detach the RX audio track sink so a dead stream doesn't linger.
      stopRemoteRxAudioTrack();
    }
  });

  // Ask the radio to start the RX display + audio streams. The server bumps its
  // global display/audio gates on these (RemoteWebRtcSession → hub), which is
  // what actually opens the panadapter frame fan-out for this session.
  sendDisplayStreamRequest(true);
  sendAudioStreamRequest(true);

  return conn;
}
