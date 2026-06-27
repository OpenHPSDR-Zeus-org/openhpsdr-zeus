// SPDX-License-Identifier: GPL-2.0-or-later
//
// Ft8PopBody REUSE-WIRING test: clicking a station in the decode list must feed
// the operator's EXISTING QRZ panel (runQrzLookup from the workspace context) —
// no new QRZ panel is built. Harness import first installs the localStorage
// polyfill + act flag before any store loads.

import { describe, expect, it, beforeEach, vi } from 'vitest';
import { createElement } from 'react';
import { act, render } from '../../components/meters/__tests__/harness';
import { WorkspaceContext, type WorkspaceCtx } from '../WorkspaceContext';
import { Ft8PopBody } from './Ft8PopBody';
import { useFt8Store, type Ft8Row } from '../../state/ft8-store';
import { useOperatorStore } from '../../state/operator-store';
import { useLayoutStore } from '../../state/layout-store';

function row(text: string): Ft8Row {
  return {
    id: `r-${text}`,
    receiver: 0,
    protocol: 'FT8',
    slotStartUnixMs: 0,
    snrDb: -8,
    dtSec: 0.2,
    freqHz: 1500,
    score: 0,
    text,
  };
}

function renderWithQrz(runQrzLookup: (cs?: string) => void) {
  const ctx = { runQrzLookup } as unknown as WorkspaceCtx;
  return render(
    createElement(WorkspaceContext.Provider, { value: ctx }, createElement(Ft8PopBody)),
  );
}

describe('Ft8PopBody → existing QRZ panel wiring', () => {
  beforeEach(() => {
    act(() => {
      useFt8Store.setState({ open: true, protocol: 'FT8', band: '20m', rows: [row('CQ K1ABC FN42')] });
      // Operator call must be set or click-to-call bails to the settings view.
      useOperatorStore.setState({ resolvedCall: 'MYCALL', resolvedGrid: 'FN31' } as never);
    });
  });

  it('clicking a decoded station populates the existing QRZ panel with its callsign', () => {
    const runQrzLookup = vi.fn();
    const { container, unmount } = renderWithQrz(runQrzLookup);

    const targetRow = Array.from(container.querySelectorAll('tbody tr')).find((tr) =>
      tr.textContent?.includes('K1ABC'),
    );
    expect(targetRow).toBeTruthy();

    act(() => {
      targetRow!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });

    expect(runQrzLookup).toHaveBeenCalledWith('K1ABC');
    unmount();
  });

  it('the settings gear opens the main Settings → Zeus Digital section', () => {
    act(() => {
      useLayoutStore.setState({ settingsViewOpen: false, settingsInitialTab: undefined });
    });
    const { container, unmount } = renderWithQrz(vi.fn());
    const gear = container.querySelector(
      'button[aria-label="Zeus Digital settings"]',
    ) as HTMLButtonElement;
    expect(gear).toBeTruthy();
    act(() => {
      gear.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });
    expect(useLayoutStore.getState().settingsViewOpen).toBe(true);
    expect(useLayoutStore.getState().settingsInitialTab).toBe('zeus-digital');
    unmount();
  });

  it('clicking a decode with NO operator call opens Settings → Zeus Digital (not the macros view)', () => {
    act(() => {
      useOperatorStore.setState({ resolvedCall: '', resolvedGrid: '' } as never);
      useLayoutStore.setState({ settingsViewOpen: false, settingsInitialTab: undefined });
    });
    const { container, unmount } = renderWithQrz(vi.fn());

    const targetRow = Array.from(container.querySelectorAll('tbody tr')).find((tr) =>
      tr.textContent?.includes('K1ABC'),
    );
    expect(targetRow).toBeTruthy();
    act(() => {
      targetRow!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });

    // With no station call we can't form TX messages — jump to the menu to set it.
    expect(useLayoutStore.getState().settingsViewOpen).toBe(true);
    expect(useLayoutStore.getState().settingsInitialTab).toBe('zeus-digital');
    // The in-pop view did NOT switch to the macros editor.
    expect(container.textContent).not.toContain('CQ message');
    unmount();
  });

  it('the ✎ button opens the in-pop message editor (editing stays in the pop-out)', () => {
    act(() => {
      useLayoutStore.setState({ settingsViewOpen: false, settingsInitialTab: undefined });
    });
    const { container, unmount } = renderWithQrz(vi.fn());
    const msg = container.querySelector(
      'button[aria-label="Edit messages"]',
    ) as HTMLButtonElement;
    expect(msg).toBeTruthy();
    act(() => {
      msg.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });
    expect(container.textContent).toContain('CQ message');
    // Main settings menu was NOT opened — editing is local to the pop-out.
    expect(useLayoutStore.getState().settingsViewOpen).toBe(false);
    unmount();
  });
});
