// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

import { useRef, useState } from 'react';
import {
  useDisplaySettingsStore,
  type BackgroundImageFit,
  type PanBackgroundMode,
} from '../state/display-settings-store';

type ModeOption = {
  id: PanBackgroundMode;
  label: string;
  help: string;
  icon: React.ReactNode;
};

const iconSvg: React.CSSProperties = {
  width: 13,
  height: 13,
  stroke: 'currentColor',
  fill: 'none',
  strokeWidth: 1.6,
  strokeLinecap: 'round',
  strokeLinejoin: 'round',
};

const MODE_OPTIONS: ReadonlyArray<ModeOption> = [
  {
    id: 'basic',
    label: 'Basic',
    help: 'Plain panadapter and waterfall — no overlay.',
    icon: (
      <svg viewBox="0 0 16 16" style={iconSvg}>
        <path d="M2 11l3-3 3 3 3-4 3 3" />
        <path d="M2 14h12" />
      </svg>
    ),
  },
  {
    id: 'beam-map',
    label: 'Beam Map',
    help: 'World map with QRZ contact and rotator overlays.',
    icon: (
      <svg viewBox="0 0 16 16" style={iconSvg}>
        <circle cx="8" cy="8" r="6" />
        <path d="M2 8h12M8 2c2.5 2.7 2.5 9.3 0 12M8 2c-2.5 2.7-2.5 9.3 0 12" />
      </svg>
    ),
  },
  {
    id: 'image',
    label: 'Image',
    help: 'Show a custom image behind the panadapter.',
    icon: (
      <svg viewBox="0 0 16 16" style={iconSvg}>
        <rect x="2" y="3" width="12" height="10" rx="1.5" />
        <circle cx="6" cy="7" r="1.2" />
        <path d="M2 11l3-3 4 4 2-2 3 3" />
      </svg>
    ),
  },
];

const FIT_OPTIONS: ReadonlyArray<{ id: BackgroundImageFit; label: string }> = [
  { id: 'fit', label: 'Fit' },
  { id: 'fill', label: 'Fill' },
  { id: 'stretch', label: 'Stretch' },
];

// Downscale large images on the way into localStorage so a phone-camera
// JPEG doesn't blow the ~5 MB browser quota. We never upscale; if the
// source is already <= MAX_DIM on its longest edge it passes through
// unchanged. JPEG quality 0.85 is a sweet spot for photographs; PNGs are
// re-encoded as JPEG since the panadapter background never benefits from
// alpha or lossless edges.
const MAX_DIM = 1920;
const JPEG_QUALITY = 0.85;

async function fileToCompressedDataUrl(file: File): Promise<string> {
  const url = URL.createObjectURL(file);
  try {
    const img = await loadImage(url);
    const longest = Math.max(img.naturalWidth, img.naturalHeight);
    const scale = longest > MAX_DIM ? MAX_DIM / longest : 1;
    const w = Math.max(1, Math.round(img.naturalWidth * scale));
    const h = Math.max(1, Math.round(img.naturalHeight * scale));
    const canvas = document.createElement('canvas');
    canvas.width = w;
    canvas.height = h;
    const ctx = canvas.getContext('2d');
    if (!ctx) throw new Error('Canvas 2D unavailable');
    ctx.drawImage(img, 0, 0, w, h);
    return canvas.toDataURL('image/jpeg', JPEG_QUALITY);
  } finally {
    URL.revokeObjectURL(url);
  }
}

function loadImage(src: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const img = new Image();
    img.onload = () => resolve(img);
    img.onerror = () => reject(new Error('Image decode failed'));
    img.src = src;
  });
}

