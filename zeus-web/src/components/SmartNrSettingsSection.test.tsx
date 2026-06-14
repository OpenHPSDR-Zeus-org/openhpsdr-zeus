// SPDX-License-Identifier: GPL-2.0-or-later
//
// Smart NR settings must drive the persisted automation store; otherwise the
// panadapter-driven controller cannot be armed from Settings > DSP.

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { SmartNrSettingsSection } from './SmartNrSettingsSection';
import { NR_CONFIG_DEFAULT } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useSmartNrStore } from '../state/smart-nr-store';

describe('SmartNrSettingsSection', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    useSmartNrStore.getState().resetSettings();
    useConnectionStore.setState({
      status: 'Connected',
      nr: { ...NR_CONFIG_DEFAULT },
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
    useSmartNrStore.getState().resetSettings();
    useConnectionStore.setState({
      status: 'Disconnected',
      nr: { ...NR_CONFIG_DEFAULT },
    });
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

  it('applies the current suggested profile', () => {
    const suggested = { ...NR_CONFIG_DEFAULT, nrMode: 'Sbnr' as const, nr4ReductionAmount: 13 };
    useSmartNrStore.getState().setAutomationMode('suggest');
    useSmartNrStore.getState().setStatus({
      atUtc: '2026-06-14T00:00:00.000Z',
      profile: 'NR4',
      reason: 'Weak narrow-signal profile',
      maxSnrDb: 14,
      occupancyPct: 3,
      peakCount: 1,
      pending: false,
      applied: false,
      nr: suggested,
    });

    act(() => {
      root.render(<SmartNrSettingsSection />);
    });

    const apply = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((b) => b.textContent?.trim() === 'Apply');

    expect(apply).toBeDefined();

    act(() => {
      apply!.click();
    });

    expect(useConnectionStore.getState().nr.nrMode).toBe('Sbnr');
    expect(useSmartNrStore.getState().status?.applied).toBe(true);
  });
});
