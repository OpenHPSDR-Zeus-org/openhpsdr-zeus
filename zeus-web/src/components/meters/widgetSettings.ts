// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Lightweight settings shape consumed by HBarMeter. Pulled into its own
// file when the heavier metersConfig.ts was retired alongside the old
// MetersPanel — the primitive still needs a way to take operator-level
// axis-range and label overrides without dragging in the deleted
// per-tile-config machinery.

export interface WidgetSettings {
  /** Operator override for axis min — falls back to catalog `defaultMin`. */
  min?: number;
  /** Operator override for axis max — falls back to catalog `defaultMax`. */
  max?: number;
  /** Operator label override — defaults to `def.label`. */
  label?: string;
  /** Show the peak-hold tick (default true on bar meters). */
  peakHold?: boolean;
}
