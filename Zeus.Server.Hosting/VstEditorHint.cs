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
// VstEditorHint — picks the operator-facing message when a plugin editor can't
// be opened because no host will load the plugin in the current mode. Two cases:
//   * VST processing mode selected but the out-of-process engine isn't routing.
//   * Native processing mode while the in-process bridge won't host the plugin
//     (TX native load stays opt-in — a crashing in-process TX VST takes the
//     radio down).
// In both, the bare in-process bridge would surface a "set ZEUS_ENABLE_VST_LOAD=1"
// hint — a developer-only escape hatch that points a new operator at the wrong
// (and unsafe) fix. The supported path is the crash-isolated out-of-process
// engine: install it ("Download VST Engine") and run the Audio Suite in VST mode.

namespace Zeus.Server;

internal static class VstEditorHint
{
    /// <summary>
    /// The operator-facing error to return when an editor open can't succeed, or
    /// <c>null</c> when the open should be attempted normally — i.e. the engine
    /// is routing, or Native mode with the in-process bridge able to host the
    /// plugin (<paramref name="nativeLoadEnabled"/> is true).
    /// </summary>
    /// <param name="nativeLoadEnabled">Whether the in-process bridge will host
    /// this plugin in Native mode. RX VSTs load in-process by default; TX VSTs
    /// only when <c>ZEUS_ENABLE_VST_LOAD=1</c>. When false, the in-process editor
    /// can't open, so point the operator at the out-of-process engine instead.</param>
    public static string? EngineUnavailableMessage(
        AudioProcessingMode mode, bool engineActive, bool engineInstalled,
        bool nativeLoadEnabled = true)
    {
        // Engine is routing — the editor open should be attempted via the engine.
        if (engineActive)
            return null;

        // VST processing mode selected but the engine isn't routing yet.
        if (mode == AudioProcessingMode.Vst)
            return engineInstalled
                ? "The VST engine is installed but isn't routing yet. Give it a moment, then reopen the editor."
                : "The VST engine isn't installed yet. Open the TX Audio Suite and click "
                  + "\"Download VST Engine\" to download and enable it, then reopen the editor.";

        // Native mode, but the in-process bridge won't host this plugin (the safe
        // default for TX). Guide to the crash-isolated engine, not the dev hatch.
        if (!nativeLoadEnabled)
            return engineInstalled
                ? "TX VSTs run in the dedicated VST engine. Switch the Audio Suite "
                  + "processing mode to \"VST\" to load and edit this plugin."
                : "TX VSTs run in the dedicated VST engine. Open the TX Audio Suite, click "
                  + "\"Download VST Engine\", then switch the processing mode to \"VST\" to "
                  + "load and edit this plugin.";

        return null;
    }
}
