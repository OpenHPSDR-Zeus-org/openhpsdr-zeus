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
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useState, useEffect } from 'react';

type UpdatePromptProps = {
  onUpdate: (() => Promise<void>) | null;
  show: boolean;
};

/**
 * Displays a prominent notification when a service worker update is available.
 * User can click to reload and apply the update immediately.
 */
export function UpdatePrompt({ onUpdate, show }: UpdatePromptProps) {
  const [visible, setVisible] = useState(false);
  const [updating, setUpdating] = useState(false);

  useEffect(() => {
    if (show) {
      // Small delay for animation
      setTimeout(() => setVisible(true), 100);
    }
  }, [show]);

  if (!show || !onUpdate) {
    return null;
  }

  const handleUpdate = async () => {
    setUpdating(true);
    try {
      await onUpdate();
      // Page will reload automatically after update
    } catch (err) {
      console.error('Failed to apply update:', err);
      setUpdating(false);
    }
  };

  return (
    <div
      style={{
        position: 'fixed',
        top: 16,
        left: '50%',
        transform: `translate(-50%, ${visible ? '0' : '-120%'})`,
        transition: 'transform 0.3s ease-out',
        zIndex: 10000,
        maxWidth: 480,
        width: 'calc(100% - 32px)',
      }}
    >
      <div
        style={{
          background: 'linear-gradient(135deg, var(--accent) 0%, #3a7edf 100%)',
          color: 'white',
          padding: 16,
          borderRadius: 'var(--r-md)',
          boxShadow: '0 4px 12px rgba(0, 0, 0, 0.3), 0 8px 24px rgba(0, 0, 0, 0.2)',
          display: 'flex',
          alignItems: 'center',
          gap: 12,
        }}
      >
        <div style={{ flex: 1 }}>
          <div style={{ fontWeight: 700, marginBottom: 4, fontSize: 14 }}>
            Update Available
          </div>
          <div style={{ fontSize: 12, opacity: 0.95 }}>
            A new version of Zeus is ready. Click to reload and update.
          </div>
        </div>
        <button
          type="button"
          onClick={handleUpdate}
          disabled={updating}
          style={{
            padding: '8px 16px',
            background: 'white',
            color: 'var(--accent)',
            border: 'none',
            borderRadius: 'var(--r-sm)',
            fontWeight: 700,
            fontSize: 12,
            cursor: updating ? 'wait' : 'pointer',
            whiteSpace: 'nowrap',
            opacity: updating ? 0.7 : 1,
          }}
        >
          {updating ? 'UPDATING...' : 'RELOAD NOW'}
        </button>
      </div>
    </div>
  );
}
