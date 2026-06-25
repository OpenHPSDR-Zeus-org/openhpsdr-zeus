# OpenHPSDR Zeus voice audio chain — Phase 0 master PRD

**Issue:** [#332](https://github.com/Kb2uka/openhpsdr-zeus/issues/332) · **Phase:** 0 (research + sign-off, **docs-only**) · **Branch:** `feature/332-audio-chain-phase0` · **Authority:** KB2UKA per [feedback_audio_plugin_authority](https://github.com/Kb2uka/openhpsdr-zeus) (Brian retains plugin-system architecture; audio-chain content is KB2UKA's call).

This document synthesizes three Phase 0 research streams into the unified plan and the sign-off list. The full research lives in the sibling files under `docs/proposals/audio-chain/` — this PRD points at them rather than restating them.

| Sibling | Topic |
|---|---|
| `audio-chain/01-aethersdr-and-external-deps.md` | AetherSDR chain analysis (reference-only), GPL-2/GPL-3 license posture, external-dependency evaluation per v1 block |
| `audio-chain/02-wdsp-gap-analysis.md` | What WDSP already exposes vs. what we hand-author vs. what we pull from external libraries, per v1 block |
| `audio-chain/03-ux-and-integration.md` | Chain-panel UX, slot semantics, CPU budget, integration with `WdspDspEngine` |

---

## 1. What's locked

### v1 block list — KB2UKA-locked 2026-05-17

Five blocks, in proposed signal order:

1. **8-10 band parametric EQ** (per-band freq / gain / Q)
2. **Compressor** — single-stage VCA-style (threshold / ratio / attack / release + makeup gain)
3. **Exciter** — Aural-Exciter-style top-end harmonic enhancement (frequency / amount / mix)
4. **Bass enhancer** — Aphex 204 "Big Bottom" / Waves MaxxBass-style **psychoacoustic missing-fundamental synthesis** (NOT a low-frequency boost — synthesizes upper harmonics of low content so the ear perceives bass without the radio transmitting frequencies the antenna can't radiate)
5. **Reverb** — short-tail spatial enhancement (size / decay / mix)

Explicit non-v1 (worth surfacing to KB2UKA as "want to add these?" rather than panel work to skip silently): Gate, De-esser, Tube saturation, Limiter (downstream WDSP ALC covers it). Leveler stays in WDSP upstream of the new chain.

### Deployment model — each block is a registry plugin

Per Brian's plugin system rebuild ([PR #368](https://github.com/Kb2uka/openhpsdr-zeus/pull/368), merged to `develop` at `a5f5df4` 2026-05-17), each of the five blocks ships as a **separate Zeus-native registry plugin**, NOT as code baked into Zeus core. The blocks are managed .NET assemblies authored by Zeus contributors and distributed through `OpenHPSDR-Zeus-org/openhpsdr-zeus-plugins`. They are **not VST3 plugins** and have no relationship to the Steinberg ecosystem; the operational "MaxxBass" / "Aphex 204" / "Aural Exciter" naming refers to the well-known *processing techniques* we reimplement, not to any third-party plugin product.

Each block implements:

- `IZeusPlugin` (base)
- `IAudioPlugin` — `Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx)`, realtime, no-allocate, no-lock, no-IO
- `IBackendPlugin` — parameter get/set under `/api/plugins/{id}/...`
- `IUiPlugin` — operator-facing parameter panel in the chain-panel tile

Slot routing comes from `plugin.json` (`audio.slot = "tx.pre-cfc"` for all five blocks). Block size / sample rate / channel count are negotiated via `AudioPluginRequirements` so the same plugin runs unchanged across Protocol 1 (1024 frames @ 48 kHz) and Protocol 2 (2048 frames @ 96 kHz).

### Source-of-truth integration seam

The integration seam is **shipped** as of #368, no longer speculative:

- `Zeus.Plugins.Contracts/Audio/AudioBlockContext.cs` — `AudioBlockContext` (read-only ref struct: `SampleRate`, `Channels`, `Frames`, `SampleTime`, `Mox`), `AudioPluginRequirements` record, `IAudioHost` interface (exposes `CurrentSampleRate` / `CurrentChannels` / `CurrentBlockSize` / `Slot`).
- `Zeus.Plugins.Contracts/Extensions/IAudioPlugin.cs` — the realtime-audio extension interface our blocks implement.
- `Zeus.Plugins.Host/Audio/AudioChain.cs` — generic 8-slot serial chain (stays in core).
- `Zeus.Server.Hosting/AudioPluginBridge.cs` — hosted service that taps `WdspDspEngine.ProcessTxBlock` via `SetTxAudioPluginHandler`. Bit-identical passthrough when no audio plugins are loaded (single `Span.CopyTo`, no virtual dispatch).

The `Process` call gets a `ReadOnlySpan<float> input` and `Span<float> output` of `Requirements.BlockSize * Requirements.Channels` planar floats. In-place processing (copy input to output, then mutate output) is acceptable. Bypassed slots **should** `input.CopyTo(output)` rather than skip the call — the host handles chain-disabled short-circuit.

### License posture

- **AetherSDR is GPL-3-or-later**; Zeus is **GPL-2-or-later**. Copying AetherSDR `.cpp` would convert Zeus to GPL-3, breaking Zeus's existing GPL-2 license contract with operators. **Clean-room rule:** read AetherSDR headers / comments / paper citations for algorithm structure, never copy implementation.
- AetherSDR's `Client*.h` files are catalogued in `audio-chain/01-aethersdr-and-external-deps.md` with an explicit "do this instead" mapping per tempting file.
- **External libraries we DO pull in** must be GPL-2-or-later-compatible (BSD, MIT, public-domain, Apache 2.0). Per-library evaluation is in the sibling doc.

### Per-block source plan (Phase 1 scoping)

| Block | Source | Notes |
|---|---|---|
| 8-10 band parametric EQ | **HAND-AUTHOR** (cascaded biquad IIR per Audio EQ Cookbook) | WDSP `eq.c` exists but exposes only on/off toggle, not per-band tuning — cleaner to author against EQ Cookbook formulas than wrap WDSP's opaque API |
| Compressor | **HAND-AUTHOR** (envelope follower + gain stage) | Same WDSP-cookie story — `compress.c` exists, opaque tuning API. Hand-author keeps the parameter surface honest |
| Exciter | **HAND-AUTHOR** (highpass + waveshaper + mix) | No WDSP primitive. ~150-250 lines. No dependencies. |
| Bass enhancer | **HAND-AUTHOR** (port of Bankstown's MIT Rust LV2 to managed C#) | Bankstown ([github.com/chadmed/bankstown](https://github.com/chadmed/bankstown)) is the only license-clean public Family B implementation. ~150 Rust lines port to ~250 C# lines per the gap analysis. Zeus would become the first HPSDR client to ship this technique. |
| Reverb | **EXTERNAL-DEP candidate** — verblib (MIT/public-domain single-file C89 Freeverb) | Hand-authoring a passable reverb is its own project (~400 lines + tuning). verblib via P/Invoke is 1 file + ~30 lines of bindings. **Open question** (#3 below) — managed-only stance might force hand-author. |

### CPU budget

Per the UX-and-integration analysis, total all-on chain CPU is **~16-22% of one core** on a 12-core M-series Mac running Zeus desktop mode. Today's Zeus baseline is 14-34% single-core during TX; chain adds ~18% peak, landing the total around 40-48% single-core (~3.5% of total system CPU). Reverb is the heaviest at 6-8%; EQ + Compressor are 3-4% each; Exciter and Bass enhancer are 3-4% each.

If field telemetry shows sustained > 80% single-core, defaults can be tuned (reverb off, bass enhancer off) without code changes.

### UX panel

The chain panel sits in **Settings → Audio Tools** as a tab next to CFC. Master enable + preset dropdown at the top, then five collapsible block cards, then per-block bypass + whole-chain bypass for A/B comparison. Full UX prose lives in `audio-chain/03-ux-and-integration.md`. All colors via existing `tokens.css` tokens; no raw hex. Fonts unchanged (Inter UI, JetBrains Mono numerics). Meter slots reuse existing `HBarMeter` primitive.

### Sensible-defaults principle

A first-time operator who enables the master toggle and turns on every block should **not sound worse than no chain**. Default state per block:

- EQ: flat (every band gain = 0 dB)
- Compressor: 3:1 / -18 dBFS threshold / 5 ms attack / 100 ms release / 0 dB makeup
- Exciter: minimal mix (10%) on a useful default frequency (3-5 kHz)
- Bass enhancer: minimal amount (10%) on a low default frequency (100-150 Hz)
- Reverb: 0% mix (effectively off until operator dials it in)

---

## 2. Sign-off questions

Per [feedback_audio_plugin_authority](https://github.com/Kb2uka/openhpsdr-zeus), all algorithm / block-content / default-value questions go to **KB2UKA**. System / contracts / loader questions go to **Brian**.

### KB2UKA decides

1. **Bass-enhancer Family A vs Family B.** Family A = Aphex 204 dynamic-EQ + saturation (more bass). Family B = MaxxBass missing-fundamental harmonic synthesis (fake bass without transmitting lows). On an HF antenna that can't radiate sub-100 Hz, Family B is the technically correct call. **Confirm Family B** before Phase 1 starts.
2. **EQ band count.** Fixed 10 (simpler defaults, fits the "10-band parametric EQ" naming convention) or operator-tunable 8-10 (more flexible, more UI complexity)?
3. **Reverb library path.** Hand-authored managed Schroeder reverberator (~300-400 lines, no native dep) vs. verblib P/Invoke (1 file + ~30 lines bindings) vs. C# port of a FOSS reverb (e.g. mverb)? Managed-only is the simplest distribution story for a registry plugin.
4. **Exciter + bass enhancer combined panel or separate?** The actual Aphex 204 hardware combined them as a single front panel ("Aural Exciter + Big Bottom"). Two separate cards is cleaner block-as-plugin model; one combined card preserves operator familiarity.
5. **Chain reorderability.** Fixed order EQ → Comp → Exciter → Bass → Reverb (simpler UI) or operator drag-to-reorder?
6. **Parameter visibility tier.** All parameters always visible (AetherSDR pattern) or Basic / Advanced split (modern voice-plugin convention)?
7. **Bass-enhancer source path.** Direct C# port of Bankstown's algorithm (MIT, clean-room) vs. FFI to Bankstown as a Rust cdylib vs. independent algorithm derived from the MaxxBass paper directly? Pure C# port keeps the managed-only distribution story; FFI adds a Rust toolchain dependency we don't currently have.
8. **Initial Phase 1 block.** Recommended: **Compressor first** — simplest algorithm (well-understood envelope-follower + gain stage), shortest path to validating the end-to-end pipeline (build → package as registry plugin → install → load → process → meter → persist params). Once the pipeline is proven on one block, the harder blocks (EQ UX, bass enhancer algorithm, reverb library choice) can be built without architecture risk.

### Brian decides (system / contracts)

These came out of Brian's prompt to KB2UKA dated 2026-05-17 about the broader plugin-system restructuring. Audio-chain work flushes them out as concrete questions:

9. **Multi-slot conflict policy.** `AudioChain` is 8 slots serial. If two installed plugins both declare `audio.slot = "tx.pre-cfc"`, what happens today? (Last-wins? Error? User picks order?) For five blocks all targeting `tx.pre-cfc`, the chain order matters — `AudioChain`'s current implementation may need a chain-position field on the manifest, or an operator-facing reorder UI.
10. **Native bridge distribution model.** Our v1 blocks are managed-only (assuming KB2UKA rules out verblib P/Invoke and the Bankstown Rust FFI in Q3/Q7 above). If any future plugin needs a native binary, does the binary ship inside the plugin zip per-OS, or via a separate per-OS download path? Worth establishing the policy now even though v1 doesn't trigger it.

### Already locked (not asking again)

- License posture: clean-room reference-only against AetherSDR (GPL-2 ↔ GPL-3 incompatibility).
- v1 block list: 5 blocks per §1 above, KB2UKA-locked.
- Insertion point: post-Leveler / pre-CFC (matches existing `AudioPluginBridge` TX seam).
- Visual design constraint: existing `tokens.css` palette + Inter / JetBrains Mono + existing meter primitives.

---

## 3. What ships in Phase 0 (this PRD)

- This document (`docs/proposals/audio-chain-phase0.md`).
- The three sibling research files in `docs/proposals/audio-chain/`.
- **No code.** Phase 0 is docs-only, green-light per `CLAUDE.md` (additions under `docs/proposals/`, no architecture / protocol / UI / defaults change).

When this PR merges, Phase 0 is closed. Phase 1 begins with KB2UKA's answers to the ten sign-off questions above.

---

## 4. Phase 1 entry plan (preview, NOT in scope of this PR)

1. **Repo setup** for the first plugin. Either:
   - A new repo `Kb2uka/zeus-plugin-compressor` (matches Brian's stated pattern of "one plugin = one repo")
   - A shared monorepo `Kb2uka/zeus-audio-chain-plugins` containing all five plugins (simpler ops; needs Brian's input)
2. **Skeleton plugin** implementing `IZeusPlugin + IAudioPlugin` with a no-op `Process` (passthrough). Validates: build → package as zip per the manifest schema → SHA-256 → registry entry → install via Zeus's `POST /api/plugins/install` → AssemblyLoadContext load → tap on `AudioPluginBridge` → `Process` called → uninstall.
3. **First real algorithm** — recommended: Compressor. Smallest correct implementation ships first.
4. **Operator-facing UI** for the compressor via `IUiPlugin` + `IBackendPlugin` for parameter persistence.
5. **Bench verify** on HL2 at 28.4 MHz (the only safe TX frequency per [`CLAUDE.md`](https://github.com/Kb2uka/openhpsdr-zeus/blob/main/CLAUDE.md)).
6. **Iterate**: EQ → Exciter → Bass enhancer → Reverb on the proven pipeline.

---

## 5. Out of scope (explicit)

- **VST3 third-party plugin support.** The existing VST host in Zeus core (PR #368) is queued for removal once the native chain ships — per #332's Phase 7 plan. **Brian's separate proposal** to extract the VST host as its own external plugin (so operators retain third-party VST3 capability) is NOT being adopted here — KB2UKA's plan is removal, not extraction. This is a divergence worth coordinating with Brian before VST code starts getting deleted, but it's not Phase 0 / Phase 1 work.
- **RX-side audio plugins.** All five v1 blocks are TX-only. Brian's prompt proposes adding an `SetRxAudioPluginHandler` mirror; that work doesn't block our chain and isn't part of this proposal.
- **Block presets library.** Per-block presets ("SSB contest", "FT8 macro", "podcast voice") are queued for Phase 1.6 or Phase 2. v1 ships with sensible defaults + operator-editable parameters only.
- **Multi-band EQ curve display.** A live spectrum-overlay EQ curve panel is a Phase 2 enhancement. v1 EQ is per-band sliders only.
- **Operator-tunable chain CPU budget.** No "limit each block to X% CPU" knob in v1. Defaults plus operator-disable-blocks is the v1 mitigation.

---

## 6. References

- Audio EQ Cookbook (Bristow-Johnson): <https://webaudio.github.io/Audio-EQ-Cookbook/audio-eq-cookbook.html>
- Bankstown (MIT, Rust LV2 Family B bass enhancer): <https://github.com/chadmed/bankstown>
- verblib (MIT/public-domain single-file Schroeder reverb): <https://github.com/blastbay/verblib>
- AetherSDR (GPL-3 reference-only): <https://github.com/ten9876/AetherSDR>
- Larsen & Aarts, *Audio Bandwidth Extension* (Wiley 2005) — psychoacoustic-bass theory
- Aphex 204 owner's manual: <https://cdn.aphex.com/assets/pdf/Aphex_Exciter_OM.pdf>
- PR #368 unified plugin system: <https://github.com/Kb2uka/openhpsdr-zeus/pull/368>
- Issue #332 RFC: <https://github.com/Kb2uka/openhpsdr-zeus/issues/332>
- CLAUDE.md autonomous-agent boundaries: `/CLAUDE.md`

---

**Phase 0 complete on merge of this PR.** Awaiting KB2UKA sign-off on the ten questions above before Phase 1 begins.
