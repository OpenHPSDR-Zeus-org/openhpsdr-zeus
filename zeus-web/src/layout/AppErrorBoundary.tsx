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

import { Component, type ErrorInfo, type ReactNode } from 'react';

type Props = {
  children: ReactNode;
  /**
   * Short scope label for the fallback copy, e.g. "Settings". Defaults to the
   * whole app — used by the top-level boundary in main.tsx.
   */
  scope?: string;
  /**
   * Optional in-app recovery offered alongside a full reload — e.g. closing the
   * Settings view to return to the live workspace. When omitted, only Reload is
   * shown (the top-level boundary has nowhere else to go but a fresh load).
   */
  recover?: { label: string; run: () => void };
  /**
   * When this value changes, the boundary clears any latched error so navigating
   * away from the broken view re-renders its children without a page reload.
   */
  resetKey?: unknown;
};

type State = {
  error: Error | null;
};

/**
 * Generic render-error boundary used as the app-wide safety net.
 *
 * Without it, an unhandled throw anywhere in the tree unmounts the entire React
 * root and leaves a blank white screen that only a hard refresh recovers — the
 * exact failure operators hit when a Settings panel (e.g. Signal Intelligence)
 * throws mid-render, because the Settings view is rendered outside the
 * workspace's own boundary.
 *
 * Mounted at two levels:
 *  - main.tsx wraps <App/> so no throw can ever produce a pure blank screen.
 *  - App.tsx wraps the lazy Settings view (and its dynamic-import Suspense) so a
 *    settings crash — or a failed settings chunk load — degrades to a small
 *    recoverable card while the surrounding chrome stays live.
 *
 * Styling uses tokens.css variables only (no raw hex) so the fallback tracks the
 * light/dark theme like the rest of the chrome.
 */
export class AppErrorBoundary extends Component<Props, State> {
  constructor(props: Props) {
    super(props);
    this.state = { error: null };
  }

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  override componentDidUpdate(prev: Props) {
    if (this.state.error && prev.resetKey !== this.props.resetKey) {
      this.setState({ error: null });
    }
  }

  override componentDidCatch(error: Error, info: ErrorInfo) {
    // Surface to the console so the desktop (WebView, no DevTools-on-LAN) build
    // still leaves a breadcrumb pointing at the offending component. The visible
    // fallback is the operator's recovery path.
    console.error(`${this.props.scope ?? 'App'} render error:`, error, info.componentStack);
  }

  private handleRecover = () => {
    this.setState({ error: null });
    this.props.recover?.run();
  };

  private handleReload = () => {
    window.location.reload();
  };

  override render() {
    if (!this.state.error) return this.props.children;

    const scope = this.props.scope ?? 'app';
    const recover = this.props.recover;

    return (
      <div
        role="alert"
        style={{
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          justifyContent: 'center',
          gap: 12,
          height: '100%',
          minHeight: 160,
          padding: 24,
          textAlign: 'center',
          background: 'var(--bg-1)',
          color: 'var(--fg-1)',
          border: '1px solid var(--panel-border)',
          borderRadius: 6,
        }}
      >
        <div style={{ fontSize: 14, fontWeight: 600, color: 'var(--fg-0)' }}>
          The {scope} hit a rendering problem.
        </div>
        <div style={{ fontSize: 12, color: 'var(--fg-2)', maxWidth: 420 }}>
          Something failed to render. {recover ? 'Go back' : 'Reloading'} to
          recover — your radio connection and settings are unaffected.
        </div>
        <div style={{ display: 'flex', gap: 8 }}>
          {recover && (
            <button type="button" className="btn sm" onClick={this.handleRecover}>
              {recover.label}
            </button>
          )}
          <button type="button" className="btn ghost sm" onClick={this.handleReload}>
            Reload
          </button>
        </div>
      </div>
    );
  }
}
