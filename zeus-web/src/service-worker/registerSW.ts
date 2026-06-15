// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { Workbox } from 'workbox-window';
import { isCapacitorRuntime } from '../serverUrl';

async function isDesktopHost(): Promise<boolean> {
  try {
    const res = await fetch('/api/capabilities', { cache: 'no-store' });
    if (!res.ok) return false;
    const body = (await res.json()) as { host?: unknown };
    return body.host === 'desktop';
  } catch {
    return false;
  }
}

async function unregisterExistingServiceWorkers(): Promise<boolean> {
  const registrations = await navigator.serviceWorker.getRegistrations();
  const hadRegistrations = registrations.length > 0 || !!navigator.serviceWorker.controller;
  await Promise.all(registrations.map((registration) => registration.unregister()));

  if ('caches' in window) {
    const keys = await caches.keys();
    await Promise.all(keys.map((key) => caches.delete(key)));
  }

  return hadRegistrations;
}

/**
 * Register the service worker and handle updates.
 * Returns a function to trigger update installation.
 */
export function registerServiceWorker(
  onUpdateAvailable: () => void,
): (() => Promise<void>) | null {
  // Service worker only works in production builds
  if (import.meta.env.DEV) {
    return null;
  }

  // Capacitor's WebView already ships the bundled assets — running the PWA
  // service worker on top of capacitor:// scope causes redundant caching
  // and update prompts that don't make sense for a native shell.
  if (isCapacitorRuntime()) {
    return null;
  }

  // Check if service workers are supported
  if (!('serviceWorker' in navigator)) {
    console.warn('Service workers not supported in this browser');
    return null;
  }

  let registration: ServiceWorkerRegistration | undefined;
  let disabledForDesktop = false;

  const register = async () => {
    if (await isDesktopHost()) {
      disabledForDesktop = true;
      const hadServiceWorker = await unregisterExistingServiceWorkers();
      if (hadServiceWorker) window.location.reload();
      return;
    }

    const wb = new Workbox('/sw.js');

    // Handle service worker waiting to activate
    // This fires when a new service worker has installed but is waiting
    // for the old one to be released (usually when tabs are still open)
    wb.addEventListener('waiting', () => {
      console.log('New service worker waiting to activate');
      onUpdateAvailable();
    });

    // Handle case where service worker is already waiting when we register
    wb.addEventListener('controlling', () => {
      console.log('New service worker is now controlling the page');
      // Reload to use the new service worker
      window.location.reload();
    });

    // Register the service worker
    wb.register()
      .then((reg) => {
        registration = reg;
        console.log('Service worker registered successfully');

        // Check for updates every 60 seconds when the page is visible
        setInterval(() => {
          if (document.visibilityState === 'visible') {
            reg?.update().catch((err) => {
              console.warn('SW update check failed:', err);
            });
          }
        }, 60000);
      })
      .catch((err) => {
        console.error('Service worker registration failed:', err);
      });
  };

  void register();

  // Return function to trigger update installation
  return async () => {
    if (disabledForDesktop) return;
    if (!registration?.waiting) {
      console.warn('No service worker waiting to install');
      return;
    }

    // Send SKIP_WAITING message to the waiting service worker
    registration.waiting.postMessage({ type: 'SKIP_WAITING' });

    // The 'controlling' event will fire and trigger a reload
  };
}
