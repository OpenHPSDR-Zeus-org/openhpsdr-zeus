// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

const WORKSPACE_WINDOW_PARAM = 'workspaceWindow';
const WORKSPACE_LAYOUT_PARAM = 'layout';

interface PhotinoExternal {
  sendMessage?: (message: string) => void;
}

interface PhotinoWindowSurface {
  external?: PhotinoExternal;
}

export function detachedWorkspaceUrl(layoutId: string): string {
  const url = new URL(window.location.href);
  url.searchParams.set(WORKSPACE_WINDOW_PARAM, '1');
  url.searchParams.set(WORKSPACE_LAYOUT_PARAM, layoutId);
  url.hash = '';
  return url.toString();
}

export function currentDetachedWorkspaceLayoutId(): string | null {
  const sp = new URLSearchParams(window.location.search);
  return sp.get(WORKSPACE_WINDOW_PARAM) === '1'
    ? sp.get(WORKSPACE_LAYOUT_PARAM)
    : null;
}

export function openWorkspaceWindow(layoutId: string, title: string): void {
  const url = detachedWorkspaceUrl(layoutId);
  const external = (window as unknown as PhotinoWindowSurface).external;
  const sendMessage = external?.sendMessage;
  if (typeof sendMessage === 'function') {
    sendMessage(JSON.stringify({
      type: 'zeus.openWorkspaceWindow',
      layoutId,
      title,
      url,
    }));
    return;
  }

  window.open(
    url,
    `zeus-workspace-${layoutId}`,
    'popup,width=1180,height=760,noopener,noreferrer',
  );
}