export function BackgroundSettingsPanel() {
  const panBackground = useDisplaySettingsStore((s) => s.panBackground);
  const setPanBackground = useDisplaySettingsStore((s) => s.setPanBackground);
  const backgroundImage = useDisplaySettingsStore((s) => s.backgroundImage);
  const setBackgroundImage = useDisplaySettingsStore((s) => s.setBackgroundImage);
  const backgroundImageFit = useDisplaySettingsStore((s) => s.backgroundImageFit);
  const setBackgroundImageFit = useDisplaySettingsStore((s) => s.setBackgroundImageFit);

  const fileInputRef = useRef<HTMLInputElement>(null);
  const [dropActive, setDropActive] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleFile = async (file: File | null | undefined) => {
    if (!file) return;
    if (!file.type.startsWith('image/')) {
      setError('Not an image file.');
      return;
    }
    setError(null);
    setBusy(true);
    try {
      const dataUrl = await fileToCompressedDataUrl(file);
      const ok = await setBackgroundImage(dataUrl);
      if (!ok) {
        setError('Image upload failed. Check the server connection and try again.');
      } else if (panBackground !== 'image') {
        await setPanBackground('image');
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const onDragEnter = (e: React.DragEvent) => {
    e.preventDefault();
    if (e.dataTransfer?.types.includes('Files')) setDropActive(true);
  };
  const onDragOver = (e: React.DragEvent) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = 'copy';
  };
  const onDragLeave = (e: React.DragEvent) => {
    if (e.currentTarget.contains(e.relatedTarget as Node | null)) return;
    setDropActive(false);
  };
  const onDrop = (e: React.DragEvent) => {
    e.preventDefault();
    setDropActive(false);
    const file = e.dataTransfer.files?.[0];
    void handleFile(file);
  };

  return (
    <section>
      <div style={sectionHead}>
        <h3 style={sectionH3}>Panadapter Background</h3>
        <p style={sectionP}>Beam Map needs QRZ configured to populate contact lookups.</p>
      </div>

      <div role="radiogroup" aria-label="Panadapter background mode" style={segmentedGrid(3)}>
        {MODE_OPTIONS.map((opt) => {
          const active = panBackground === opt.id;
          return (
            <button
              key={opt.id}
              type="button"
              role="radio"
              aria-checked={active}
              onClick={() => { void setPanBackground(opt.id); }}
              style={segCardStyle(active)}
            >
              <span style={checkDotStyle(active)} aria-hidden />
              <div style={segTopRow}>
                <span style={segIconBoxStyle(active)} aria-hidden>{opt.icon}</span>
                <span style={segTitle}>{opt.label}</span>
              </div>
              <span style={segHelp}>{opt.help}</span>
            </button>
          );
        })}
      </div>

      {panBackground === 'image' && (
        <div style={{ marginTop: 14, display: 'flex', flexDirection: 'column', gap: 12 }}>
          <div
            onDragEnter={onDragEnter}
            onDragOver={onDragOver}
            onDragLeave={onDragLeave}
            onDrop={onDrop}
            onClick={() => fileInputRef.current?.click()}
            role="button"
            tabIndex={0}
            onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') fileInputRef.current?.click(); }}
            style={dropZoneStyle(dropActive)}
          >
            {backgroundImage ? (
              <img
                src={backgroundImage}
                alt="Background preview"
                style={{
                  maxWidth: '100%',
                  maxHeight: 140,
                  borderRadius: 'var(--r-xs)',
                  display: 'block',
                  margin: '0 auto',
                }}
              />
            ) : (
              <div style={{ textAlign: 'center', color: 'var(--fg-2)', fontSize: 12 }}>
                {dropActive ? 'Release to load image' : 'Drop an image here, or click to choose a file'}
              </div>
            )}
            <input
              ref={fileInputRef}
              type="file"
              accept="image/*"
              style={{ display: 'none' }}
              onChange={(e) => {
                void handleFile(e.target.files?.[0]);
                e.currentTarget.value = '';
              }}
            />
          </div>

          {busy && <div style={{ fontSize: 11, color: 'var(--fg-2)' }}>Processing…</div>}
          {error && <div style={{ fontSize: 11, color: 'var(--tx)' }}>{error}</div>}

          <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
            <span style={inlineLabel}>Sizing</span>
            <div style={{ display: 'flex', gap: 6 }}>
              {FIT_OPTIONS.map((opt) => {
                const active = backgroundImageFit === opt.id;
                return (
                  <button
                    key={opt.id}
                    type="button"
                    className={`btn sm ${active ? 'active' : ''}`}
                    onClick={() => { void setBackgroundImageFit(opt.id); }}
                  >
                    {opt.label}
                  </button>
                );
              })}
            </div>
            {backgroundImage && (
              <button
                type="button"
                className="btn sm"
                style={{ marginLeft: 'auto' }}
                onClick={() => { void setBackgroundImage(null); }}
              >
                CLEAR IMAGE
              </button>
            )}
          </div>
        </div>
      )}
    </section>
  );
}

const sectionHead: React.CSSProperties = {
  display: 'flex',
  alignItems: 'baseline',
  flexWrap: 'wrap',
  gap: 10,
  marginBottom: 10,
};
const sectionH3: React.CSSProperties = {
  margin: 0,
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: '0.18em',
  textTransform: 'uppercase',
  color: 'var(--fg-0)',
};
const sectionP: React.CSSProperties = {
  margin: 0,
  fontSize: 12,
  lineHeight: 1.5,
  color: 'var(--fg-2)',
};
const inlineLabel: React.CSSProperties = {
  fontSize: 10,
  letterSpacing: '0.16em',
  textTransform: 'uppercase',
  color: 'var(--fg-2)',
  fontWeight: 600,
};

function segmentedGrid(cols: number): React.CSSProperties {
  return {
    display: 'grid',
    gridTemplateColumns: `repeat(${cols}, 1fr)`,
    gap: 8,
  };
}

function segCardStyle(active: boolean): React.CSSProperties {
  return {
    position: 'relative',
    display: 'flex',
    flexDirection: 'column',
    gap: 6,
    minHeight: 76,
    padding: '12px 12px 11px',
    textAlign: 'left',
    border: '1px solid',
    borderColor: active ? 'var(--accent)' : 'var(--line)',
    background: active ? 'var(--accent-soft)' : 'var(--bg-1)',
    boxShadow: active ? 'inset 0 0 0 1px var(--accent)' : 'none',
    borderRadius: 'var(--r-md)',
    cursor: 'pointer',
    color: 'var(--fg-1)',
    transition: 'background var(--dur-fast), border-color var(--dur-fast)',
  };
}

function segIconBoxStyle(active: boolean): React.CSSProperties {
  return {
    width: 22,
    height: 22,
    display: 'inline-grid',
    placeItems: 'center',
    borderRadius: 'var(--r-sm)',
    background: active ? 'var(--accent)' : 'var(--bg-3)',
    border: active ? '1px solid var(--accent)' : '1px solid var(--line)',
    color: active ? '#0b1220' : 'var(--fg-1)',
    flexShrink: 0,
  };
}

function checkDotStyle(active: boolean): React.CSSProperties {
  return {
    position: 'absolute',
    top: 9,
    right: 9,
    width: 14,
    height: 14,
    borderRadius: '50%',
    border: `1.5px solid ${active ? 'var(--accent)' : 'var(--line)'}`,
    background: active
      ? 'radial-gradient(circle at center, var(--accent) 0 4px, transparent 4.5px)'
      : 'transparent',
    transition: 'border-color var(--dur-fast), background var(--dur-fast)',
  };
}

const segTopRow: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 8,
};
const segTitle: React.CSSProperties = {
  fontSize: 13,
  fontWeight: 700,
  color: 'var(--fg-0)',
  letterSpacing: '0.02em',
};
const segHelp: React.CSSProperties = {
  fontSize: 11.5,
  color: 'var(--fg-2)',
  lineHeight: 1.45,
};

function dropZoneStyle(active: boolean): React.CSSProperties {
  return {
    border: '2px dashed',
    borderColor: active ? 'var(--accent)' : 'var(--line)',
    borderRadius: 'var(--r-sm)',
    padding: 14,
    cursor: 'pointer',
    background: active ? 'var(--accent-soft)' : 'rgba(255,255,255,0.02)',
    transition: 'all var(--dur-fast)',
  };
}
