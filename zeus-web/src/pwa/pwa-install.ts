// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

// PWA install plumbing. Chromium fires `beforeinstallprompt` once, early in
// page life — usually before the lazily-loaded mobile shell mounts. This
// module is imported eagerly from main.tsx so the event is captured and
// stashed regardless of which UI tree (desktop or mobile) renders, and the
// banner can replay the saved prompt whenever it mounts.

// The event isn't in lib.dom yet; model the bits we use.
export interface BeforeInstallPromptEvent extends Event {
  readonly platforms: string[];
  prompt(): Promise<void>;
  readonly userChoice: Promise<{ outcome: 'accepted' | 'dismissed'; platform: string }>;
}

let deferredPrompt: BeforeInstallPromptEvent | null = null;
let installed = false;
const listeners = new Set<() => void>();

function emit(): void {
  for (const l of listeners) l();
}

if (typeof window !== 'undefined') {
  window.addEventListener('beforeinstallprompt', (e) => {
    // Stop Chrome's default mini-infobar so we can offer install on our own
    // terms inside the mobile shell.
    e.preventDefault();
    deferredPrompt = e as BeforeInstallPromptEvent;
    emit();
  });
  window.addEventListener('appinstalled', () => {
    installed = true;
    deferredPrompt = null;
    emit();
  });
}

/** The saved install event, or null when none is pending. */
export function getDeferredPrompt(): BeforeInstallPromptEvent | null {
  return deferredPrompt;
}

/** True once the browser reports the app was installed this session. */
export function isAppInstalled(): boolean {
  return installed;
}

/** Subscribe to install-availability changes. Returns an unsubscribe fn. */
export function subscribeInstallState(cb: () => void): () => void {
  listeners.add(cb);
  return () => {
    listeners.delete(cb);
  };
}

/**
 * Replay the saved install prompt. Resolves with the user's choice, or
 * 'unavailable' when no prompt is pending (e.g. iOS, already installed).
 * The prompt is single-use — it's cleared once shown.
 */
export async function triggerInstall(): Promise<'accepted' | 'dismissed' | 'unavailable'> {
  const pending = deferredPrompt;
  if (!pending) return 'unavailable';
  await pending.prompt();
  const choice = await pending.userChoice;
  deferredPrompt = null;
  emit();
  return choice.outcome;
}

/**
 * True when the page is already running as an installed PWA (standalone
 * display mode, or iOS Safari's legacy navigator.standalone flag).
 */
export function isRunningStandalone(): boolean {
  if (typeof window === 'undefined') return false;
  if (window.matchMedia?.('(display-mode: standalone)').matches) return true;
  if (window.matchMedia?.('(display-mode: fullscreen)').matches) return true;
  // iOS Safari predates display-mode and exposes a non-standard flag.
  return (window.navigator as { standalone?: boolean }).standalone === true;
}

/** True on iOS / iPadOS, where install is a manual Add-to-Home-Screen flow. */
export function isIosDevice(): boolean {
  if (typeof navigator === 'undefined') return false;
  const ua = navigator.userAgent || '';
  if (/iPad|iPhone|iPod/.test(ua)) return true;
  // iPadOS 13+ reports a desktop Mac UA; touch points disambiguate it.
  return navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1;
}

/**
 * True on an iOS browser that can actually Add to Home Screen — only Safari's
 * WebKit shell offers it. Chrome/Firefox/Edge on iOS (CriOS/FxiOS/EdgiOS) and
 * in-app webviews cannot, so we don't show them instructions they can't follow.
 */
export function isIosSafari(): boolean {
  if (!isIosDevice()) return false;
  const ua = navigator.userAgent || '';
  return /Safari/.test(ua) && !/CriOS|FxiOS|EdgiOS|GSA/.test(ua);
}
