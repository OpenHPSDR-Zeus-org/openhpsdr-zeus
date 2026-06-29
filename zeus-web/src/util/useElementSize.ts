// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

import { useEffect, useRef, useState, type RefObject } from 'react';

export interface ElementSize {
  width: number;
  height: number;
}

/**
 * Measure a DOM element's content-box via a ResizeObserver, returning its live
 * {width, height} in CSS pixels. The same getBoundingClientRect-backed pattern
 * the panadapter/waterfall canvases use to size their backing store — lifted
 * into one hook so layout-fit consumers (ScaleToFitTile) don't each re-roll the
 * observer wiring.
 *
 * Returns a ref to attach to the element plus its current size. Size starts at
 * {0,0} until the first observer tick fires, which callers must treat as "not
 * yet measured" (a scale of 0 should be clamped, never applied).
 */
export function useElementSize<T extends HTMLElement = HTMLDivElement>(): {
  ref: RefObject<T | null>;
  size: ElementSize;
} {
  const ref = useRef<T>(null);
  const [size, setSize] = useState<ElementSize>({ width: 0, height: 0 });

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => {
      const rect = el.getBoundingClientRect();
      setSize((prev) =>
        prev.width === rect.width && prev.height === rect.height
          ? prev
          : { width: rect.width, height: rect.height },
      );
    };
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  return { ref, size };
}
