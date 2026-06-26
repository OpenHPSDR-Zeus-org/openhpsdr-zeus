// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { useConnectionStore } from '../../state/connection-store';
import { Nr3ModelPanel } from './Nr3ModelPanel';

describe('Nr3ModelPanel', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    useConnectionStore.setState({
      wdspNr3RnnrAvailable: true,
      nr3ModelName: null,
    });
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
  });

  function renderPanel() {
    act(() => {
      root.render(<Nr3ModelPanel />);
    });
  }

  function linkByLabel(label: string): HTMLAnchorElement | undefined {
    return Array.from(container.querySelectorAll<HTMLAnchorElement>('a'))
      .find((a) => a.getAttribute('aria-label') === label);
  }

  it('links users to official RNNoise model sources', () => {
    renderPanel();

    const xiphModels = linkByLabel('Open the Xiph RNNoise model data directory');
    const rnnoiseRepo = linkByLabel('Open the official RNNoise source repository');

    if (!xiphModels || !rnnoiseRepo) throw new Error('NR3 model source links were not rendered');
    expect(xiphModels.getAttribute('href')).toBe('https://media.xiph.org/rnnoise/models/');
    expect(xiphModels.getAttribute('target')).toBe('_blank');
    expect(rnnoiseRepo.getAttribute('href')).toBe('https://gitlab.xiph.org/xiph/rnnoise');
    expect(rnnoiseRepo.getAttribute('target')).toBe('_blank');
  });

  it('keeps model source links visible when NR3 is unavailable', () => {
    useConnectionStore.setState({
      wdspNr3RnnrAvailable: false,
      nr3ModelName: null,
    });

    renderPanel();

    expect(linkByLabel('Open the Xiph RNNoise model data directory')).toBeDefined();
    expect(linkByLabel('Open the official RNNoise source repository')).toBeDefined();
  });
});
