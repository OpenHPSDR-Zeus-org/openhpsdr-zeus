// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { beforeEach, describe, expect, it, vi } from 'vitest';
import '../../components/meters/__tests__/harness';
import { DEFAULT_WORKSPACE_LAYOUT } from '../../layout/defaultLayout';
import { HAMCLOCK_LAYOUT_NAME, useHamClockStore } from '../hamclock-store';
import { useLayoutStore } from '../layout-store';

describe('hamclock-store workspace persistence', () => {
  beforeEach(() => {
    useLayoutStore.setState({
      radioKey: 'radio-1',
      layouts: [
        {
          id: 'default',
          name: 'Default',
          layoutJson: JSON.stringify(DEFAULT_WORKSPACE_LAYOUT),
        },
      ],
      activeLayoutId: 'default',
      workspace: DEFAULT_WORKSPACE_LAYOUT,
      isLoaded: true,
    });
    (globalThis as unknown as { fetch: typeof fetch }).fetch = vi
      .fn()
      .mockResolvedValue({ ok: true, status: 200, json: async () => ({}) });
  });

  it('creates and persists a single-tile HamClock workspace immediately', () => {
    useHamClockStore.getState().openWorkspace();

    const state = useLayoutStore.getState();
    const layout = state.layouts.find((l) => l.name === HAMCLOCK_LAYOUT_NAME);
    expect(layout).toBeDefined();
    expect(state.activeLayoutId).toBe(layout!.id);
    expect(state.workspace.tiles).toEqual([
      {
        uid: 'tile-hamclock',
        panelId: 'hamclock',
        x: 0,
        y: 0,
        w: 24,
        h: 48,
      },
    ]);

    const fetchMock = globalThis.fetch as unknown as ReturnType<typeof vi.fn>;
    const puts = fetchMock.mock.calls.filter(
      (call) =>
        call[0] === '/api/ui/layouts' &&
        (call[1] as RequestInit | undefined)?.method === 'PUT',
    );
    expect(puts).toHaveLength(1);

    const body = JSON.parse((puts[0]![1] as RequestInit).body as string);
    const saved = JSON.parse(body.layoutJson);
    expect(body.radioKey).toBe('radio-1');
    expect(body.name).toBe(HAMCLOCK_LAYOUT_NAME);
    expect(saved.tiles).toEqual(state.workspace.tiles);
  });
});
