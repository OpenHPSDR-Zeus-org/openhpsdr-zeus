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

  it('links users to the official Xiph RNNoise model directory', () => {
    renderPanel();

    const xiphModels = linkByLabel('Open the Xiph RNNoise model data directory');

    if (!xiphModels) throw new Error('NR3 model source link was not rendered');
    expect(xiphModels.getAttribute('href')).toBe('https://media.xiph.org/rnnoise/models/');
    expect(xiphModels.getAttribute('target')).toBe('_blank');

    // The gitlab.xiph.org source repo hosts no loadable weights, so it must not
    // be offered as a model source.
    expect(linkByLabel('Open the official RNNoise source repository')).toBeUndefined();
  });

  it('keeps the model source link visible when NR3 is unavailable', () => {
    useConnectionStore.setState({
      wdspNr3RnnrAvailable: false,
      nr3ModelName: null,
    });

    renderPanel();

    expect(linkByLabel('Open the Xiph RNNoise model data directory')).toBeDefined();
  });
});
