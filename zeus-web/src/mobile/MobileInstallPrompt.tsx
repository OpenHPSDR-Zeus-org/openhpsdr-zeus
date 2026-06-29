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

import { useEffect, useState } from 'react';
import { Download, Share, SquarePlus, X } from 'lucide-react';
import { isCapacitorRuntime } from '../serverUrl';
import {
  getDeferredPrompt,
  isAppInstalled,
  isIosSafari,
  isRunningStandalone,
  subscribeInstallState,
  triggerInstall,
} from '../pwa/pwa-install';

const DISMISS_KEY = 'zeus.pwa.installDismissed';

function readDismissed(): boolean {
  try {
    return localStorage.getItem(DISMISS_KEY) === '1';
  } catch {
    return false;
  }
}

function writeDismissed(): void {
  try {
    localStorage.setItem(DISMISS_KEY, '1');
  } catch {
    /* private mode — banner just reappears next session, harmless. */
  }
}

/**
 * "Install as app" banner for the mobile shell. Two paths:
 *  - Chromium (Android, desktop) hands us a `beforeinstallprompt` we replay
 *    behind an Install button.
 *  - iOS Safari can't be prompted programmatically, so we show the manual
 *    Share → Add to Home Screen steps instead.
 * Hidden when already installed (standalone), inside the Capacitor native
 * shell, or once the operator dismisses it.
 */
export function MobileInstallPrompt() {
  // Re-render when the deferred prompt arrives or the app gets installed.
  const [, forceTick] = useState(0);
  useEffect(() => subscribeInstallState(() => forceTick((n) => n + 1)), []);

  const [dismissed, setDismissed] = useState(readDismissed);
  const [installing, setInstalling] = useState(false);

  // Never offer a PWA install inside the native Capacitor wrapper — it already
  // is the installed app.
  if (isCapacitorRuntime()) return null;
  if (dismissed) return null;
  if (isRunningStandalone() || isAppInstalled()) return null;

  const canPrompt = getDeferredPrompt() != null;
  const iosManual = !canPrompt && isIosSafari();

  // Nothing actionable on this browser (e.g. desktop Firefox, iOS Chrome) —
  // stay out of the way rather than show instructions that lead nowhere.
  if (!canPrompt && !iosManual) return null;

  const dismiss = () => {
    writeDismissed();
    setDismissed(true);
  };

  const onInstall = async () => {
    setInstalling(true);
    try {
      const outcome = await triggerInstall();
      // Accepted → appinstalled fires and unmounts us. Dismissed → leave the
      // banner so the operator can reconsider; only an explicit X hides it.
      if (outcome === 'accepted') dismiss();
    } finally {
      setInstalling(false);
    }
  };

  return (
    <div className="m-install" role="region" aria-label="Install Zeus as an app">
      <div className="m-install__icon" aria-hidden="true">
        <Download size={20} strokeWidth={2.2} />
      </div>
      <div className="m-install__body">
        <div className="m-install__title">Install Zeus</div>
        {iosManual ? (
          <div className="m-install__msg">
            Tap <Share size={13} strokeWidth={2.4} className="m-install__inline" aria-label="Share" />{' '}
            then <SquarePlus size={13} strokeWidth={2.4} className="m-install__inline" aria-label="Add to Home Screen" />{' '}
            <strong>Add to Home Screen</strong> to run Zeus full-screen like an app.
          </div>
        ) : (
          <div className="m-install__msg">
            Add Zeus to your home screen for a full-screen, app-like experience.
          </div>
        )}
      </div>
      {canPrompt && (
        <button
          type="button"
          className="m-install__btn"
          onClick={onInstall}
          disabled={installing}
        >
          {installing ? 'Installing…' : 'Install'}
        </button>
      )}
      <button
        type="button"
        className="m-install__close"
        onClick={dismiss}
        aria-label="Dismiss install prompt"
        title="Dismiss"
      >
        <X size={16} strokeWidth={2.4} />
      </button>
    </div>
  );
}
