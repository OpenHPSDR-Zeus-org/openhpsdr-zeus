// SPDX-License-Identifier: GPL-2.0-or-later

import type { ToolbarFavKind } from '../../state/toolbar-favorites-store';

const DRAG_MIME_PREFIX = 'application/x-zeus-toolbar-fav-';

// MIME used by toolbar favorite drop targets. External components set this on
// their own buttons' dragstart so the operator can drag any option onto a
// toolbar slot to pin it.
export function toolbarFavDragMime(kind: ToolbarFavKind): string {
  return DRAG_MIME_PREFIX + kind;
}
