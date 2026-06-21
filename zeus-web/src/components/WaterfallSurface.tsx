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

// Renderer switch for the waterfall surface. The WebGPU heightfield is now the
// DEFAULT (gl/webgpu/flag.ts) for BOTH single-RX and stitched dual-RX (RX2); the
// legacy WebGL2 Waterfall is the fallback, used when:
//   • the heightfield is force-disabled (?webgpuWaterfall=0), or
//   • the device lacks WebGPU / the renderer fails to init (reported via
//     onUnavailable → we swap to WebGL for the rest of this mount).

import { useState, type ComponentProps } from 'react';
import { Waterfall } from './Waterfall';
import { WaterfallHeightfield } from './WaterfallHeightfield';
import { isWebGpuWaterfallEnabled } from '../gl/webgpu/flag';

// Waterfall's props default to {}, so ComponentProps is `Props | undefined`;
// NonNullable narrows it back to the object so field access is clean.
type WaterfallProps = NonNullable<ComponentProps<typeof Waterfall>>;

export function WaterfallSurface(props: WaterfallProps) {
  // Latches when the heightfield reports it can't run (no WebGPU / init failure),
  // so we render the WebGL waterfall instead of a broken/blank surface.
  const [heightfieldUnavailable, setHeightfieldUnavailable] = useState(false);

  // Cheap localStorage/URL check; this wrapper re-renders only on layout changes,
  // never per frame.
  const useHeightfield = isWebGpuWaterfallEnabled() && !heightfieldUnavailable;

  if (useHeightfield) {
    return (
      <WaterfallHeightfield
        receiver={props.receiver}
        stitched={props.stitched}
        foreground={props.foreground}
        touchMode={props.touchMode}
        tuneReceiver={props.tuneReceiver}
        onUnavailable={() => setHeightfieldUnavailable(true)}
      />
    );
  }
  return <Waterfall {...props} />;
}
