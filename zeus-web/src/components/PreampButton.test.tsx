// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { useConnectionStore } from '../state/connection-store';
import { PreampButton } from './PreampButton';

// The boolean RX preamp bit is honored only by the original Mercury/Metis "G1"
// firmware (Thetis console.cs:19223). Every other board uses the step
// attenuator, so the PRE toggle must hide. These tests lock that gate.
function preButton(container: HTMLElement): HTMLButtonElement | null {
  return Array.from(container.querySelectorAll('button')).find(
    (b) => b.textContent?.trim() === 'PRE',
  ) as HTMLButtonElement | undefined ?? null;
}

describe('PreampButton visibility', () => {
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
    useConnectionStore.setState({ status: 'Disconnected', boardId: null });
  });

  function renderFor(boardId: string | null) {
    useConnectionStore.setState({ status: 'Connected', boardId });
    act(() => {
      root.render(<PreampButton />);
    });
  }

  it('renders the PRE button on the original Mercury/Metis G1', () => {
    renderFor('Metis');
    expect(preButton(container)).not.toBeNull();
  });

  it('hides the PRE button on Orion-MkII / G2 (step attenuator only)', () => {
    renderFor('OrionMkII');
    expect(preButton(container)).toBeNull();
  });

  it('hides the PRE button on Hermes-Lite 2', () => {
    renderFor('HermesLite2');
    expect(preButton(container)).toBeNull();
  });

  it('hides the PRE button when no board is connected', () => {
    renderFor(null);
    expect(preButton(container)).toBeNull();
  });
});
