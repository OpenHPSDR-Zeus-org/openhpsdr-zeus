// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
// See ATTRIBUTIONS.md at the repository root.

import { useEffect } from 'react';
// @ts-expect-error: Virtual module injected by vite-plugin-pwa at build time
import { useRegisterSW } from 'virtual:pwa-register/react';

import { AlertKind, useTxStore } from '../state/tx-store';

// vite-plugin-pwa is configured with registerType: 'prompt' so a freshly
// installed service worker stays in `waiting` until we call
// updateServiceWorker(true). Without this surface, the previous frontend
// keeps serving from cache until the operator fully restarts the browser.
export function useSwUpdatePrompt() {
  const setAlert = useTxStore((s) => s.setAlert);
  const {
    needRefresh: [needRefresh],
    updateServiceWorker,
  } = useRegisterSW();

  useEffect(() => {
    if (!needRefresh) return;
    setAlert({
      kind: AlertKind.FrontendUpdate,
      message: 'A new version of Zeus is ready — reload to apply.',
      action: {
        label: 'Reload',
        onClick: () => updateServiceWorker(true),
      },
    });
  }, [needRefresh, setAlert, updateServiceWorker]);
}
