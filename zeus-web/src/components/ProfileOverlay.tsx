// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// ProfileOverlay — the QRZ contact card modal opened by clicking a callsign
// (chat roster, message bubble, or the panadapter chat-roster overlay). Lifted
// out of ChatPanel so it can be mounted once at the app root and driven by
// profile-overlay-store, giving every surface the same card + lookup behaviour.

import { useEffect, useMemo, useState } from 'react';
import { useQrzStore } from '../state/qrz-store';
import { useProfileOverlayStore } from '../state/profile-overlay-store';
import { QrzCard } from './design/QrzCard';
import { qrzStationToContact } from './design/qrz-contact';
import type { Contact } from './design/data';
import type { QrzStation } from '../api/qrz';

export function ProfileOverlay({
  callsign,
  onClose,
}: {
  callsign: string;
  onClose: () => void;
}) {
  const lookupCached = useQrzStore((s) => s.lookupCached);
  const qrzConnected = useQrzStore((s) => s.connected);
  const qrzHome = useQrzStore((s) => s.home);
  const [station, setStation] = useState<QrzStation | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Esc closes — matches the chat lightbox / dialogs.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  useEffect(() => {
    let live = true;
    setLoading(true);
    setError(null);
    void lookupCached(callsign)
      .then((s) => {
        if (!live) return;
        if (s) setStation(s);
        else setError(qrzConnected ? 'No QRZ record' : 'Log into QRZ to view profiles');
      })
      .finally(() => {
        if (live) setLoading(false);
      });
    return () => {
      live = false;
    };
  }, [callsign, lookupCached, qrzConnected]);

  const contact: Contact | null = useMemo(
    () => qrzStationToContact(station, qrzHome),
    [station, qrzHome],
  );

  return (
    <div
      onClick={onClose}
      style={{
        position: 'fixed',
        inset: 0,
        background: 'rgba(0,0,0,0.55)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        zIndex: 400,
        padding: 16,
      }}
    >
      <div
        onClick={(e) => e.stopPropagation()}
        style={{
          background: 'var(--panel-top)',
          border: '1px solid var(--panel-border)',
          borderRadius: 'var(--r-lg)',
          boxShadow: '0 12px 40px rgba(0,0,0,0.6)',
          width: 340,
          maxWidth: '100%',
          maxHeight: '90%',
          display: 'flex',
          flexDirection: 'column',
          overflow: 'hidden',
        }}
      >
        <div
          style={{
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'space-between',
            padding: '6px 10px',
            borderBottom: '1px solid var(--panel-border)',
          }}
        >
          <span
            className="mono"
            style={{ fontWeight: 700, letterSpacing: '0.06em', color: 'var(--fg-0)' }}
          >
            {callsign}
          </span>
          <button type="button" className="btn sm" onClick={onClose} title="Close">
            ✕
          </button>
        </div>
        <div style={{ flex: 1, overflow: 'auto', minHeight: 0 }}>
          <QrzCard
            contact={contact}
            enriching={loading}
            lookupError={!loading && !contact ? (error ?? 'No QRZ record') : null}
          />
        </div>
      </div>
    </div>
  );
}

/**
 * App-root host: renders the QRZ profile card whenever a callsign is set in
 * profile-overlay-store. Mounted once (in App) so any surface can open it via
 * useProfileOverlayStore.open(callsign).
 */
export function ProfileOverlayHost() {
  const callsign = useProfileOverlayStore((s) => s.callsign);
  const close = useProfileOverlayStore((s) => s.close);
  if (!callsign) return null;
  return <ProfileOverlay callsign={callsign} onClose={close} />;
}
