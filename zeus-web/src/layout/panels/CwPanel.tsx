// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { CwKeyer } from '../../components/design/CwKeyer';
import { ZeroBeatButton } from '../../components/ZeroBeatButton';
import { abortCw, sendCw } from '../../api/cw';
import { useCwStore } from '../../state/cw-store';

const MAX_MACROS = 32;

export function CwPanel() {
  const settings = useCwStore((s) => s.settings);
  const status = useCwStore((s) => s.status);
  const setSettingsLocal = useCwStore((s) => s.setSettingsLocal);
  const commitDebounced = useCwStore((s) => s.commitDebounced);
  const setMacro = useCwStore((s) => s.setMacro);
  const addMacro = useCwStore((s) => s.addMacro);
  const removeMacro = useCwStore((s) => s.removeMacro);

  return (
    <div style={{ flex: 1, overflow: 'auto' }}>
      <div className="btn-row" style={{ padding: '4px 6px' }}>
        <ZeroBeatButton />
      </div>
      <CwKeyer
        wpm={settings.wpm}
        setWpmLocal={(v) => setSettingsLocal({ wpm: v })}
        setWpmCommit={(v) => commitDebounced({ wpm: v })}
        macros={settings.macros}
        onSend={(macro) => void sendCw(macro, settings.wpm)}
        onAbort={() => void abortCw()}
        onMacroEdit={(i, v) => void setMacro(i, v)}
        onMacroDelete={(i) => void removeMacro(i)}
        onMacroAdd={() => void addMacro()}
        maxMacros={MAX_MACROS}
        status={status}
      />
    </div>
  );
}
