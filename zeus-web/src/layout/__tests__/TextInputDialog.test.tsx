// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

/** @vitest-environment jsdom */

import { createElement } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { act, render } from '../../components/meters/__tests__/harness';
import { TextInputDialog } from '../TextInputDialog';

function textInputDialog(onCancel: () => void) {
  return createElement(
    TextInputDialog,
    {
      title: 'Save profile',
      label: 'Profile name',
      initialValue: 'Ragchew',
      confirmLabel: 'Save Profile',
      onSubmit: vi.fn(),
      onCancel,
    },
    createElement('p', null, 'Save the current Audio Suite chain.'),
  );
}

describe('TextInputDialog', () => {
  it('does not reselect the input when parent callbacks change', () => {
    const { container, rerender, unmount } = render(textInputDialog(vi.fn()));
    const input = container.querySelector('input') as HTMLInputElement;

    expect(document.activeElement).toBe(input);
    expect(input.selectionStart).toBe(0);
    expect(input.selectionEnd).toBe('Ragchew'.length);

    act(() => {
      const valueSetter = Object.getOwnPropertyDescriptor(
        window.HTMLInputElement.prototype,
        'value',
      )!.set!;
      valueSetter.call(input, 'Ragchewer');
      input.dispatchEvent(new Event('input', { bubbles: true }));
      input.setSelectionRange('Ragchewer'.length, 'Ragchewer'.length);
    });

    rerender(textInputDialog(vi.fn()));

    expect(input.value).toBe('Ragchewer');
    expect(input.selectionStart).toBe('Ragchewer'.length);
    expect(input.selectionEnd).toBe('Ragchewer'.length);
    unmount();
  });

  it('uses the latest close callback for Escape after a rerender', () => {
    const firstCancel = vi.fn();
    const secondCancel = vi.fn();
    const { rerender, unmount } = render(textInputDialog(firstCancel));

    rerender(textInputDialog(secondCancel));

    act(() => {
      document.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
      );
    });

    expect(firstCancel).not.toHaveBeenCalled();
    expect(secondCancel).toHaveBeenCalledTimes(1);
    unmount();
  });
});
