// SPDX-License-Identifier: GPL-2.0-or-later
interface PhotinoSurface {
  external?: { sendMessage?: (message: string) => void };
}

/**
 * Open an external URL in the operator's real browser.
 *
 * Inside the Photino desktop shell, `window.open` to an external site is
 * unreliable (the webview swallows it), so we post a `zeus.openExternal`
 * message and the C# host opens it via the OS browser. In a normal browser
 * (web-dev / LAN client) there is no host bridge, so we fall back to
 * `window.open`.
 */
export function openExternalUrl(url: string): void {
  const sendMessage = (window as unknown as PhotinoSurface).external?.sendMessage;
  if (typeof sendMessage === 'function') {
    sendMessage(JSON.stringify({ type: 'zeus.openExternal', url }));
    return;
  }
  window.open(url, '_blank', 'noopener,noreferrer');
}
