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
      nr3UsingBundledDefault: false,
    });
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  function hintText(): string {
    return container.querySelector('.nr-settings__hint')?.textContent ?? '';
  }

  function removeButton(): HTMLButtonElement | undefined {
    return Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((b) => b.textContent?.trim() === 'Remove model');
  }

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

  it('shows the bundled-default state and hides Remove when no operator model is installed', () => {
    useConnectionStore.setState({
      wdspNr3RnnrAvailable: true,
      nr3ModelName: 'RNNoise (bundled default)',
      nr3UsingBundledDefault: true,
    });

    renderPanel();

    expect(hintText()).toContain('bundled default');
    // Nothing to remove when running on the shipped default.
    expect(removeButton()).toBeUndefined();
  });

  it('shows the installed model name and a Remove action for an operator model', () => {
    useConnectionStore.setState({
      wdspNr3RnnrAvailable: true,
      nr3ModelName: 'hf-voice.rnnn',
      nr3UsingBundledDefault: false,
    });

    renderPanel();

    expect(hintText()).toContain('hf-voice.rnnn');
    const btn = removeButton();
    if (!btn) throw new Error('Remove button not rendered for an operator model');
    expect(btn.getAttribute('title')).toContain('revert to the bundled default');
  });
});
