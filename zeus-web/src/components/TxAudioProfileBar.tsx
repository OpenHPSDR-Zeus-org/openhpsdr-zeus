// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// Shared "TX Audio Profile" bar. Mounted MIRRORED in BOTH the TX Fidelity panel
// and the TX Audio Suite window; both reads/writes go through the same
// useTxAudioProfileStore slice, so the two surfaces stay in sync live.
//
// Controls:
//   - a dropdown listing all saved profiles -> selecting one APPLIES it,
//   - a "Save TX Audio Profile" button -> a NAME dialog (modal) -> save-by-name,
//   - a Delete (confirm dialog) for the selected profile.
//
// Token-only styling (var(--*) — never raw hex). The Save body is captured
// server-side from live state; this component never assembles a profile body.

import { useEffect, useId, useRef, useState, type CSSProperties } from 'react';
import { ChevronDown, Plus } from 'lucide-react';

import { ConfirmDialog } from '../layout/ConfirmDialog';
import { TextInputDialog } from '../layout/TextInputDialog';
import { useTxAudioProfileStore } from '../state/tx-audio-profile-store';

type Notice = { tone: 'ok' | 'error'; text: string } | null;

function profileLabel(profile: { name: string; processingMode: string }): string {
  return `${profile.name} [${profile.processingMode === 'vst' ? 'VST' : 'Native'}]`;
}

function buttonStyle(disabled: boolean, accent = false): CSSProperties {
  return {
    height: 26,
    border: '1px solid ' + (accent ? 'var(--accent)' : 'var(--line)'),
    borderRadius: 4,
    background: accent ? 'var(--accent-soft)' : 'var(--bg-2)',
    color: accent ? 'var(--fg-0)' : 'var(--fg-1)',
    cursor: disabled ? 'not-allowed' : 'pointer',
    fontSize: 10,
    fontWeight: 900,
    opacity: disabled ? 0.55 : 1,
    padding: '0 9px',
    textTransform: 'uppercase',
    whiteSpace: 'nowrap',
  };
}

export interface TxAudioProfileBarProps {
  /** Compact layout (TX Fidelity panel rail) vs. full-width (Audio Suite bar). */
  compact?: boolean;
}

/**
 * Shared TX Audio Profile control. Mounted in both the TX Fidelity panel and the
 * TX Audio Suite window — both instances share one store, so selection, the
 * list, and busy state are mirrored live.
 */
