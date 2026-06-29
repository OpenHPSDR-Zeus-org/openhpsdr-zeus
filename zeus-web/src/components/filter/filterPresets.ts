// SPDX-License-Identifier: GPL-2.0-or-later
//
// Thetis default filter preset tables from console.cs:5182–5585.
// Reference: docs/proposals/research/thetis-filter-ux.md §2.
// Numbers are signed Hz, VFO-relative. CW uses default cw_pitch=600.
// DIGL/DIGU use default offset=0.

import type { RxMode } from '../../api/client';

export type FilterPresetSlot = {
  slotName: string;
  label: string;
  lowHz: number;
  highHz: number;
  isVar: boolean;
};

const CW_PITCH = 600;

const LSB: readonly FilterPresetSlot[] = [
  { slotName: 'F1',   label: '5.0k',  lowHz: -5100, highHz: -100,  isVar: false },
  { slotName: 'F2',   label: '4.4k',  lowHz: -4500, highHz: -100,  isVar: false },
  { slotName: 'F3',   label: '3.8k',  lowHz: -3900, highHz: -100,  isVar: false },
  { slotName: 'F4',   label: '3.3k',  lowHz: -3400, highHz: -100,  isVar: false },
  { slotName: 'F5',   label: '2.9k',  lowHz: -3000, highHz: -100,  isVar: false },
  { slotName: 'F6',   label: '2.7k',  lowHz: -2800, highHz: -100,  isVar: false },
  { slotName: 'F7',   label: '2.4k',  lowHz: -2500, highHz: -100,  isVar: false },
  { slotName: 'F8',   label: '2.1k',  lowHz: -2200, highHz: -100,  isVar: false },
  { slotName: 'F9',   label: '1.8k',  lowHz: -1900, highHz: -100,  isVar: false },
  { slotName: 'F10',  label: '1.0k',  lowHz: -1100, highHz: -100,  isVar: false },
  { slotName: 'VAR1', label: 'Var 1', lowHz: -2800, highHz: -100,  isVar: true  },
  { slotName: 'VAR2', label: 'Var 2', lowHz: -2800, highHz: -100,  isVar: true  },
];

const USB: readonly FilterPresetSlot[] = [
  { slotName: 'F1',   label: '5.0k',  lowHz:  100, highHz: 5100,  isVar: false },
  { slotName: 'F2',   label: '4.4k',  lowHz:  100, highHz: 4500,  isVar: false },
  { slotName: 'F3',   label: '3.8k',  lowHz:  100, highHz: 3900,  isVar: false },
  { slotName: 'F4',   label: '3.3k',  lowHz:  100, highHz: 3400,  isVar: false },
  { slotName: 'F5',   label: '2.9k',  lowHz:  100, highHz: 3000,  isVar: false },
  { slotName: 'F6',   label: '2.7k',  lowHz:  100, highHz: 2800,  isVar: false },
  { slotName: 'F7',   label: '2.4k',  lowHz:  100, highHz: 2500,  isVar: false },
  { slotName: 'F8',   label: '2.1k',  lowHz:  100, highHz: 2200,  isVar: false },
  { slotName: 'F9',   label: '1.8k',  lowHz:  100, highHz: 1900,  isVar: false },
  { slotName: 'F10',  label: '1.0k',  lowHz:  100, highHz: 1100,  isVar: false },
  { slotName: 'VAR1', label: 'Var 1', lowHz:  100, highHz: 2800,  isVar: true  },
  { slotName: 'VAR2', label: 'Var 2', lowHz:  100, highHz: 2800,  isVar: true  },
];

const CWL: readonly FilterPresetSlot[] = [
  { slotName: 'F1',   label: '1.0k',  lowHz: -(CW_PITCH + 500), highHz: -(CW_PITCH - 500), isVar: false },
  { slotName: 'F2',   label: '800',   lowHz: -(CW_PITCH + 400), highHz: -(CW_PITCH - 400), isVar: false },
  { slotName: 'F3',   label: '600',   lowHz: -(CW_PITCH + 300), highHz: -(CW_PITCH - 300), isVar: false },
  { slotName: 'F4',   label: '500',   lowHz: -(CW_PITCH + 250), highHz: -(CW_PITCH - 250), isVar: false },
  { slotName: 'F5',   label: '400',   lowHz: -(CW_PITCH + 200), highHz: -(CW_PITCH - 200), isVar: false },
  { slotName: 'F6',   label: '250',   lowHz: -(CW_PITCH + 125), highHz: -(CW_PITCH - 125), isVar: false },
  { slotName: 'F7',   label: '150',   lowHz: -(CW_PITCH +  75), highHz: -(CW_PITCH -  75), isVar: false },
  { slotName: 'F8',   label: '100',   lowHz: -(CW_PITCH +  50), highHz: -(CW_PITCH -  50), isVar: false },
  { slotName: 'F9',   label: '50',    lowHz: -(CW_PITCH +  25), highHz: -(CW_PITCH -  25), isVar: false },
  { slotName: 'F10',  label: '25',    lowHz: -(CW_PITCH +  13), highHz: -(CW_PITCH -  13), isVar: false },
  { slotName: 'VAR1', label: 'Var 1', lowHz: -(CW_PITCH + 250), highHz: -(CW_PITCH - 250), isVar: true  },
  { slotName: 'VAR2', label: 'Var 2', lowHz: -(CW_PITCH + 250), highHz: -(CW_PITCH - 250), isVar: true  },
];

