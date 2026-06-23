// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Per-tile config shape for the MeterGroup panel — a top-level workspace
// tile that holds a single row OR column of meters that share the
// available width / height. Replaces the older MetersPanel which nested
// groups of meters inside one tile.
//
// Persistence shape stays minimal: title, direction, list of widget refs.
// Widget axis range / explicit kind override go in `widget.settings` as a
// loose record so the schema doesn't have to chase per-meter knobs.

import type { WidgetSettings } from '../meters/widgetSettings';
import {
  MeterReadingId,
  METER_CATALOG,
  METER_KINDS,
  type MeterDefaultKind,
} from '../meters/meterCatalog';

export type MeterGroupDirection = 'row' | 'column';

export type MeterGroupWidgetSettings = WidgetSettings;

export interface MeterGroupWidget {
  /** Stable per-instance id. */
  uid: string;
  /** Catalog reading the widget displays. */
  reading: MeterReadingId;
  /** Optional kind override. When absent, uses `METER_CATALOG[reading].defaultKind`. */
  kind?: MeterDefaultKind;
  /** Widget-level operator overrides. */
  settings?: MeterGroupWidgetSettings;
}

export interface MeterGroupConfig {
  schemaVersion: 1;
  title: string;
  direction: MeterGroupDirection;
  widgets: MeterGroupWidget[];
}

export const EMPTY_METER_GROUP_CONFIG: MeterGroupConfig = {
  schemaVersion: 1,
  title: 'Meters',
  direction: 'row',
  widgets: [],
};

export function newWidgetUid(): string {
  // Time-prefixed ish; collision-resistant enough for in-memory uniqueness.
  return `mgw-${Math.random().toString(36).slice(2, 10)}`;
}

/** Best-effort parse of an unknown JSON blob from the workspace store.
 *  Tolerates missing fields by filling defaults; unknown reading ids drop
 *  the widget rather than crashing the whole panel. */
export function parseMeterGroupConfig(raw: unknown): MeterGroupConfig {
  if (!raw || typeof raw !== 'object') return { ...EMPTY_METER_GROUP_CONFIG };
  const r = raw as Record<string, unknown>;
  const title = typeof r.title === 'string' && r.title.trim() ? r.title : 'Meters';
  const direction: MeterGroupDirection = r.direction === 'column' ? 'column' : 'row';
  const widgets: MeterGroupWidget[] = [];
  if (Array.isArray(r.widgets)) {
    for (const item of r.widgets) {
      if (!item || typeof item !== 'object') continue;
      const w = item as Record<string, unknown>;
      const reading = typeof w.reading === 'string' ? (w.reading as MeterReadingId) : null;
      if (!reading || !METER_CATALOG[reading]) continue;
      const uid = typeof w.uid === 'string' && w.uid ? w.uid : newWidgetUid();
      // Auto-migrate legacy 'sparkline' / 'digital' overrides — drop the
      // override so the widget falls back to the catalog default.
      const kind =
        typeof w.kind === 'string' && (METER_KINDS as ReadonlyArray<string>).includes(w.kind)
          ? (w.kind as MeterDefaultKind)
          : undefined;
      const settings: MeterGroupWidgetSettings = {};
      if (w.settings && typeof w.settings === 'object') {
        const s = w.settings as Record<string, unknown>;
        if (typeof s.min === 'number') settings.min = s.min;
        if (typeof s.max === 'number') settings.max = s.max;
        if (typeof s.label === 'string') settings.label = s.label;
        if (typeof s.peakHold === 'boolean') settings.peakHold = s.peakHold;
      }
      widgets.push({ uid, reading, kind, settings });
    }
  }
  return { schemaVersion: 1, title, direction, widgets };
}

/** Resolve the effective widget kind — operator override falls back to
 *  the catalog default for the reading. */
export function effectiveKind(widget: MeterGroupWidget): MeterDefaultKind {
  if (widget.kind) return widget.kind;
  const def = METER_CATALOG[widget.reading];
  return def.defaultKind;
}
