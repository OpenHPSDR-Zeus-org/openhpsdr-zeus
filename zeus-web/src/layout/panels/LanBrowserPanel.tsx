// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.
//
// "LAN Browser" — a multi-instance workspace tile that frames the web UI of a
// device on the RADIO HOST's LAN (router, antenna rotator, amplifier, another
// SDR's console). Unlike URL Embed, the page is fetched by the Zeus *server*
// through GET /api/lan/proxy and relayed back, so a REMOTE operator — whose own
// browser can't reach the home LAN — still sees it. The server restricts targets
// to private (RFC1918 / IPv6-ULA) addresses; see LanProxyService.
//
// Two render paths, picked by transport:
//   • On the LAN (direct), the iframe loads `src=/api/lan/proxy?url=…` straight
//     from the backend — full fidelity (sub-resources, scripts and navigation
//     all resolve same-origin through the proxy).
//   • Remote (over the WebRTC tunnel), the operator's browser can't load the
//     iframe's sub-resources from the LAN, so we fetch() a SELF-CONTAINED page
//     (server inlines CSS + images, `?inline=1`) — which tunnels — and drop it
//     into `srcDoc`. Anchor/form navigation is relayed back via postMessage.
//     JS-heavy SPAs degrade; typical device admin pages render.
//
// Headerless panel (owns the .workspace-tile-header drag strip + close button),
// mirroring UrlEmbedPanel.

import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type FormEvent,
} from 'react';
import {
  GripVertical,
  ArrowRight,
  RefreshCw,
  X,
  Network,
  Star,
} from 'lucide-react';
import { TileLockButton } from '../TileChrome';
import { isRemoteMode } from '../../remote/remote-client';
import { urlEmbedTitle } from './urlEmbedConfig';
import {
  EMPTY_LAN_BROWSER_CONFIG,
  lanBookmarkLabel,
  normalizeLanUrl,
  type LanBrowserConfig,
} from './lanBrowserConfig';

interface LanBrowserPanelProps {
  /** Per-instance config blob (URL-embed shape: url + title, plus bookmarks). */
  config?: LanBrowserConfig;
  setConfig?: (next: LanBrowserConfig) => void;
  onRemove?: () => void;
  tileLocked?: boolean;
  workspaceLocked?: boolean;
  onToggleLock?: () => void;
}

// Proxied LAN content is served from Zeus's OWN origin (/api/lan/proxy). We
// deliberately omit `allow-same-origin` so a compromised/hostile device page
// runs in an opaque origin and cannot reach window.parent, Zeus cookies, or
// the operator's session. Scripts/forms still work for ordinary device UIs.
const SANDBOX = 'allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox';

/** Wrap an absolute LAN URL as the same-origin proxy path the iframe loads. */
function proxySrc(url: string): string {
  return `/api/lan/proxy?url=${encodeURIComponent(url)}`;
}

/** Proxy path that returns a self-contained (inlined) page for srcDoc — the
 *  remote path, fetched over the tunnel. */
function proxyInlineSrc(url: string): string {
  return `/api/lan/proxy?url=${encodeURIComponent(url)}&inline=1`;
}

