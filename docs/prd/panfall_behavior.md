# Panadapter / Waterfall Behavior — PRD

**Beads issue:** zeus-nnc
**Status:** spec, awaiting implementation
**Author:** Brian Keating (EI6LF), drafted 2026-05-23
**Supersedes UX from:** issue #427 (CTUN toggle introduced), commit 4491ad9 (#461 — band-change retune)

---

## Goal

Collapse Zeus's panadapter/waterfall tuning model into a single, intuitive interaction:

1. CTUN behavior (frozen hardware NCO, dial roams) becomes the **only** mode. The toggle button and all `CtunEnabled` plumbing is removed.
2. Press-and-drag horizontally on the panadapter or waterfall pans the view across the radio's already-sampled bandwidth. The radio retunes only when a drag would otherwise extend the viewport beyond the IQ capture window.

Single tuning model, no operator-visible mode switch, no hidden state.

## Background

Today Zeus has two tuning modes selected by a CTUN button in the bottom transport bar:

- **CTUN OFF (legacy):** panadapter clicks call `setVfo`, which retunes the hardware NCO so the clicked frequency becomes the new center. The view always centers on `RadioLoHz == vfoHz`.
- **CTUN ON (`#427`):** the hardware NCO is frozen at the value `vfoHz` had when CTUN was toggled on (`RadioLoHz`). Subsequent `setVfo` calls move only the dial; WDSP's `shift` stage relocates the IF so the operator's tuned signal still demodulates. The dial cursor visually roams across a stationary spectrum.

The existing drag handler in `zeus-web/src/util/use-pan-tune-gesture.ts:309` interprets a drag as repeated `setVfo` calls (i.e. dial tuning). Combined with CTUN-on, drag-to-tune lets the dial roam within the frozen viewport but cannot bring new spectrum into view — the visible window is locked to `RadioLoHz`.

The desired interaction (this PRD) is the inverse: drag moves the **view** through the IQ capture window, and the dial stays anchored to its true frequency.

## Definitions

- **`vfoHz`** — the operator's tuned frequency. Where the dial cursor sits; what gets demodulated.
- **`RadioLoHz`** — the radio's hardware NCO center frequency. Defines the center of the IQ stream the radio is capturing.
- **Sample bandwidth (`sampleBw`)** — the width of the IQ window the radio is capturing, equal to the radio's sample rate in Hz (e.g. 192_000 on a 192 ksps configuration). Brian's default working setup uses ~19.2 kHz at low sample rate; the spec is rate-agnostic.
- **Viewport span (`viewportSpan`)** — the frequency width currently displayed on the panadapter, set by the zoom level. Always `≤ sampleBw`.
- **Viewport center** — the frequency at the horizontal center of the visible panel. Equals `RadioLoHz + viewportOffsetHz`.
- **`viewportOffsetHz`** — frontend-only state introduced by this PRD. Hz offset of the viewport center from `RadioLoHz`. Range constrained so the viewport stays inside the IQ window; outside that range triggers a retune.

## Part 1 — Remove the CTUN toggle

CTUN behavior becomes implicit and unconditional. The toggle, its persisted flag, and all conditional code paths are deleted.

### Backend changes

**`Zeus.Contracts/Dtos.cs:259`** — remove `CtunEnabled`. Keep `RadioLoHz` (still needed; now always reflects the hardware NCO center, which is independent of `vfoHz` except at hydration).

**`Zeus.Server.Hosting/RadioStateStore.cs:157`** — drop the persisted `CtunEnabled` field from the LiteDB schema. On hydrating an older prefs DB, ignore any stale `CtunEnabled` value (treat as always-on). `RadioLoHz` continues to be persisted so the operator's frozen NCO survives restart.

**`Zeus.Server.Hosting/RadioService.cs`** —
- Delete the `ToggleCtun` method and remove all `_state.CtunEnabled` reads.
- `SetVfo` keeps the auto-recenter heuristic from #461 but drops the `if (ctun)` gate so it applies unconditionally. Small dial movements (resulting shift stays inside both the visible panadapter span minus a 5 % inset, and inside ±0.46×sample_rate IF capacity) leave `RadioLoHz` alone — the frozen-NCO ideal. Large dial movements (band-button jumps, CAT/TCI external sources) trigger an auto-retune: `RadioLoHz := EffectiveLoHz(mode, newDial)` plus a P1 `SetVfoAHz`. External callers (`fromExternal=true`) always retune regardless of geometry, matching Thetis `CATChangesCenterFreq=true`.
- Operator-driven "pure pan" movements (panadapter drag-release past the IQ window edge) take the explicit `POST /api/radio/lo` path instead, leaving `VfoHz` untouched.
- On reconnect / hydration, if the persisted `RadioLoHz == 0` (fresh DB or migrated-from-CTUN-off DB), initialize `RadioLoHz := vfoHz`. Otherwise restore the persisted value as today.

