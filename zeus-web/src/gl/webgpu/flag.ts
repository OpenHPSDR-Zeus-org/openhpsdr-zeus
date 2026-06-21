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

// Opt-in flag for the experimental WebGPU 3D heightfield waterfall.
//
// OFF by default: the production surface stays on the proven WebGL2 renderer
// (gl/waterfall.ts) until the heightfield path is benchmarked and signed off.
// Two ways to enable, both read live so no rebuild is needed:
//   • URL:          ?webgpuWaterfall=1   (sticky — persists to localStorage)
//   • localStorage: zeus.waterfall.webgpu = "1"
// Append ?webgpuWaterfall=0 to force back off.
//
// This only expresses INTENT. Actual use still requires the capability probe
// (caps.ts) to confirm the device supports WebGPU — see resolveWaterfallRenderer.

const STORAGE_KEY = 'zeus.waterfall.webgpu';
const URL_PARAM = 'webgpuWaterfall';

function readUrlOverride(): boolean | null {
  try {
    if (typeof window === 'undefined') return null;
    const raw = new URLSearchParams(window.location.search).get(URL_PARAM);
    if (raw === null) return null;
    return raw === '1' || raw === 'true';
  } catch {
    return null;
  }
}

function readStored(): '1' | '0' | null {
  try {
    if (typeof localStorage === 'undefined') return null;
    const v = localStorage.getItem(STORAGE_KEY);
    return v === '1' || v === '0' ? v : null;
  } catch {
    return null;
  }
}

function writeStored(enabled: boolean): void {
  try {
    if (typeof localStorage === 'undefined') return;
    // Store the explicit choice (including off) so `?webgpuWaterfall=0` sticks.
    localStorage.setItem(STORAGE_KEY, enabled ? '1' : '0');
  } catch {
    // private mode / quota — URL override still works for this session.
  }
}

/** Whether the WebGPU heightfield is the active waterfall. Default ON — the
 *  heightfield is the standard waterfall now; the legacy WebGL surface is the
 *  fallback. Force the legacy one with `?webgpuWaterfall=0` (sticky). Actual use
 *  still requires the capability probe (caps.ts) and a clean renderer init; on
 *  any failure WaterfallSurface falls back to WebGL. */
export function isWebGpuWaterfallEnabled(): boolean {
  const override = readUrlOverride();
  if (override !== null) {
    writeStored(override);
    return override;
  }
  return readStored() !== '0';
}

/** Programmatic toggle (e.g. a Settings switch). */
export function setWebGpuWaterfallEnabled(enabled: boolean): void {
  writeStored(enabled);
}
