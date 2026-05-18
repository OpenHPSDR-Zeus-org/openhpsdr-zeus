# Proposal: MIDI controller support in Zeus

**Status:** Draft — awaiting maintainer review
**Author:** AI agent survey, for Brian (EI4HQ) review
**Scope:** Introduce MIDI controller input (and, later, LED feedback) to Zeus, taking what is useful from Thetis's `Midi2Cat` without inheriting its Windows-only foundations.

---

## 1. Background: what Thetis ships today (Midi2Cat)

Thetis contains a ~6,000 LOC subsystem called `Midi2Cat` (authored by Andrew Mansfield M0YGG, extended by Chris Codella W2PA for Behringer devices). It maps MIDI controller input to radio CAT operations and, for a handful of devices, drives LEDs back from radio state.

Key facts:

- **244 CAT commands** exposed to mapping (VFO tune, band/mode/filter, AGC, NR/NB, MOX, VOX, mute, RIT/XIT, multi-RX, zoom, CWX, EQ, compander, VAC, stereo diversity, …).
- **Devices named in source:** Behringer CMD PL-1 (full LED), Behringer CMD Micro (partial LED), Behringer generic CMD, Numark DJ2GO2 Touch, DJControl Starlight. Detection is case-insensitive substring match on the device name string.
- **Mappings persisted** as XML DataSet in `%AppData%/midi2cat.xml`, one table per device name, columns added over time with legacy-column fallback on every load.
- **UI:** ~938-LOC WinForms UserControl plus four nested dialogs (Load / Save As / Organise / Pick) for naming, swapping, importing mapping sets. Includes a "learn" mode.

## 2. Complexity hotspots (what not to port)

| Hotspot | Why it's a problem for Zeus |
|---|---|
| **Direct `winmm.dll` P/Invoke** | Hard Windows binding. Callback fires on the MIDI driver thread. Zeus is cross-platform (.NET 8, macOS primary dev, Windows + Linux targets). |
| **Reflection dispatch** | Every MIDI event does a dictionary lookup then `MethodInfo.Invoke` keyed by `CatCmd` enum name. Zero compile-time safety, silent failures on rename. |
| **Tight console coupling** | All 243 handler methods mutate `console.xxx` directly. No abstract command layer, so lifting handlers out is effectively a rewrite. |
| **WinForms UI** | Entire 938-LOC UserControl + four dialogs are throwaway for a web frontend. |
| **XML DataSet persistence** | Schema evolves by column-add; fragile round-tripping with legacy files. |
| **No hot-plug** | Devices enumerated once at startup. USB reconnect needs an app restart. |
| **Device-specific LED strings** | Hex message literals (`"902201"` etc.) scattered through code per Behringer button. Adding a controller = code change. |
| **Callback threading under one lock** | A slow handler blocks all MIDI across all devices. |

## 3. What to simplify (or drop)

1. **Curate the command set.** Start with ~25–35 commands that Zeus's backend actually exposes today (VFO A tune, VFO B tune, band up/down, mode cycle, filter wider/narrower, AGC mode, AGC level, NR on/off, NB on/off, MOX, mute, RIT on/off, RIT tune, zoom, volume, drive, multi-RX gain, …). Grow the list as operators ask. The 244-command Thetis catalogue is bloat.
2. **No reflection dispatch.** Typed command enum + `switch` → `RadioService` / `TxService` calls. Compile-time checked.
3. **No WinForms.** Mapping UI is a React page talking to existing SignalR hub + a small REST surface.
4. **Plain JSON, not XML DataSet.** `midi-mappings.json` alongside Zeus's other config.
5. **Cross-platform MIDI library, not WinMM.** See §5 for library choice.
6. **LED feedback deferred.** It is the messiest, most device-specific part of Midi2Cat and is not required to be useful.
7. **No named preset library on day one.** Single active mapping file. Named presets can come later if demand exists.

## 4. Proposed architecture

Fits Zeus's existing shape: a new class library, a hosted service in `Zeus.Server`, DTOs in `Zeus.Contracts`, events on the existing `StreamingHub`, a new settings page in `zeus-web`.

### 4.1 New project: `Zeus.Midi`

```
Zeus.Midi/
  IMidiEngine.cs            // enumerate, open, close, event stream
  DryWetMidiEngine.cs       // default implementation
  NullMidiEngine.cs         // headless/test/CI
  MidiEvent.cs              // { DeviceName, ControlType, ControlId, Value }
```

- Thin abstraction over the chosen MIDI library.
- Device hot-plug surfaced via the library's watcher.
- No knowledge of Zeus radio commands — pure I/O.

### 4.2 `Zeus.Server.Hosting/MidiService.cs` (hosted service)

- Owns the `IMidiEngine` instance.
- Loads / saves `midi-mappings.json`.
- Dispatches `MidiEvent` through a typed `ZeusMidiCommand` handler that calls the same `RadioService` / `TxService` / DSP methods the SignalR hub already calls (no parallel surface).
- Publishes `MidiLearnFrame` on the hub while learn mode is active.

