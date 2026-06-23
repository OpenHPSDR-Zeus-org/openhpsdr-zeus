// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Strip of read-only live monitor panes for the multi-DDC RX3+ receivers. RX1
// and RX2 keep their full interactive panadapter/waterfall in HeroPanel; this
// strip appears only once the operator exposes a third receiver (Settings ->
// RECEIVERS), and renders one RxMonitorPane per contiguous enabled RX3+.
// Multi-DDC is a Protocol-2 feature, so the strip self-gates on a live P2 link.

import { useConnectionStore } from '../state/connection-store';
import { RxMonitorPane } from './RxMonitorPane';

export function MultiRxMonitorStrip() {
  const receivers = useConnectionStore((s) => s.receivers);
  const status = useConnectionStore((s) => s.status);
  const connectedProtocol = useConnectionStore((s) => s.connectedProtocol);

  const connected = status === 'Connected';
  const isP2 = connectedProtocol === 'P2';
  // RX3+ = receiver index >= 2 that is enabled. RX1/RX2 render in HeroPanel.
  const extraReceivers = receivers.filter((r) => r.index >= 2 && r.enabled);

  if (!connected || !isP2 || extraReceivers.length === 0) return null;

  // Stitch up to 4 receivers across one row; beyond that, stack into additional
  // rows of 4 (intuitive stacking for the last receivers). Columns are capped at
  // the row width so each pane keeps a usable size instead of shrinking to slivers.
  const MAX_PER_ROW = 4;
  const columns = Math.min(extraReceivers.length, MAX_PER_ROW);

  return (
    <div
      data-multi-rx-strip
      style={{
        display: 'grid',
        gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))`,
        gridAutoRows: '1fr',
        gap: 2,
        width: '100%',
        height: '100%',
        minHeight: 0,
      }}
    >
      {extraReceivers.map((r) => (
        <div key={r.index} style={{ minWidth: 0, minHeight: 0 }}>
          <RxMonitorPane rxIndex={r.index} />
        </div>
      ))}
    </div>
  );
}
