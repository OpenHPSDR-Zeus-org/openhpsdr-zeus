// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// In-app password gate for remote (WebRTC) RX monitoring. Rendered only in
// remote mode (?remote=<CALLSIGN>). Prompts for the session password using the
// project's in-app dialog chrome (NEVER window.prompt — hard project rule),
// connects via the broker, and once the SPAKE2+ handshake unlocks it marks the
// display store connected and unmounts itself so the normal UI takes over.
// Connection errors (wrong password, radio offline, broker unreachable) surface
// inline with a retry.

import { useCallback, useEffect, useRef, useState } from 'react';
import { ConfirmDialog } from '../layout/ConfirmDialog';
import { useDisplayStore } from '../state/display-store';
import { getRemoteCallsign, startRemoteClient } from './remote-client';
import type { RemoteConnection } from './connect';

type Phase = 'prompt' | 'connecting' | 'connected';

export function RemoteGate() {
  const callsign = getRemoteCallsign() ?? '';
  const [password, setPassword] = useState('');
  const [phase, setPhase] = useState<Phase>('prompt');
  const [error, setError] = useState<string | null>(null);
  const inputRef = useRef<HTMLInputElement | null>(null);
  const connRef = useRef<RemoteConnection | null>(null);

  // Focus the password field once the dialog's own focus trap settles.
  useEffect(() => {
    if (phase !== 'prompt') return;
    const t = setTimeout(() => inputRef.current?.focus(), 0);
    return () => clearTimeout(t);
  }, [phase]);

  // Tear the WebRTC session down if the gate unmounts mid-flight.
  useEffect(() => () => connRef.current?.close(), []);

  const connect = useCallback(() => {
    const pw = password;
    if (!pw) return;
    setError(null);
    setPhase('connecting');
    startRemoteClient(callsign, pw)
      .then((conn) => {
        connRef.current = conn;
        // Flip the panadapter/UI to "connected" — display frames now arrive
        // over WebRTC and dispatchServerFrame feeds the same stores.
        useDisplayStore.getState().setConnected(true);
        setPhase('connected');
      })
      .catch((err: unknown) => {
        setError(err instanceof Error ? err.message : 'Connection failed.');
        setPhase('prompt');
      });
  }, [callsign, password]);

  // Once unlocked the gate has nothing to render — the live UI shows through.
  if (phase === 'connected') return null;

  const connecting = phase === 'connecting';

  return (
    <ConfirmDialog
      title={`Remote · ${callsign}`}
      intent="primary"
      confirmLabel={connecting ? 'Connecting…' : 'Connect'}
      cancelLabel="Cancel"
      onCancel={() => {
        // No local app to fall back to in remote mode; closing the tab is the
        // operator's exit. Keep the gate up so they can retry.
        setError(null);
      }}
      onConfirm={connect}
    >
      <p>Enter the session password to monitor {callsign}'s radio.</p>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          if (!connecting) connect();
        }}
      >
        <input
          ref={inputRef}
          type="password"
          className="mono"
          autoComplete="current-password"
          placeholder="Session password"
          value={password}
          disabled={connecting}
          onChange={(e) => setPassword(e.currentTarget.value)}
          style={{
            width: '100%',
            padding: '6px 8px',
            borderRadius: 'var(--r-sm)',
            border: '1px solid var(--line-strong)',
            background: '#0c0c10',
            color: '#d8d8dc',
            fontSize: 13,
            outline: 'none',
          }}
        />
      </form>
      {error && (
        <p role="alert" style={{ color: 'var(--tx)', marginTop: 8 }}>
          {error}
        </p>
      )}
    </ConfirmDialog>
  );
}
