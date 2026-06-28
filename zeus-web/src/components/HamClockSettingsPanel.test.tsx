// SPDX-License-Identifier: GPL-2.0-or-later
//
// HamClockSettingsPanel — "Push DX to HamClock" section test. Verifies the
// outbound DX-push form: default OFF, opting in reveals trigger/target/host/port,
// selecting the bundled target surfaces a clear "map won't auto-pin" notice (the
// bundled OpenHamClock has no set-DX web command, #1110), and SAVE persists the
// config. Harness import first installs the localStorage polyfill + act flag
// before any store loads; store methods are stubbed so no real fetch fires.

import { describe, expect, it, beforeEach, vi } from 'vitest';
import { createElement } from 'react';
import { act, render } from './meters/__tests__/harness';
import { HamClockSettingsPanel } from './HamClockSettingsPanel';
import { useHamClockStore, type HamClockPushConfig } from '../state/hamclock-store';

const OFF_PUSH: HamClockPushConfig = {
  enabled: false,
  trigger: 'on-click',
  target: 'external',
  externalHost: '',
  externalPort: 8080,
};

function setSelectValue(select: HTMLSelectElement, value: string) {
  const setter = Object.getOwnPropertyDescriptor(
    window.HTMLSelectElement.prototype,
    'value',
  )!.set!;
  act(() => {
    setter.call(select, value);
    select.dispatchEvent(new Event('change', { bubbles: true }));
  });
}

function setInputValue(input: HTMLInputElement, value: string) {
  const setter = Object.getOwnPropertyDescriptor(
    window.HTMLInputElement.prototype,
    'value',
  )!.set!;
  act(() => {
    setter.call(input, value);
    input.dispatchEvent(new Event('input', { bubbles: true }));
  });
}

function clickByText(container: HTMLElement, text: string) {
  const btn = Array.from(container.querySelectorAll('button')).find((b) => b.textContent === text);
  expect(btn, `button "${text}"`).toBeTruthy();
  act(() => (btn as HTMLButtonElement).click());
}

describe('HamClockSettingsPanel — Push DX', () => {
  beforeEach(() => {
    act(() => {
      // Stub every fetch-backed store method so the panel mounts cleanly.
      useHamClockStore.setState({
        pushConfig: { ...OFF_PUSH },
        loadStatus: async () => {},
        loadPushConfig: async () => {},
        savePushConfig: async () => {},
      } as never);
    });
  });

  it('renders the push section OFF by default with the controls hidden', () => {
    const { container, unmount } = render(createElement(HamClockSettingsPanel));
    expect(container.textContent).toContain('Push DX to HamClock');
    const toggle = container.querySelector(
      'button[aria-label="Push DX to HamClock"]',
    ) as HTMLButtonElement;
    expect(toggle.getAttribute('aria-checked')).toBe('false');
    // Trigger/target/host controls only appear once enabled.
    expect(container.querySelector('select[aria-label="Push trigger"]')).toBeNull();
    unmount();
  });

  it('opting in reveals trigger/target/host/port with both targets selectable', () => {
    const { container, unmount } = render(createElement(HamClockSettingsPanel));
    const toggle = container.querySelector(
      'button[aria-label="Push DX to HamClock"]',
    ) as HTMLButtonElement;
    act(() => toggle.click());
    expect(container.querySelector('select[aria-label="Push trigger"]')).toBeTruthy();
    expect(container.querySelector('input[aria-label="HamClock host"]')).toBeTruthy();
    // Both targets are present and selectable; the bundled one is annotated, not
    // disabled, so a persisted bundled config still surfaces its limitation.
    const targetSel = container.querySelector('select[aria-label="Push target"]') as HTMLSelectElement;
    const bundled = Array.from(targetSel.options).find((o) => o.value === 'bundled');
    expect(bundled).toBeTruthy();
    expect(bundled?.disabled).toBe(false);
    unmount();
  });

  it('shows the "map won\'t auto-pin" notice only when the bundled target is selected', () => {
    const { container, unmount } = render(createElement(HamClockSettingsPanel));
    const toggle = container.querySelector(
      'button[aria-label="Push DX to HamClock"]',
    ) as HTMLButtonElement;
    act(() => toggle.click());

    // External (default) target: no limitation notice.
    expect(container.querySelector('[data-testid="bundled-dx-notice"]')).toBeNull();

    setSelectValue(
      container.querySelector('select[aria-label="Push target"]') as HTMLSelectElement,
      'bundled',
    );
    const notice = container.querySelector('[data-testid="bundled-dx-notice"]');
    expect(notice).toBeTruthy();
    expect(notice?.textContent).toContain("can't receive auto-pins");
    // The bundled target hides the external host/port inputs.
    expect(container.querySelector('input[aria-label="HamClock host"]')).toBeNull();

    // Switching back to external removes the notice again.
    setSelectValue(
      container.querySelector('select[aria-label="Push target"]') as HTMLSelectElement,
      'external',
    );
    expect(container.querySelector('[data-testid="bundled-dx-notice"]')).toBeNull();
    unmount();
  });

  it('SAVE persists the full external push config to the backend', () => {
    const savePushConfig = vi.fn(async () => {});
    act(() => {
      useHamClockStore.setState({ savePushConfig } as never);
    });
    const { container, unmount } = render(createElement(HamClockSettingsPanel));
    const toggle = container.querySelector(
      'button[aria-label="Push DX to HamClock"]',
    ) as HTMLButtonElement;
    act(() => toggle.click());

    setSelectValue(
      container.querySelector('select[aria-label="Push trigger"]') as HTMLSelectElement,
      'on-active-QSO',
    );
    setInputValue(container.querySelector('input[aria-label="HamClock host"]') as HTMLInputElement, '10.0.0.5');
    setInputValue(container.querySelector('input[aria-label="HamClock port"]') as HTMLInputElement, '8080');

    clickByText(container, 'SAVE');
    expect(savePushConfig).toHaveBeenCalledWith(
      expect.objectContaining({
        enabled: true,
        trigger: 'on-active-QSO',
        target: 'external',
        externalHost: '10.0.0.5',
        externalPort: 8080,
      }),
    );
    unmount();
  });
});
