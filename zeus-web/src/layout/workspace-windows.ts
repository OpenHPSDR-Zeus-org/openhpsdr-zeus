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

/** True when running inside the Photino desktop shell (the host bridge that
 *  can open real OS windows). Detached "windows" only persist/restore there;
 *  in a plain browser they'd be popups, which the restore path must not spawn
 *  on load. */
export function isDesktopShell(): boolean {
  const external = (window as unknown as PhotinoWindowSurface).external;
  return typeof external?.sendMessage === 'function';
}

interface PersistedWorkspaceWindow {
  layoutId: string;
  title: string;
}

/** Reopen the detached workspace windows the operator left open at the last
 *  desktop shutdown. Desktop shell only (see isDesktopShell); a no-op in the
 *  browser and remote clients. Best-effort — a fetch failure just means nothing
 *  is restored. Call once, from the MAIN window only. */
export async function restorePersistedWorkspaceWindows(): Promise<void> {
  if (!isDesktopShell()) return;
  try {
    const res = await fetch('/api/ui/workspace-windows');
    if (!res.ok) return;
    const list = (await res.json()) as PersistedWorkspaceWindow[];
    if (!Array.isArray(list)) return;
    for (const w of list) {
      if (w?.layoutId) {
        openWorkspaceWindow(w.layoutId, w.title || 'Workspace');
      }
    }
  } catch {
    // Best-effort restore — never block app startup on it.
  }
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
