// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Shared SVG render helpers for the liquid-metal meter kit: per-instance id
// namespacing (many gauges share one page, so a shared <filter>/<gradient> id
// MUST be prefixed or one gauge's blur bleeds into another's) and the
// bloom-behind-crisp "lit trace" idiom (a blurred wide low-opacity copy under
// a crisp copy). DISPLAY-ONLY.

/** Sanitise a caller-supplied defs prefix into a DOM-safe id stem + local part. */
export function prefixDefs(defsId: string, local: string): string {
  const stem = defsId.replace(/\W/g, '_');
  return `${stem}-${local}`;
}
