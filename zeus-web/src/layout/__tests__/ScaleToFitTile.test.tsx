// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { render } from '../../components/meters/__tests__/harness';
import { ScaleToFitTile } from '../ScaleToFitTile';

// jsdom ships no ResizeObserver and getBoundingClientRect returns zeros. We
// install a fake observer that fires its callback synchronously on observe()
// and stub the outer element's rect so the scale math has a real tile size to
// chew on. Each test sets `tileRect` to the size it wants the tile to be.
let tileRect = { width: 0, height: 0 };

class FakeResizeObserver {
  private cb: ResizeObserverCallback;
  constructor(cb: ResizeObserverCallback) {
    this.cb = cb;
  }
  observe(_el: Element) {
    // Fire immediately so the hook reads the stubbed rect on mount.
    this.cb([], this as unknown as ResizeObserver);
  }
  unobserve() {}
  disconnect() {}
}

function outer(container: HTMLElement): HTMLElement {
  // The component's outer (measured) div is the first child of the tile body
  // root we render into.
  return container.firstElementChild as HTMLElement;
}

function inner(container: HTMLElement): HTMLElement {
  return outer(container).firstElementChild as HTMLElement;
}

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', FakeResizeObserver);
  // Route every getBoundingClientRect through the per-test tileRect. Only the
  // outer measured div matters; the children inherit the same stub but the
  // component only reads the outer element's rect.
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(
    () =>
      ({
        width: tileRect.width,
        height: tileRect.height,
        top: 0,
        left: 0,
        right: tileRect.width,
        bottom: tileRect.height,
        x: 0,
        y: 0,
        toJSON: () => ({}),
      }) as DOMRect,
  );
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('ScaleToFitTile', () => {
  it('scales uniformly to the limiting axis (width-bound)', () => {
    // Tile 200×400, design 100×100. min(200/100, 400/100) = 2.
    tileRect = { width: 200, height: 400 };
    const { container } = render(
      createElement(ScaleToFitTile, {
        designW: 100,
        designH: 100,
        children: createElement('span', null, 'x'),
      }),
    );
    const box = inner(container);
    expect(box.style.transform).toContain('scale(2)');
    // Design box stays at its authored size; only the transform scales it.
    expect(box.style.width).toBe('100px');
    expect(box.style.height).toBe('100px');
    // Width fills (200 == 200), height centres: (400 - 200)/2 = 100.
    expect(box.style.transform).toContain('translate(0px, 100px)');
  });

  it('scales to the height axis when height-bound and centres horizontally', () => {
    // Tile 400×100, design 100×100. min(400/100, 100/100) = 1.
    tileRect = { width: 400, height: 100 };
    const { container } = render(
      createElement(ScaleToFitTile, {
        designW: 100,
        designH: 100,
        children: createElement('span', null, 'x'),
      }),
    );
    const box = inner(container);
    expect(box.style.transform).toContain('scale(1)');
    // Height fills (100 == 100), width centres: (400 - 100)/2 = 150.
    expect(box.style.transform).toContain('translate(150px, 0px)');
  });

  it('falls back to scale 1 before the observer has measured a size', () => {
    // Zero rect = not yet measured. Component must render at scale 1 so the
    // subtree is laid out and measurable rather than collapsed.
    tileRect = { width: 0, height: 0 };
    const { container } = render(
      createElement(ScaleToFitTile, {
        designW: 320,
        designH: 180,
        children: createElement('span', null, 'x'),
      }),
    );
    const box = inner(container);
    expect(box.style.transform).toContain('scale(1)');
    expect(box.style.transform).toContain('translate(0px, 0px)');
  });

  it('preserves aspect ratio (shrinks the design box uniformly)', () => {
    // Tile 50×50, design 200×100. min(50/200, 50/100) = 0.25.
    tileRect = { width: 50, height: 50 };
    const { container } = render(
      createElement(ScaleToFitTile, {
        designW: 200,
        designH: 100,
        children: createElement('span', null, 'x'),
      }),
    );
    const box = inner(container);
    expect(box.style.transform).toContain('scale(0.25)');
    // Scaled box: 200*0.25=50 wide (fills), 100*0.25=25 tall, centred:
    // (50 - 25)/2 = 12.5.
    expect(box.style.transform).toContain('translate(0px, 12.5px)');
  });

  it('auto-measures the content footprint when no design size is given', () => {
    // No designW/designH → auto mode. The inner box shrink-wraps to content
    // (max-content) and its offsetWidth/offsetHeight stand in for the design
    // size. Stub those as the natural footprint 100×50. Tile 200×400 →
    // min(200/100, 400/50) = 2.
    tileRect = { width: 200, height: 400 };
    vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(100);
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(50);

    const { container } = render(
      createElement(ScaleToFitTile, {
        children: createElement('span', null, 'x'),
      }),
    );
    const box = inner(container);
    // Auto mode shrink-wraps the inner box so its natural size is the
    // content's intrinsic footprint, not the tile size.
    expect(box.style.width).toBe('max-content');
    expect(box.style.height).toBe('max-content');
    expect(box.style.transform).toContain('scale(2)');
    // Scaled box: 100*2=200 wide (fills, 200==200), 50*2=100 tall, centred:
    // (400 - 100)/2 = 150.
    expect(box.style.transform).toContain('translate(0px, 150px)');
  });

  it('stays at scale 1 in auto mode until the content is measured', () => {
    // Tile is sized, but offsetWidth/offsetHeight report 0 (content not yet
    // laid out). Auto mode must hold scale 1 rather than divide by zero.
    tileRect = { width: 200, height: 400 };
    vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(0);
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(0);

    const { container } = render(
      createElement(ScaleToFitTile, {
        children: createElement('span', null, 'x'),
      }),
    );
    const box = inner(container);
    expect(box.style.transform).toContain('scale(1)');
    expect(box.style.transform).toContain('translate(0px, 0px)');
  });
});
