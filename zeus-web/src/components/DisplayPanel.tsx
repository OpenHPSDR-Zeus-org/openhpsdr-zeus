// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Display settings panel. Layout-mode toggle (Default vs Flex) was retired
// in issue #241 — layout switching now lives entirely in the LeftLayoutBar.

import { BackgroundSettingsPanel } from './BackgroundSettingsPanel';
import { ThemeSettingsPanel } from './ThemeSettingsPanel';
import { TraceColorPanel } from './TraceColorPanel';
import { WaterfallColormapPanel } from './WaterfallColormapPanel';

export function DisplayPanel() {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 22 }}>
      <ThemeSettingsPanel />
      <BackgroundSettingsPanel />
      <TraceColorPanel />
      <WaterfallColormapPanel />
    </div>
  );
}
