// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Ft8MessageEditor — the compact CQ / CQ DX / free-text MACRO editor that lives
// INSIDE the digital pop-out. Message editing deliberately stays in the pop-out
// (not the main Settings menu) so the operator can tweak a macro without leaving
// the live operating view; the macros still persist PER MODE in the same
// server-backed store the menu uses, so FT8 and FT4 keep their own.

import { useFt8SettingsStore } from '../../state/ft8-settings-store';
import type { DigitalMode } from '../../api/ft8-settings';
import { TextRow } from './ft8-settings-controls';

export function Ft8MessageEditor({ mode }: { mode: DigitalMode }) {
  const settings = useFt8SettingsStore((s) => s.byMode[mode]);
  const update = useFt8SettingsStore((s) => s.update);

  return (
    <div className="ft8-settings" aria-label={`${mode} message macros`}>
      <section className="ft8-region ft8-set-section">
        <div className="ft8-region__head">Messages · {mode}</div>
        <div className="ft8-set-body">
          <TextRow
            label="CQ message"
            hint="The CQ button text (e.g. CQ or CQ TEST)."
            value={settings.cqMessage}
            maxLength={32}
            onChange={(v) => void update(mode, { cqMessage: v })}
          />
          <TextRow
            label="CQ DX message"
            value={settings.cqDxMessage}
            maxLength={32}
            onChange={(v) => void update(mode, { cqDxMessage: v })}
          />
          <TextRow
            label="Free-text macro"
            hint="A reusable 13-char free-text message."
            value={settings.freeTextMacro}
            maxLength={13}
            upper
            onChange={(v) => void update(mode, { freeTextMacro: v })}
          />
          <p className="ft8-set-note">
            Macros persist per mode. Behaviour, decode and waterfall settings live in the main
            Settings → Zeus Digital section.
          </p>
        </div>
      </section>
    </div>
  );
}
