// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect } from 'react';
import { useConnectionStore } from '../../state/connection-store';

export const FILTER_DRAG_MIME = 'application/x-zeus-filter-slot';

const LOCAL_STORAGE_KEY = 'zeus.filter.advancedPaneOpen';

export function cachePaneOpenLocal(open: boolean) {
  try { window.localStorage.setItem(LOCAL_STORAGE_KEY, open ? '1' : '0'); } catch { /* ok */ }
}

export function useFilterRibbonOpenSync() {
  useEffect(() => {
    try {
      const cached = window.localStorage.getItem(LOCAL_STORAGE_KEY);
      if (cached === '1') {
        useConnectionStore.setState({ filterAdvancedPaneOpen: true });
      }
    } catch { /* ok */ }
  }, []);
}
