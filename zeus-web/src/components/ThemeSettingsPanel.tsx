// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.
// See LICENSE for the full GPL text.

import { useEffect, useState, type CSSProperties } from 'react';
import {
  TWEAKABLE_TOKENS,
  useThemeStore,
  type ThemeId,
  type TweakableToken,
} from '../state/theme-store';

// Operator-facing label + group per tweakable token. "group" controls which
// section of the panel renders the row — accent tokens get the friendly
// "tweak the feel" group, surface tokens get a separate section with a
// warning that pushing them too far can break contrast.
//
// The shown swatch value is read live from the document's computed style
// (so it tracks both the active theme overlay and any operator overrides),
// not from a hardcoded default. This means surface tokens like --bg-0 show
// the silver value in light mode and the near-black value in dark mode,
// even before the user has saved an override.
type TokenGroup = 'accent' | 'chassis' | 'line' | 'text';

const TOKEN_META: Record<
  TweakableToken,
  { label: string; help: string; group: TokenGroup }
> = {
  '--accent': {
    label: 'Accent',
    help: 'Active controls, selection rings, sidebar highlight.',
    group: 'accent',
  },
  '--accent-bright': {
    label: 'Accent (bright)',
    help: 'Highlighted text — VFO label, value digits.',
    group: 'accent',
  },
  '--tx': {
    label: 'TX',
    help: 'MOX / ON-AIR red, gain-reduction caps.',
    group: 'accent',
  },
  '--power': {
    label: 'Power',
    help: 'Forward-power, drive digits, yellow accents.',
    group: 'accent',
  },
  '--amber': {
    label: 'Amber',
    help: 'Warning band on meters, warm halo around LEDs.',
    group: 'accent',
  },
  '--cyan': {
    label: 'Cyan',
    help: 'Secondary signal indicators, scope grid.',
    group: 'accent',
  },
  '--ok': {
    label: 'OK / Green',
    help: 'Healthy state, lower band of meter ramps.',
    group: 'accent',
  },
  '--orange': {
    label: 'Orange',
    help: 'Mid band on meter ramps, intermediate signals.',
    group: 'accent',
  },
  '--bg-0': {
    label: 'Chassis backdrop',
    help: 'App backdrop and topbar — outermost chrome.',
    group: 'chassis',
  },
  '--bg-1': {
    label: 'Panel base',
    help: 'Panel body fill — what the gauges sit on.',
    group: 'chassis',
  },
  '--bg-2': {
    label: 'Control surface',
    help: 'Button/input rest state, settings card fill.',
    group: 'chassis',
  },
  '--bg-3': {
    label: 'Control hover',
    help: 'Hover state for buttons and inputs.',
    group: 'chassis',
  },
  '--line': {
    label: 'Hairline',
    help: 'Default border and divider colour.',
    group: 'line',
  },
  '--line-soft': {
    label: 'Hairline (soft)',
    help: 'Subtle row separators, inset borders.',
    group: 'line',
  },
  '--line-strong': {
    label: 'Hairline (strong)',
    help: 'Stronger borders — input outlines, vfo tab edge.',
    group: 'line',
  },
  '--fg-0': {
    label: 'Text — primary',
    help: 'Strongest text colour: brand, readouts, headings.',
    group: 'text',
  },
  '--fg-1': {
    label: 'Text — secondary',
    help: 'Most labels and chip values.',
    group: 'text',
  },
  '--fg-2': {
    label: 'Text — muted',
    help: 'Help text, less-critical labels.',
    group: 'text',
  },
};

