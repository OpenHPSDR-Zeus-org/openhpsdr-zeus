// SPDX-License-Identifier: GPL-2.0-or-later

export type PollDelay = number | false;
export type PollDelayResolver = PollDelay | (() => PollDelay);

export type EfficientPollingOptions = {
  intervalMs: PollDelayResolver;
  hiddenIntervalMs?: PollDelayResolver;
  leading?: boolean;
  isEnabled?: () => boolean;
  onError?: (error: unknown) => void;
};

function readDelay(value: PollDelayResolver | undefined): PollDelay | undefined {
  if (typeof value === 'function') return value();
  return value;
}

function normalizeDelay(value: PollDelay | undefined): PollDelay {
  if (value === false) return false;
  if (typeof value !== 'number' || !Number.isFinite(value)) return 1000;
  return Math.max(0, value);
}

function pageHidden(): boolean {
  return typeof document !== 'undefined' && document.hidden;
}

function nextDelay(options: EfficientPollingOptions): PollDelay {
  const hiddenDelay = pageHidden()
    ? readDelay(options.hiddenIntervalMs)
    : undefined;
  return normalizeDelay(hiddenDelay ?? readDelay(options.intervalMs));
}

function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError';
}

export function startEfficientPolling(
  task: (signal: AbortSignal) => void | Promise<void>,
  options: EfficientPollingOptions,
): () => void {
  let stopped = false;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let inFlight: AbortController | null = null;

  const clearTimer = () => {
    if (timer === null) return;
    clearTimeout(timer);
    timer = null;
  };

  const schedule = () => {
    if (stopped) return;
    clearTimer();
    const delay = nextDelay(options);
    if (delay === false) return;
    timer = setTimeout(run, delay);
  };

  const run = async () => {
    if (stopped || inFlight !== null) return;
    if (options.isEnabled && !options.isEnabled()) {
      schedule();
      return;
    }

    const controller = new AbortController();
    inFlight = controller;
    try {
      await task(controller.signal);
    } catch (error) {
      if (!controller.signal.aborted && !isAbortError(error)) options.onError?.(error);
    } finally {
      if (inFlight === controller) inFlight = null;
      schedule();
    }
  };

  const onVisibilityChange = () => {
    if (stopped) return;
    clearTimer();
    if (pageHidden()) {
      if (nextDelay(options) === false) inFlight?.abort();
      schedule();
      return;
    }
    void run();
  };

  if (typeof document !== 'undefined') {
    document.addEventListener('visibilitychange', onVisibilityChange);
  }

  if ((options.leading ?? true) && nextDelay(options) !== false) {
    void run();
  } else {
    schedule();
  }

  return () => {
    stopped = true;
    clearTimer();
    inFlight?.abort();
    inFlight = null;
    if (typeof document !== 'undefined') {
      document.removeEventListener('visibilitychange', onVisibilityChange);
    }
  };
}
