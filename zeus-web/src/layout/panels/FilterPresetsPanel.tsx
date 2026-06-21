// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Filter Presets panel — the preset chip grid (F1..F10 + VAR1/VAR2) plus the
// custom Lo/Hi Hz row, split out of the Bandwidth Filter panel so the operator
// can dock and size it independently of the mini-pan. Renders the same
// FilterRibbon component in its 'presets' section; both halves share the
// connection-store filter state, so a chip click here repaints the mini-pan in
// the Bandwidth Filter panel and vice-versa.

import { FilterRibbon } from '../../components/filter/FilterRibbon';

export function FilterPresetsPanel() {
  return (
    <div className="filter-ribbon-panel filter-ribbon-panel--presets" style={{ flex: 1, overflow: 'auto', padding: 8 }}>
      <FilterRibbon embedded section="presets" />
    </div>
  );
}
