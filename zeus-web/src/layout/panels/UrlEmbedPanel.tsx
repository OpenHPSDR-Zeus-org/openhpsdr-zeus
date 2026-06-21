// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// "URL Embed" — a multi-instance workspace tile that frames an arbitrary
// web page. The operator types a URL into the header address bar; pressing
// Enter (or the load button) normalises it, persists it to this tile's
// instance config, and points the iframe at it. Because the URL lives in
// the per-tile config, the page survives a reload — and because the panel
// is multi-instance, an operator can pin as many pages as they like.
//
// Headerless panel: it owns the `.workspace-tile-header` strip (so RGL
// picks up dragging) and a `.workspace-tile-close` button wired to the
// injected `onRemove`. Interactive controls in the strip stop mousedown
// propagation so typing / clicking the address bar doesn't start a tile
// drag (mirrors the HeroPanel / MeterGroup pattern).

import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type FormEvent,
} from 'react';
import { GripVertical, ArrowRight, ExternalLink, X, Globe } from 'lucide-react';
import { TileLockButton } from '../TileChrome';
import {
  EMPTY_URL_EMBED_CONFIG,
  normalizeEmbedUrl,
  urlEmbedTitle,
  type UrlEmbedConfig,
} from './urlEmbedConfig';

interface UrlEmbedPanelProps {
  /** Per-instance config blob from the workspace store. */
  config?: UrlEmbedConfig;
  /** Persist a new config blob to the workspace store. */
  setConfig?: (next: UrlEmbedConfig) => void;
  /** Drop this tile (injected for headerless panels). */
  onRemove?: () => void;
  tileLocked?: boolean;
  workspaceLocked?: boolean;
  onToggleLock?: () => void;
}

// External pages run in their own origin so they can't reach the Zeus
// session. allow-scripts/allow-forms keep ordinary sites functional;
// allow-popups + escape lets target=_blank links open in a real tab.
const SANDBOX =
  'allow-scripts allow-same-origin allow-forms allow-popups allow-modals allow-popups-to-escape-sandbox';

export function UrlEmbedPanel({
  config = EMPTY_URL_EMBED_CONFIG,
  setConfig,
  onRemove,
  tileLocked = false,
  workspaceLocked = false,
  onToggleLock,
}: UrlEmbedPanelProps) {
  const committedUrl = config.url;
  const [draft, setDraft] = useState(committedUrl);
  const [error, setError] = useState(false);
  const inputRef = useRef<HTMLInputElement | null>(null);

  // Re-sync the draft when the committed URL changes underneath us (tile
  // swap, layout reload, config edited elsewhere).
  useEffect(() => {
    setDraft(committedUrl);
    setError(false);
  }, [committedUrl]);

  const assign = useCallback(
    (e: FormEvent) => {
      e.preventDefault();
      const normalized = normalizeEmbedUrl(draft);
      if (!normalized) {
        setError(true);
        return;
      }
      setError(false);
      setDraft(normalized);
      // Persist to this tile's instance config — this is what makes the
      // page reload on the next session.
      setConfig?.({ ...config, url: normalized });
    },
    [draft, config, setConfig],
  );

  const handleRemove = onRemove ?? (() => {});
  const stop = (e: { stopPropagation: () => void }) => e.stopPropagation();
  const title = urlEmbedTitle(config);

  return (
    <>
      <div className="workspace-tile-header url-embed-header">
        <span
          className="workspace-tile-drag-handle"
          aria-hidden="true"
          title={
            tileLocked || workspaceLocked
              ? 'Panel position is locked'
              : 'Drag to reposition'
          }
        >
          <GripVertical size={12} />
        </span>
        <form className="url-embed-bar" onSubmit={assign}>
          <input
            ref={inputRef}
            type="text"
            inputMode="url"
            spellCheck={false}
            autoComplete="off"
            className={`url-embed-input${error ? ' url-embed-input--error' : ''}`}
            placeholder="Enter a URL and press Enter…"
            value={draft}
            title={title}
            aria-label="Page URL"
            aria-invalid={error}
            onChange={(e) => {
              setDraft(e.target.value);
              if (error) setError(false);
            }}
            onMouseDown={stop}
            onPointerDown={stop}
          />
          <button
            type="submit"
            className="url-embed-btn"
            title="Load and pin this URL"
            aria-label="Load and pin URL"
            onMouseDown={stop}
            onPointerDown={stop}
          >
            <ArrowRight size={13} />
          </button>
        </form>
        {committedUrl ? (
          <a
            className="url-embed-btn url-embed-ext"
            href={committedUrl}
            target="_blank"
            rel="noreferrer noopener"
            title="Open in a new browser tab"
            aria-label="Open in a new browser tab"
            onMouseDown={stop}
            onPointerDown={stop}
          >
            <ExternalLink size={13} />
          </a>
        ) : null}
        {onToggleLock ? (
          <TileLockButton
            locked={tileLocked}
            workspaceLocked={workspaceLocked}
            onToggleLock={onToggleLock}
          />
        ) : null}
        <button
          type="button"
          className="workspace-tile-close"
          aria-label="Remove panel"
          title="Remove panel"
          onClick={(e) => {
            stop(e);
            handleRemove();
          }}
          onPointerDown={stop}
          onMouseDown={stop}
        >
          <X size={12} />
        </button>
      </div>
      <div className="workspace-tile-body url-embed-body">
        {committedUrl ? (
          <iframe
            key={committedUrl}
            title={title}
            src={committedUrl}
            className="url-embed-frame"
            referrerPolicy="no-referrer"
            sandbox={SANDBOX}
          />
        ) : (
          <div className="url-embed-empty">
            <Globe size={28} aria-hidden />
            <p className="url-embed-empty-title">No page pinned</p>
            <p className="url-embed-empty-hint">
              Type a URL in the bar above and press Enter to pin it to this
              panel. It will reload here every session.
            </p>
            {error ? (
              <p className="url-embed-empty-error">
                That doesn&apos;t look like a valid http(s) URL.
              </p>
            ) : null}
          </div>
        )}
      </div>
    </>
  );
}
