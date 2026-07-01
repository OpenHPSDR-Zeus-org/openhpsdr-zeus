// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.

export type WindowsFirewallStatus = {
  supported: boolean;
  canApply: boolean;
  localRequest: boolean;
  ruleName: string;
  programPath: string | null;
  message: string;
};

export type WindowsFirewallApplyResult = {
  supported: boolean;
  applied: boolean;
  elevationAttempted: boolean;
  elevationCanceled: boolean;
  ruleName: string;
  programPath: string | null;
  message: string;
};

export class WindowsFirewallApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'WindowsFirewallApiError';
  }
}

function asString(v: unknown, fallback = ''): string {
  return typeof v === 'string' ? v : fallback;
}

function normalizeStatus(raw: unknown): WindowsFirewallStatus {
  const o = (raw ?? {}) as Record<string, unknown>;
  return {
    supported: o.supported === true,
    canApply: o.canApply === true,
    localRequest: o.localRequest !== false,
    ruleName: asString(o.ruleName, 'OpenHPSDR Zeus (HPSDR receive)'),
    programPath: typeof o.programPath === 'string' && o.programPath.trim() ? o.programPath : null,
    message: asString(o.message),
  };
}

function normalizeApplyResult(raw: unknown): WindowsFirewallApplyResult {
  const o = (raw ?? {}) as Record<string, unknown>;
  return {
    supported: o.supported === true,
    applied: o.applied === true,
    elevationAttempted: o.elevationAttempted === true,
    elevationCanceled: o.elevationCanceled === true,
    ruleName: asString(o.ruleName, 'OpenHPSDR Zeus (HPSDR receive)'),
    programPath: typeof o.programPath === 'string' && o.programPath.trim() ? o.programPath : null,
    message: asString(o.message),
  };
}

async function jsonFetch<T>(
  input: RequestInfo,
  init: RequestInit | undefined,
  normalize: (raw: unknown) => T,
): Promise<T> {
  const res = await fetch(input, init);
  const raw = await res.json().catch(() => ({}));
  if (!res.ok) {
    const message =
      raw &&
      typeof raw === 'object' &&
      'error' in raw &&
      typeof (raw as { error: unknown }).error === 'string'
        ? (raw as { error: string }).error
        : `${res.status} ${res.statusText}`;
    throw new WindowsFirewallApiError(res.status, message);
  }
  return normalize(raw);
}

export function getWindowsFirewallStatus(signal?: AbortSignal): Promise<WindowsFirewallStatus> {
  return jsonFetch('/api/system/windows-firewall', { signal }, normalizeStatus);
}

export function applyWindowsFirewallRule(
  signal?: AbortSignal,
): Promise<WindowsFirewallApplyResult> {
  return jsonFetch(
    '/api/system/windows-firewall/allow',
    { method: 'POST', signal },
    normalizeApplyResult,
  );
}