export function LanBrowserPanel({
  config = EMPTY_LAN_BROWSER_CONFIG,
  setConfig,
  onRemove,
  tileLocked = false,
  workspaceLocked = false,
  onToggleLock,
}: LanBrowserPanelProps) {
  const remote = useMemo(() => isRemoteMode(), []);
  const committedUrl = config.url;
  const [draft, setDraft] = useState(committedUrl);
  const [error, setError] = useState(false);
  // Bump to force the iframe to reload the same URL (device pages have no
  // navigation chrome of their own).
  const [reloadNonce, setReloadNonce] = useState(0);
  const inputRef = useRef<HTMLInputElement | null>(null);
  const iframeRef = useRef<HTMLIFrameElement | null>(null);

  // Remote-only: the inlined page fetched through the tunnel for srcDoc.
  const [remoteDoc, setRemoteDoc] = useState<{
    loading: boolean;
    html?: string;
    error?: string;
  }>({ loading: false });

  useEffect(() => {
    setDraft(committedUrl);
    setError(false);
  }, [committedUrl]);

  // Remote: fetch a self-contained page through the tunnel and render via srcDoc.
  useEffect(() => {
    if (!remote || !committedUrl) {
      setRemoteDoc({ loading: false });
      return;
    }
    let aborted = false;
    const ctrl = new AbortController();
    setRemoteDoc({ loading: true });
    fetch(proxyInlineSrc(committedUrl), { signal: ctrl.signal })
      .then(async (r) => {
        const text = await r.text();
        if (aborted) return;
        setRemoteDoc(
          r.ok
            ? { loading: false, html: text }
            : { loading: false, error: text || `HTTP ${r.status}` },
        );
      })
      .catch((e) => {
        if (!aborted) setRemoteDoc({ loading: false, error: String(e) });
      });
    return () => {
      aborted = true;
      ctrl.abort();
    };
  }, [remote, committedUrl, reloadNonce]);

  // Remote: the inlined page relays anchor/form navigation up via postMessage
  // (it can't navigate the sandboxed srcDoc iframe over the tunnel itself).
  useEffect(() => {
    if (!remote) return;
    const onMessage = (e: MessageEvent) => {
      if (e.source !== iframeRef.current?.contentWindow) return;
      const d = e.data as { type?: unknown; url?: unknown } | null;
      if (!d || d.type !== 'zeus-lan-nav' || typeof d.url !== 'string') return;
      const normalized = normalizeLanUrl(d.url);
      if (normalized) setConfig?.({ ...config, url: normalized });
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, [remote, config, setConfig]);

  const assign = useCallback(
    (e: FormEvent) => {
      e.preventDefault();
      const normalized = normalizeLanUrl(draft);
      if (!normalized) {
        setError(true);
        return;
      }
      setError(false);
      setDraft(normalized);
      setConfig?.({ ...config, url: normalized });
    },
    [draft, config, setConfig],
  );

  const bookmarks = config.bookmarks;
  const isBookmarked = useMemo(
    () => !!committedUrl && bookmarks.some((b) => b.url === committedUrl),
    [bookmarks, committedUrl],
  );

  // Navigate to a pinned page (used by the bookmark chips).
  const openBookmark = useCallback(
    (url: string) => {
      setDraft(url);
      setError(false);
      setConfig?.({ ...config, url });
    },
    [config, setConfig],
  );

  // Star button: pin the current page if it isn't pinned, else un-pin it.
  const toggleBookmark = useCallback(() => {
    if (!committedUrl) return;
    const next = isBookmarked
      ? bookmarks.filter((b) => b.url !== committedUrl)
      : [...bookmarks, { url: committedUrl, label: '' }];
    setConfig?.({ ...config, bookmarks: next });
  }, [committedUrl, isBookmarked, bookmarks, config, setConfig]);

  const removeBookmark = useCallback(
    (url: string) => {
      setConfig?.({ ...config, bookmarks: bookmarks.filter((b) => b.url !== url) });
    },
    [bookmarks, config, setConfig],
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
            placeholder="LAN address, e.g. 192.168.1.1 …"
            value={draft}
            title={title}
            aria-label="LAN device address"
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
            title="Open this LAN address"
            aria-label="Open LAN address"
            onMouseDown={stop}
            onPointerDown={stop}
          >
            <ArrowRight size={13} />
          </button>
        </form>
        {committedUrl ? (
          <button
            type="button"
            className={`url-embed-btn${isBookmarked ? ' url-embed-btn--active' : ''}`}
            title={isBookmarked ? 'Remove bookmark' : 'Bookmark this page'}
            aria-label={isBookmarked ? 'Remove bookmark' : 'Bookmark this page'}
            aria-pressed={isBookmarked}
            onClick={(e) => {
              stop(e);
              toggleBookmark();
            }}
            onMouseDown={stop}
            onPointerDown={stop}
          >
            <Star size={13} fill={isBookmarked ? 'currentColor' : 'none'} />
          </button>
        ) : null}
        {committedUrl ? (
          <button
            type="button"
            className="url-embed-btn url-embed-ext"
            title="Reload page"
            aria-label="Reload page"
            onClick={(e) => {
              stop(e);
              setReloadNonce((n) => n + 1);
            }}
            onMouseDown={stop}
            onPointerDown={stop}
          >
            <RefreshCw size={13} />
          </button>
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
      {bookmarks.length ? (
        <div className="lan-bookmark-bar" onPointerDown={stop} onMouseDown={stop}>
          {bookmarks.map((bm) => {
            const label = lanBookmarkLabel(bm);
            const active = bm.url === committedUrl;
            return (
              <span
                key={bm.url}
                className={`lan-bookmark${active ? ' lan-bookmark--active' : ''}`}
              >
                <button
                  type="button"
                  className="lan-bookmark-go"
                  title={bm.url}
                  onClick={(e) => {
                    stop(e);
                    openBookmark(bm.url);
                  }}
                >
                  {label}
                </button>
                <button
                  type="button"
                  className="lan-bookmark-del"
                  aria-label={`Remove bookmark ${label}`}
                  title="Remove bookmark"
                  onClick={(e) => {
                    stop(e);
                    removeBookmark(bm.url);
                  }}
                >
                  <X size={9} />
                </button>
              </span>
            );
          })}
        </div>
      ) : null}
      <div className="workspace-tile-body url-embed-body">
        {committedUrl && remote ? (
          // Remote: render the tunnelled, self-contained page via srcDoc.
          remoteDoc.error ? (
            <div className="url-embed-empty">
              <Network size={28} aria-hidden />
              <p className="url-embed-empty-title">Couldn&apos;t load that device</p>
              <p className="url-embed-empty-error">{remoteDoc.error}</p>
            </div>
          ) : (
            <iframe
              ref={iframeRef}
              title={title}
              srcDoc={remoteDoc.html ?? '<!doctype html><title>Loading…</title>'}
              className="url-embed-frame"
              referrerPolicy="no-referrer"
              sandbox={SANDBOX}
            />
          )
        ) : committedUrl ? (
          // On the LAN: load straight from the backend for full fidelity.
          <iframe
            ref={iframeRef}
            key={`${committedUrl}#${reloadNonce}`}
            title={title}
            src={proxySrc(committedUrl)}
            className="url-embed-frame"
            referrerPolicy="no-referrer"
            sandbox={SANDBOX}
          />
        ) : (
          <div className="url-embed-empty">
            <Network size={28} aria-hidden />
            <p className="url-embed-empty-title">No device opened</p>
            <p className="url-embed-empty-hint">
              Type a LAN address (your router, rotator, amplifier, another SDR…)
              and press Enter. Zeus fetches it from the radio host&apos;s network,
              so it works even when you&apos;re connected remotely. Only private
              LAN addresses are reachable.
            </p>
            {error ? (
              <p className="url-embed-empty-error">
                That doesn&apos;t look like a valid LAN address.
              </p>
            ) : null}
          </div>
        )}
      </div>
    </>
  );
}
