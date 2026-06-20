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

// "Report a problem" self-diagnostic modal. Two states:
//   (a) FORM   — grouped symptom dropdown + free-text box + "Run diagnostic".
//   (b) RESULT — dead-simple, jargon-free instructions for opening the
//                pre-filled bug-report page, a "Copy report" fallback, and a
//                transparency preview of exactly what will be sent.
//
// Language is written for a ham operator who has never heard of GitHub: we say
// "bug report page", never "GitHub issue", and spell out every click.

import { useMemo, useState } from 'react';
import { useReportProblemStore } from '../../state/report-problem-store';
import type { Symptom } from '../../api/diagnostics';

// Shown verbatim so operators can read the page address; it is selectable text,
// not a working hyperlink (the real link is the result's pre-filled URL button).
const BUG_REPORT_PAGE_URL =
  'https://github.com/OpenHPSDR-Zeus-org/openhpsdr-zeus/issues';

/** Group symptoms by their `group` field, preserving first-seen order. */
function groupSymptoms(symptoms: Symptom[]): Array<[string, Symptom[]]> {
  const order: string[] = [];
  const byGroup = new Map<string, Symptom[]>();
  for (const s of symptoms) {
    const key = s.group || 'Other';
    let bucket = byGroup.get(key);
    if (!bucket) {
      bucket = [];
      byGroup.set(key, bucket);
      order.push(key);
    }
    bucket.push(s);
  }
  return order.map((g) => [g, byGroup.get(g) ?? []]);
}

