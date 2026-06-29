// SPDX-License-Identifier: GPL-2.0-or-later
//
// enter-digital tests — the mode-toggle contract that drives the FT8/FT4/WSPR
// mode-picker buttons and the pop-out close button. enterDigital QSYs the radio
// (openWorkspace, which snapshots + retunes), exitDigital restores it
// (closeWorkspace), toggleDigital flips between the two, and isDigitalEngaged
// reports the depressed-button state. FT8/FT4/WSPR are mutually exclusive.
//
// The radio QSY/restore mechanics themselves are covered by digital-mode.test.ts;
// here we mock the two mode stores so we assert enter-digital's branching
// (which workspace opens/closes, in which order) deterministically.

import { beforeEach, describe, expect, it, vi } from 'vitest';

interface MockMode {
  open: boolean;
  protocol: 'FT8' | 'FT4';
  priorRadio: unknown;
  openWorkspace: ReturnType<typeof vi.fn>;
  closeWorkspace: ReturnType<typeof vi.fn>;
}

const ft8: MockMode = {
  open: false,
  protocol: 'FT8',
  priorRadio: null,
  openWorkspace: vi.fn(),
  closeWorkspace: vi.fn(),
};
const wspr = {
  open: false,
  priorRadio: null as unknown,
  openWorkspace: vi.fn(),
  closeWorkspace: vi.fn(),
};

vi.mock('./ft8-store', () => ({
  useFt8Store: { getState: () => ft8 },
}));
vi.mock('./wspr-store', () => ({
  useWsprStore: { getState: () => wspr },
}));

import {
  DIGITAL_UNAVAILABLE,
  enterDigital,
  exitDigital,
  isDigitalEngaged,
  isDigitalEntryAvailable,
  toggleDigital,
} from './enter-digital';

beforeEach(() => {
  ft8.open = false;
  ft8.protocol = 'FT8';
  ft8.priorRadio = null;
  wspr.open = false;
  wspr.priorRadio = null;
  // Default ship state: WSPR is gated off (see enter-digital). Reset it here so
  // tests that temporarily un-gate it (below) don't leak into other tests.
  DIGITAL_UNAVAILABLE.WSPR = true;
  vi.clearAllMocks();
});

describe('enterDigital', () => {
  it('FT8 entry opens the FT8 workspace with the protocol (QSY happens there)', () => {
    enterDigital('FT8');
    expect(ft8.openWorkspace).toHaveBeenCalledWith({ protocol: 'FT8' });
    expect(wspr.closeWorkspace).not.toHaveBeenCalled();
  });

  it('WSPR entry closes an open FT8 workspace first (mutual exclusion)', () => {
    delete DIGITAL_UNAVAILABLE.WSPR; // un-gate WSPR to exercise its mechanics
    ft8.open = true;
    enterDigital('WSPR');
    expect(ft8.closeWorkspace).toHaveBeenCalledTimes(1);
    expect(wspr.openWorkspace).toHaveBeenCalledTimes(1);
  });

  it('FT4 entry closes an open WSPR workspace first (mutual exclusion)', () => {
    wspr.open = true;
    enterDigital('FT4');
    expect(wspr.closeWorkspace).toHaveBeenCalledTimes(1);
    expect(ft8.openWorkspace).toHaveBeenCalledWith({ protocol: 'FT4', prior: undefined });
  });

  it('switching modes carries the pre-digital snapshot forward and skips restore', () => {
    delete DIGITAL_UNAVAILABLE.WSPR; // un-gate WSPR to exercise its mechanics
    // FT8 already engaged with a captured pre-digital snapshot.
    const snap = { mode: 'USB' } as unknown;
    ft8.open = true;
    ft8.priorRadio = snap;
    enterDigital('WSPR');
    // Close WITHOUT restoring (WSPR will reconfigure the radio)…
    expect(ft8.closeWorkspace).toHaveBeenCalledWith({ restore: false });
    // …and the TRUE pre-digital snapshot is carried into WSPR so its exit
    // restores the operator's real config, not the FT8 DIGU dial.
    expect(wspr.openWorkspace).toHaveBeenCalledWith({ prior: snap });
  });
});

describe('availability gate', () => {
  it('reports FT8/FT4 available and WSPR gated off by default', () => {
    expect(isDigitalEntryAvailable('FT8')).toBe(true);
    expect(isDigitalEntryAvailable('FT4')).toBe(true);
    expect(isDigitalEntryAvailable('WSPR')).toBe(false);
  });

  it('enterDigital is a no-op for a gated mode (WSPR cannot be engaged)', () => {
    ft8.open = true; // an open workspace must NOT be touched by a gated entry
    enterDigital('WSPR');
    expect(wspr.openWorkspace).not.toHaveBeenCalled();
    expect(ft8.closeWorkspace).not.toHaveBeenCalled();
  });
});

describe('exitDigital', () => {
  it('closes whichever workspace is open (restores the radio)', () => {
    ft8.open = true;
    exitDigital();
    expect(ft8.closeWorkspace).toHaveBeenCalledTimes(1);
  });

  it('is a no-op when nothing is engaged', () => {
    exitDigital();
    expect(ft8.closeWorkspace).not.toHaveBeenCalled();
    expect(wspr.closeWorkspace).not.toHaveBeenCalled();
  });
});

describe('isDigitalEngaged', () => {
  it('matches FT8 only when FT8 is open AND the active protocol is FT8', () => {
    ft8.open = true;
    ft8.protocol = 'FT8';
    expect(isDigitalEngaged('FT8')).toBe(true);
    expect(isDigitalEngaged('FT4')).toBe(false);
    expect(isDigitalEngaged('WSPR')).toBe(false);
  });

  it('matches FT4 when FT8 store is open with the FT4 protocol', () => {
    ft8.open = true;
    ft8.protocol = 'FT4';
    expect(isDigitalEngaged('FT4')).toBe(true);
    expect(isDigitalEngaged('FT8')).toBe(false);
  });

  it('matches WSPR off the WSPR store', () => {
    wspr.open = true;
    expect(isDigitalEngaged('WSPR')).toBe(true);
  });
});

describe('toggleDigital', () => {
  it('enters when not engaged', () => {
    toggleDigital('FT8');
    expect(ft8.openWorkspace).toHaveBeenCalledWith({ protocol: 'FT8' });
  });

  it('exits when already engaged (un-depress restores the radio)', () => {
    ft8.open = true;
    ft8.protocol = 'FT8';
    toggleDigital('FT8');
    expect(ft8.closeWorkspace).toHaveBeenCalledTimes(1);
    expect(ft8.openWorkspace).not.toHaveBeenCalled();
  });

  it('switches FT8→FT4 (not engaged for FT4, so it re-opens with FT4)', () => {
    ft8.open = true;
    ft8.protocol = 'FT8';
    toggleDigital('FT4');
    expect(ft8.openWorkspace).toHaveBeenCalledWith({ protocol: 'FT4' });
  });
});
