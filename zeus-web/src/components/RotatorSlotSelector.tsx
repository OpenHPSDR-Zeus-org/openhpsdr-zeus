// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.

import { useRotatorStore } from '../state/rotator-store';

// Compact dropdown that shows the currently-active rotator slot and lets the
// operator override it from the Compass / Dial panel header. Hidden when the
// operator has only one slot configured — single-rotator stations don't need
// the chooser cluttering their compass.
export function RotatorSlotSelector(props: { compact?: boolean }) {
  const multi = useRotatorStore((s) => s.multi);
  const status = useRotatorStore((s) => s.status);
  const setActiveSlot = useRotatorStore((s) => s.setActiveSlot);
  const autoRoute = multi.autoRoute;
  const activeId = status?.activeSlotId ?? multi.activeSlotId;

  if (!multi.slots || multi.slots.length < 2) return null;

  return (
    <label
      style={{
        display: 'inline-flex',
        alignItems: 'center',
        gap: 6,
        fontSize: 11,
        color: 'var(--fg-2)',
        whiteSpace: 'nowrap',
      }}
      title={
        autoRoute
          ? 'Manually overriding the auto-routed rotator. Save the rotator settings to update default routing.'
          : 'Pick which rotator the panel controls.'
      }
    >
      {!props.compact && <span style={{ fontSize: 10, letterSpacing: '0.08em' }}>ROT</span>}
      <select
        value={activeId}
        onChange={(e) => void setActiveSlot(Number(e.currentTarget.value))}
        style={{
          padding: '2px 4px',
          fontSize: 11,
          background: 'var(--bg-0)',
          border: '1px solid var(--panel-border)',
          borderRadius: 'var(--r-sm)',
          color: 'var(--fg-0)',
          maxWidth: 140,
        }}
      >
        {multi.slots.map((s) => (
          <option key={s.id} value={s.id}>
            {s.label}
          </option>
        ))}
      </select>
      {autoRoute && (
        <span
          style={{ fontSize: 9, color: 'var(--accent)', fontWeight: 600, letterSpacing: '0.08em' }}
          title="Auto-route by TX band is ON. Changing the rotator here overrides it until the next band switch."
        >
          AUTO
        </span>
      )}
    </label>
  );
}
