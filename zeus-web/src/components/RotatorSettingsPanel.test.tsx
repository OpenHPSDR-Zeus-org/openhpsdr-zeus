// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Issue #917 — pin the multi-rotator settings UI fix that has nothing to do
// with hardware and so must be proven in software: switching the active slot
// (the panel ACTIVE radio, or the Compass/Dial selector) refreshes `multi`
// underneath the form, and that refresh must NOT wipe unsaved edits.

import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { RotctldMultiConfig, RotctldStatus } from '../api/rotator';

const { twoSlots, idleStatus } = vi.hoisted(() => {
  const twoSlots: RotctldMultiConfig = {
    activeSlotId: 1,
    autoRoute: false,
    slots: [
      { id: 1, label: 'HF Tower', enabled: true, host: '127.0.0.1', port: 4533, bands: ['20m'], pollingIntervalMs: 500 },
      { id: 2, label: 'VHF Yagi', enabled: true, host: '127.0.0.1', port: 4534, bands: ['6m'], pollingIntervalMs: 500 },
    ],
  };
  const idleStatus: RotctldStatus = {
    enabled: true, connected: false, host: '127.0.0.1', port: 4533,
    currentAz: null, targetAz: null, moving: false, error: null, activeSlotId: 1, slotCount: 2,
  };
  return { twoSlots, idleStatus };
});

// Keep the store's module-load hydration off the network and deterministic.
vi.mock('../api/rotator', () => ({
  getRotatorConfig: vi.fn(async () => ({ enabled: true, host: '127.0.0.1', port: 4533, pollingIntervalMs: 500 })),
  getRotatorMultiConfig: vi.fn(async () => twoSlots),
  getRotatorStatus: vi.fn(async () => idleStatus),
  postRotatorConfig: vi.fn(async () => idleStatus),
  postRotatorMultiConfig: vi.fn(async (c: unknown) => c),
  setRotatorActiveSlot: vi.fn(async () => ({ ...idleStatus, activeSlotId: 2 })),
  setRotatorAz: vi.fn(async () => idleStatus),
  stopRotator: vi.fn(async () => idleStatus),
  testRotator: vi.fn(async () => ({ ok: true, error: null })),
}));

const { useRotatorStore } = await import('../state/rotator-store');
const { RotatorSettingsPanel } = await import('./RotatorSettingsPanel');

function setInputValue(input: HTMLInputElement, value: string) {
  const desc = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
  desc!.set!.call(input, value);
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

function findInputByValue(container: HTMLElement, value: string): HTMLInputElement {
  const el = Array.from(container.querySelectorAll<HTMLInputElement>('input')).find((i) => i.value === value);
  if (!el) throw new Error(`no <input> with value "${value}"`);
  return el;
}

describe('RotatorSettingsPanel', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    act(() => {
      useRotatorStore.setState({ multi: twoSlots, status: idleStatus });
    });
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
  });

  it('keeps unsaved edits when the active rotator is switched', () => {
    act(() => root.render(<RotatorSettingsPanel />));

    // Operator edits the label but has NOT saved.
    const labelInput = findInputByValue(container, 'HF Tower');
    act(() => setInputValue(labelInput, 'HF Tower EDITED'));
    expect(findInputByValue(container, 'HF Tower EDITED')).toBeDefined();

    // An active-slot switch (panel radio or Compass/Dial selector) pushes a
    // fresh `multi` snapshot into the store. With the form dirty, the rehydrate
    // must be suppressed so the unsaved edit survives.
    act(() => {
      useRotatorStore.setState({ multi: { ...twoSlots, activeSlotId: 2 } });
    });

    expect(findInputByValue(container, 'HF Tower EDITED')).toBeDefined();
  });

  it('rehydrates from the backend when there are no unsaved edits', () => {
    act(() => root.render(<RotatorSettingsPanel />));
    expect(findInputByValue(container, 'HF Tower')).toBeDefined();

    // No local edits → an external label change should flow into the form.
    act(() => {
      useRotatorStore.setState({
        multi: {
          ...twoSlots,
          slots: twoSlots.slots.map((s) => (s.id === 1 ? { ...s, label: 'HF Tower RENAMED' } : s)),
        },
      });
    });

    expect(findInputByValue(container, 'HF Tower RENAMED')).toBeDefined();
  });
});
