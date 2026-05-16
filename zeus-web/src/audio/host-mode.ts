// SPDX-License-Identifier: GPL-2.0-or-later
//
// Audio host-mode flag. Single source of truth for "is the server playing
// RX audio natively?" in non-React code paths (audio-client, ws-client).
//
// React components subscribe directly to capabilities-store; non-React
// consumers (audio-client singleton, WebSocket message dispatcher) read
// the same store via getState() through the cheap helpers here. The local
// `mode` flag is kept only for the once-per-session log side-effect on the
// browser→native transition (`setAudioHostMode`) — read-side queries hit
// the canonical store directly so there's no race between the WS
// dispatcher coming up and App.tsx's subscribe callback firing.

import { useCapabilitiesStore } from '../state/capabilities-store';

export type AudioHostMode = 'browser' | 'native';

let logLatched = false;

/**
 * Set the active audio host mode (logging side-effect only — the read path
 * goes through the capabilities store directly). Idempotent; logs once when
 * the mode transitions to `'native'` so the operator sees a single
 * "native audio active (desktop mode)" line in the devtools console
 * confirming the opt-out fired.
 */
export function setAudioHostMode(next: AudioHostMode): void {
  if (next === 'native' && !logLatched) {
    logLatched = true;
    console.log('audio.host: native audio active (desktop mode)');
  }
}

export function getAudioHostMode(): AudioHostMode {
  return isNativeAudio() ? 'native' : 'browser';
}

/** True when the desktop host is rendering RX audio via its native sink. */
export function isNativeAudio(): boolean {
  return useCapabilitiesStore.getState().capabilities?.host === 'desktop';
}

// Test-only escape hatch. Clears the log latch so a `describe` block can
// drive transitions without polluting later tests.
export function __resetAudioHostModeForTests(): void {
  logLatched = false;
}