### 4.3 `Zeus.Contracts` additions

- `MidiDeviceDto { Name, IsOpen, IsMapped }`
- `MidiMappingDto { DeviceName, ControlId, ControlType, Command, Min, Max, Toggle }`
- `ZeusMidiCommand` enum (the curated ~30 commands from §3.1).
- `MidiLearnFrame` (push: device, control id, control type, value).

### 4.4 `StreamingHub` additions

- `ListMidiDevices()`
- `GetMappings()` / `SaveMapping(dto)` / `DeleteMapping(...)`
- `StartLearn(deviceName)` / `StopLearn()` — streams `MidiLearnFrame` while active.

### 4.5 `zeus-web`: `/settings/midi`

- Device list (connected / mapped).
- Per-device mapping table.
- Learn button — grey out other controls, highlight next incoming MIDI event, prompt for target `ZeusMidiCommand`.
- Save / reset.
- **Single-hue amber** per `docs/lessons/dev-conventions.md` — no new palette.

## 5. Library choice

Primary recommendation: **`Melanchall.DryWetMidi`**.

- Pure managed .NET, no native deps — works out of the box on macOS / Windows / Linux.
- `DevicesWatcher` raises events on hot-plug.
- Event callbacks arrive on a managed thread, not the driver thread.
- Actively maintained (MIT).

Alternative considered: `RtMidi.Core` — thinner wrapper over native RtMidi, lower-level, less ergonomic for our needs, requires a native library per platform. Rejected unless DryWetMidi proves inadequate during Phase 1.

**This is a new runtime dependency. Per `CLAUDE.md`, a new NuGet package is red-light and needs explicit approval before it lands in a PR.**

## 6. Mapping file format (sketch)

```json
{
  "version": 1,
  "mappings": [
    {
      "deviceName": "Behringer CMD PL-1",
      "controlId": 16,
      "controlType": "Wheel",
      "command": "VfoATune",
      "min": -64,
      "max": 64
    },
    {
      "deviceName": "Behringer CMD PL-1",
      "controlId": 34,
      "controlType": "Button",
      "command": "Mox",
      "toggle": true
    }
  ]
}
```

- Single-file, human-readable, diffable. No DataSet, no schema migration.
- Path alongside Zeus's existing config (TBD: `%AppData%/Zeus/midi-mappings.json` vs. next to `appsettings.json` — maintainer call).

## 7. Phased plan

| Phase | Scope | Rough effort |
|---|---|---|
| **1** | `Zeus.Midi` library + DryWetMidi integration; `MidiService` enumerates devices and logs events. No mappings, no UI. Verify input arrives from macOS and Windows. | ~1 week |
| **2** | Static JSON-driven mapping for ~10 commands (VFO tune, volume, mode cycle, band up/down, MOX, mute, AGC). Hand-edit the JSON. No UI yet. | ~1 week |
| **3** | React settings page + learn mode + save/load of the single active mapping. Curated command list grown to ~25–35. | 1–2 weeks |
| **4** | Grow the curated command list as operators ask. Ongoing, opportunistic. | as needed |
| **5 (deferred)** | LED feedback for specific controllers (Behringer CMD PL-1 first). Only if there is pull. | separate proposal |

## 8. Testing

- `NullMidiEngine` lets unit tests drive `MidiService` without hardware.
- Integration tests: feed synthetic `MidiEvent`s, assert the right `RadioService` call (e.g. band up from 14 MHz lands on 18 MHz).
- Manual hardware smoke test on at least one cheap controller (Korg nanoKontrol 2, Akai LPD8, or similar) per platform before merging Phase 3.

## 9. Out of scope

- Windows MIDI-over-Bluetooth quirks.
- Network MIDI (rtpMIDI, AppleMIDI).
- MIDI *output* beyond LED feedback (no sequencing, no clock).
- Importing Thetis's `midi2cat.xml`. Mappings are user-specific and quick to recreate in a learn UI; a converter is not worth the maintenance.

## 10. Items requiring maintainer decision (red-light per `CLAUDE.md`)

Per the autonomous-agent boundaries in `CLAUDE.md`, these are not autonomously resolvable:

1. **New runtime dependency** — `Melanchall.DryWetMidi`. OK to add?
2. **Curated command subset** vs. Thetis's 244. Any commands you specifically want in or out of the v1 list? (Draft list in §3.1.)
3. **Mapping file location and format.** Amber-settings next to `appsettings.json`, or under `%AppData%/Zeus/`? JSON shape per §6 acceptable?
4. **LED feedback scope.** Drop entirely, defer to Phase 5, or required for v1 on one specific controller you own?
5. **UI/UX of learn flow.** Single active mapping vs. named preset sets from day one.
6. **Target platforms for v1.** macOS + Windows only, or Linux too?

## 11. Why an issue *and* this doc

The issue is the tracking handle and discussion space. This doc is the versioned record that PRs can reference. When Phase 1 lands, its PR description points back here.