const CWU: readonly FilterPresetSlot[] = [
  { slotName: 'F1',   label: '1.0k',  lowHz: CW_PITCH - 500, highHz: CW_PITCH + 500, isVar: false },
  { slotName: 'F2',   label: '800',   lowHz: CW_PITCH - 400, highHz: CW_PITCH + 400, isVar: false },
  { slotName: 'F3',   label: '600',   lowHz: CW_PITCH - 300, highHz: CW_PITCH + 300, isVar: false },
  { slotName: 'F4',   label: '500',   lowHz: CW_PITCH - 250, highHz: CW_PITCH + 250, isVar: false },
  { slotName: 'F5',   label: '400',   lowHz: CW_PITCH - 200, highHz: CW_PITCH + 200, isVar: false },
  { slotName: 'F6',   label: '250',   lowHz: CW_PITCH - 125, highHz: CW_PITCH + 125, isVar: false },
  { slotName: 'F7',   label: '150',   lowHz: CW_PITCH -  75, highHz: CW_PITCH +  75, isVar: false },
  { slotName: 'F8',   label: '100',   lowHz: CW_PITCH -  50, highHz: CW_PITCH +  50, isVar: false },
  { slotName: 'F9',   label: '50',    lowHz: CW_PITCH -  25, highHz: CW_PITCH +  25, isVar: false },
  { slotName: 'F10',  label: '25',    lowHz: CW_PITCH -  13, highHz: CW_PITCH +  13, isVar: false },
  { slotName: 'VAR1', label: 'Var 1', lowHz: CW_PITCH - 250, highHz: CW_PITCH + 250, isVar: true  },
  { slotName: 'VAR2', label: 'Var 2', lowHz: CW_PITCH - 250, highHz: CW_PITCH + 250, isVar: true  },
];

const AM: readonly FilterPresetSlot[] = [
  { slotName: 'F1',   label: '20k',   lowHz: -10000, highHz: 10000, isVar: false },
  { slotName: 'F2',   label: '18k',   lowHz:  -9000, highHz:  9000, isVar: false },
  { slotName: 'F3',   label: '16k',   lowHz:  -8000, highHz:  8000, isVar: false },
  { slotName: 'F4',   label: '12k',   lowHz:  -6000, highHz:  6000, isVar: false },
  { slotName: 'F5',   label: '10k',   lowHz:  -5000, highHz:  5000, isVar: false },
  { slotName: 'F6',   label: '9.0k',  lowHz:  -4500, highHz:  4500, isVar: false },
  { slotName: 'F7',   label: '8.0k',  lowHz:  -4000, highHz:  4000, isVar: false },
  { slotName: 'F8',   label: '7.0k',  lowHz:  -3500, highHz:  3500, isVar: false },
  { slotName: 'F9',   label: '6.0k',  lowHz:  -3000, highHz:  3000, isVar: false },
  { slotName: 'F10',  label: '5.0k',  lowHz:  -2500, highHz:  2500, isVar: false },
  { slotName: 'VAR1', label: 'Var 1', lowHz:  -3000, highHz:  3000, isVar: true  },
  { slotName: 'VAR2', label: 'Var 2', lowHz:  -3000, highHz:  3000, isVar: true  },
];

const DSB: readonly FilterPresetSlot[] = [
  { slotName: 'F1',   label: '16k',   lowHz:  -8000, highHz:  8000, isVar: false },
  { slotName: 'F2',   label: '12k',   lowHz:  -6000, highHz:  6000, isVar: false },
  { slotName: 'F3',   label: '10k',   lowHz:  -5000, highHz:  5000, isVar: false },
  { slotName: 'F4',   label: '8.0k',  lowHz:  -4000, highHz:  4000, isVar: false },
  { slotName: 'F5',   label: '6.6k',  lowHz:  -3300, highHz:  3300, isVar: false },
  { slotName: 'F6',   label: '5.2k',  lowHz:  -2600, highHz:  2600, isVar: false },
  { slotName: 'F7',   label: '4.0k',  lowHz:  -2000, highHz:  2000, isVar: false },
  { slotName: 'F8',   label: '3.1k',  lowHz:  -1550, highHz:  1550, isVar: false },
  { slotName: 'F9',   label: '2.9k',  lowHz:  -1450, highHz:  1450, isVar: false },
  { slotName: 'F10',  label: '2.4k',  lowHz:  -1200, highHz:  1200, isVar: false },
  { slotName: 'VAR1', label: 'Var 1', lowHz:  -3300, highHz:  3300, isVar: true  },
  { slotName: 'VAR2', label: 'Var 2', lowHz:  -3300, highHz:  3300, isVar: true  },
];

