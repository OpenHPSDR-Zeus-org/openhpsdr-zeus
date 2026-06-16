// SPDX-License-Identifier: GPL-2.0-or-later

import { createRef } from 'react';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { useDisplayStore } from '../state/display-store';
import { useNotchStore } from '../state/notch-store';
import { NotchOverlay } from './NotchOverlay';

const CENTER_HZ = 14_200_000;

describe('NotchOverlay edit gating', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    useDisplayStore.setState({
      centerHz: BigInt(CENTER_HZ),
      hzPerPixel: 100,
      panDb: new Float32Array(256),
    });
    useNotchStore.setState({
      notches: [{ id: 'n1', centerHz: CENTER_HZ, widthHz: 1_000 }],
      armed: false,
      pending: null,
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
    useNotchStore.setState({ notches: [], armed: false, pending: null });
    useDisplayStore.setState({ panDb: null, hzPerPixel: 0 });
  });

  it('does not expose notch edit controls until NOTCH is armed', () => {
    const containerRef = createRef<HTMLDivElement>();

    act(() => {
      root.render(<NotchOverlay interactive resizable containerRef={containerRef} />);
    });

    expect(container.querySelector('button')).toBeNull();
    expect(container.querySelectorAll('[title="Drag to resize notch"]')).toHaveLength(0);

    act(() => {
      useNotchStore.setState({ armed: true });
    });

    expect(container.querySelector('button')).not.toBeNull();
    expect(container.querySelectorAll('[title="Drag to resize notch"]')).toHaveLength(2);
  });
});
