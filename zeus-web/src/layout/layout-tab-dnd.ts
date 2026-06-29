// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// Cross-component glue for "drag a workspace panel onto a layout tab to move
// it there." The workspace tiles drag via react-grid-layout's pointer-based
// drag (not native HTML5 DnD), so the LeftLayoutBar never receives
// dragenter/dragover events. Instead the workspace tracks the pointer during a
// tile drag and geometrically hit-tests it against the layout tabs, which mark
// themselves with `data-layout-tab-id`. These helpers keep that contract in
// one place so the tab markup and the workspace drop logic agree on the
// attribute names.

/** Attribute carrying a layout id on each LeftLayoutBar tab — the drop target
 *  identity the workspace reads at drop time. */
export const LAYOUT_TAB_ID_ATTR = 'data-layout-tab-id';
/** Attribute toggled on the tab currently under a dragged panel, so CSS can
 *  highlight the drop target. */
export const LAYOUT_TAB_DROP_ACTIVE_ATTR = 'data-panel-drop-active';

/** Return the layout id of the tab whose bounding box contains (x, y), or null
 *  when the point is over no tab. A plain rect hit-test (not elementFromPoint)
 *  because the dragged tile sits under the cursor and would otherwise occlude
 *  the tab beneath it. */
export function findLayoutTabAtPoint(x: number, y: number): string | null {
  if (typeof document === 'undefined') return null;
  const tabs = document.querySelectorAll<HTMLElement>(`[${LAYOUT_TAB_ID_ATTR}]`);
  for (const el of tabs) {
    const r = el.getBoundingClientRect();
    if (x >= r.left && x <= r.right && y >= r.top && y <= r.bottom) {
      return el.getAttribute(LAYOUT_TAB_ID_ATTR);
    }
  }
  return null;
}

/** Mark `layoutId`'s tab as the active drop target and clear the mark from all
 *  others. Pass null to clear every highlight (drag ended / left the tabs). */
export function setLayoutTabDropTarget(layoutId: string | null): void {
  if (typeof document === 'undefined') return;
  const tabs = document.querySelectorAll<HTMLElement>(`[${LAYOUT_TAB_ID_ATTR}]`);
  for (const el of tabs) {
    if (layoutId && el.getAttribute(LAYOUT_TAB_ID_ATTR) === layoutId) {
      el.setAttribute(LAYOUT_TAB_DROP_ACTIVE_ATTR, 'true');
    } else {
      el.removeAttribute(LAYOUT_TAB_DROP_ACTIVE_ATTR);
    }
  }
}
