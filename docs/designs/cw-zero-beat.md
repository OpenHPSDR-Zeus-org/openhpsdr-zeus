# Design: CW Zero Beat (issue #300, PR 1 of 2)

> **Status: ACCEPTED — cleared to implement.**
> Maintainer (Doug, KB2UKA) reviewed and greenlit on 2026-05-15:
> backend approach approved in full, all four UX questions answered
> (Brian's UI bypassed-with-defaults — he can iterate post-merge).
> Task 1 (panadapter FFT flow investigation) completed and folded
> below. Implementation continues on the `iu3qez/cw-zero-beat`
> branch; on-air validation remains a hard merge gate.

Pairs with issue [#300](https://github.com/Kb2uka/openhpsdr-zeus/issues/300).
APF is the sibling feature in a separate PR; this doc covers **Zero Beat only**.

**Goal:** add a one-shot Zero Beat action to Zeus that snaps the VFO onto
the strongest CW carrier in the current passband, with sub-Hz resolution
and graceful fallback when no carrier is present.

## Status legend (per `CLAUDE.md`)

- 🟢 **green** — backend / engine plumbing; agent-autonomous.
- 🟡 **yellow** — operator-felt default or new control surface; maintainer review before merge.
- 🔴 **red** — architecture or visual design; maintainer alignment *before* code lands.

## Background

Issue #300 lists two CW-only features (Zero Beat, APF) and suggests both
go through WDSP entry points (`SetRXAAMSQRun`, `SetRXAAPFRun`). After
reading Thetis source and the WDSP Guide rev 1.23 (NR0V) the picture is
different in a load-bearing way:

### Zero Beat is not a WDSP feature in Thetis

This is common for Thetis given its long history tracing back to
PowerSDR: many features live frontend-side rather than inside WDSP,
and Zero Beat is one of them. The actual Thetis path is purely
display-side FFT peak detection. From
`Project Files/Source/Console/console.cs`:

```csharp
// console.cs:36185-36258 — FindPeakFreqInPassband()
double hz_per_bucket = sample_rate_rx1 / (double)specRX.GetSpecRX(0).FFTSize;
// ... walk bins between filter low/high cuts, track max(I²+Q²) ...
int peak_hz = (int)((max_bucket - zero_hz_bucket) * hz_per_bucket);
return peak_hz;

// console.cs btnZeroBeat_Click — applies the offset:
VFOAFreq += peak_hz * 0.0000010;   // Hz → MHz
```
Keeping Zero Beat outside WDSP is fine for Zeus too. Implications:
1. **No new WDSP P/Invoke is needed** for Zero Beat.
2. The work lives in `Zeus.Server` (read panadapter FFT, find peak,
   retune VFO) and `zeus-web` (trigger + visual feedback).

## Proposed design

### Server-side action

Single new method `RadioService.ZeroBeat(byte rxId = 0)` (see Task 1
findings below — the work fits naturally in `RadioService`, no new
service class needed). It reads the **raw 16384-bin FFT** directly
from the WDSP analyzer via a new `SnapSpectrumTimeout` P/Invoke, NOT
the 2048-pixel pre-zoomed `PanDb` exposed to the frontend. Raw FFT
gives ~2.93 Hz/bin at 48 kHz sample rate (vs ~23 Hz/pixel for the
pixel path with no zoom), and is independent of the operator's
current panadapter zoom level. See the rationale in §"Task 1
findings" below.

1. Check mode is CWL or CWU; else return state unchanged.
2. Begin peak-hold accumulation by repeatedly calling
   `engine.TrySnapRawSpectrum(channelId, magnitudesDb)`. Each call
   blocks for ~33 ms (the next FFT frame at 30 Hz fixed cadence) and
   returns 16384 dB magnitudes. Track `max[i]` per bin across frames.
3. **Phase 1 — coarse tune at 500 ms.** After ~500 ms of peak-hold,
   inside the bins corresponding to the current filter passband, find
   the max bin and:
   - **SNR gate**: if `peak_dBm − median_dBm < 6 dB`, do NOT yet
     abort — fall through to phase 2 and let more signal accumulate.
   - Otherwise: parabolic 3-point interpolation → Δ₁ = `peak_hz −
     target_pitch_hz` (target = `CwOffset.CwPitchHz`, negated for CWL).
     **Retune the VFO by Δ₁ immediately** through the existing tuning
     path.
4. **Phase 2 — fine refinement (up to ~1.5 s more).** Continue
   peak-hold for an additional window of up to ~1.5 s, then recompute
   the peak and parabolic interpolation against the *new* (already
   shifted) passband.
   - If a coarse Δ₁ was applied: compute Δ₂ as the residual and apply
     it. Most cases Δ₂ ≈ 0 ±1 Hz.
   - If phase 1's SNR gate had failed: do the SNR check now. If it
     still fails, abort with "no signal" (no VFO movement, since
     phase 1 didn't move it either).
5. **Parabolic 3-point interpolation** (used in both phases): vertex
   formula on the three dB magnitudes around the peak bin gives ~10×
   sub-bin frequency resolution. Three lines of math, costs nothing.

**Why two-phase instead of one long wait:** a single 2 s window means
the operator presses the button and sees nothing happen for two
seconds — it feels broken, and people will press again. Splitting at
500 ms means the VFO snaps to the right ballpark immediately
(perceived latency ≈ display frame rate, not "two whole seconds"),
and the fine pass that follows is invisible to the operator unless
they happen to be watching the dial — by which point it's already
settled. This is consistent with how other rigs handle the same
problem.

**Why peak-hold at all:** Thetis takes one frame and tells operators
"set display to AVG mode" via tooltip. That fails if the snapshot
lands during an inter-element gap, an operator pause, a QSB null, or
simply when the remote operator's hand is off the key — the "peak"
then is just the loudest noise bin and
the VFO jumps to garbage. Max-holding across multiple frames catches
at least one dit at any sane CW speed (50 ms dit at 25 WPM, 100 ms
at 12 WPM) while noise stays roughly stationary, so signal pulses
clearly clear the noise floor in the held buffer.

**Why baked-in 500 ms / 1.5 s / 6 dB:** these are best-effort tuning
constants, not operator-facing knobs. Exposing sliders would clutter
the CW UI with parameters most operators will never touch.

The numbers themselves are **initial estimates that need on-air
validation before the PR merges.** Unit tests with synthetic FFT
magnitudes can prove the algorithm is correct, but they cannot tell us
whether 500 ms is enough to land within ±10 Hz on a real weak signal
at 25 WPM, whether 6 dB above the median is the right SNR gate for the
panadapter Zeus actually serves, or whether 1.5 s of phase 2 is enough
to settle the residual. Plan: bench-test on real CW traffic (a mix of
slow / fast / weak / strong / fading), tune the constants once based
on what we observe, and only then merge. If a constant needs to change
post-merge we change it in code with a small follow-up PR — still no
slider.

**Why not "correlate the window with the configured CW WPM":**
considered and rejected. The relevant speed is the speed of the
*received* station, not our local keyer setting; correlating to the
local WPM would be wrong as often as right, and there is no clean way
to detect the remote's speed before doing exactly the work this
feature is for. Fixed 500 ms + 1.5 s wins on simplicity and on being
right at any incoming speed.

### Wire surface

```
POST /api/rx/zero-beat
  body: { "rxId": 0 }   // optional; missing body defaults to RX1
  response: 200 → updated StateDto (new VFOA), or
            422 → { error: "no-signal" } with state unchanged
```

The `rxId` field is in place from day 1 to keep the contract
forward-compatible with multi-RX hardware (see §Future). No new DTO
fields on `StateDto`. No `ZeroBeatConfig` record. No new persisted
setting in `DspSettingsStore`. The action is fire-and-forget; its
only durable effect is the VFO move, which already round-trips
through the existing state mechanism.

### Frontend

Button in `zeus-web/src/layout/panels/CwPanel.tsx` invoking the endpoint,
plus `Z` keybind via `use-keyboard-shortcuts.ts`. Mode-gated render to
CWL/CWU. Visual feedback during the action: accent border on the
button. Full details in §Decisions below.

### What is explicitly out of scope for PR1

- **RIT-mode Zero Beat** — Thetis offers it as an alternative target
  (tune RIT by Δ instead of the main VFO). Genuinely useful and
  standard on most modern transceivers, but **Zeus has no RIT/XIT
  plumbing yet** — the only references in-tree are a placeholder
  button in `zeus-web/src/App.tsx:743` and TCI protocol stubs at
  `Zeus.Server.Hosting/Tci/TciSession.cs:1095-1134` that explicitly
  ignore RIT/XIT set commands. There is no `Rit*` field in
  `Zeus.Contracts`, no `RadioService.SetRit(...)`, no persisted
  setting. So RIT-mode Zero Beat is deferred *because the substrate
  doesn't exist*, not because we don't want it. When Zeus grows
  RIT/XIT (separate effort outside #300), Zero Beat should gain a
  RIT-target toggle as a follow-up PR.
- **CAT command** — Thetis exposes `ZZZB`; not in scope.
- **AM / FM / SAM / digital modes** — out of scope; Zero Beat is
  treated as a CW-only feature in this PR.
- **The APF feature** — separate PR, separate design doc.
- **Auto VFO step → 1 Hz when APF is enabled** — this is part of the
  APF feature's UX bridge for the Matched filter (which needs sub-Hz
  alignment). It belongs in the APF PR, not here.

## Task 1 findings — panadapter FFT flow

Where panadapter FFT magnitudes live, who produces them, and what hook
Zero Beat uses.

**Producer:** `Zeus.Server.Hosting.DspPipelineService.Tick()`
(`DspPipelineService.cs:1249`) drives a 30 Hz fixed loop
(`TickPeriod = TimeSpan.FromMilliseconds(1000.0 / 30.0)`, line 65).
On each tick it calls `engine.TryGetDisplayPixels(...)` (line 1342)
which wraps WDSP's `GetPixels` export. Output is a `float[2048]` of
dB magnitudes, **already pixel-resampled and zoom-clipped** by WDSP,
packed into a `DisplayFrame` record (Width=2048, CenterHz, HzPerPixel,
PanDb, WfDb) and broadcast via SignalR.

**Two access paths considered:**

| Path | Resolution @ 48k | Resolution @ 192k | Zoom-dependent | Code to add |
|---|---|---|---|---|
| Pixel `PanDb` (already exposed) | 23 Hz/px no-zoom, ~5.9 @ 4× | 94 Hz/px, ~11.7 @ 8× | yes | ~5 lines (event hook in DspPipelineService) |
| **Raw FFT via `SnapSpectrumTimeout`** | **2.93 Hz/bin always** | **11.7 Hz/bin always** | **no** | ~30 lines (new P/Invoke + engine method) |

**Decision: raw FFT.** The pixel path's resolution depends on whatever
zoom the operator currently has on the panadapter; with a wide-zoom
display Zero Beat lands tens of Hz off. Raw FFT keeps a fixed
2.93 Hz/bin grid at 48 kHz regardless of UI state, which is
exactly what the Matched APF companion (BW 10–30 Hz) needs.

Parabolic 3-point interpolation on top still applies and yields
sub-Hz accuracy in practice.

**WDSP entry point:** `SnapSpectrumTimeout(int disp, int ss, int LO,
double* snap_buff, DWORD timeout, int* flag)` in
`native/wdsp/analyzer.c:1633`. The function blocks until the next FFT
frame is ready (or `timeout` expires), then copies the FFT output into
the caller's buffer as `2 × AnalyzerFftSize` doubles (Re/Im interleaved,
FFT-shifted so the negative-frequency half precedes the positive half).

`AnalyzerFftSize = 16384` (`WdspDspEngine.cs:110`). Caller buffer size
= 32768 doubles ≈ 256 KB, allocated once and reused.

**Bin → Hz math:**
- `hzPerBin = sampleRate / 16384`
- After unpacking, the DC bin sits at index `8192` (centre of the
  unpacked array).
- `hzOffsetFromLO = (binIndex - 8192) * hzPerBin`
- Add the `CwOffset.CwPitchHz` correction for CW pitch.

**Filter passband bins:** `RadioService.Snapshot().FilterLowHz/HighHz`
gives signed Hz around baseband; convert to bin indices around 8192
and walk only those bins for the peak.

**Implementation home:** `RadioService.ZeroBeat(byte rxId = 0)`.
- Zero Beat is fundamentally a VFO-adjustment action; it belongs next
  to `SetVfo` / `SetMode` / `SetFilter` in `RadioService`.
- Injects `IDspEngine` (already available to `RadioService`).
- The pull-on-demand model of `SnapSpectrumTimeout` (each call blocks
  ~33 ms for the next FFT frame) means **no new event/subscription
  surface is needed on `DspPipelineService`**. The two FFT consumers
  (existing `GetPixels` for pan display, new `SnapSpectrumTimeout` for
  Zero Beat) are independent drains and don't interfere.

**No-go zones cleared:** the FFT path does not touch
`audio-client.ts`, PureSignal methods in `WdspDspEngine.cs`, or
`PsAutoAttenuateService.cs`.

## Decisions (UX confirmed by maintainer 2026-05-15)

Doug (KB2UKA) provided the UX answers directly to unblock implementation;
Brian (EI6LF) keeps the right to iterate the UI post-merge. The defaults
below are the implementation contract.

### Visibility
Hide Zero Beat (and APF) controls outside CWL/CWU. Mode-gated rendering
in the React tree — not just disabled.

### Trigger location
Button in `zeus-web/src/layout/panels/CwPanel.tsx` **plus** `Z` keybind
via `use-keyboard-shortcuts.ts`. Mode-gated to CWL/CWU on both surfaces.

### Visual feedback during the action
Button gets an accent border (`var(--accent)`) while running — from
press until both phases complete. Result is then communicated by the
VFO move (or its absence). No spinner, no overlay. Total runtime
≤ ~2 s (500 ms phase 1 + 1.5 s phase 2).

### No-signal path (SNR gate failure)
Silent. The VFO simply does not move. No toast, no red flash, no log
line surfaced to the operator. The button returns to its normal state
at end-of-window, and the operator infers "nothing happened" from the
fact that the dial didn't move. Matches Thetis and convention in most
CW rigs.

### Style constraints (from `CLAUDE.md` + Doug's reminder)
- Tokens from `zeus-web/src/styles/tokens.css` only — never raw hex.
- Reuse existing `.btn` / `.btn.sm` classes.
- Match the wiring shape and sizing of a recent CW/filter button.
- Archivo Narrow is global; no font overrides.

### Thetis-divergent improvements (approved)

Three places where this PR deviates from Thetis, all confirmed by the
maintainer:

1. **Two-phase peak-hold** (500 ms coarse + 1.5 s refinement) instead
   of a single snapshot — addresses Thetis's "set display to AVG mode"
   workaround for the operator-pause / inter-element-gap problem.
2. **Parabolic interpolation** instead of bin-truncation — ~10× sub-bin
   accuracy on top of the raw FFT's already-finer grid.
3. **SNR gate** instead of "silently jump to noise" — refuses to move
   the VFO when no carrier is present, matching the convention of
   every modern CW rig.

All three are pure backend, cost ~tens of lines each, and address real
operator-visible weaknesses in Thetis. None changes any default that
an existing Thetis operator would feel beyond "Zero Beat just works
better".

## Implementation tasks

1. ✅ **Investigate** (done): panadapter FFT flow folded into the
   "Task 1 findings" section above. Decision: raw FFT via
   `SnapSpectrumTimeout`, served from `RadioService.ZeroBeat()`.
2. 🟢 Add P/Invoke for `SnapSpectrumTimeout` in
   `Zeus.Dsp/Wdsp/NativeMethods.cs` and a `TrySnapRawSpectrum(int
   channelId, Span<double> outMagDb)` method on `IDspEngine` (with
   no-op in `SyntheticDspEngine`). Allocate the 32k-double Re/Im
   buffer once inside `WdspDspEngine`, reuse on each call.
3. 🟢 Implement `RadioService.ZeroBeat(byte rxId = 0)`: two-phase
   peak-hold (15 frames + 45 frames at 30 Hz fixed cadence), SNR
   gate at peak − median ≥ 6 dB, parabolic 3-point interpolation,
   VFO retune through existing tuning path. Constants are private
   compile-time fields (no DTO, no persisted setting, no slider).
4. 🟢 Endpoint `POST /api/rx/zero-beat` in `ZeusEndpoints.cs`,
   accepting an optional `ZeroBeatRequest { byte? RxId = 0 }` body.
   Returns 200 + updated `StateDto` on success, 422 with
   `{ "error": "no-signal" }` on SNR gate failure.
5. 🟢 Client binding `zeroBeat()` in `zeus-web/src/api/client.ts`.
6. 🟡 Frontend: button in `CwPanel.tsx` with `var(--accent)` border
   while running, plus `Z` keybind via `use-keyboard-shortcuts.ts`.
   Mode-gated render to CWL/CWU.
7. 🟢 Unit tests on the peak-hold + interpolation + SNR gate (synthetic
   FFT magnitudes, deterministic). No on-air dependency. Prove the
   algorithm; do NOT prove the constants.
8. 🟡 **On-air validation before merge.** Bench-test on real CW
   traffic across the operating envelope: slow (≤12 WPM) and fast
   (~25–30 WPM); weak (near noise floor, SNR gate exercised) and
   strong (S5+); steady and fading. Observe phase-1 landing accuracy,
   phase-2 residual, and SNR-gate false-positive / false-negative
   rate. Record findings in the PR description and adjust the three
   constants if needed before requesting merge. This is the gate
   that's load-bearing — unit tests alone are not enough.
9. 🟢 Lesson: `docs/lessons/cw-zero-beat.md` summarizing the
   Thetis-divergence rationale (peak-hold two-phase, parabolic, SNR)
   *and* the on-air-validated values of the three constants, so it
   survives the PR and future maintainers see *why*, not just *what*.

## Future: multi-RX / multi-slice support

Zero Beat is single-slice today (RX1 only). The shape is forward-
compatible with multi-RX hardware (RX1 + RX2 on the same board) at
near-zero cost.

**Existing Zeus precedent.** The wire format already carries a
`byte RxId` as the first body byte of both `DisplayFrame.cs:62` and
`AudioFrame.cs:54`, currently hard-coded to `0` in
`DspPipelineService.cs:1369, 1419, 1442`. `BoardCapabilities` records
`HasSteppedAttenuationRx2` per board, and `docs/designs/radio-support-plan.md`
already plans an RX2 attenuator UI. So `byte RxId` is the
established disambiguator across the codebase.

**Adopted in this design:**
- Method signature is `RadioService.ZeroBeat(byte rxId = 0)`. Default
  preserves current single-RX behaviour.
- Endpoint accepts an optional body `{ "rxId": 0 }` (and a missing
  body defaults to `0`). This is in-place from day 1 so the wire
  contract does not need to break when RX2 lands.
- Internally, `rxId` maps to WDSP `disp` (and the matching `channel`
  for the RXA chain). A small resolver function centralises the
  mapping rather than hard-coding `disp=0, ss=0, LO=0` at the call
  site.

**Multi-slice (WDSP sub-receivers via different `LO` on the same
display) is explicitly NOT in scope.** No Zeus doc, capability, or DTO
references sub-receivers today; introducing one for Zero Beat would
be speculative. If Zeus ever adds sub-receivers, the same `byte RxId`
slot can grow into a richer slice descriptor (or a sibling byte added
to the wire) without touching the algorithmic core of this feature.

## Notes / receipts

- Thetis source consulted via shallow clone of `ramdor/Thetis` at
  `/tmp/thetis-research`.
- WDSP Guide rev 1.23 by Warren Pratt (NR0V) at
  `Documentation/Radio/WDSP Guide, Rev 1.23.pdf` in the Thetis repo —
  the source of truth for the SPCW (APF) entry points and parameters,
  not consulted for Zero Beat (Zero Beat is not a WDSP feature in
  Thetis).
- N2PK / MW0LGE Discord commentary on the four APF types is captured in
  the sibling APF design doc (to follow).
- Personal operating experience using Thetis in CW-only mode — source
  of the perceived-latency, silent-on-no-signal, and APF/Zero Beat
  companion choices.
