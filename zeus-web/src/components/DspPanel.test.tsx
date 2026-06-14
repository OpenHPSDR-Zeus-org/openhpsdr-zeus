// SPDX-License-Identifier: GPL-2.0-or-later
//
// The visible DSP SMART button must be a real arming control, not a silent
// one-shot. Operators expect it to visibly select and expose the automation
// status row when clicked.

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';

import { NR_CONFIG_DEFAULT } from '../api/client';
import { useConnectionStore } from '../state/connection-store';
import { useDisplayStore } from '../state/display-store';
import { useSmartNrStore } from '../state/smart-nr-store';
import { DspPanel } from './DspPanel';

describe('DspPanel SMART control', () => {
  let container: HTMLDivElement;
  let root: Root;

  beforeEach(() => {
    useSmartNrStore.getState().resetSettings();
    useConnectionStore.setState({
      status: 'Connected',
      mode: 'USB',
      nr: { ...NR_CONFIG_DEFAULT },
    });
    useDisplayStore.setState({
      panDb: null,
      panValid: false,
      lastSeq: 0,
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

  it('arms and disarms Smart NR from the panel button', () => {
    act(() => {
      root.render(<DspPanel />);
    });

    const smart = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((b) => b.textContent?.trim() === 'SMART');

    expect(smart).toBeDefined();
    expect(smart!.getAttribute('aria-pressed')).toBe('false');
    expect(smart!.className).not.toContain('active');

    act(() => {
      smart!.click();
    });

    expect(useSmartNrStore.getState().automationMode).toBe('auto');
    expect(smart!.getAttribute('aria-pressed')).toBe('true');
    expect(smart!.className).toContain('active');
    expect(container.textContent).toContain('AUTO');
    expect(container.textContent).toContain('WAIT');

    act(() => {
      smart!.click();
    });

    expect(useSmartNrStore.getState().automationMode).toBe('manual');
    expect(smart!.getAttribute('aria-pressed')).toBe('false');
    expect(smart!.className).not.toContain('active');
  });

  it('applies a suggested Smart NR profile from the status row', () => {
    const suggested = { ...NR_CONFIG_DEFAULT, nrMode: 'Emnr' as const, emnrPost2Factor: 18 };
    useSmartNrStore.getState().setAutomationMode('suggest');
    useSmartNrStore.getState().setStatus({
      atUtc: '2026-06-14T00:00:00.000Z',
      profile: 'NR2',
      reason: 'SSB noise profile',
      maxSnrDb: 16,
      occupancyPct: 24,
      peakCount: 7,
      pending: false,
      applied: false,
      nr: suggested,
    });

    act(() => {
      root.render(<DspPanel />);
    });

    const apply = Array.from(container.querySelectorAll<HTMLButtonElement>('button'))
      .find((b) => b.textContent?.trim() === 'APPLY');

    expect(apply).toBeDefined();

    act(() => {
      apply!.click();
    });

    expect(useConnectionStore.getState().nr.nrMode).toBe('Emnr');
    expect(useSmartNrStore.getState().status?.applied).toBe(true);
    expect(container.textContent).toContain('APPLIED');
  });
});