// Read the live effective value of a CSS variable from the document root.
// Falls back to '#000000' if the value isn't a 6-digit hex (e.g. rgba()).
// Used so the colour swatch reflects the *currently rendered* colour even
// when no override is set, which matters for surface tokens that flip
// between themes.
function readEffective(token: TweakableToken): string {
  if (typeof window === 'undefined') return '#000000';
  const raw = getComputedStyle(document.documentElement)
    .getPropertyValue(token)
    .trim()
    .toLowerCase();
  if (/^#[0-9a-f]{6}$/.test(raw)) return raw.toUpperCase();
  if (/^#[0-9a-f]{3}$/.test(raw)) {
    return ('#' + raw[1] + raw[1] + raw[2] + raw[2] + raw[3] + raw[3]).toUpperCase();
  }
  return '#000000';
}

const THEME_OPTIONS: ReadonlyArray<{
  id: ThemeId;
  label: string;
  blurb: string;
  swatch: string;
}> = [
  {
    id: 'dark',
    label: 'Dark',
    blurb: 'Near-black chrome, lit-display feel. The original Zeus aesthetic.',
    swatch: '#0a0a0c',
  },
  {
    id: 'light',
    label: 'Light',
    blurb: 'Brushed-silver chassis with dark display wells. Day-shack mode.',
    swatch: '#c4c8ce',
  },
];

function isHexColor(v: string): boolean {
  return /^#[0-9A-Fa-f]{6}$/.test(v);
}

const GROUP_META: Record<
  TokenGroup,
  { title: string; blurb: string; warn?: boolean }
> = {
  accent: {
    title: 'Accent palette',
    blurb:
      'Active controls, signal indicators, meter halos. Tweak any value; changes apply to both themes.',
  },
  chassis: {
    title: 'Chassis surfaces',
    blurb:
      'App backdrop, panel chrome, button surfaces. These flip per theme — the swatch shows the current effective value.',
    warn: true,
  },
  line: {
    title: 'Lines & borders',
    blurb: 'Hairlines, dividers, input outlines.',
    warn: true,
  },
  text: {
    title: 'Text',
    blurb:
      'Primary/secondary/muted text ramp. Pushing these too close to a chassis surface will break contrast.',
    warn: true,
  },
};

const GROUP_ORDER: ReadonlyArray<TokenGroup> = ['accent', 'chassis', 'line', 'text'];

export function ThemeSettingsPanel() {
  const theme = useThemeStore((s) => s.theme);
  const overrides = useThemeStore((s) => s.overrides);
  const setTheme = useThemeStore((s) => s.setTheme);
  const setOverride = useThemeStore((s) => s.setOverride);
  const resetOverrides = useThemeStore((s) => s.resetOverrides);

  // Live effective colours — re-read whenever the theme flips or an override
  // changes, so the swatches reflect what's actually on the page.
  const [effective, setEffective] = useState<Partial<Record<TweakableToken, string>>>({});
  useEffect(() => {
    // Defer one frame so any pending ThemeApplier <style> tag has been
    // applied to the document before we read computed styles.
    const id = requestAnimationFrame(() => {
      const next: Partial<Record<TweakableToken, string>> = {};
      for (const tok of TWEAKABLE_TOKENS) next[tok] = readEffective(tok);
      setEffective(next);
    });
    return () => cancelAnimationFrame(id);
  }, [theme, overrides]);

  const hasOverrides = Object.keys(overrides).length > 0;

  // Bucket tokens by group, preserving TWEAKABLE_TOKENS order within each.
  const tokensByGroup: Record<TokenGroup, TweakableToken[]> = {
    accent: [], chassis: [], line: [], text: [],
  };
  for (const tok of TWEAKABLE_TOKENS) {
    tokensByGroup[TOKEN_META[tok].group].push(tok);
  }

  return (
    <section style={{ display: 'flex', flexDirection: 'column', gap: 22 }}>
      <div>
        <div style={sectionHead}>
          <h3 style={sectionH3}>Theme</h3>
          <p style={sectionP}>
            Pick the workspace look. Display surfaces (panadapter, gauges,
            VFO) stay dark in both themes so signals stay readable.
          </p>
        </div>

        <div style={themeGrid}>
          {THEME_OPTIONS.map((opt) => {
            const active = theme === opt.id;
            return (
              <button
                key={opt.id}
                type="button"
                aria-pressed={active}
                onClick={() => setTheme(opt.id)}
                style={themeCard(active)}
              >
                <span style={{ ...themeSwatch, background: opt.swatch }} />
                <span style={themeCardBody}>
                  <span style={themeLabel}>{opt.label}</span>
                  <span style={themeBlurb}>{opt.blurb}</span>
                </span>
              </button>
            );
          })}
        </div>
      </div>

      {GROUP_ORDER.map((group) => {
        const groupMeta = GROUP_META[group];
        const tokens = tokensByGroup[group];
        if (tokens.length === 0) return null;
        return (
          <div key={group}>
            <div style={sectionHead}>
              <h3 style={sectionH3}>{groupMeta.title}</h3>
              <p style={sectionP}>{groupMeta.blurb}</p>
              {groupMeta.warn && (
                <p style={sectionWarn}>
                  ⚠ Surface tokens flip per theme. Push too far from defaults
                  and text may become unreadable — Reset to recover.
                </p>
              )}
            </div>

            <div style={paletteList}>
              {tokens.map((tok) => {
                const meta = TOKEN_META[tok];
                const overridden = overrides[tok];
                const current = (overridden ?? effective[tok] ?? '#000000').toUpperCase();
                return (
                  <div key={tok} style={paletteRow}>
                    <div style={paletteLabels}>
                      <span style={paletteLabel}>{meta.label}</span>
                      <span style={paletteHelp}>{meta.help}</span>
                    </div>
                    <div style={paletteControls}>
                      <input
                        type="color"
                        value={current}
                        onChange={(e) =>
                          setOverride(tok, e.target.value.toUpperCase())
                        }
                        style={pickerStyle}
                        aria-label={`${meta.label} colour`}
                      />
                      <input
                        type="text"
                        value={current}
                        onChange={(e) => {
                          const raw = e.target.value.trim();
                          const v = raw.startsWith('#') ? raw : `#${raw}`;
                          if (isHexColor(v)) setOverride(tok, v.toUpperCase());
                        }}
                        maxLength={7}
                        spellCheck={false}
                        style={hexInput}
                        aria-label={`${meta.label} hex value`}
                      />
                      <button
                        type="button"
                        onClick={() => setOverride(tok, null)}
                        disabled={!overridden}
                        style={resetBtn(!!overridden)}
                        title="Restore default"
                      >
                        Reset
                      </button>
                    </div>
                  </div>
                );
              })}
            </div>
          </div>
        );
      })}

      <div style={footerRow}>
        <button
          type="button"
          onClick={resetOverrides}
          disabled={!hasOverrides}
          style={resetAllBtn(hasOverrides)}
        >
          Reset all colours
        </button>
      </div>
    </section>
  );
}

const sectionHead: CSSProperties = {
  display: 'flex',
  alignItems: 'baseline',
  flexWrap: 'wrap',
  gap: 10,
  marginBottom: 12,
};
const sectionH3: CSSProperties = {
  margin: 0,
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: '0.18em',
  textTransform: 'uppercase',
  color: 'var(--fg-0)',
};
const sectionP: CSSProperties = {
  margin: 0,
  fontSize: 12,
  lineHeight: 1.5,
  color: 'var(--fg-2)',
};
const sectionWarn: CSSProperties = {
  margin: 0,
  flexBasis: '100%',
  fontSize: 11,
  lineHeight: 1.5,
  color: 'var(--amber)',
};

const themeGrid: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: 'repeat(2, 1fr)',
  gap: 10,
};

