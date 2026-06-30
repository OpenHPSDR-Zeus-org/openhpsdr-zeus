// SPDX-License-Identifier: GPL-2.0-or-later
//
// Multi-select / focus invariants for the uniform RX1-RX6 receiver model.
// The selection drives ganged toolbar controls; the focused receiver is the
// primary (always a member) whose values the controls display.

import { beforeEach, describe, expect, it } from 'vitest';
import type { RadioStateDto } from '../api/client';
import { useConnectionStore } from './connection-store';

function reset() {
  useConnectionStore.setState({
    status: 'Disconnected',
    connectedProtocol: null,
    focusedRxIndex: 0,
    selectedRxIndices: [0],
    rxFocus: 'A',
  });
}

describe('connection-store multi-select', () => {
  beforeEach(reset);

  it('defaults to RX1 focused and solely selected', () => {
    const s = useConnectionStore.getState();
    expect(s.focusedRxIndex).toBe(0);
    expect(s.selectedRxIndices).toEqual([0]);
  });

  it('plain focus collapses the selection to that receiver', () => {
    useConnectionStore.getState().setFocusedRxIndex(3);
    const s = useConnectionStore.getState();
    expect(s.focusedRxIndex).toBe(3);
    expect(s.selectedRxIndices).toEqual([3]);
  });

  it('toggle adds (and focuses) a receiver, kept sorted', () => {
    useConnectionStore.getState().setFocusedRxIndex(2);
    useConnectionStore.getState().toggleRxSelection(5);
    useConnectionStore.getState().toggleRxSelection(3);
    const s = useConnectionStore.getState();
    expect(s.selectedRxIndices).toEqual([2, 3, 5]);
    expect(s.focusedRxIndex).toBe(3); // last toggled-on receiver takes focus
  });

  it('toggling off a non-focused receiver leaves focus put', () => {
    useConnectionStore.getState().setSelectedRxIndices([1, 2, 4]);
    useConnectionStore.setState({ focusedRxIndex: 2 });
    useConnectionStore.getState().toggleRxSelection(4);
    const s = useConnectionStore.getState();
    expect(s.selectedRxIndices).toEqual([1, 2]);
    expect(s.focusedRxIndex).toBe(2);
  });

  it('toggling off the focused receiver moves focus to the lowest remaining', () => {
    useConnectionStore.getState().setSelectedRxIndices([1, 2, 4]);
    useConnectionStore.setState({ focusedRxIndex: 2 });
    useConnectionStore.getState().toggleRxSelection(2);
    const s = useConnectionStore.getState();
    expect(s.selectedRxIndices).toEqual([1, 4]);
    expect(s.focusedRxIndex).toBe(1);
  });

  it('never empties the selection — removing the last one is a no-op', () => {
    useConnectionStore.getState().setFocusedRxIndex(3); // [3]
    useConnectionStore.getState().toggleRxSelection(3);
    const s = useConnectionStore.getState();
    expect(s.selectedRxIndices).toEqual([3]);
    expect(s.focusedRxIndex).toBe(3);
  });

  it('setSelectedRxIndices keeps focus if it survives, else picks the lowest', () => {
    useConnectionStore.setState({ focusedRxIndex: 4, selectedRxIndices: [4] });
    useConnectionStore.getState().setSelectedRxIndices([3, 1]);
    let s = useConnectionStore.getState();
    expect(s.selectedRxIndices).toEqual([1, 3]); // sorted
    expect(s.focusedRxIndex).toBe(1); // 4 didn't survive → lowest

    useConnectionStore.getState().setSelectedRxIndices([1, 5]);
    s = useConnectionStore.getState();
    expect(s.focusedRxIndex).toBe(1); // 1 survived → kept
  });

  it('mirrors rxFocus for the RX1/RX2 stitched view', () => {
    useConnectionStore.getState().setFocusedRxIndex(1);
    expect(useConnectionStore.getState().rxFocus).toBe('B');
    useConnectionStore.getState().setFocusedRxIndex(0);
    expect(useConnectionStore.getState().rxFocus).toBe('A');
  });

  it('hydrates connectedProtocol from StateDto', () => {
    const base = useConnectionStore.getState() as unknown as RadioStateDto;

    useConnectionStore.getState().applyState({
      ...base,
      status: 'Connected',
      connectedProtocol: 'P2',
    });

    expect(useConnectionStore.getState().connectedProtocol).toBe('P2');

    useConnectionStore.getState().applyState({
      ...base,
      status: 'Disconnected',
      connectedProtocol: null,
    });

    expect(useConnectionStore.getState().connectedProtocol).toBeNull();
  });
});
