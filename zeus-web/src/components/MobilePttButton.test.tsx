// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';

import { render, act } from './meters/__tests__/harness';

const setMoxMock = vi.hoisted(() => vi.fn());
const ensureMicUplinkRunningMock = vi.hoisted(() => vi.fn());
const setMicUplinkTxForcedMock = vi.hoisted(() => vi.fn());
const waitForMicPcmTransportReadyMock = vi.hoisted(() => vi.fn());

vi.mock('../api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/client')>();
  return {
    ...actual,
    setMox: setMoxMock,
  };
});

vi.mock('../audio/mic-uplink-session', () => ({
  ensureMicUplinkRunning: ensureMicUplinkRunningMock,
  setMicUplinkTxForced: setMicUplinkTxForcedMock,
}));

vi.mock('../realtime/ws-client', () => ({
  waitForMicPcmTransportReady: waitForMicPcmTransportReadyMock,
}));

import { useConnectionStore } from '../state/connection-store';
import { useTxStore } from '../state/tx-store';
import { MobilePttButton } from './MobilePttButton';

function pointer(button: HTMLButtonElement, type: string): void {
  const ev = new Event(type, { bubbles: true, cancelable: true });
  Object.defineProperty(ev, 'pointerId', { value: 1 });
  button.dispatchEvent(ev);
}

async function flush(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
}

describe('MobilePttButton', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    setMoxMock.mockResolvedValue({});
    ensureMicUplinkRunningMock.mockResolvedValue(true);
    waitForMicPcmTransportReadyMock.mockResolvedValue(true);
    useConnectionStore.setState({ status: 'Connected' });
    useTxStore.setState({
      moxOn: false,
      localMicArmed: false,
      micError: null,
    });

    Object.defineProperty(HTMLButtonElement.prototype, 'setPointerCapture', {
      configurable: true,
      value: vi.fn(),
    });
    Object.defineProperty(HTMLButtonElement.prototype, 'hasPointerCapture', {
      configurable: true,
      value: vi.fn(() => true),
    });
    Object.defineProperty(HTMLButtonElement.prototype, 'releasePointerCapture', {
      configurable: true,
      value: vi.fn(),
    });
  });

  it('starts the mobile mic before keying MOX', async () => {
    const { container, unmount } = render(createElement(MobilePttButton));
    const button = container.querySelector('button') as HTMLButtonElement;

    await act(async () => {
      pointer(button, 'pointerdown');
      await flush();
    });

    expect(ensureMicUplinkRunningMock).toHaveBeenCalledTimes(1);
    expect(waitForMicPcmTransportReadyMock).toHaveBeenCalledTimes(1);
    expect(setMicUplinkTxForcedMock).toHaveBeenCalledWith(true);
    expect(setMoxMock).toHaveBeenCalledWith(true, expect.any(Object));
    const micCallOrder = ensureMicUplinkRunningMock.mock.invocationCallOrder[0]!;
    const transportCallOrder = waitForMicPcmTransportReadyMock.mock.invocationCallOrder[0]!;
    const forceCallOrder = setMicUplinkTxForcedMock.mock.invocationCallOrder[0]!;
    const moxCallOrder = setMoxMock.mock.invocationCallOrder[0]!;
    expect(micCallOrder).toBeLessThan(transportCallOrder);
    expect(transportCallOrder).toBeLessThan(forceCallOrder);
    expect(forceCallOrder).toBeLessThan(moxCallOrder);
    expect(useTxStore.getState().moxOn).toBe(true);
    expect(useTxStore.getState().localMicArmed).toBe(true);

    unmount();
  });

  it('does not key TX when the mobile mic cannot be opened', async () => {
    ensureMicUplinkRunningMock.mockRejectedValueOnce(new Error('denied'));
    const { container, unmount } = render(createElement(MobilePttButton));
    const button = container.querySelector('button') as HTMLButtonElement;

    await act(async () => {
      pointer(button, 'pointerdown');
      await flush();
    });

    expect(setMoxMock).not.toHaveBeenCalled();
    expect(setMicUplinkTxForcedMock).toHaveBeenCalledWith(false);
    expect(useTxStore.getState().moxOn).toBe(false);
    expect(useTxStore.getState().localMicArmed).toBe(false);

    unmount();
  });

  it('does not key TX when the mic websocket is not open', async () => {
    waitForMicPcmTransportReadyMock.mockResolvedValueOnce(false);
    const { container, unmount } = render(createElement(MobilePttButton));
    const button = container.querySelector('button') as HTMLButtonElement;

    await act(async () => {
      pointer(button, 'pointerdown');
      await flush();
    });

    expect(ensureMicUplinkRunningMock).toHaveBeenCalledTimes(1);
    expect(waitForMicPcmTransportReadyMock).toHaveBeenCalledTimes(1);
    expect(setMoxMock).not.toHaveBeenCalled();
    expect(setMicUplinkTxForcedMock).not.toHaveBeenCalledWith(true);
    expect(useTxStore.getState().moxOn).toBe(false);
    expect(useTxStore.getState().localMicArmed).toBe(false);

    unmount();
  });

  it('cancels a pending key-down if the finger releases before mic startup finishes', async () => {
    let resolveMic!: (running: boolean) => void;
    ensureMicUplinkRunningMock.mockReturnValueOnce(
      new Promise<boolean>((resolve) => {
        resolveMic = resolve;
      }),
    );
    const { container, unmount } = render(createElement(MobilePttButton));
    const button = container.querySelector('button') as HTMLButtonElement;

    act(() => {
      pointer(button, 'pointerdown');
    });
    act(() => {
      pointer(button, 'pointerup');
    });
    await act(async () => {
      resolveMic(true);
      await flush();
    });

    expect(setMoxMock).not.toHaveBeenCalled();
    expect(setMicUplinkTxForcedMock).toHaveBeenCalledWith(false);
    expect(useTxStore.getState().moxOn).toBe(false);
    expect(useTxStore.getState().localMicArmed).toBe(false);

    unmount();
  });
});
