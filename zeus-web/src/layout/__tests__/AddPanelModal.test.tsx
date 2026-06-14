// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

/** @vitest-environment jsdom */

import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { render, act } from '../../components/meters/__tests__/harness';
import { AddPanelModal } from '../AddPanelModal';
import { PANELS } from '../panels';

function setup(existingPanels: Set<string> = new Set()) {
  const onAdd = vi.fn();
  const onClose = vi.fn();
  const result = render(
    createElement(AddPanelModal, {
      existingPanels,
      onAdd,
      onClose,
    }),
  );
  return { ...result, onAdd, onClose };
}

describe('AddPanelModal', () => {
  it('marks the dialog modal, focuses search, and closes on Escape', () => {
    const { container, unmount, onClose } = setup();
    const dialog = container.querySelector('[role="dialog"]') as HTMLElement;
    const input = container.querySelector(
      '.add-panel-search',
    ) as HTMLInputElement;
    expect(dialog).not.toBeNull();
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(input).not.toBeNull();
    expect(document.activeElement).toBe(input);

    act(() => {
      document.dispatchEvent(
        new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
      );
    });

    expect(onClose).toHaveBeenCalledTimes(1);
    unmount();
  });

  it('renders the category rail with All + every PanelCategory', () => {
    const { container, unmount } = setup();
    const rail = container.querySelector(
      '[data-testid="add-panel-rail"]',
    ) as HTMLElement;
    expect(rail).not.toBeNull();
    expect(
      rail.querySelector('[data-testid="add-panel-category-all"]'),
    ).not.toBeNull();
    for (const cat of [
      'spectrum',
      'vfo',
      'meters',
      'dsp',
      'log',
      'tools',
      'amplifiers',
      'controls',
    ]) {
      expect(
        rail.querySelector(`[data-testid="add-panel-category-${cat}"]`),
      ).not.toBeNull();
    }
    unmount();
  });

  it('"All" filter shows every panel by default', () => {
    const { container, unmount } = setup();
    const cards = container.querySelectorAll(
      '[data-testid="add-panel-cards"] .add-panel-card',
    );
    expect(cards.length).toBe(Object.values(PANELS).length);
    unmount();
  });

  it('clicking a category filters the right pane', () => {
    const { container, unmount } = setup();
    const metersBtn = container.querySelector(
      '[data-testid="add-panel-category-meters"]',
    ) as HTMLButtonElement;
    act(() => {
      metersBtn.click();
    });
    const cards = container.querySelectorAll(
      '[data-testid="add-panel-cards"] .add-panel-card',
    );
    const expectedIds = Object.values(PANELS)
      .filter((panel) => panel.category === 'meters')
      .map((panel) => panel.id)
      .sort();
    const ids = Array.from(cards).map((c) =>
      c.getAttribute('data-panel-id'),
    ).filter((id): id is string => id !== null).sort();
    expect(ids).toEqual(expectedIds);
    expect(ids).toContain('txfidelity');
    unmount();
  });

  it('clicking a card invokes onAdd(panelId) and onClose', () => {
    const { container, unmount, onAdd, onClose } = setup();
    const metersBtn = container.querySelector(
      '[data-testid="add-panel-category-meters"]',
    ) as HTMLButtonElement;
    act(() => {
      metersBtn.click();
    });
    const card = container.querySelector(
      '.add-panel-card[data-panel-id="metergroup"]',
    ) as HTMLButtonElement;
    expect(card).not.toBeNull();
    act(() => {
      card.click();
    });
    expect(onAdd).toHaveBeenCalledWith('metergroup');
    expect(onClose).toHaveBeenCalled();
    unmount();
  });

  it('hides single-instance panels that already exist; keeps multi-instance ones', () => {
    // 'cw' is single-instance; 'metergroup' is multiInstance.
    const { container, unmount } = setup(new Set(['cw', 'metergroup']));
    const ids = Array.from(
      container.querySelectorAll(
        '[data-testid="add-panel-cards"] .add-panel-card',
      ),
    ).map((c) => c.getAttribute('data-panel-id'));
    expect(ids).not.toContain('cw');
    expect(ids).toContain('metergroup');
    unmount();
  });

  it('shows the "+ Add another" badge on a multi-instance card already in use', () => {
    const { container, unmount } = setup(new Set(['metergroup']));
    const card = container.querySelector(
      '.add-panel-card[data-panel-id="metergroup"]',
    ) as HTMLElement;
    expect(card).not.toBeNull();
    expect(card.textContent).toContain('Add another');
    unmount();
  });

  it('search term filters across name + tags', () => {
    const { container, unmount } = setup();
    const input = container.querySelector(
      '.add-panel-search',
    ) as HTMLInputElement;
    expect(input).not.toBeNull();
    act(() => {
      const setter = Object.getOwnPropertyDescriptor(
        window.HTMLInputElement.prototype,
        'value',
      )!.set!;
      setter.call(input, 'azimuth');
      input.dispatchEvent(new Event('input', { bubbles: true }));
    });
    const cards = container.querySelectorAll(
      '[data-testid="add-panel-cards"] .add-panel-card',
    );
    expect(cards.length).toBe(1);
    expect(cards[0]?.getAttribute('data-panel-id')).toBe('azimuth');
    unmount();
  });
});