export default function ReportProblemModal() {
  const isOpen = useReportProblemStore((s) => s.isOpen);
  const symptoms = useReportProblemStore((s) => s.symptoms);
  const selectedSymptomId = useReportProblemStore((s) => s.selectedSymptomId);
  const freeText = useReportProblemStore((s) => s.freeText);
  const loading = useReportProblemStore((s) => s.loading);
  const error = useReportProblemStore((s) => s.error);
  const result = useReportProblemStore((s) => s.result);
  const close = useReportProblemStore((s) => s.close);
  const setSymptom = useReportProblemStore((s) => s.setSymptom);
  const setFreeText = useReportProblemStore((s) => s.setFreeText);
  const run = useReportProblemStore((s) => s.run);
  const reset = useReportProblemStore((s) => s.reset);

  const [copied, setCopied] = useState(false);

  const grouped = useMemo(() => groupSymptoms(symptoms), [symptoms]);

  if (!isOpen) return null;

  const onCopy = () => {
    if (!result) return;
    void navigator.clipboard
      .writeText(result.markdown)
      .then(() => {
        setCopied(true);
        window.setTimeout(() => setCopied(false), 2000);
      })
      .catch(() => {
        // Clipboard can be blocked (insecure context / permissions). The
        // preview below still lets the operator select + copy manually.
        setCopied(false);
      });
  };

  const onOpenPage = () => {
    if (!result) return;
    window.open(result.githubIssueUrl, '_blank', 'noopener');
  };

  const labelStyle: React.CSSProperties = {
    fontSize: 11,
    fontWeight: 600,
    letterSpacing: 0.5,
    textTransform: 'uppercase',
    color: 'var(--fg-2)',
  };

  return (
    <div
      className="modal-backdrop"
      style={{
        position: 'fixed',
        inset: 0,
        background: 'rgba(0, 0, 0, 0.7)',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        zIndex: 10000,
      }}
      onClick={close}
    >
      <div
        onClick={(e) => e.stopPropagation()}
        role="dialog"
        aria-modal="true"
        aria-labelledby="report-problem-title"
        style={{
          maxWidth: 560,
          width: '92vw',
          maxHeight: '88vh',
          overflowY: 'auto',
          padding: 20,
          background: 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))',
          border: '1px solid var(--line)',
          borderRadius: 8,
          color: 'var(--fg-0)',
          fontFamily: 'var(--font-sans, Inter, system-ui, sans-serif)',
          boxShadow: '0 10px 30px rgba(0, 0, 0, 0.55)',
          display: 'flex',
          flexDirection: 'column',
          gap: 14,
        }}
      >
        <h2
          id="report-problem-title"
          style={{
            margin: 0,
            fontSize: 14,
            fontWeight: 600,
            letterSpacing: 1.5,
            textTransform: 'uppercase',
            color: 'var(--fg-0)',
          }}
        >
          {result ? 'Send this to the developers' : 'Report a problem'}
        </h2>

        {error && (
          <div
            role="alert"
            style={{
              padding: '8px 12px',
              background: 'var(--tx-soft)',
              border: '1px solid var(--tx)',
              borderRadius: 4,
              fontSize: 13,
              color: 'var(--fg-0)',
              lineHeight: 1.5,
            }}
          >
            <div style={{ marginBottom: 8 }}>
              Sorry — something went wrong putting your report together. Please
              try again.
            </div>
            <button type="button" className="btn ghost" onClick={() => void run()}>
              Try again
            </button>
          </div>
        )}

        {!result ? (
          // ---------------------------------------------------------------
          // (a) FORM
          // ---------------------------------------------------------------
          <>
            <p style={{ margin: 0, fontSize: 13, color: 'var(--fg-1)', lineHeight: 1.5 }}>
              Tell us what's not working. Pick the closest match and/or describe
              it in your own words — then run the diagnostic and we'll package it
              all up for you.
            </p>

            <label style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              <span style={labelStyle}>What's the problem?</span>
              <select
                value={selectedSymptomId ?? ''}
                onChange={(e) => setSymptom(e.target.value === '' ? null : e.target.value)}
                style={{
                  width: '100%',
                  padding: '6px 8px',
                  background: 'var(--bg-2)',
                  color: 'var(--fg-0)',
                  border: '1px solid var(--line-strong)',
                  borderRadius: 4,
                  fontSize: 13,
                  fontFamily: 'inherit',
                }}
              >
                <option value="">— Choose the closest match (optional) —</option>
                {grouped.map(([group, items]) => (
                  <optgroup key={group} label={group}>
                    {items.map((s) => (
                      <option key={s.id} value={s.id}>
                        {s.label}
                      </option>
                    ))}
                  </optgroup>
                ))}
              </select>
            </label>

            <label style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              <span style={labelStyle}>
                Describe what's wrong in your own words (optional)
              </span>
              <textarea
                value={freeText}
                onChange={(e) => setFreeText(e.target.value)}
                rows={5}
                placeholder="For example: the audio cuts out for a second every time I transmit."
                style={{
                  width: '100%',
                  padding: '8px',
                  background: 'var(--bg-2)',
                  color: 'var(--fg-0)',
                  border: '1px solid var(--line-strong)',
                  borderRadius: 4,
                  fontSize: 13,
                  fontFamily: 'inherit',
                  resize: 'vertical',
                }}
              />
            </label>

            <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
              <button type="button" className="btn ghost" onClick={close}>
                Cancel
              </button>
              <button
                type="button"
                className="btn sm active"
                disabled={loading}
                onClick={() => void run()}
              >
                {loading ? 'Running…' : 'Run diagnostic'}
              </button>
            </div>
          </>
        ) : (
          // ---------------------------------------------------------------
          // (b) RESULT
          // ---------------------------------------------------------------
          <>
            <ol
              style={{
                margin: 0,
                paddingLeft: 20,
                fontSize: 13,
                color: 'var(--fg-1)',
                lineHeight: 1.6,
                display: 'flex',
                flexDirection: 'column',
                gap: 8,
              }}
            >
              <li>
                Click the blue "Open bug report page" button below. It opens in
                your web browser.
              </li>
              <li>
                If the page asks you to sign in, create a free account (it only
                takes a minute) or log in.
              </li>
              <li>
                Good news — we already filled in the whole report for you. If the
                title box at the top is empty, type a few words describing the
                problem.
              </li>
              <li>
                Click the green "Submit new issue" button at the bottom of that
                page. That's it — you're done, and the developers will see it.
              </li>
            </ol>

            <p style={{ margin: 0, fontSize: 12, color: 'var(--fg-2)', lineHeight: 1.5 }}>
              If the page didn't fill itself in, click "Copy report" below, then
              paste it into the big description box on that page (right-click →
              Paste, or Ctrl/Cmd+V).
            </p>

            <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
              <span style={labelStyle}>The bug report page is here:</span>
              <code
                style={{
                  userSelect: 'all',
                  WebkitUserSelect: 'all',
                  padding: '6px 8px',
                  background: 'var(--bg-1)',
                  border: '1px solid var(--line)',
                  borderRadius: 4,
                  fontSize: 12,
                  fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                  color: 'var(--fg-0)',
                  wordBreak: 'break-all',
                }}
              >
                {BUG_REPORT_PAGE_URL}
              </code>
            </div>

            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
              <button
                type="button"
                className="btn sm active"
                autoFocus
                onClick={onOpenPage}
              >
                Open bug report page
              </button>
              <button type="button" className="btn ghost" onClick={onCopy}>
                {copied ? 'Copied!' : 'Copy report'}
              </button>
              <button
                type="button"
                className="btn ghost"
                style={{ marginLeft: 'auto' }}
                onClick={close}
              >
                Close
              </button>
            </div>

            <details>
              <summary
                style={{
                  cursor: 'pointer',
                  fontSize: 12,
                  color: 'var(--fg-2)',
                  marginBottom: 6,
                }}
              >
                This is exactly what will be sent (you can read it first):
              </summary>
              <pre
                style={{
                  margin: 0,
                  maxHeight: 240,
                  overflow: 'auto',
                  padding: 10,
                  background: 'var(--bg-1)',
                  border: '1px solid var(--line)',
                  borderRadius: 4,
                  fontSize: 11,
                  lineHeight: 1.5,
                  fontFamily: 'var(--font-mono, JetBrains Mono, ui-monospace, monospace)',
                  color: 'var(--fg-1)',
                  whiteSpace: 'pre-wrap',
                  wordBreak: 'break-word',
                }}
              >
                {result.markdown}
              </pre>
            </details>

            <div style={{ display: 'flex', justifyContent: 'flex-start' }}>
              <button type="button" className="btn ghost" onClick={reset}>
                Report something else
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
