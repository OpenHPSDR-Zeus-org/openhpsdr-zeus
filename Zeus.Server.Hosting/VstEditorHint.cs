// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// VstEditorHint — picks the operator-facing message when a plugin editor is
// opened while the out-of-process VST route is selected but the engine isn't
// routing. Without this, the open falls through to the in-process bridge and
// surfaces its "set ZEUS_ENABLE_VST_LOAD=1" hint, which is irrelevant to VST
// mode and points a new operator at the wrong fix. The real fix in VST mode is
// to install the engine ("Download VST Engine") or wait for it to come up.

namespace Zeus.Server;

internal static class VstEditorHint
{
    /// <summary>
    /// The error to return when opening an editor in VST mode without a routing
    /// engine, or <c>null</c> when the guard does not apply — i.e. the route is
    /// Native (in-process editor path is correct) or the engine is already
    /// active (the editor open should be attempted normally).
    /// </summary>
    public static string? EngineUnavailableMessage(
        AudioProcessingMode mode, bool engineActive, bool engineInstalled)
    {
        if (mode != AudioProcessingMode.Vst || engineActive)
            return null;
        return engineInstalled
            ? "The VST engine is installed but isn't routing yet. Give it a moment, then reopen the editor."
            : "The VST engine isn't installed yet. Open the TX Audio Suite and click "
              + "\"Download VST Engine\" to download and enable it, then reopen the editor.";
    }
}