**`Zeus.Server.Hosting/DspPipelineService.cs`** —
- The conditional WDSP `shift` stage setup (around `:540`) becomes unconditional. The shift always equals `vfoHz − RadioLoHz`.
- The fresh-channel-open replay at `:840` keeps the same behavior.

**`Zeus.Server.Hosting/ZeusEndpoints.cs`** — delete the `/api/radio/ctun` endpoint at `:309`. Add a new endpoint:

- `POST /api/radio/lo` with body `{ hz: number }` → calls a new `RadioService.SetRadioLo(hz)` that updates `RadioLoHz` directly (hardware retune + WDSP shift adjusted to keep `vfoHz` audible). Returns the updated `StateDto`. Returns 400 if `hz` is out of range for the connected radio.

### Frontend changes

**`zeus-web/src/App.tsx:775-793`** — delete the CTUN `<button>`.

**`zeus-web/src/state/connection-store.ts:66`** — remove `ctunEnabled` from the store state and from `applyState`'s destructuring. Keep `radioLoHz`.

**`zeus-web/src/api/client.ts:222`** — remove `setCtun`. Remove the `CtunEnabled` field from any DTO interfaces. Add `setRadioLo(hz: number)` calling `POST /api/radio/lo`.

**`zeus-web/src/api/client.ts:482`** — drop the legacy-server fallback that defaulted `CtunEnabled` to false. The frontend assumes CTUN-style behavior unconditionally.

**`Panadapter.tsx`, `Waterfall.tsx`, `PassbandOverlay.tsx`, `gl/panadapter.ts`, `gl/shaders.ts`** — comments mentioning "when CTUN is on/off" updated to reflect the unconditional model. The rendering math already handles the dial-roams case; nothing functional changes here in Part 1.

### Tests

- `tests/Zeus.Server.Tests/TxAudioIngestTests.cs`, `MicGainEndpointTests.cs`, and any other test that toggles CTUN or asserts on `CtunEnabled` — rewritten to assume CTUN-on semantics or deleted.
- Add a new test for `POST /api/radio/lo` covering: in-range set, out-of-range rejection, and the invariant that `vfoHz` is unchanged when only `RadioLoHz` moves.

## Part 2 — Drag = pure viewport pan

The drag handler in `zeus-web/src/util/use-pan-tune-gesture.ts` is rewritten to pan the viewport visually, not to tune the dial. Click-without-drag (movement ≤ `CLICK_SLOP_PX = 3`) continues to tune the dial via `setVfo`.

### State

A new frontend-only piece of state, `viewportOffsetHz`, lives in the display store (or panadapter view state — exact location decided at plan time, but it is **not** persisted and **not** sent to the server during the drag).

The panadapter and waterfall render the spectrum centered on `RadioLoHz + viewportOffsetHz`. The dial cursor X position is computed from `(vfoHz − (RadioLoHz + viewportOffsetHz)) / viewportSpan`; if `|vfoHz − viewportCenter| > viewportSpan / 2` the cursor is clipped off-screen but the dial frequency is unchanged.

### Drag lifecycle

**Pointer down:**
- Capture `startX`, `startViewportCenterHz = RadioLoHz + viewportOffsetHz`, `viewportSpan`.
- No state change yet.

**Pointer move:**
- If `|x − startX| ≤ CLICK_SLOP_PX`, do nothing (preserve click-to-tune).
- Otherwise: `viewportOffsetHz := (startViewportCenterHz − RadioLoHz) − (dx / canvasWidth) × viewportSpan`
  - Polarity matches the existing grab-and-pull idiom (drag right → see lower freqs).
- No POST. No `setVfo`, no `setRadioLo`.
- The panadapter and waterfall repaint with the new offset; the dial cursor visually slides off the edge if dragged past it.

**Pointer up:**

Determine whether the dragged viewport sits entirely inside the IQ capture window:

```
viewportLowerEdge = (RadioLoHz + viewportOffsetHz) − viewportSpan / 2
viewportUpperEdge = (RadioLoHz + viewportOffsetHz) + viewportSpan / 2
sampleLowerEdge   = RadioLoHz − sampleBw / 2
sampleUpperEdge   = RadioLoHz + sampleBw / 2

inside = viewportLowerEdge ≥ sampleLowerEdge AND viewportUpperEdge ≤ sampleUpperEdge
```

