// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { startEfficientPolling } from './efficient-polling';

describe('startEfficientPolling', () => {
  let hidden = false;
  let ownHiddenDescriptor: PropertyDescriptor | undefined;

  beforeEach(() => {
    vi.useFakeTimers();
    hidden = false;
    ownHiddenDescriptor = Object.getOwnPropertyDescriptor(document, 'hidden');
    Object.defineProperty(document, 'hidden', {
      configurable: true,
      get: () => hidden,
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    if (ownHiddenDescriptor) {
      Object.defineProperty(document, 'hidden', ownHiddenDescriptor);
    } else {
      Reflect.deleteProperty(document, 'hidden');
    }
  });

  it('does not overlap slow tasks', async () => {
    let resolveFirst!: () => void;
    const task = vi.fn(() => {
      if (task.mock.calls.length === 1) {
        return new Promise<void>((resolve) => {
          resolveFirst = resolve;
        });
      }
      return undefined;
    });

    const stop = startEfficientPolling(task, { intervalMs: 100 });

    expect(task).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(500);
    expect(task).toHaveBeenCalledTimes(1);

    resolveFirst();
    await Promise.resolve();
    await vi.advanceTimersByTimeAsync(100);
    expect(task).toHaveBeenCalledTimes(2);

    stop();
  });

  it('aborts an in-flight task when stopped', () => {
    const signals: AbortSignal[] = [];
    const task = vi.fn((nextSignal: AbortSignal) => {
      signals.push(nextSignal);
      return new Promise<void>(() => {
        /* held open until stop() aborts it */
      });
    });

    const stop = startEfficientPolling(task, { intervalMs: 100 });

    expect(task).toHaveBeenCalledTimes(1);
    expect(signals[0]?.aborted).toBe(false);

    stop();

    expect(signals[0]?.aborted).toBe(true);
  });

  it('aborts an in-flight task when hidden polling is disabled', () => {
    const signals: AbortSignal[] = [];
    const task = vi.fn((nextSignal: AbortSignal) => {
      signals.push(nextSignal);
      return new Promise<void>(() => {
        /* held open until visibility aborts it */
      });
    });

    const stop = startEfficientPolling(task, {
      intervalMs: 100,
      hiddenIntervalMs: false,
    });

    expect(signals[0]?.aborted).toBe(false);

    hidden = true;
    document.dispatchEvent(new Event('visibilitychange'));

    expect(signals[0]?.aborted).toBe(true);
    stop();
  });

  it('pauses while hidden and resumes on visibilitychange', async () => {
    hidden = true;
    const task = vi.fn();
    const stop = startEfficientPolling(task, {
      intervalMs: 100,
      hiddenIntervalMs: false,
    });

    expect(task).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(500);
    expect(task).not.toHaveBeenCalled();

    hidden = false;
    document.dispatchEvent(new Event('visibilitychange'));

    expect(task).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(100);
    expect(task).toHaveBeenCalledTimes(2);

    stop();
  });
});
