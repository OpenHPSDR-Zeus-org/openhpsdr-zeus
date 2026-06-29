// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// FlexLayout wrapper for the analog S-meter tile. The tile draws its own
// header (RX/TX tabs, gear, close) so it registers as `headerless` in the
// panel registry — this lets the workspace dragger pick up
// `.workspace-tile-header` from inside the tile body, and the close button
// is the `.workspace-tile-close` element wired to the injected onRemove.

import { AnalogMeterPanel as AnalogMeterTile } from '../../components/analog-meter/AnalogMeterPanel';
import type { PanelComponentProps } from '../panels';

export function AnalogMeterPanel({
  onRemove,
  tileLocked,
  workspaceLocked,
  onToggleLock,
}: PanelComponentProps) {
  return (
    <AnalogMeterTile
      onClose={onRemove}
      tileLocked={tileLocked}
      workspaceLocked={workspaceLocked}
      onToggleLock={onToggleLock}
    />
  );
}