- **If `inside` (the common case):** leave `viewportOffsetHz` as-is. The radio keeps capturing the same IQ window; the panadapter continues to render the offset view. The dial may be off-screen but still audibly demodulating (it's inside the IQ capture).

- **If not inside:** retune `RadioLoHz` so the new sample window adjoins the released viewport in the pan direction:
  - Pan up (`viewportOffsetHz > 0`): `newRadioLoHz = viewportLowerEdge + sampleBw / 2` — new IQ window's bottom edge = current viewport's bottom edge.
  - Pan down (`viewportOffsetHz < 0`): `newRadioLoHz = viewportUpperEdge − sampleBw / 2` — new IQ window's top edge = current viewport's top edge.

  Then, **before** the round-trip to the server completes:
  - Optimistically update `RadioLoHz` in the store to `newRadioLoHz`.
  - Recompute `viewportOffsetHz := (RadioLoHz_old + viewportOffsetHz_old) − newRadioLoHz`. This preserves the on-screen position of every frequency across the retune — no visual jump.
  - Call `POST /api/radio/lo {hz: newRadioLoHz}`; reconcile the returned `StateDto` on response.

- **`vfoHz` is never modified by a drag**, on any code path. The dial stays at its true Hz throughout — even if the resulting `RadioLoHz` after a retune places `vfoHz` outside the new IQ window. In that case the signal goes silent and the dial cursor is hidden off-screen; the operator regains hearing by click-to-tuning somewhere inside the new visible window or by a band/preset change.

### Click-without-drag

Unchanged from today: a pointer release with `|dx| ≤ CLICK_SLOP_PX` calls `setVfo` with the clicked frequency (resolved through the current viewport center + offset). The dial moves to the click point; `RadioLoHz` is not affected.

### Wheel, pinch, alt-drag

Unchanged. Wheel tunes the dial (`nudgeVfo`); shift-wheel zooms; alt-drag pans the background map. Multi-touch pinch zooms the spectrum.

### Mobile / touch parity

Already handled by PointerEvents. No new code needed beyond keeping the existing pointer-capture + multi-touch branches in `use-pan-tune-gesture.ts` working with the new offset-based drag.

## Edge cases & invariants

- **Drag past the radio's overall tunable range** — `setRadioLo` rejects with 400; frontend reverts the optimistic `RadioLoHz` and snaps `viewportOffsetHz` back to the last valid offset.
- **Zoom changes during a drag** — interrupted: the in-flight drag is cancelled (pointer capture released) and the offset is preserved. Re-press to continue panning.
- **Band button / preset selection while a non-zero `viewportOffsetHz` is held** — reset `viewportOffsetHz := 0` when the user invokes any explicit tune-to-frequency action (band buttons, preset clicks, typed frequency, click-to-tune). Existing behavior is preserved — the new state just gets cleared on those paths.
- **Frame reconnect / hub re-handshake** — `viewportOffsetHz` is frontend-only and resets to 0 on disconnect. No persistence.
- **Multiple receivers** — out of scope for v1. Multi-panadapter support (see `project_multipanadapter_resume`) will require per-RX offsets when revived.

## Migration

- Existing prefs DBs with `CtunEnabled = false` and `RadioLoHz = 0`: on first hydrate, set `RadioLoHz := vfoHz` so the radio resumes at the previously tuned frequency with the new always-CTUN model.
- Existing prefs DBs with `CtunEnabled = true` and a non-zero `RadioLoHz`: behave exactly as before — `RadioLoHz` restored from persistence, dial restored to `vfoHz`.
- No web-frontend cache to clear; the removed `CtunEnabled` field is silently ignored if a stale DTO arrives during a rolling deploy.

## Acceptance criteria

- CTUN button no longer appears in the UI.
- No `CtunEnabled` field on the wire; no `/api/radio/ctun` endpoint.
- `POST /api/radio/lo` works, moves `RadioLoHz` without touching `vfoHz`, and returns the updated `StateDto`.
- Click on panadapter → dial moves to the clicked frequency (existing behavior, unchanged).
- Press-and-drag on panadapter or waterfall pans the visible window; dial stays at its true frequency throughout the drag and after release.
- Drag entirely within the sample bandwidth → no retune, no POST during or after the drag.
- Drag past the edge of the sample bandwidth → on release, exactly one `POST /api/radio/lo` fires with the directional landing per the formulas above; on-screen frequencies do not visually jump across the retune.
- Touch drag behaves identically on mobile/tablet.
- Build green (`dotnet build Zeus.slnx`, `npm --prefix zeus-web run build`, all existing tests).
- HL2 bench-tested with the radio tuned to 28400 (10 m, Brian's only resonant antenna). RX-only — no MOX needed for this change.

## Out of scope

- Inertia / momentum on drag release.
- Snap-to-band-edge or snap-to-bookmark while panning.
- Vertical drag interactions (dB-range adjust via drag remains where it is in `Waterfall.tsx:195`).
- Multi-RX viewport offsets — deferred to the multi-panadapter revival (#155).

## Risks

- **Operator muscle memory**: anyone who currently uses drag-to-tune within the frozen CTUN view loses that gesture. This is explicitly intended; click-to-tune covers the same use case more naturally.
- **WDSP shift large excursions**: when the dial is well outside the sample window, the WDSP `shift` value is large in absolute terms — needs verification that the shift stage handles `|shift| > sampleBw` cleanly (it should, because the result simply produces no signal; but worth a smoke test).
- **Cross-radio**: P1 and P2 both expose `RadioLoHz` updates through `RadioService`. The new `SetRadioLo` path needs to fire the same retune sequence on both, including any P2 DDC re-program. HL2 (P1) is the primary bench target; ANAN-class (P2) verification deferred to whoever has the hardware.
