// SPDX-License-Identifier: GPL-2.0-or-later
//
// Dockable TX fidelity panel. The analyzer itself lives in
// components/TxFidelityAdvisor so the same surface can be reused later in
// compact/mobile contexts without depending on workspace chrome.

import { TxFidelityAdvisor } from '../../components/TxFidelityAdvisor';

export function TxFidelityPanel() {
  return (
    <div style={{ height: '100%', minHeight: 0, overflow: 'auto', padding: 10, background: 'var(--bg-1)' }}>
      <TxFidelityAdvisor />
    </div>
  );
}
