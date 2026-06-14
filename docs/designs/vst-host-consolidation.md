# VST Host Consolidation — one out-of-process engine for audio + editors

**Status:** proposed (RED-LIGHT — TX-path / architecture change, needs maintainer sign-off)
**Author:** N9WAR · **Date:** 2026-06-14
**Related:** `docs/designs/vst-out-of-process-engine.md`, `docs/designs/vst-engine-bridge-protocol.md`

## Problem

Zeus currently hosts VST3 plugins **two different ways**, and they don't share a plugin instance:

1. **In-process** (`zeus-vst-bridge.dll` via `VstHostAudioPlugin`) — used by the
   default **Native** Audio-Suite path. Loads the VST3 *inside the Zeus
   process*; shows the editor as a top-level HWND. A plugin that segfaults on
   load or in its editor **takes the radio backend down**. Gated behind
   `ZEUS_ENABLE_VST_LOAD` (now default-on in `--desktop`).
2. **Out-of-process** (`VSTHostEngine.exe --zeus-bridge`, the upstream
   KlayaR/VSTHost engine) — used by the opt-in **VST** processing mode. Audio
   only today; editor + per-knob state are *not* wired, so the engine's plugin
   instance and the in-process editor instance are **different objects**.

Result: redundant code, a crash-risk path, and an editor that (in VST mode)
doesn't reflect the instance actually processing audio.

## Decision

**Consolidate onto the out-of-process engine as the single VST host** for both
**audio and editors**, and retire the in-process load path. Chosen over adopting
`Kb2uka/openhpsdr-zeus-plughost` because plughost — though the maintainers'
official multi-format sidecar — is **Linux-first and not Windows-ready**
(POSIX-only VST2/CLAP loaders hard-wired into `plugin_chain.cpp`; the native
editor is X11-only — issue
[brianbruff/openhpsdr-zeus#106](https://github.com/brianbruff/openhpsdr-zeus/issues/106)).
Standardising on plughost would mean doing its entire Windows port first; that is
a separate, maintainer-owned decision (see "Future" below).

The Zeus side is built behind an **engine abstraction** so the backend
(KlayaR/VSTHost now) can later be swapped for plughost without touching the
Audio Suite — one standard at the integration seam, swappable underneath.

## Why this is mostly done already

The out-of-process engine (`ZeusBridgeHost`, branch `feat/zeus-bridge-mode`
@`3609b5c`, installed at `%LOCALAPPDATA%\Zeus\vst-engine\VSTHostEngine.exe`)
**already implements everything** the consolidation needs:

| Capability | Engine command / event | State |
|---|---|---|
| Out-of-process editor (crash-isolated JUCE window) | `open_editor` / `close_editor` → `editor_opened` / `editor_closed` | ✅ done |
| Per-param set | `set_param` | ✅ done |
| Full plugin state restore | `set_plugin_state`, `load_chain` (`state` blob / `parameters[]`) | ✅ done |
| Chain add/remove/move/enable/bypass/gain | `add_plugin`, `remove_plugin`, `move_plugin`, … | ✅ done |
| Bounded-wait realtime passthrough | SHM ring (`ZeusAudioBridge`) | ✅ done |

So the engine's crash-isolated editor **strictly replaces** the in-process
`zeus-vst-bridge` editor. The remaining work is **Zeus-side wiring**.

## Plan (Zeus side)

1. **Engine controller editor API.** Add `OpenEditor(slot)` / `CloseEditor(slot)`
   to `VstEngineController` (send `open_editor`/`close_editor`); parse
   `editor_opened`/`editor_closed` engine events to track per-slot open state.
   Maintain a plugin-id ⇄ engine-slot-index map built when the chain loads.
2. **Route the Audio-Suite editor endpoints to the engine** when the
   out-of-process engine is active. `AudioPluginBridge.OpenEditor/CloseEditor/
   IsEditorOpen` (today → in-process `IVstBridgeNative`) become mode-aware:
   engine path when active, else the existing in-process path. `GET/POST/DELETE
   /api/audio-suite/plugins/{id}/editor` and `GenericVstPanel` auto-open are
   unchanged at the UI layer.
3. **Load chain with state, not defaults.** Replace the per-plugin `add_plugin`
   in `AudioProcessingModeService.LoadChainIntoEngine` with one `load_chain`
   carrying each slot's saved state/params.
4. **Make the engine the primary path.** Keep the Native/VST toggle during
   migration; once the engine path is proven on HL2, flip the default and treat
   the in-process load as a fallback.
5. **Retire** `zeus-vst-bridge` in-process load (keep history) → single host.

## What is preserved (lose nothing)

- The chips+detail Audio Suite UI, the processing-mode toggle, `GenericVstPanel`
  auto-open — all unchanged at the UI layer.
- The realtime-safe `AudioPluginBridge` routing + master/TCI bypass.
- The Windows editor capability — now crash-isolated in the engine process
  instead of in the radio's.
- The bounded-wait passthrough robustness rule (engine down ⇒ clean passthrough).

## Future: plughost

If/when Brian (EI6LF) standardises Zeus on `plughost` and its Windows port lands
(VST2/CLAP gating + the HWND editor, #106 — for which Zeus's existing
`zeus-vst-bridge` `IPlugView` one-thread solution is directly reusable), the
engine abstraction from step 1 lets it slot in as an alternate backend without a
UI rewrite. That remains a maintainer decision.
