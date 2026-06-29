// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Single global tracker that keeps the TX Audio Profile "dirty" flag honest.
// Mounted once (App.tsx). It (1) establishes a clean baseline when the radio
// connects, (2) recomputes dirty whenever a watched audio setting changes, and
// (3) installs a best-effort browser close guard. The rich "TX Audio Profile
// '{name}' changed — save?" prompt itself is driven on the in-app Disconnect
// path (ConnectPanel), where an async save can still round-trip to the server.

import { useEffect } from 'react';

import { useAudioSuiteStore } from './audio-suite-store';
import { useConnectionStore } from './connection-store';
import { useTxAudioProfileStore } from './tx-audio-profile-store';
import { useTxStore } from './tx-store';

export function useTxAudioProfileDirtyTracker(): void {
  const status = useConnectionStore((s) => s.status);

  // The backend applies the last-loaded profile BEFORE connect, so at the moment
  // we reach Connected the live state equals that profile — capture it as the
  // clean baseline. On disconnect, drop the baseline so a stale dirty flag can
  // never survive into the next session.
  useEffect(() => {
    const store = useTxAudioProfileStore.getState();
    if (status === 'Connected') {
      if (store.baseline == null) store.markClean();
    } else if (status === 'Disconnected') {
      useTxAudioProfileStore.setState({ baseline: null, dirty: false });
    }
  }, [status]);

  // Recompute dirty on any change to a watched store. recomputeDirty is a cheap
  // string compare and only writes state on an actual clean<->dirty transition,
  // so subscribing to the full stores (incl. high-rate meter frames) is safe.
  useEffect(() => {
    const recompute = () => useTxAudioProfileStore.getState().recomputeDirty();
    const unsubscribers = [
      useTxStore.subscribe(recompute),
      useConnectionStore.subscribe(recompute),
      useAudioSuiteStore.subscribe(recompute),
    ];
    return () => unsubscribers.forEach((unsub) => unsub());
  }, []);

  // Closing the Zeus window with unsaved TX-audio changes: beforeunload cannot
  // run an async save or show a custom modal, so fall back to the browser's
  // native "leave?" confirmation. (A true custom save-prompt on OS-window-close
  // would require the desktop shell to intercept the close and round-trip.)
  useEffect(() => {
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      if (useTxAudioProfileStore.getState().dirty) {
        event.preventDefault();
        event.returnValue = '';
      }
    };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, []);
}
