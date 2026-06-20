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
  /** Invoked when the operator clicks "Reset Workspace" in the fallback. */
  onReset: () => void;
};

type State = {
  error: Error | null;
};

/**
 * Error boundary around the workspace grid (FlexWorkspace). The workspace is a
 * drag/drop grid of arbitrary panels; a single misbehaving panel or a layout
 * that drives react-grid-layout into a bad render must not blank the entire
 * app. When a render throws, this catches it and shows a small token-styled
 * fallback with a recover action (reset the active layout to its default
 * arrangement, which clears whatever state triggered the throw) instead of a
 * white screen.
 *
 * Styling uses tokens.css variables only — no raw hex — so it tracks the
 * light/dark theme like the rest of the chrome.
 */
export class WorkspaceErrorBoundary extends Component<Props, State> {
  constructor(props: Props) {
    super(props);
    this.state = { error: null };
  }

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  override componentDidCatch(error: Error, info: ErrorInfo) {
    // Surface to the console so the desktop (WebView, no DevTools-on-LAN) build
    // still leaves a breadcrumb. The visible fallback is the operator path.
    console.error('Workspace render error:', error, info.componentStack);
  }

  private handleReset = () => {
    // Clear the latched error first so the boundary re-renders its children,
    // then ask the host to reset the active layout. If the reset removes the
    // offending state the workspace comes back; if it throws again the boundary
    // simply re-catches and shows the fallback once more.
    this.setState({ error: null });
    this.props.onReset();
  };

  override render() {
    if (!this.state.error) return this.props.children;

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
          The workspace hit a rendering problem.
        </div>
        <div style={{ fontSize: 12, color: 'var(--fg-2)', maxWidth: 420 }}>
          One of the panels in this layout failed to render. Resetting the
          layout restores the default panel arrangement and clears the error.
        </div>
        <button
          type="button"
          className="btn sm"
          onClick={this.handleReset}
        >
          Reset Workspace
        </button>
      </div>
    );
  }
}
