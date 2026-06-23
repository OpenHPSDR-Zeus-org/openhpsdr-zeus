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
//
// ScaleToFitTile — wraps a panel that draws at a fixed "design size" and
// uniformly scales it to fill its workspace tile, the way the panadapter's
// canvas fills its tile. Used for panels whose content is laid out at a
// natural pixel footprint and would otherwise clip (tile smaller than design)
// or leave dead whitespace (tile larger than design).
//
// Two modes:
//   • Explicit design size — pass designW/designH (CSS px). The scale is
//     min(tileW/designW, tileH/designH) and the design box is sized to those
//     authored dimensions. Use when a panel has a known natural footprint.
//   • Auto-measure — omit designW/designH. The inner box shrink-wraps to its
//     children's intrinsic content (width/height: max-content) and a
//     ResizeObserver reads that content's offsetWidth/offsetHeight as the
//     design size. offsetWidth/offsetHeight are PRE-transform layout metrics:
//     the CSS `transform` we apply lives on the SAME element but does not feed
//     back into its own offset box (offset* is the untransformed border box),
//     so the measured natural size is stable and the scale never oscillates.
//
// Uniform scale = min(tileW/designW, tileH/designH) preserves aspect like a
// real instrument face. Because the scale is uniform and applied via a single
// CSS transform, getBoundingClientRect on descendants stays correct post-
// transform, so pointer-coordinate controls (digit drag, sliders, canvas
// drag) keep working — the standard react-resizable behaviour. Panels whose
// pointer model or layout breaks under transform (Leaflet maps, canvas mini-
// pans, iframes, already-fluid flex panels) must NOT be wrapped (set
// fillNative) and instead render natively at the .workspace-tile-body seam.

import { useEffect, useRef, useState, type ReactNode } from 'react';
import { useElementSize } from '../util/useElementSize';

interface ScaleToFitTileProps {
  /** Natural width the children are authored at, in CSS px. Omit (together
   *  with designH) to auto-measure the children's intrinsic content size. */
  designW?: number;
  /** Natural height the children are authored at, in CSS px. Omit (together
   *  with designW) to auto-measure the children's intrinsic content size. */
  designH?: number;
  children: ReactNode;
}

export function ScaleToFitTile({ designW, designH, children }: ScaleToFitTileProps) {
  const { ref, size } = useElementSize<HTMLDivElement>();

  // Auto-measure mode is selected when neither design dimension is supplied.
  const auto = designW === undefined || designH === undefined;

  // In auto mode we read the inner content box's PRE-transform layout size
  // (offsetWidth/offsetHeight) via a ResizeObserver. These metrics are the
  // untransformed border-box, so the CSS scale we apply to the same element
  // never feeds back into the measurement — no oscillation.
  const innerRef = useRef<HTMLDivElement>(null);
  const [natural, setNatural] = useState<{ w: number; h: number }>({ w: 0, h: 0 });

  useEffect(() => {
    if (!auto) return;
    const el = innerRef.current;
    if (!el) return;
    const measure = () => {
      const w = el.offsetWidth;
      const h = el.offsetHeight;
      setNatural((prev) => (prev.w === w && prev.h === h ? prev : { w, h }));
    };
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, [auto]);

  // Resolve the design size from explicit props or the measured natural size.
  const naturalW = auto ? natural.w : (designW as number);
  const naturalH = auto ? natural.h : (designH as number);

  // Before the first ResizeObserver tick (size 0) — and, in auto mode, before
  // the content has been measured — render at scale 1 so the content is laid
  // out and measurable; the observer then corrects on the next frame. A 0
  // scale would collapse the subtree and any focus/measure inside it.
  const ready =
    size.width > 0 && size.height > 0 && naturalW > 0 && naturalH > 0;
  const scale = ready
    ? Math.min(size.width / naturalW, size.height / naturalH)
    : 1;
  // Centre the scaled box inside the tile so a panel narrower or shorter than
  // the tile's aspect sits in the middle rather than pinning top-left.
  const scaledW = naturalW * scale;
  const scaledH = naturalH * scale;
  const offsetX = ready ? Math.max(0, (size.width - scaledW) / 2) : 0;
  const offsetY = ready ? Math.max(0, (size.height - scaledH) / 2) : 0;

  // Explicit mode pins the inner box to the authored design size; auto mode
  // lets it shrink-wrap to content so offset* reports the intrinsic footprint
  // rather than the tile size.
  const innerSizing = auto
    ? ({ width: 'max-content', height: 'max-content' } as const)
    : ({ width: designW, height: designH } as const);

  return (
    <div
      ref={ref}
      style={{
        position: 'relative',
        width: '100%',
        height: '100%',
        overflow: 'hidden',
      }}
    >
      <div
        ref={innerRef}
        style={{
          position: 'absolute',
          top: 0,
          left: 0,
          ...innerSizing,
          transform: `translate(${offsetX}px, ${offsetY}px) scale(${scale})`,
          transformOrigin: 'top left',
        }}
      >
        {children}
      </div>
    </div>
  );
}