function themeCard(active: boolean): CSSProperties {
  return {
    display: 'flex',
    gap: 12,
    padding: 14,
    background: active ? 'var(--bg-2)' : 'var(--bg-1)',
    border: `1.5px solid ${active ? 'var(--accent)' : 'var(--line)'}`,
    borderRadius: 'var(--r-md)',
    cursor: 'pointer',
    textAlign: 'left',
    transition: 'border-color var(--dur-fast), background var(--dur-fast)',
    boxShadow: active
      ? '0 0 0 3px rgba(46,142,255,0.18)'
      : 'inset 0 0 0 1px rgba(255,255,255,0.02)',
  };
}

const themeSwatch: CSSProperties = {
  width: 36,
  height: 36,
  flex: 'none',
  borderRadius: 'var(--r-sm)',
  border: '1px solid var(--line-strong)',
  boxShadow: 'inset 0 0 0 1px rgba(255,255,255,0.06)',
};

const themeCardBody: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 4,
  minWidth: 0,
};

const themeLabel: CSSProperties = {
  fontSize: 13,
  fontWeight: 600,
  color: 'var(--fg-0)',
  letterSpacing: '0.04em',
};

const themeBlurb: CSSProperties = {
  fontSize: 11,
  lineHeight: 1.4,
  color: 'var(--fg-2)',
};

const paletteList: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
  gap: 1,
  border: '1px solid var(--line)',
  borderRadius: 'var(--r-md)',
  background: 'var(--line-soft)',
  overflow: 'hidden',
};

const paletteRow: CSSProperties = {
  display: 'grid',
  gridTemplateColumns: '1fr auto',
  alignItems: 'center',
  gap: 14,
  padding: '11px 14px',
  background: 'var(--bg-1)',
};

const paletteLabels: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 3,
  minWidth: 0,
};

const paletteLabel: CSSProperties = {
  fontSize: 12,
  fontWeight: 600,
  letterSpacing: '0.04em',
  color: 'var(--fg-0)',
};

const paletteHelp: CSSProperties = {
  fontSize: 11,
  color: 'var(--fg-2)',
  lineHeight: 1.4,
};

const paletteControls: CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 8,
};

const pickerStyle: CSSProperties = {
  width: 34,
  height: 24,
  borderRadius: 'var(--r-sm)',
  border: '1px solid var(--line-strong)',
  padding: 0,
  background: 'transparent',
  cursor: 'pointer',
  overflow: 'hidden',
};

const hexInput: CSSProperties = {
  background: 'var(--bg-2)',
  border: '1px solid var(--line)',
  color: 'var(--fg-0)',
  borderRadius: 'var(--r-sm)',
  padding: '5px 8px',
  width: 92,
  fontSize: 12,
  letterSpacing: '0.04em',
  fontFamily: 'var(--font-mono)',
};

function resetBtn(enabled: boolean): CSSProperties {
  return {
    background: 'transparent',
    border: '1px solid var(--line-strong)',
    color: enabled ? 'var(--fg-1)' : 'var(--fg-3)',
    borderRadius: 'var(--r-sm)',
    padding: '5px 10px',
    fontSize: 11,
    letterSpacing: '0.06em',
    cursor: enabled ? 'pointer' : 'default',
    opacity: enabled ? 1 : 0.5,
  };
}

const footerRow: CSSProperties = {
  display: 'flex',
  justifyContent: 'flex-end',
  marginTop: 10,
};

function resetAllBtn(enabled: boolean): CSSProperties {
  return {
    background: enabled ? 'var(--bg-2)' : 'transparent',
    border: '1px solid var(--line-strong)',
    color: enabled ? 'var(--fg-0)' : 'var(--fg-3)',
    borderRadius: 'var(--r-sm)',
    padding: '7px 14px',
    fontSize: 11,
    fontWeight: 600,
    letterSpacing: '0.1em',
    textTransform: 'uppercase',
    cursor: enabled ? 'pointer' : 'default',
    opacity: enabled ? 1 : 0.5,
  };
}
