// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

/** @vitest-environment jsdom */

import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  LAYOUT_TAB_DROP_ACTIVE_ATTR,
  findLayoutTabAtPoint,
  setLayoutTabDropTarget,
} from '../layout-tab-dnd';

function makeTab(id: string, rect: { left: number; top: number; right: number; bottom: number }) {
  const el = document.createElement('button');
  el.setAttribute('data-layout-tab-id', id);
  el.getBoundingClientRect = vi.fn(
    () =>
      ({
        left: rect.left,
        top: rect.top,
        right: rect.right,
        bottom: rect.bottom,
        width: rect.right - rect.left,
        height: rect.bottom - rect.top,
        x: rect.left,
        y: rect.top,
        toJSON: () => ({}),
      }) as DOMRect,
  );
  document.body.appendChild(el);
  return el;
}

afterEach(() => {
  document.body.innerHTML = '';
});

describe('layout-tab-dnd', () => {
  it('findLayoutTabAtPoint returns the id of the tab under the point', () => {
    makeTab('a', { left: 0, top: 0, right: 40, bottom: 40 });
    makeTab('b', { left: 0, top: 40, right: 40, bottom: 80 });
    expect(findLayoutTabAtPoint(20, 20)).toBe('a');
    expect(findLayoutTabAtPoint(20, 60)).toBe('b');
  });

  it('findLayoutTabAtPoint returns null when the point is over no tab', () => {
    makeTab('a', { left: 0, top: 0, right: 40, bottom: 40 });
    expect(findLayoutTabAtPoint(200, 200)).toBeNull();
  });

  it('setLayoutTabDropTarget marks only the named tab and clears it again', () => {
    const a = makeTab('a', { left: 0, top: 0, right: 40, bottom: 40 });
    const b = makeTab('b', { left: 0, top: 40, right: 40, bottom: 80 });

    setLayoutTabDropTarget('b');
    expect(a.getAttribute(LAYOUT_TAB_DROP_ACTIVE_ATTR)).toBeNull();
    expect(b.getAttribute(LAYOUT_TAB_DROP_ACTIVE_ATTR)).toBe('true');

    // Switching target moves the mark.
    setLayoutTabDropTarget('a');
    expect(a.getAttribute(LAYOUT_TAB_DROP_ACTIVE_ATTR)).toBe('true');
    expect(b.getAttribute(LAYOUT_TAB_DROP_ACTIVE_ATTR)).toBeNull();

    // Null clears every mark.
    setLayoutTabDropTarget(null);
    expect(a.getAttribute(LAYOUT_TAB_DROP_ACTIVE_ATTR)).toBeNull();
    expect(b.getAttribute(LAYOUT_TAB_DROP_ACTIVE_ATTR)).toBeNull();
  });
});