// DIGL/DIGU centered on offset=0 (default). Symmetric because offset defaults to 0.
const DIGL: readonly FilterPresetSlot[] = [
  { slotName: 'F1',   label: '3.0k',  lowHz:  -1500, highHz:  1500, isVar: false },
  { slotName: 'F2',   label: '2.5k',  lowHz:  -1250, highHz:  1250, isVar: false },
  { slotName: 'F3',   label: '2.0k',  lowHz:  -1000, highHz:  1000, isVar: false },
  { slotName: 'F4',   label: '1.5k',  lowHz:   -750, highHz:   750, isVar: false },
  { slotName: 'F5',   label: '1.0k',  lowHz:   -500, highHz:   500, isVar: false },
  { slotName: 'F6',   label: '800',   lowHz:   -400, highHz:   400, isVar: false },
  { slotName: 'F7',   label: '600',   lowHz:   -300, highHz:   300, isVar: false },
  { slotName: 'F8',   label: '300',   lowHz:   -150, highHz:   150, isVar: false },
  { slotName: 'F9',   label: '150',   lowHz:    -75, highHz:    75, isVar: false },
  { slotName: 'F10',  label: '75',    lowHz:    -38, highHz:    38, isVar: false },
  { slotName: 'VAR1', label: 'Var 1', lowHz:   -400, highHz:   400, isVar: true  },
  { slotName: 'VAR2', label: 'Var 2', lowHz:   -400, highHz:   400, isVar: true  },
];

// FM has no presets in Thetis.
const FM: readonly FilterPresetSlot[] = [];

const PRESET_MAP: Record<RxMode, readonly FilterPresetSlot[]> = {
  LSB:  LSB,
  USB:  USB,
  CWL:  CWL,
  CWU:  CWU,
  AM:   AM,
  SAM:  AM,  // SAM uses identical table to AM
  DSB:  DSB,
  DIGL: DIGL,
  DIGU: DIGL, // DIGU uses identical half-widths to DIGL
  FM:   FM,
  FREEDV: USB, // FreeDV runs USB underneath — reuse the USB preset table.
};

export function getPresetsForMode(mode: RxMode): readonly FilterPresetSlot[] {
  return PRESET_MAP[mode] ?? USB;
}

// Per-mode favorite-slot defaults. Mirrors FilterPresetStore.GetFavoriteSlots
// on the server so the client can render correct defaults synchronously,
// before the /api/filter/favorites round-trip resolves. Without this, every
// first paint shows the ascending ['F4','F5','F6'] generic fallback even
// though USB/LSB really default to ['F6','F5','F4'] (descending = ascending
// passband width).
export function defaultFavoritesForMode(mode: RxMode): readonly string[] {
  switch (mode) {
    case 'USB': case 'LSB': case 'DIGL': case 'DIGU': case 'FREEDV': return ['F6', 'F5', 'F4'];
    case 'CWU': case 'CWL': return ['F4', 'F5', 'F6'];
    case 'AM':  case 'SAM': return ['F7', 'F8', 'F9'];
    case 'DSB': return ['F6', 'F7', 'F8'];
    case 'FM':  return ['F6', 'F5', 'F4'];
  }
}

export function formatFilterWidth(lowHz: number, highHz: number): string {
  const width = Math.abs(highHz - lowHz);
  if (width >= 1000) {
    const khz = width / 1000;
    return `${khz % 1 === 0 ? khz.toFixed(0) : khz.toFixed(1)} kHz`;
  }
  return `${width} Hz`;
}

// Format a VFO-relative filter edge as a signed offset. Uses a typographic
// minus sign, collapses ≥1 kHz to two-decimal kHz ("+2.70 kHz"), and keeps
// sub-kHz values in whole Hz ("−200 Hz"). Used by both the compact filter
// bar readout and the mini-pan wall callouts.
export function formatCutOffset(hz: number): string {
  const rounded = Math.round(hz);
  const sign = rounded < 0 ? '−' : '+';
  const abs = Math.abs(rounded);
  if (abs >= 1000) {
    return `${sign}${(abs / 1000).toFixed(2)} kHz`;
  }
  return `${sign}${abs} Hz`;
}

// Per-mode nudge step for edge adjustments (keyboard arrow keys in the ribbon,
// eventually the compact-panel Lo/Hi pairs in Phase 5). PRD §3.2.1 /
// §3.4 open question — defaults are: SSB 10 Hz, CW 10 Hz, AM/SAM/DSB 100 Hz,
// DIGL/DIGU 50 Hz.
export function nudgeStepHz(mode: RxMode): number {
  switch (mode) {
    case 'USB': case 'LSB': case 'CWU': case 'CWL': return 10;
    case 'DIGL': case 'DIGU': return 50;
    case 'AM': case 'SAM': case 'DSB': case 'FM': return 100;
    default: return 10;
  }
}
