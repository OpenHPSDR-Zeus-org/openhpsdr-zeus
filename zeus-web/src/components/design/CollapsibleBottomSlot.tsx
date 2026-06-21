// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
// See LICENSE for the full GPL text.

import type { ReactNode } from 'react';
import { Pin, PinOff } from 'lucide-react';
import { Dockable } from './Dockable';
import { useBottomPinStore, type BottomSlotId } from '../../state/bottom-pin-store';

type CollapsibleBottomSlotProps = {
  slotId: BottomSlotId;
  title: ReactNode;
  children: ReactNode;
  ledOn?: boolean;
  ledTx?: boolean;
  // Caller-supplied action buttons (e.g. Publish / Export on the Logbook).
  // We append the pin toggle to the right of these so the pin always sits
  // closest to the drag handle.
  actions?: ReactNode;
};

export function CollapsibleBottomSlot({
  slotId,
  title,
  children,
  ledOn,
  ledTx,
  actions,
}: CollapsibleBottomSlotProps) {
  const pinned = useBottomPinStore((s) => s.pinned[slotId]);
  const togglePin = useBottomPinStore((s) => s.togglePin);

  const pinButton = (
    <button
      type="button"
      className="btn ghost sm"
      title={pinned ? 'Unpin — collapse to header' : 'Pin — keep panel open'}
      aria-label={pinned ? 'Unpin panel' : 'Pin panel'}
      aria-pressed={!pinned}
      onClick={() => togglePin(slotId)}
      style={{ display: 'inline-flex', alignItems: 'center' }}
    >
      {pinned ? <Pin size={12} /> : <PinOff size={12} />}
    </button>
  );

  // When unpinned we hide the body and let the panel sit at the top of its
  // grid cell — see .panel--unpinned in layout.css. The actions row, LED
  // and title remain visible so the operator can still see the live LED
  // state (e.g. TX active on the meters strip) and click the pin to bring
  // the body back.
  return (
    <Dockable
      title={title}
      ledOn={ledOn}
      ledTx={ledTx}
      className={pinned ? '' : 'panel--unpinned'}
      actions={
        <>
          {actions}
          {pinButton}
        </>
      }
    >
      {children}
    </Dockable>
  );
}
