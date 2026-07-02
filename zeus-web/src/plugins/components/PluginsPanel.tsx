// SPDX-License-Identifier: GPL-2.0-or-later
//
// PluginsPanel — three-tab container mounted from SettingsMenu.
//   Installed  — currently loaded plugins (default)
//   VSTs       — operator-scanned VST/AU wrappers
//   Browse     — registry catalog
//   Install from URL — BYOP form
//
// Styles inline so we don't fight the surrounding .settings-view-panel
// chrome. All colours come from tokens.css — no raw hex.

import { useState } from 'react';

import { InstalledPlugins, InstalledVsts } from './InstalledPlugins';
import { PluginBrowser } from './PluginBrowser';
import { InstallFromUrl } from './InstallFromUrl';

type SubTabId = 'installed' | 'vsts' | 'browse' | 'install-url';

const SUB_TABS: ReadonlyArray<{ id: SubTabId; label: string }> = [
  { id: 'installed', label: 'INSTALLED' },
  { id: 'vsts', label: 'VSTS' },
  { id: 'browse', label: 'BROWSE' },
  { id: 'install-url', label: 'INSTALL FROM URL' },
];

export function PluginsPanel() {
  const [active, setActive] = useState<SubTabId>('installed');

  return (
    <div
      data-testid="plugins-panel"
      style={{
        display: 'flex',
        flexDirection: 'column',
        gap: 12,
        maxWidth: 760,
        flex: 1,
        minHeight: 0,
      }}
    >
      <h3
        style={{
          margin: 0,
          fontSize: 13,
          fontWeight: 700,
          letterSpacing: '0.12em',
          textTransform: 'uppercase',
          color: 'var(--fg-0)',
        }}
      >
        Plugins
      </h3>

      <div
        role="tablist"
        aria-label="Plugin views"
        style={{
          display: 'flex',
          gap: 4,
          padding: 2,
          background: 'var(--bg-0)',
          border: '1px solid var(--panel-border)',
          borderRadius: 'var(--r-sm)',
          alignSelf: 'flex-start',
        }}
      >
        {SUB_TABS.map((t) => {
          const isActive = t.id === active;
          return (
            <button
              key={t.id}
              type="button"
              role="tab"
              aria-selected={isActive}
              onClick={() => setActive(t.id)}
              className={`btn sm${isActive ? ' active' : ''}`}
            >
              {t.label}
            </button>
          );
        })}
      </div>

      <div role="tabpanel" style={{ flex: 1, minHeight: 0 }}>
        {active === 'installed' && <InstalledPlugins />}
        {active === 'vsts' && <InstalledVsts />}
        {active === 'browse' && <PluginBrowser />}
        {active === 'install-url' && <InstallFromUrl />}
      </div>
    </div>
  );
}
