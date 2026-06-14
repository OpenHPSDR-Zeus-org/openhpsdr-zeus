// SPDX-License-Identifier: GPL-2.0-or-later
//
// Smart NR settings must drive the persisted automation store; otherwise the
// panadapter-driven controller cannot be armed from Settings > DSP.

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { SmartNrSettingsSection } from './SmartNrSettingsSection';
import { useSmartNrStore } from '../state/smart-nr-store';

describe('SmartNrSettingsSection', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    useSmartNrStore.getState().resetSettings();
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });

  afterEach(() => {
    act(() => {
      root.unmount();
    });
    container.remove();
    useSmartNrStore.getState().resetSettings();
  });

  it('toggles automation mode from the settings buttons', () => {
    act(() => {
      root.render(<SmartNrSettingsSection />);
    });

    const buttons = Array.from(container.querySelectorAll<HTMLButtonElement>('.sig-profile-btn'));
    const suggest = buttons.find((b) => b.textContent?.trim() === 'Suggest');
    const manual = buttons.find((b) => b.textContent?.trim() === 'Manual');

    expect(suggest).toBeDefined();
    expect(manual).toBeDefined();
    expect(manual!.getAttribute('aria-pressed')).toBe('true');

    act(() => {
      suggest!.click();
    });

    expect(useSmartNrStore.getState().automationMode).toBe('suggest');
    expect(suggest!.getAttribute('aria-pressed')).toBe('true');
    expect(container.textContent).toContain('SUGGEST');
  });
});
