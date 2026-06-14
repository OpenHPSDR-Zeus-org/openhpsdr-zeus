# VST Mode — Out-of-Process Engine via Upstream VSTHost

**Status:** Proposed — **RED-LIGHT.** New opt-in processing mode + a child
process + an external downloadable dependency. Needs maintainer (Brian, EI6LF)
sign-off before merge. Built out on `feat/vst-host-native-bridge`.

**Companion:** `docs/designs/vst-engine-bridge-protocol.md` — the exact
shared-memory + stdio contract both sides implement.

---

## 1. Goal

Add an **opt-in "VST" mode** to the Audio Suite that processes the operator's
mic/TX audio through a chain of VST3 plugins hosted in a **separate process** —
the **upstream [`KlayaR/VSTHost`](https://github.com/KlayaR/VSTHost)** engine,
run headless as a Zeus sidecar.

**Brian's existing native Audio Suite stays the untouched default.** VST is a
*separate, mutually-exclusive route* the operator selects — never a replacement.

## 2. The hard constraint (non-negotiable)

> **VST mode must be the robust path INTO the radio, never a hazard to it.** It
> must not conflict with, block, stall, starve, or corrupt the radio's realtime
> audio logic. When VST mode is off, absent, or the engine is slow/crashed, the
> radio audio path behaves exactly as it does today.

Concretely, the realtime TX thread **never blocks unbounded** on the engine
(bounded-wait round-trip; on timeout/down/crash → clean passthrough and keep
feeding the radio), **never allocates or locks** on the realtime path, and the
whole integration is **additive and gated** (Native = byte-for-byte today).

## 3. Why upstream-consume instead of fork/vendor

The user's call (2026-06-14): **point at upstream so the host stays maintained
in one place, and let users download it from its own releases.** This is
strictly better than vendoring the engine into Zeus:

- **One source of truth.** `KlayaR/VSTHost` keeps evolving (8 releases, latest
  1.4.0); Zeus rides the updates instead of carrying a frozen copy.
- **Cleaner licensing.** VSTHost links JUCE (→ GPLv3). Because the operator
  downloads it from *its own* repo and it runs as a *separate process*, Zeus
  never bundles or redistributes the GPLv3 binary — the **process boundary and
  the distribution boundary** both keep JUCE isolated from Zeus's license.
- **No JUCE in Zeus's build/CI.** Zeus ships small; no JUCE FetchContent, no
  extra build time or artifact bloat.

(An earlier attempt vendored the engine into `native/zeus-vst-engine/`; it was
removed in favour of this model.)

## 4. The one upstream change required

The published VSTHost is a standalone app that owns a **sound card** (ASIO /
WASAPI) and routes to **virtual cables**. It has no way to accept Zeus's in-app
TX audio today. So upstream `KlayaR/VSTHost` gains a **headless bridge mode**:

```
VSTHostEngine.exe --zeus-bridge --shm <name> --block <frames> --rate <hz> --channels <n>
```

- **No Tauri UI, no sound card.** The JUCE engine's existing
  `AudioDeviceManager` / `AudioIODeviceCallback` half is bypassed; a new
  `ZeusAudioBridge` reads input from / writes output to a shared-memory ring
  fed by Zeus, and drives `PluginChain::processBlock` on its own audio thread.
- **Control still over stdio** newline-JSON (the existing `IPCBridge`): scan,
  load, remove, move, bypass, set-param, open/close-editor, preset, level
  events — unchanged from today's command set, minus the device commands.
- Everything else upstream is reused as-is: `PluginChain`, `PluginScanner`
  (blacklist + dead-man's-pedal), `PresetManager`, editor windows (JUCE
  `DocumentWindow`; the message thread is the single UI thread, which is exactly
  the thread-affinity problem the in-process bridge hand-rolled — solved free).

This `--zeus-bridge` feature **lives upstream** so it stays updated. Zeus
launches the downloaded engine binary with that flag.

## 5. Where it taps in (the Zeus seam)

`Zeus.Plugins.Host/Audio/AudioChain.cs` → `Process(input, output, ctx)` is the
realtime entry point, already allocation-free, lock-free, bit-identical
passthrough on master bypass, graceful passthrough on size mismatch. Today a VST
slot is the in-process `VstHostAudioPlugin`. **VST mode swaps the route** so the
TX block goes to the sidecar engine instead — behind the same realtime contract
(no alloc, no lock, passthrough on failure). Upstream/downstream radio DSP is
untouched.

## 6. Zeus side — pure .NET, no new native code

```
┌──────────────────────── Zeus backend (.NET) ────────────────────────┐
│  WDSP/radio TX ─► AudioChain.Process (realtime)                      │
│       Native mode  ─► native in-process slots (Brian's, unchanged)   │
│       VST mode     ─► VstEngineClient.Process  (bounded round-trip,  │
│                          passthrough on timeout/down)                 │
│                              │                                        │
│   VstEngineProcess           ├─ shared-memory ring (MemoryMappedFile)│
│   - locate/download engine   │   in[] inReady(event)                 │
│   - launch --zeus-bridge     │   out[] outReady(event)               │
│   - supervise / restart      └─ stdio newline-JSON (control) ────────┼─┐
└──────────────────────────────────────────────────────────────────────┘ │
                          ┌─────────────────────────────────────────────▼─┐
                          │  VSTHostEngine.exe --zeus-bridge (downloaded) │
                          │   ZeusAudioBridge ─ ring ◄► PluginChain        │
                          │   IPCBridge (stdio) ─ scan/load/editor/preset  │
                          └────────────────────────────────────────────────┘
```

- **`VstEngineProcess`** — locates an installed/downloaded VSTHost engine,
  launches it `--zeus-bridge`, supervises it, relaunches on crash (with
  dead-man's-pedal blacklisting the offender), relays stdio JSON.
- **`VstEngineClient`** — the realtime tap. Maps the ring once at startup;
  per block does memcpy-in → signal `inReady` → bounded wait `outReady` →
  memcpy-out, else passthrough. No alloc, no lock. (§2)
- .NET primitives only: `System.Diagnostics.Process`,
  `System.IO.MemoryMappedFiles.MemoryMappedFile`,
  `System.Threading.EventWaitHandle`.

## 7. Distribution / "Get VSTHost" UX

VST mode is gated on the engine being present. Zeus locates the engine exe —
the installer drops it at **`C:\Program Files\VSTHost\engine\VSTHostEngine.exe`**
(alongside the Tauri shell `vsthost.exe`, which Zeus does **not** use); the
portable zip has the same `engine\VSTHostEngine.exe` layout. Zeus checks the
default install path, then a Zeus-managed download location, then `PATH`. When
no engine is found, Zeus surfaces a **"Get VSTHost"** action linking to
`KlayaR/VSTHost` releases (or, with consent, downloads the portable zip to a
Zeus-managed location and verifies it). The engine is **never bundled** in the
Zeus installer. Auto-installing third-party audio drivers (e.g. VB-Audio)
is explicitly **out of scope** — see §10.

## 8. Mode selector — Native vs VST (decided)

The Audio Suite gains a **processing-mode selector**: **Native** (Brian's
in-process chain — default) or **VST** (the sidecar). **Mutually exclusive** —
exactly one route is hot, so there is no cross-process interleaving to solve and
Brian's path is byte-identical whenever Native is selected. Switching is a
control-thread op between blocks (swap which tap the realtime path calls), never
mid-block. The selector + persisted preference is the one operator-visible
surface → **red-light placement/labels for Brian**, but it changes no existing
default (Native remains default).

## 9. Phased plan

In-process bridge / Native path stays the default throughout; VST mode is opt-in
and gated by a flag until proven.

- **Phase A — Upstream headless bridge (`KlayaR/VSTHost`).** Add `--zeus-bridge`
  mode: `ZeusAudioBridge` (ring ↔ `PluginChain`), strip device commands, keep
  scan/load/editor/preset/levels. Built in the local `VSTHost-main` snapshot,
  pushed upstream by the maintainer. *(separate repo)*
- **Phase B — Bridge protocol freeze.** Land
  `docs/designs/vst-engine-bridge-protocol.md` and mirror it in both repos so
  the two sides build to the same contract.
- **Phase C — Zeus control plane.** `VstEngineProcess`: locate/launch/supervise
  the engine, stdio JSON, scan → surface in the existing Audio Suite plugin
  list. No audio yet. "Get VSTHost" UX (§7).
- **Phase D — Zeus audio plane (behind a flag).** `VstEngineClient` + the ring;
  `ZEUS_VST_ENGINE=1` default OFF. Soak test: kill the engine mid-stream → radio
  audio continues (passthrough proven).
- **Phase E — Wire the mode selector.** Audio Suite **Native vs VST** selector
  (§8) + persisted preference. **Native stays default.** Red-light surface for
  Brian's nod.

## 10. Rejected: VB-Audio virtual cable

Considered auto-installing VB-Audio Hi-Fi Cable + ASIO Bridge to route
mic→VSTHost→radio. **Rejected** — it's a *competing* transport to the bridge,
not complementary (using both double-processes/loops audio). Auto-installing a
kernel audio driver is fragile (admin/reboot/AV/SmartScreen), licensing-
restricted (donationware, redistribution terms), pollutes every app with a
system-wide device, and is not bounded-wait/passthrough-degradable — the
opposite of §2. The shared-memory bridge replaces it. A *manual, documented*
VB-cable route (wiki + self-download link, no auto-install) may exist as a
power-user escape hatch, but is not a Zeus feature.

## 11. Risks / open questions for the maintainer

1. **Realtime IPC latency.** One round-trip per block adds ≤ ~1 block to the
   *mic/TX* path (not RX). Acceptable for SSB/digital voice? Bench on HL2.
2. **Engine provisioning.** Download/verify/version-pin policy for the upstream
   engine binary; what happens when upstream's protocol drifts (version
   handshake in the contract).
3. **GPLv3** — user-downloaded + separate process keeps it out of Zeus's bundle
   (§3); confirm the posture is acceptable.
4. **Mode-selector UX** (§8) — placement/labels. Native stays default.
5. **Default values / UX** — selecting Native must reproduce today byte-for-byte.

## 12. What does NOT change

- RX path, WDSP, protocol, panadapter/waterfall — untouched.
- `AudioChain` realtime contract — honoured, not modified.
- With VST mode off, behaviour is identical to today.
