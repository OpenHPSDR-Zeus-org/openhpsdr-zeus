# Zeus — Project Context for AI Agents

## Project Goal

Zeus is a cross-platform, web-frontend HPSDR client for original-protocol (Protocol 1) radios — Hermes, Mercury/Penelope/Metis, ANAN-class boards, and similar. It replaces the Windows-only **Thetis** client with a .NET 8 backend (`Zeus.Server`) and a Vite + React frontend, keeping **WDSP** as the DSP engine via P/Invoke.

**Reference implementation:** Thetis (C# / WinForms). This is the *sole* authoritative source for protocol and DSP behavior.

## Autonomous-Agent Boundaries

AI agents opening PRs against this repo may autonomously fix:

**Green-light (just do it, open a PR):**
- **Bugs with a clear root cause** — null refs, missing guards, off-by-one, persistence/wiring bugs where the fix is obvious from the symptom
- **Build / CI fixes** — missing NuGet refs, csproj typos, dotnet version bumps, workflow YAML breakage, Vite / npm config fixes
- **Protocol / WDSP compliance fixes** — where the Zeus behavior diverges from Thetis and Thetis source confirms Zeus is wrong. *Exception:* if the fix changes a default that an operator will feel (TX power cap, filter bandwidth, AGC curve, meter scaling), that is red-light — see below.
- **Docs and lessons updates** — additions to `docs/lessons/`, `docs/rca/`, `README.md`. Renames and restructuring are red-light. **README scope:** `README.md` stays tight — one-line radio status per board and a high-level feature list only. Extensive feature write-ups, per-panel guides, and step-by-step how-tos belong in the [GitHub wiki](https://github.com/Kb2uka/openhpsdr-zeus/wiki), not the README.

**Red-light (flag for maintainer review, do NOT merge without approval):**
- **Visual design** — colors, fonts, layout, spacing, typography. The Zeus aesthetic is faithful to the **Hermes Lite 2 hardware front panel**: near-black beveled panel chrome (`--panel-top`/`--panel-bot` gradient) on a blue-gray workspace (`--bg-app` `#657486`), Archivo Narrow type, and a restrained accent system — `--accent` blue `#4a9eff` for focus/state, `--tx` red `#e63a2b` for TX/gain-reduction, `--power` yellow `#ffc93a` for output power, `--orange` `#f28524` reserved for the QRZ button. The single-hue amber `#FFA028` is the **panadapter WebGL trace + meter peak-tick** color (signal-strength visualization, varying alpha — see `gl/panadapter.ts` and `docs/lessons/dev-conventions.md`); it is not a global UI accent and must not be applied to chrome, buttons, or controls. **Source of truth for the global palette is `zeus-web/src/styles/tokens.css`.** Use the existing token variables, never raw hex. Do not propose palette changes.
- **UX behavior** — what a click/drag/scroll does, keyboard shortcuts, panadapter/waterfall axis direction, VFO tuning feel. "Wrong scroll direction" reports are almost always a missed waterfall horizontal-shift, not an axis bug — see `docs/lessons/`.
- **Architecture** — new threads, new dependencies, new NuGet/npm packages, changes to the Zeus.Contracts wire format, signal-routing restructures.
- **Default values** — anything an operator will notice on first connect: TX power, filter widths, AGC, meter calibration, default band/mode, color palette. One bug report is not evidence that the default is wrong for everyone.
- **Feature scope creep** — if the issue says "fix meter," fix the meter. Don't add a new meter, refactor the meter pipeline, or rename the meter types.

When uncertain, implement the minimal fix and note in the PR description that design decisions need maintainer review. The maintainer (Brian, EI6LF) is the sole authority on visual design, UX, and defaults.

## Load-Bearing Invariants

Before touching DSP, protocol, or layout code, skim these — they have bitten us before:

- **`docs/lessons/wdsp-init-gotchas.md`** — WDSP RXA channels MUST open at `state=0` and flip via `SetChannelState(id, 1, 0)` *after* the worker is live. A `-400` meter reading means the xmeter thread didn't run. This ordering is load-bearing; do not reorder init without reading the lesson.
- **`docs/lessons/dev-conventions.md`** — port allocation (backend **6060**, Vite dev **5173**), panadapter trace amber (`#FFA028`, signal-strength visualization only — global palette lives in `zeus-web/src/styles/tokens.css`), getUserMedia on LAN IP quirks.
- **`docs/lessons/hl2-drive-model.md`** — HL2 does NOT use the piHPSDR / Thetis dB drive model. It uses a percentage-based model from the mi0bot openhpsdr-thetis fork. `PaGainDb` on HL2 is a per-band output % (0..100), not decibels. Touching `HermesLite2DriveProfile`, `RadioService.RecomputePaAndPush`, `PaDefaults.GetPaGainDb`, or the PA Settings panel without reading this will produce a radio that silently makes 20% of rated power. The older `hl2-drive-byte-quantization.md` is now a redirect stub — the "calibrate to 26 dB" workaround it documented is obsolete.
- **`docs/references/`** — vendor protocol PDFs + per-radio capability matrix. **If a board-specific doc exists (e.g. `docs/references/protocol-1/hermes-lite2-protocol.md`), read it before inferring behaviour from Thetis or piHPSDR.** The HL2 drive-byte quantisation bug cost two days because this folder wasn't consulted.
- **`docs/rca/`** — per-incident post-mortems. Read the relevant one before "fixing" a symptom that matches.

## Radio-Specific Behaviour — use the abstractions

Zeus supports the full MW0LGE Thetis board lineup on one codebase — Metis (original HPSDR Mercury+Penelope+Metis) / Hermes / HermesII / ANAN-10/10E/100/100B/100D/200D / OrionMkII (the 0x0A wire-byte alias family covering G2 / G2 MkII / G2-1K / 7000DLE / 8000DLE / Apache OrionMkII original / ANVELINA-PRO3 / Red Pitaya) / HermesC10 (ANAN-G2E) / Hermes-Lite 2. The same protocol wire format does NOT mean identical behaviour — different boards honour different fields, use different drive-byte resolutions, and publish different PA gains. **Go through the existing per-board abstractions. Do not hard-code board-agnostic math in the drive / PA / TX path.**

Reference docs (read before touching radio code):

- **`docs/references/protocol-1/thetis-board-matrix.md`** — every board MW0LGE supports, per-board init fingerprint (RX-ADC count / MKII BPF / ADC supply / LR audio swap), volts/amps presence, PA-gain bracket, calibration constants. The cross-reference table at the bottom maps every Thetis surface to the Zeus seam that owns it.
- **`docs/designs/radio-support-plan.md`** — six-phase implementation log; landing SHAs + the canonical naming / default decisions taken during issue #218.

Extant seams (post-#218):

- **`Zeus.Contracts/HpsdrBoardKind.cs`** — single canonical enum across both Protocol 1 and Protocol 2 (P1/P2 split was unified in Phase 4). Wire-byte values stable; serialised as bytes in `zeus-prefs.db`.
- **`Zeus.Server.Hosting/RadioDriveProfile.cs`** — `IRadioDriveProfile.EncodeDriveByte(...)`. HL2 quantises to its 4-bit drive register here; every other board uses the 8-bit default. **Add new board quirks by implementing `IRadioDriveProfile` and extending `RadioDriveProfiles.For(...)` — do not special-case inside `RecomputePaAndPush`.**
- **`Zeus.Server.Hosting/PaDefaults.cs`** — per-board PA-gain and rated-watts seeds. Variant-aware overload (`OrionMkIIVariant`) routes the 0x0A family. New boards slot in at the `TableFor` switch.
- **`Zeus.Server.Hosting/RadioCalibrations.cs`** — per-board TX forward-power calibration buckets (`bridge_volt` / `ref_voltage` / `adc_cal_offset` / `MaxWatts`). Variant-aware overload picks 8000DLE / Apache-OrionMkII-original / G2-1K / G2 within the 0x0A family.
- **`Zeus.Server.Hosting/BoardCapabilitiesTable.cs`** — per-board static fingerprint (`RxAdcCount`, `MkiiBpf`, `AdcSupplyMv`, `HasVolts`, `HasAudioAmplifier`, `HasSteppedAttenuationRx2`, `SupportsPathIllustrator`, etc.). Surfaced via `/api/radio/capabilities` so frontend can gate panels.
- **`Zeus.Contracts/OrionMkIIVariant.cs`** — disambiguates the 0x0A wire-byte alias family (G2 / G2_1K / Anan7000DLE / Anan8000DLE / OrionMkII / AnvelinaPro3 / RedPitaya). Default G2. Persisted in `PreferredRadioStore`. Read via `RadioService.EffectiveOrionMkIIVariant` and surfaced via `/api/radio/variant`.
- **`RadioService.ConnectedBoardKind`** — the authoritative "what am I talking to?" for everything downstream. Pair with `EffectiveOrionMkIIVariant` for 0x0A boards.

Anti-pattern to watch for (the one KB2UKA's PA-menu refactor fell into): adding a calibration or encoding step that's correct for the radio in front of you (e.g. ANAN G2) and untested on other boards. Drive / TX / PA changes must be sanity-checked against HL2 at minimum; if a change *can't* be tested on HL2 locally, flag it explicitly in the PR so the maintainer can bench-test before merge.

## Debugging Discipline

- **Log what's on the wire, not what you think is on the wire.** `Protocol1Client.TxLoopAsync` already prints `p1.tx.rate pkts=... drv=... peak=...` at 1 Hz during MOX/TUN; when TX power looks wrong, read that log before theorising. Two days of bandpass / amplitude / rate phantoms on the HL2 bug ended with one line showing `drv=48`, which was the whole answer.
- **Log boundary-call arguments before blaming library internals.** When something WDSP-shaped misbehaves, the first suspect is the values our C# P/Invoke passed in — not a WDSP bug. Log the inputs at the P/Invoke seam, then read the library source.
- **Verify against Thetis source, not docs — but read the board-specific reference doc FIRST.** Protocol 1 documentation is incomplete and occasionally wrong; Thetis is the ground truth for ANAN-class radios. For HL2, the truth lives in `docs/references/protocol-1/hermes-lite2-protocol.md` and in `mi0bot/openhpsdr-thetis` (the HL2-specific Thetis fork), NOT in `ramdor/Thetis`.
- **Don't flip axes unilaterally.** If a panadapter or waterfall "feels backwards," investigate the horizontal-shift path before inverting frequency direction.

## Build & Run

Backend + frontend run independently during dev:

```bash
# backend (listens on :6060)
dotnet run --project Zeus.Server

# frontend (Vite dev server on :5173, proxies /api and /hub to :6060)
npm --prefix zeus-web run dev
```

Full details, dependency list, and native WDSP build in `README.md` and `native/`. Do not duplicate them here.

## Commits & PRs

- **Never mention Anthropic or Claude** in commit messages. This is a hard rule — see `/Users/bek/CLAUDE.md`.
- Conventional prefixes (`feat:`, `fix:`, `docs:`, `refactor:`) are preferred but not strictly enforced; match the style of recent `git log` output.
- Ensure the solution builds (`dotnet build Zeus.slnx`) and any existing tests pass before opening a PR.
- For worktrees, use the sibling layout `OPENHPSDR-Nereus.Worktrees/<branch_with_underscores>/`.

## Architecture Snapshot

- **`Zeus.Contracts`** — wire format shared between server and web (frames, DTOs, enums). Changes here are red-light.
- **`Zeus.Protocol1`** — HPSDR original-protocol UDP client, discovery, packet parsing, TX IQ ring.
- **`Zeus.Protocol2`** — HPSDR Protocol 2 (ANAN-class / Orion) UDP client: discovery, DDC RX streaming, TX IQ/mic path.
- **`Zeus.Dsp`** — DSP engine abstraction (`IDspEngine`), synthetic and WDSP implementations, TX-stage meters.
- **`Zeus.Server`** — ASP.NET host, SignalR `StreamingHub`, radio / DSP / TX pipeline services, discovery.
- **`zeus-web`** (frontend) — Vite + React, connects to the hub, renders panadapter/waterfall/VFO/meters.

When in doubt about where code belongs, match the existing project's single responsibility rather than introducing a new one.
