// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// FIX/DYN and the threshold slider don't affect audio while SQL is OFF — they
// must gray out in that state so the operator can't dial a phantom setting.
// These tests lock that disabled-state matrix (issue #1130).

/** @vitest-environment jsdom */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { useConnectionStore } from '../state/connection-store';
import { SQUELCH_CONFIG_DEFAULT, type SquelchConfigDto } from '../api/client';

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return {
    ...actual,
    setSquelch: vi.fn(async (cfg: SquelchConfigDto) => cfg),
  };
});

import { SquelchSlider } from './SquelchSlider';

function btn(container: HTMLElement, label: string): HTMLButtonElement {
  const found = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
    .find((b) => b.textContent?.trim() === label);
  expect(found, `button labeled "${label}"`).toBeDefined();
  return found!;
}

function slider(container: HTMLElement): HTMLInputElement {
  const found = container.querySelector<HTMLInputElement>('input[type="range"]');
  expect(found, 'threshold range input').not.toBeNull();
  return found!;
}

describe('SquelchSlider disabled-state matrix (issue #1130)', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    useConnectionStore.setState({
      status: 'Disconnected',
      squelch: { ...SQUELCH_CONFIG_DEFAULT },
    });
  });

  function renderWith(squelch: Partial<SquelchConfigDto>, connected = true) {
    useConnectionStore.setState({
      status: connected ? 'Connected' : 'Disconnected',
      squelch: { ...SQUELCH_CONFIG_DEFAULT, ...squelch },
    });
    act(() => {
      root.render(<SquelchSlider />);
    });
  }

  it('SQL stays clickable while squelch is OFF (operator can turn it on)', () => {
    renderWith({ enabled: false, adaptive: false });
    expect(btn(container, 'SQL').disabled).toBe(false);
  });

  it('grays FIX/DYN and the threshold slider while SQL is OFF', () => {
    renderWith({ enabled: false, adaptive: false });
    expect(btn(container, 'FIX').disabled).toBe(true);
    expect(slider(container).disabled).toBe(true);
  });

  it('keeps FIX/DYN grayed while SQL is OFF even if DYN was previously selected', () => {
    renderWith({ enabled: false, adaptive: true });
    expect(btn(container, 'DYN').disabled).toBe(true);
    expect(slider(container).disabled).toBe(true);
  });

  it('enables the threshold slider when SQL is ON in FIX mode', () => {
    renderWith({ enabled: true, adaptive: false });
    expect(btn(container, 'FIX').disabled).toBe(false);
    expect(slider(container).disabled).toBe(false);
  });

  it('grays the slider when SQL is ON in DYN mode (FIX/DYN itself stays clickable)', () => {
    renderWith({ enabled: true, adaptive: true });
    expect(btn(container, 'DYN').disabled).toBe(false);
    expect(slider(container).disabled).toBe(true);
  });

  it('grays everything when not connected, regardless of squelch state', () => {
    renderWith({ enabled: true, adaptive: false }, /* connected */ false);
    expect(btn(container, 'SQL').disabled).toBe(true);
    expect(btn(container, 'FIX').disabled).toBe(true);
    expect(slider(container).disabled).toBe(true);
  });
});
