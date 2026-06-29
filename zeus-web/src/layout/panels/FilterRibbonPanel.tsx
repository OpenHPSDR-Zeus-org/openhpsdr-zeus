// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

import { FilterRibbon } from '../../components/filter/FilterRibbon';

// Bandwidth Filter panel — mini-pan + drag hint only. The preset table and
// custom Hz row now live in their own "Filter Presets" panel (see
// FilterPresetsPanel) so each can be sized independently; both drive the same
// connection-store filter state.
export function FilterRibbonPanel() {
  return (
    <div
      className="filter-ribbon-panel filter-ribbon-panel--minipan"
      style={{ flex: 1, minHeight: 0, height: '100%', overflow: 'hidden', display: 'flex' }}
    >
      <FilterRibbon embedded section="minipan" />
    </div>
  );
}
