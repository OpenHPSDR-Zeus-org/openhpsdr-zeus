/** @vitest-environment jsdom */

// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  detachedSettingsUrl,
  detachedWorkspaceUrl,
  isDetachedSettingsWindow,
  openSettingsWindow,
} from './workspace-windows';

const originalExternal = Object.getOwnPropertyDescriptor(window, 'external');

function setPath(path: string): void {
  window.history.replaceState(null, '', path);
}

function restoreExternal(): void {
  if (originalExternal) {
    Object.defineProperty(window, 'external', originalExternal);
  } else {
    Reflect.deleteProperty(window, 'external');
  }
}

describe('workspace window helpers', () => {
  afterEach(() => {
    restoreExternal();
    vi.restoreAllMocks();
    setPath('/');
  });

  it('builds a detached settings URL without stale workspace routing', () => {
    setPath('/?workspaceWindow=1&layout=bench#server');

    const url = new URL(detachedSettingsUrl());

    expect(url.searchParams.get('settingsWindow')).toBe('1');
    expect(url.searchParams.get('workspaceWindow')).toBeNull();
    expect(url.searchParams.get('layout')).toBeNull();
    expect(url.hash).toBe('');
  });

  it('builds a detached workspace URL without stale settings routing', () => {
    setPath('/?settingsWindow=1');

    const url = new URL(detachedWorkspaceUrl('bench'));

    expect(url.searchParams.get('workspaceWindow')).toBe('1');
    expect(url.searchParams.get('layout')).toBe('bench');
    expect(url.searchParams.get('settingsWindow')).toBeNull();
  });

  it('detects detached settings windows from the query string', () => {
    setPath('/?settingsWindow=1');
    expect(isDetachedSettingsWindow()).toBe(true);

    setPath('/?workspaceWindow=1&layout=bench');
    expect(isDetachedSettingsWindow()).toBe(false);
  });

  it('uses the Photino bridge when opening settings from desktop', () => {
    const sendMessage = vi.fn();
    Object.defineProperty(window, 'external', {
      configurable: true,
      value: { sendMessage },
    });

    openSettingsWindow();

    expect(sendMessage).toHaveBeenCalledTimes(1);
    const message = JSON.parse(sendMessage.mock.calls[0]?.[0] ?? '{}') as {
      type?: string;
      title?: string;
      url?: string;
    };
    expect(message.type).toBe('zeus.openSettingsWindow');
    expect(message.title).toBe('Settings');
    expect(new URL(message.url ?? '').searchParams.get('settingsWindow')).toBe('1');
  });

  it('falls back to a browser popup outside the desktop shell', () => {
    Object.defineProperty(window, 'external', {
      configurable: true,
      value: {},
    });
    const open = vi.spyOn(window, 'open').mockImplementation(() => null);

    openSettingsWindow();

    expect(open).toHaveBeenCalledTimes(1);
    expect(open.mock.calls[0]?.[1]).toBe('zeus-settings');
    expect(new URL(String(open.mock.calls[0]?.[0])).searchParams.get('settingsWindow')).toBe('1');
  });
});