export function TxAudioProfileBar({ compact = false }: TxAudioProfileBarProps) {
  const profiles = useTxAudioProfileStore((s) => s.profiles);
  const lastLoadedId = useTxAudioProfileStore((s) => s.lastLoadedId);
  const busy = useTxAudioProfileStore((s) => s.busy);
  const load = useTxAudioProfileStore((s) => s.load);
  const save = useTxAudioProfileStore((s) => s.save);
  const apply = useTxAudioProfileStore((s) => s.apply);
  const remove = useTxAudioProfileStore((s) => s.remove);

  const [saveOpen, setSaveOpen] = useState(false);
  const [deletePending, setDeletePending] = useState<string | null>(null);
  const [notice, setNotice] = useState<Notice>(null);
  const [menuOpen, setMenuOpen] = useState(false);
  const pickerRef = useRef<HTMLDivElement | null>(null);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const menuId = useId();

  // Load the list + last-loaded pointer once. Mounting in two places is fine —
  // both share the store; the second mount sees the already-loaded list.
  const loaded = useTxAudioProfileStore((s) => s.loaded);
  useEffect(() => {
    if (!loaded) void load();
  }, [loaded, load]);

  // The dropdown shows the last-loaded profile as selected even after the
  // operator drifts off it with live edits — re-selecting it re-applies.
  const selectedId = lastLoadedId && profiles.some((p) => p.id === lastLoadedId) ? lastLoadedId : '';
  const selectedProfile = profiles.find((p) => p.id === selectedId);
  const triggerLabel = selectedProfile
    ? profileLabel(selectedProfile)
    : profiles.length ? 'Select profile...' : 'No profiles saved';

  useEffect(() => {
    if (!menuOpen) return;

    const onPointerDown = (event: PointerEvent) => {
      const root = pickerRef.current;
      if (!root || !event.target || root.contains(event.target as Node)) return;
      setMenuOpen(false);
    };

    window.addEventListener('pointerdown', onPointerDown);
    return () => window.removeEventListener('pointerdown', onPointerDown);
  }, [menuOpen]);

  const onSelect = async (id: string) => {
    setNotice(null);
    if (!id) return;
    setMenuOpen(false);
    const result = await apply(id);
    if (!result.ok) {
      setNotice({ tone: 'error', text: result.error ?? `Could not apply profile.` });
    }
  };

  // The "+" / name-dialog path: capture the live state under a NEW name.
  const onSaveSubmit = async (name: string) => {
    setSaveOpen(false);
    setNotice(null);
    const result = await save(name);
    if (!result.ok) {
      setNotice({ tone: 'error', text: result.error ?? 'Profile save failed.' });
      return;
    }
    setNotice({ tone: 'ok', text: `Saved "${name}".` });
  };

  // The "Save" button: overwrite the currently-selected profile in place, no
  // name prompt. With nothing selected there's no target, so fall back to the
  // new-profile dialog (the "+" path).
  const onSaveSelected = async () => {
    setNotice(null);
    if (!selectedProfile) {
      setSaveOpen(true);
      return;
    }
    const result = await save(selectedProfile.name);
    if (!result.ok) {
      setNotice({ tone: 'error', text: result.error ?? 'Profile save failed.' });
      return;
    }
    setNotice({ tone: 'ok', text: `Saved "${selectedProfile.name}".` });
  };

  const onDeleteConfirm = async () => {
    const id = deletePending;
    setDeletePending(null);
    if (!id) return;
    setNotice(null);
    const result = await remove(id);
    if (!result.ok) {
      setNotice({ tone: 'error', text: result.error ?? 'Profile delete failed.' });
    }
  };

  return (
    <section
      aria-label="TX audio profile bar"
      style={{
        display: 'grid',
        gap: 6,
        minWidth: 0,
        padding: compact ? '8px 10px' : '6px 12px',
        boxSizing: 'border-box',
        border: '1px solid var(--line)',
        borderRadius: compact ? 6 : 0,
        borderLeft: compact ? '1px solid var(--line)' : 'none',
        borderRight: compact ? '1px solid var(--line)' : 'none',
        borderTop: compact ? '1px solid var(--line)' : 'none',
        background: compact
          ? 'linear-gradient(180deg, var(--panel-top), var(--panel-bot))'
          : 'var(--bg-1)',
        borderBottom: '1px solid var(--line)',
      }}
    >
      <div
        style={{
          display: 'flex',
          alignItems: 'center',
          gap: 6,
          minWidth: 0,
        }}
      >
        <span
          className="label-xs"
          style={{
            color: 'var(--fg-3)',
            fontSize: 9,
            fontWeight: 900,
            letterSpacing: '0.08em',
            textTransform: 'uppercase',
            whiteSpace: 'nowrap',
          }}
        >
          TX Audio Profile
        </span>
      </div>
      <div style={{ display: 'flex', alignItems: 'center', gap: 6, minWidth: 0 }}>
        <div className="tx-audio-profile-picker" ref={pickerRef}>
          <button
            type="button"
            ref={triggerRef}
            className="tx-audio-profile-trigger"
            aria-label="TX audio profile"
            aria-haspopup="listbox"
            aria-expanded={menuOpen}
            aria-controls={menuId}
            title="Select a saved TX audio profile to apply it"
            disabled={busy}
            onClick={() => setMenuOpen((open) => !open)}
            onKeyDown={(event) => {
              if (event.key === 'ArrowDown' || event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                setMenuOpen(true);
              } else if (event.key === 'Escape') {
                setMenuOpen(false);
              }
            }}
          >
            <span className="tx-audio-profile-trigger-text">{triggerLabel}</span>
            <ChevronDown className="tx-audio-profile-caret" size={14} aria-hidden />
          </button>
          {menuOpen && (
            <div
              id={menuId}
              className="tx-audio-profile-menu"
              role="listbox"
              aria-label="TX audio profile options"
              onKeyDown={(event) => {
                if (event.key === 'Escape') {
                  event.preventDefault();
                  setMenuOpen(false);
                  triggerRef.current?.focus();
                }
              }}
            >
              <div
                className={`tx-audio-profile-option tx-audio-profile-option--placeholder${selectedId ? '' : ' is-active'}`}
                role="option"
                aria-selected={!selectedId}
                aria-disabled="true"
              >
                {profiles.length ? 'Select profile...' : 'No profiles saved'}
              </div>
              {profiles.map((p) => {
                const active = p.id === selectedId;
                return (
                  <button
                    key={p.id}
                    type="button"
                    role="option"
                    aria-selected={active}
                    className={`tx-audio-profile-option${active ? ' is-active' : ''}`}
                    onClick={() => void onSelect(p.id)}
                  >
                    {profileLabel(p)}
                  </button>
                );
              })}
            </div>
          )}
        </div>
        <button
          type="button"
          aria-label="New TX audio profile"
          title="Save the current live TX audio settings as a new profile"
          disabled={busy}
          onClick={() => {
            setNotice(null);
            setSaveOpen(true);
          }}
          style={{ ...buttonStyle(busy), display: 'inline-flex', alignItems: 'center', justifyContent: 'center', padding: '0 7px' }}
        >
          <Plus size={14} aria-hidden />
        </button>
        <button
          type="button"
          aria-label="Save TX audio profile"
          title={
            selectedProfile
              ? `Overwrite "${selectedProfile.name}" with the current live TX audio settings`
              : 'Save the current live TX audio settings as a new profile'
          }
          disabled={busy}
          onClick={() => void onSaveSelected()}
          style={buttonStyle(busy, true)}
        >
          Save
        </button>
        <button
          type="button"
          aria-label="Delete TX audio profile"
          title="Delete the selected TX audio profile"
          disabled={busy || !selectedProfile}
          onClick={() => selectedProfile && setDeletePending(selectedProfile.id)}
          style={buttonStyle(busy || !selectedProfile)}
        >
          Delete
        </button>
      </div>

      {notice && (
        <div
          role={notice.tone === 'error' ? 'alert' : 'status'}
          className="mono"
          style={{
            minWidth: 0,
            color: notice.tone === 'error' ? 'var(--tx)' : 'var(--fg-2)',
            fontSize: 10,
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            whiteSpace: 'nowrap',
          }}
          title={notice.text}
        >
          {notice.text}
        </div>
      )}

      {saveOpen && (
        <TextInputDialog
          title="Save TX Audio Profile"
          label="Profile name"
          placeholder="e.g. Studio SSB"
          confirmLabel="Save Profile"
          onCancel={() => setSaveOpen(false)}
          onSubmit={(name) => void onSaveSubmit(name)}
        >
          <p style={{ margin: 0, color: 'var(--fg-2)', fontSize: 12 }}>
            Captures the current live TX audio settings (mic, leveler, CFC,
            filter, chain, and plugin settings) under this name. Re-using an
            existing name overwrites it.
          </p>
        </TextInputDialog>
      )}

      {deletePending && (
        <ConfirmDialog
          title="Delete TX Audio Profile"
          confirmLabel="Delete Profile"
          onCancel={() => setDeletePending(null)}
          onConfirm={() => void onDeleteConfirm()}
        >
          <p>
            Delete profile{' '}
            {profiles.find((p) => p.id === deletePending)?.name ?? deletePending}?
          </p>
          <p style={{ color: 'var(--fg-2)', fontSize: 12 }}>
            This only removes the saved profile. Your current live TX audio
            settings are unchanged.
          </p>
        </ConfirmDialog>
      )}
    </section>
  );
}
