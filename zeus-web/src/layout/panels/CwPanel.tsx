// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

import { CwKeyer } from '../../components/design/CwKeyer';
import { abortCw, sendCw } from '../../api/cw';
import { useCwStore } from '../../state/cw-store';

// Hard cap mirrors Zeus.Server.Hosting/CwSettingsStore.cs MaxMacros. If
// the server bumps the cap, also bump this so the UI's "Add" button
// matches. (We could fetch it dynamically, but a constant is cheaper
// and only changes during epic-scale revisits.)
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
      <CwKeyer
        wpm={settings.wpm}
        // Split-write pattern fixes the "slider snaps back" race. The
        // local setter updates the store immediately so the slider
        // tracks the pointer; the debounced commit schedules a single
        // PUT after the operator stops dragging.
        setWpmLocal={(v) => setSettingsLocal({ wpm: v })}
        setWpmCommit={(v) => commitDebounced({ wpm: v })}
        macros={settings.macros}
        // Pass the current WPM explicitly so a slider change that hasn't
        // round-tripped to the server yet still keys at the operator's
        // intended speed.
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
