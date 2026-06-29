// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useCallback, useEffect, useRef, useState } from 'react';
import { LifeBuoy } from 'lucide-react';
import { ConfirmDialog } from '../layout/ConfirmDialog';
import {
  approveSupportRequest,
  denySupportRequest,
  getSupportStatus,
  type PendingSupportRequest,
  type SupportStatus,
} from '../api/support';

// Mounts once at the app root. While the operator has opted in to Remote
// Diagnostics it polls /api/support/status; when a maintainer requests a
// read-only session it raises an in-app Allow/Deny prompt (never a browser
// dialog), and while a session is live it shows a persistent badge so the
// operator always knows someone is watching.
//
// The poll is cheap and stays on even when availability is OFF (so the badge
// reflects an already-running session and a freshly-toggled-on switch starts
// surfacing prompts within one interval) but backs off to a slow cadence then.

const POLL_ACTIVE_MS = 4000;
const POLL_IDLE_MS = 15000;

export function SupportSessionWatcher() {
  const [status, setStatus] = useState<SupportStatus | null>(null);
  // The request the operator is currently being asked about. Held separately so
  // a poll that drops/adds requests doesn't yank the dialog out from under them.
  const [prompt, setPrompt] = useState<PendingSupportRequest | null>(null);
  // Request ids the operator already answered this session — don't re-prompt for
  // an id that lingers a moment in /pending between Approve and its removal.
  const answered = useRef<Set<string>>(new Set());
  const busy = useRef(false);

  const poll = useCallback(async (signal: AbortSignal) => {
    try {
      const next = await getSupportStatus(signal);
      if (signal.aborted) return;
      setStatus(next);

      // Surface the oldest unanswered pending request if nothing is showing.
      setPrompt((current) => {
        if (current) {
          // Dismiss if the current request expired off the pending list.
          const still = next.pending.some((p) => p.requestId === current.requestId);
          return still ? current : null;
        }
        const fresh = next.pending.find((p) => !answered.current.has(p.requestId));
        return fresh ?? null;
      });
    } catch {
      // Transient — keep last known state and let the next tick retry.
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const ctrl = new AbortController();

    const tick = async () => {
      if (cancelled) return;
      await poll(ctrl.signal);
      if (cancelled) return;
      // Faster cadence when opted in or a session is live; slow otherwise.
      const fast =
        (status?.available ?? false) || (status?.activeSessions ?? 0) > 0 || prompt !== null;
      timer = setTimeout(() => void tick(), fast ? POLL_ACTIVE_MS : POLL_IDLE_MS);
    };

    void tick();
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
      ctrl.abort();
    };
    // Re-arm the loop when the cadence inputs change.
  }, [poll, status?.available, status?.activeSessions, prompt]);

  const decide = useCallback(
    async (requestId: string, allow: boolean) => {
      if (busy.current) return;
      busy.current = true;
      answered.current.add(requestId);
      try {
        if (allow) await approveSupportRequest(requestId);
        else await denySupportRequest(requestId);
      } catch {
        // If the call failed the request stays pending and the next poll will
        // re-surface it (we only skip ids we successfully answered).
        answered.current.delete(requestId);
      } finally {
        busy.current = false;
        setPrompt(null);
      }
    },
    [],
  );

  const activeSessions = status?.activeSessions ?? 0;

  return (
    <>
      {activeSessions > 0 && (
        <div
          role="status"
          aria-live="polite"
          title="A maintainer is currently viewing your diagnostics (read-only)."
          style={{
            position: 'fixed',
            right: 16,
            bottom: 16,
            zIndex: 9000,
            display: 'flex',
            alignItems: 'center',
            gap: 8,
            padding: '7px 12px',
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: '0.06em',
            textTransform: 'uppercase',
            color: 'var(--fg-0)',
            background: 'var(--panel-top)',
            border: '1px solid var(--accent)',
            borderRadius: 'var(--r-sm)',
            boxShadow: '0 2px 10px rgba(0,0,0,0.4)',
            pointerEvents: 'none',
          }}
        >
          <LifeBuoy size={14} color="var(--accent)" aria-hidden />
          <span>
            Support session active
            {activeSessions > 1 ? ` (${activeSessions})` : ''}
          </span>
        </div>
      )}

      {prompt && (
        <ConfirmDialog
          title="Remote diagnostics request"
          confirmLabel="Allow"
          cancelLabel="Deny"
          intent="primary"
          onConfirm={() => void decide(prompt.requestId, true)}
          onCancel={() => void decide(prompt.requestId, false)}
        >
          <p style={{ margin: '0 0 10px' }}>
            <strong style={{ color: 'var(--fg-0)' }}>
              {prompt.adminCallsign || 'A maintainer'}
            </strong>{' '}
            is requesting a <strong>read-only</strong> diagnostics session with
            your radio.
          </p>
          <p style={{ margin: 0, color: 'var(--fg-2)', fontSize: 12, lineHeight: 1.5 }}>
            If you allow it they can see your logs and diagnostics only — they
            cannot control your radio, change settings, or transmit. The session
            ends when they disconnect, and you can revoke access at any time by
            turning off Remote Diagnostics in Settings → Server.
          </p>
        </ConfirmDialog>
      )}
    </>
  );
}
