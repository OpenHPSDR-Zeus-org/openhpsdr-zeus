# RIT / XIT plumbing — design

**Status:** IMPLEMENTED — pending operator smoke test + on-air validation,
then maintainer review. Wire rename, backend, frontend, and panadapter
marker are all landed; TCI handlers are still stubs (follow-up PR).
**Date:** 2026-05-16 (revised 2026-05-25 — implementation complete).
**Author:** Simone Fabris (IU3QEZ).
**Upstream issue:** relates to #96 (Split VFO) — the `RxFreqAHz` +
`TxFreqAHz` wire rename is the substrate that #96 will build on.
**Branch:** `claude/kind-keller-hygeb` on `iu3qez/openhpsdr-zeus`.
**Pairs with:** Zero Beat (issue #300 — landed as #499, reverted in #510,
pending re-implementation).

## TL;DR

Zeus had a placeholder `RIT` button in the transport bar (`App.tsx:770`)
that did nothing, and TCI stubs (`TciSession.cs:1095-1134`) that ignore
RIT and XIT set-commands. This PR replaced the placeholder with a real
feature: a single cycle button drives a 3-state machine
(OFF → RIT → XIT → OFF), an offset sub-row appears under the VFO display
with ▲/▼ spinners and a Clear button, and the existing host-side
frequency-offset pipeline (CwOffset, freq-correction #325) grows a second
branch so RX and TX wire frequencies can diverge.

**CTUN history note (2026-05-25).** A prior draft (2026-05-22) added a
§"Interaction with CTUN" section after upstream landed the frozen-NCO CTUN
model (issue #427, commit `893b94e`). That model was subsequently **fully
reverted** in commits `d25e32a` (#495, "revert CTUN frozen-NCO — radio
follows the dial again") and `dcd414b` (#511, "restore pre-CTUN behavior")
because the single-VFO wire model (`ControlFrame` writes `VfoAHz` to all
five freq registers) caused TX to transmit on the frozen NCO instead of
the dial. This is exactly the gap RIT/XIT fills — the `VfoAHz` →
`RxFreqAHz` + `TxFreqAHz` rename that this design proposes. The CTUN
interaction section has been removed; the wire formula below is the
original v1 two-field model with no CTUN branch.

`RadioLoHz` still exists in `StateDto` and `RadioService` but now tracks
the dial's effective LO unconditionally (CW pitch baked in). It is used
for panadapter centering. RIT/XIT do not interact with it — both operate
on the wire-frequency formula only.

This PR is the **substrate** that makes RIT-aware Zero Beat possible — a
follow-up PR will extend `RadioService.ZeroBeat` to target the RIT register
when RIT is active. The two PRs are independent: this one stands on its own
as a feature ham operators actually want.

## Goal

Match the standard ham-radio RIT/XIT workflow: operator engages RIT, dials a
small offset (±3 kHz max), the RX is shifted while TX stays on the dial.
XIT mirrors that for TX. One offset active at a time, single cycle button to
switch between RIT and XIT and back to OFF, per-mode offset preserved across
the cycle so the operator can park RIT, try XIT, and come back to find their
RIT value still there.

Operating model and constants come from Thetis (the canonical reference for
Zeus), with one deliberate divergence: Zeus enforces RIT and XIT to be
mutually exclusive (one mode active at a time). Thetis allows both flags on
simultaneously because RIT applies during RX and XIT during TX, so they don't
fight each other. Zeus prefers the simpler UI — one cycle button, one offset
visible at a time. Documented in §"Open questions for the maintainer" below
because it's worth Doug's call.

## Tech stack

.NET 10 / C# 12 backend, React 19 + Vite frontend, xUnit 2.9 (backend tests),
Vitest 2.1 (frontend tests). No new runtime dependencies; the work fits in
the existing project layout.

## Background

### Why this is not just a UI wiring change

You'd think wiring up the placeholder `RIT` button is a one-day job — just
add an event handler, send the offset to the radio, done. It isn't, because
of how Zeus currently models radio frequency.

**Before this PR**, the wire format treated RX and TX freq as the same
number. `ControlFrame.WriteCcBytes` (line 265) wrote `state.VfoAHz` to all
five frequency registers. **This PR split** `CcState.VfoAHz` into
`RxFreqAHz` + `TxFreqAHz`, and `WriteCcBytes` now routes `TxFreq` (0x02) to
`TxFreqAHz` while the four RX registers use `RxFreqAHz`
(`ControlFrame.cs:272-281`). `SetVfoAHz(long)` became `SetFreqs(long rxHz,
long txHz)` on both protocol clients, and `RadioService.PushWireFreqs()`
applies the RIT/XIT formula before calling `SetFreqs`.

### Why RIT separately from SPLIT

RIT and SPLIT serve overlapping use cases — both let the operator listen on
one frequency and transmit on another — but they're mechanically different,
and conflating them causes confusion in the codebase and the UI.

- **RIT/XIT** = a small signed offset (±3 kHz in Zeus, ±9.999 on some
  commercial rigs) added to one register. Use case: a caller is slightly
  off-frequency, drift, fine alignment in pile-ups.
- **SPLIT** = two independent VFOs (A and B), arbitrarily far apart. Use
  case: a DX station on 14.220 listening up 5–10 kHz at 14.225–14.230.

Most commercial rigs implement both, and they compose (you can RIT on top
of SPLIT). Zeus has a `SPLIT` placeholder in `App.tsx:769` next to the `RIT`
one; that's a separate feature deserving its own PR.

### Why not just inherit from frequency-correction #325 / #334

Doug's frequency-correction PR uses host-side injection at the
`Protocol1Client.SetVfoAHz` / `Protocol2Client.SetVfoAHz` seam: the operator
sets a calibration factor, and the client multiplies before writing to the
wire. We could mirror that pattern for RIT/XIT — keep `SetVfoAHz`'s single
parameter, add `client.SetRitOffset(hz)` / `SetXitOffset(hz)` setters,
combine inside the client.

We didn't. Two reasons:

1. The freq-correction factor is conceptually one global multiplier applied
   to every freq the client sees. RIT/XIT are different: they're additive,
   they vary by mode (off / rit / xit), and the *math is different for RX
   vs TX*. Treating them as "yet another client-side adjustment" buries
   the rx-vs-tx split inside two protocol clients instead of in
   `RadioService` where the operator state lives.

2. The `ControlFrame.cs` comment is an invitation: *"Zeus has no separate
   TX VFO yet"*. The codebase is ready for the rename; the comment was
   left by whoever drew the line and stopped just short.

The Thetis reference (`console.cs:31773-31787`, see receipts) calculates
`rx_freq` and `tx_freq` independently in the orchestrator and pushes both
to the wire. We follow that pattern.

## Design

### Server-side state model

Three new fields on `StateDto` (`Zeus.Contracts/Dtos.cs:297-300`),
runtime-only (not persisted to LiteDB):

```csharp
// Zeus.Contracts/Dtos.cs
public enum IncrementalTuningMode : byte { Off = 0, Rit = 1, Xit = 2 }

// on StateDto (default Off / 0 / 0):
IncrementalTuningMode ItMode,
int RitOffsetHz,   // ±3000, preserved across cycle transitions
int XitOffsetHz    // ±3000, preserved across cycle transitions
```

The `enum` makes mutual exclusion structural — you cannot represent
`RitOn && XitOn`. Three states, three states only.

`RadioService.SetIncrementalTuning(mode, offsetHz)` (`RadioService.cs:685`)
validates + clamps via `RitXitMath.ClampOffset` (`RitXitMath.cs:13`), then
mutates state and calls `PushWireFreqs` to push the split frequencies to
the protocol client.

### Effective wire-frequency formula

The single load-bearing piece of math, implemented in
`RadioService.PushWireFreqs` (`RadioService.cs:1748-1759`):

```csharp
// RadioService.PushWireFreqs(RxMode mode, long dialHz)
ritDelta = (ItMode == Rit) ? RitOffsetHz : 0;
xitDelta = (ItMode == Xit) ? XitOffsetHz : 0;
rxWireHz = CwOffset.EffectiveLoHz(mode, dialHz + ritDelta);
txWireHz = CwOffset.EffectiveLoHz(mode, dialHz + xitDelta);
ActiveClient?.SetFreqs(rxWireHz, txWireHz);
```

Note: CwOffset is applied to `dial + delta`, not added after. This is
correct: `EffectiveLoHz(CWU, f) = f − pitch`, so
`EffectiveLoHz(CWU, dial + rit) = (dial + rit) − pitch`.

Worked example: dial 14.050.000, CWU mode (pitch 600 Hz), RIT engaged at
+250 Hz →
- `rxWireHz = EffectiveLoHz(CWU, 14_050_250) = 14_049_650`
- `txWireHz = EffectiveLoHz(CWU, 14_050_000) = 14_049_400` (XIT off)

The CwOffset baking is inherited from existing CW handling (`CwOffset.cs`).
RIT/XIT layer on top.

### Wire-layer changes — the rename

**Done.** The rename touched 21 files (74 occurrences). Summary:

- `CcState.VfoAHz` → `RxFreqAHz` + `TxFreqAHz` (`ControlFrame.cs:190-191`)
- `WriteCcBytes` switch (`ControlFrame.cs:272-281`):
  ```csharp
  case CcRegister.TxFreq:
      BinaryPrimitives.WriteUInt32BigEndian(cc[1..5], (uint)state.TxFreqAHz);
      break;
  case CcRegister.RxFreq:
  case CcRegister.RxFreq2:
  case CcRegister.RxFreq3:
  case CcRegister.RxFreq4:
      BinaryPrimitives.WriteUInt32BigEndian(cc[1..5], (uint)state.RxFreqAHz);
      break;
  ```
- `IProtocol1Client.SetVfoAHz(long)` → `SetFreqs(long rxHz, long txHz)`
  (`IProtocol1Client.cs:79`)
- `Protocol1Client`: `_vfoAHz` → `_rxFreqAHz` + `_txFreqAHz`
  (`Protocol1Client.cs:78-79`), freq correction applied independently
  (`Protocol1Client.cs:544-551`)
- `Protocol2Client.SetFreqs`: accepts `txHz` for API symmetry, not yet
  wired to a separate P2 NCO (`Protocol2Client.cs:400-414`)
- N2ADR OC mask uses `RxFreqAHz` (`ControlFrame.cs:470`) — BPF follows RX
- Backward compat: when IT is Off, `rxWireHz == txWireHz`. Same wire bytes.

### REST endpoint

One endpoint, replace-semantics:

```http
POST /api/rx/incremental-tuning
Content-Type: application/json

{ "mode": "off" | "rit" | "xit",
  "offsetHz": -3000..3000 }
```

Whole-state set, not PATCH. The operator's edit (cycle a mode, dial an
offset) is always a "this is the new state" decision, not a partial diff.
Response body: the post-clamp state (200 OK), so the frontend can pick up
the clamped value if the input was out of range.

400 Bad Request for malformed payloads (mode not in the enum, offsetHz not
an integer). Out-of-range `offsetHz` is silently clamped — operationally
nicer than rejecting, and matches Thetis's `Math.Min`/`Math.Max` pattern.

`GET /api/state` (existing) returns the runtime snapshot, extended with
`itMode`, `ritOffsetHz`, `xitOffsetHz`. Frontend already broadcasts state
via SignalR `StreamingHub` — the new fields ride along.

### Frontend UI surface

Four new components, two existing components touched:

**`IncrementalTuningButton.tsx`** (new — replaces `App.tsx:770` placeholder)
- Renders as a single button in the transport bar (`App.tsx:771`).
- Single click cycles: OFF → RIT → XIT → OFF.
- Label changes per state: dim "RIT/XIT" when OFF, lit "RIT" or "XIT" with
  `accent` class when active.

**`RitXitOffsetRow.tsx`** (new — sub-row under VfoDisplay)
- Renders only when `itMode != 'Off'` (`VfoDisplay.tsx:316`).
- Layout: `[ RIT  ▼  +0250 Hz  ▲  Clr ]`
- ▲ / ▼ spinners: increment / decrement by **filter-aware step** (10 Hz
  default, 5 Hz when current filter bandwidth ≤ 250 Hz — matches Thetis at
  console.cs:7624-7633).
- `Clr` button: sends `{ mode: "Off", offsetHz: 0 }` → sub-row disappears.
- Debounced POSTs: 100 ms after the last ▲/▼ click.

**`RitXitMarker.tsx`** (new — panadapter frequency indicator)
- DOM overlay inside Panadapter container (`Panadapter.tsx:266`), same
  coordinate system as `PassbandOverlay` (percentage of frequency span).
- When RIT active: red (`--tx`) vertical line + "RX" label at `dial + ritOffset`.
- When XIT active: red vertical line + "TX" label at `dial + xitOffset`.
- Hidden when offset is 0 or IT mode is Off.
- z-index 15 (same as dial marker), label with dark background for contrast.

**`VfoDisplay.tsx`** (modified)
- Imports and renders `<RitXitOffsetRow />` after the frequency hint
  (`VfoDisplay.tsx:316`). The row conditionally renders itself.

**`connection-store.ts`** (modified)
- `itMode`, `ritOffsetHz`, `xitOffsetHz` fields added to `ConnectionState`
  type, initial values, and `applyState` hydration.

**`client.ts`** (modified)
- `IncrementalTuningMode` type, `normalizeItMode`, fields on
  `RadioStateDto`, `setIncrementalTuning()` API function.

### Auto-clear events

| Event | `_itMode` | `_itRitHz` | `_itXitHz` |
|---|---|---|---|
| Cycle button (any transition) | changes | preserved | preserved |
| `Clr` button (mode = Rit) | Off | **0** | preserved |
| `Clr` button (mode = Xit) | Off | preserved | **0** |
| Band change | Off | **0** | **0** |
| Mode change (CWU/CWL/USB/...) | Off | **0** | **0** |
| Radio disconnect / reconnect | Off | **0** | **0** |
| (future) SPLIT engaged | Off | **0** | **0** |
| (future) Memory recall | overwritten | overwritten | overwritten |

"Preserved" means the value sits in `RadioService` state but is not applied
to the wire while `_itMode = Off`. Cycle back to the same mode → the value
reappears in the sub-row and lands on the wire.

### TCI handling

The existing TCI stubs at `Tci/TciSession.cs:1095-1134` get real handlers,
mapped to the Thetis CAT command names (canonical reference: Thetis
`CATCommands.cs`):

- `ZZRT` — RIT on/off (set/query)
- `ZZRF` — RIT offset frequency (Hz, signed, 5 digits, e.g. `ZZRF+00250;`)
- `ZZRU` / `ZZRD` — RIT step up/down (uses the filter-aware step)
- `ZZXS` — XIT on/off (note: not `ZZXT`)
- `ZZXF` — XIT offset
- `ZZXU` / `ZZXD` — XIT step up/down

Each command routes into the same `RadioService.SetIncrementalTuning(...)`
the REST endpoint uses, so wire side-effects and state broadcast are
identical regardless of source.

**Mutual-exclusion divergence from Thetis.** If a TCI client (WSJT-X,
N1MM+, etc.) sends `ZZRT1;` while XIT was already on, our backend cleanly
transitions: `_itMode = Rit`, `_itXitHz` is preserved in memory (so cycling
back to XIT restores it), but XIT is no longer applied to the wire. Thetis
would leave both flags on. The operator's `_xitHz` is not lost — only its
application is suspended while `_itMode != Xit`. Worth flagging to Doug;
see §"Open questions for the maintainer".

If Doug prefers, this TCI block can split into a follow-up PR to make this
one atomic on the substrate alone.

## Data flow

**Operator clicks the cycle button (OFF → RIT):**

```
IncrementalTuningButton click
  → client.ts setIncrementalTuning({ mode: "rit", offsetHz: lastRitHz })
  → POST /api/rx/incremental-tuning
  → ZeusEndpoints → RadioService.SetIncrementalTuning(...)
       1. validate + clamp via RitXitMath
       2. _itMode := Rit, _itRitHz := offsetHz
       3. PushWireFreqs(mode, dialHz) computes rxWireHz, txWireHz
       4. ActiveClient.SetFreqs(rxWireHz, txWireHz)
       5. Mutate snapshot, SignalR broadcast
  → Protocol1Client.SetFreqs
       6. apply freq-correction to each field
       7. update _rxFreqAHz, _txFreqAHz
       8. rotation 4-phase carries new values on next tx packet (~3 ms)
  → Frontend store updates, RitXitOffsetRow appears
```

**Operator clicks ▲ on sub-row:**

Same path from step 1, debounced 100 ms. Offset becomes
`current + filterAwareStep(currentFilterBw)`, clamped, sent.

**Operator clicks `Clr` on sub-row:**

```
POST { mode: "off", offsetHz: 0 }  →  ... → 
  _itMode := Off
  if previousMode == Rit: _itRitHz := 0
  else if previousMode == Xit: _itXitHz := 0
  // the other mode's offset is left untouched
  rxWireHz = txWireHz = dial + cwOffset(mode)
  push to wire, broadcast
  → sub-row disappears
```

**Auto-clear on band change:**

```
RadioService.SetBand(newBand)  →  if newBand != _band:
  _itMode := Off, _itRitHz := 0, _itXitHz := 0
  ... (recompute + push, exactly as a manual clear)
```

Same shape in `SetMode` and on disconnect.

## Error handling

### Input validation

| Input | Behavior |
|---|---|
| `offsetHz` out of range (±50_000 etc.) | Silent clamp to ±3000. Response body carries the clamped value so frontend sees the truth. |
| `offsetHz` not an integer / NaN | 400 Bad Request, no state change |
| `mode` not in `{off, rit, xit}` | 400 Bad Request |
| TCI `ZZRF+09999;` (would clamp to 3000) | Accept, clamp, store. Subsequent `ZZRF;` query returns the clamped form |
| TCI command malformed | Ignored (matches Zeus's existing stub-permissive pattern) |

Pure helper `Zeus.Server.Hosting.RitXitMath.ClampOffset(int hz)`
(`RitXitMath.cs:13`) centralises this; `MaxOffsetHz = 3000`.

### Wire-layer failures

- **`_activeClient == null`** (disconnected): state is mutated, snapshot
  broadcast, but no wire write. On reconnect the disconnect-clear rule
  fires, so the rehydrated state is clean — no stale offset reaches the
  fresh client.
- **UDP write fails**: fire-and-forget, no retry (matches every other Zeus
  wire write). The rotation schedule re-sends the register within ~3 ms
  regardless.
- **Race between `SetIncrementalTuning` and MOX edge**: `RadioService`
  serialises mutations through the existing `Mutate(...)` lock; the MOX
  edge re-reads the snapshot after each mutation completes, so no torn
  state can reach the rotation.

### Band-edge clamping

Thetis clamps the post-offset wire frequency to band limits *after* adding
the offset (`console.cs:31781`, `31788`). Example: dial 14.349.000 (top of
20 m) + RIT +500 → `rxWireHz` would be 14.349.500, out of band; Thetis
forces it to 14.350.000 (band edge), silently. The displayed offset stays
at +500 but the effective offset is +1000.

We follow Thetis exactly. This means rare drift between displayed offset
and effective offset right at band edges. Worth flagging to Doug because
the alternative (reject the set, or allow out-of-band) is also defensible.

### TCI forced-mode transition

Already covered in §"TCI handling". Recapping: an external TCI client can
switch `_itMode` without operator input. The operator's offset *data* in
the inactive mode is preserved in memory. Only application changes.

Mitigation: an info log entry on the mode-switch (`tci.it.mode-transition
from={x} to={y} source=tci`). No user-facing toast — TCI is machine-to-
machine and the operator shouldn't have to dismiss notifications from it.
Frontend learns about the switch through the normal state broadcast.

## Testing strategy

Six buckets — five automated, one manual, plus a light on-air pass.
**Current totals: 1047 backend (210 P1 + 101 P2 + 736 Server) + 229
frontend = 1276 tests, 0 failed.**

### Pure unit tests (`Zeus.Server.Tests/RitXitMathTests.cs`) — DONE

16 parametric tests covering `ClampOffset` (±3000 clamp) and
`FilterAwareStepHz` (10 Hz / 5 Hz threshold at 250 Hz BW).

### Orchestrator tests (`Zeus.Server.Tests/RadioServiceRitXitTests.cs`) — DONE

10 tests exercising `RadioService` directly (same pattern as
`RadioServiceSetRadioLoTests`). Cases:

1. `SetIncrementalTuning(Rit, +250)` with dial 14_050_000, CWU, pitch 600
   → pushed `rxWireHz=14_049_650, txWireHz=14_049_400`.
2. Cycle Off → Rit (+250) → Xit (preserve rit) → Off (preserve both).
   Final offsets equal initial dialled values, mode is Off.
3. Band change with active mode → true clear.
4. Mode change → true clear.
5. Reconnect after a disconnect → true clear.
6. Race: 100 concurrent `SetIncrementalTuning` from N threads → terminal
   state matches the final caller's args, no torn `_itMode`.
7. MOX edge with `_itMode = Xit` → `_txFreqHz = dial + xit`, `_rxFreqHz =
   dial`. The two fields differ on the wire as expected.

### Wire-layer tests (`Zeus.Protocol1.Tests/ControlFrameRxTxSplitTests.cs`) — DONE

4 tests. Direct assertions on `WriteCcBytes` output bytes:

1. `state.RxFreqAHz = X, state.TxFreqAHz = Y, register = RxFreq` → BE32
   payload = `X`.
2. Same state, `register = TxFreq` → payload = `Y`.
3. `RxFreq2 / RxFreq3 / RxFreq4` → always `RxFreqAHz`, never `TxFreqAHz`.
4. `state.Mox = true` → cc[0] bit 0 = 1, freq payload unchanged.

### REST endpoint integration (`Zeus.Server.Tests/RitXitEndpointTests.cs`)

`WebApplicationFactory<Program>`-based, hits the endpoint as HTTP.

1. Valid POST → 200 + echoed state in body.
2. Out-of-range `offsetHz` → 200 + clamped value in body.
3. Bad `mode` enum → 400.
4. `GET /api/state` reflects the new fields.

### TCI handler tests (`Zeus.Server.Tests/TciRitXitTests.cs`)

Test the new handlers in `TciSession`:

1. `ZZRT1;` enables RIT mode in `RadioService`.
2. `ZZRF+00250;` sets RIT offset to 250.
3. `ZZRU;` steps up by the current filter-aware step.
4. Mutex divergence: `ZZRT1;` then `ZZXS1;` → terminal `_itMode = Xit`,
   `ZZRT;` query returns `ZZRT0;`. (Documents the divergence in test
   form.)
5. Query forms (no argument) reply with current state in Thetis-compatible
   formatting.

### Frontend (Vitest)

- `IncrementalTuningButton.test.tsx`: state cycle, label changes, accent
  border on active.
- `RitXitOffsetRow.test.tsx`: render gating (only when `mode != Off`),
  spinner clicks issue POSTs with correct payload, `Clr` POSTs `mode:
  "off"`, click-to-edit numeric input commits on Enter.

### Manual smoke test

Pre-merge gate. With the dev stack up and a synthetic engine connected:

1. Cycle button visible in transport bar. Click → label "RIT", accent
   border lit. Sub-row appears under VFO with "RIT 0 Hz".
2. Click ▲ three times → "RIT +30" (or +15 if a narrow filter is active).
3. Click ▼ once → "RIT +20" / "+10".
4. Click `Clr` → sub-row disappears, button label back to "RIT/XIT" dim.
5. Cycle to RIT again → "RIT 0" (last Clr wiped this mode's offset).
6. Cycle to XIT → "XIT 0", RIT preserved in `/api/state` if you check.
7. Change band → sub-row gone, `/api/state` shows offsets 0.
8. Disconnect / reconnect → same.

### On-air validation

This is a **lighter envelope** than the Zero Beat merge gate. Zero Beat is
DSP — three baked-in constants whose correctness only shows on-air. RIT/XIT
is wire plumbing. The algorithm is `dial + offset`; the constants are
clamped at known bounds. What on-air checks is whether the operator's
hands-and-ears reality matches the design intent.

Minimum on-air pass (with a real HL2 or ANAN class radio):

1. Real CW signal slightly off the dial — engage RIT, walk it to bring the
   carrier to the correct pitch. Audio follows.
2. Real SSB station off-frequency — engage RIT, align by ear.
3. XIT in a QSO with a friend — dial-shift TX by some hundreds of Hz,
   confirm they receive on the original dial frequency while you continue
   to hear them on `dial + 0`.
4. Mode change in the middle of an active RIT — sub-row vanishes, dial
   frequency unchanged.
5. Disconnect / reconnect with RIT active — comes back clean.

If any of these surface a UX surprise (the `Clr` button is buried, the
cycle through XIT-to-get-back-to-OFF is awkward, the filter-aware step
feels wrong), flag it; we may need to add the `Esc` global hotkey or `×`
inline reset button after the smoke pass.

## Out of scope / future

These are deliberately not in PR-A. Each gets its own design discussion
when its turn comes.

- **RIT-aware Zero Beat (PR-B).** This PR is the substrate; the follow-up
  small PR teaches `RadioService.ZeroBeat` to target `_itRitHz` (when
  `_itMode == Rit`) instead of the main VFO. The existing `ZeroBeatRequest`
  DTO already has a `byte? RxId` forward-compatible parameter; we add a
  sibling `target: "vfo" | "rit"`.
- **SPLIT.** Independent feature, separate PR. The `SPLIT` placeholder
  in `App.tsx:769` becomes its own button when that work happens.
- **Memory recall.** Not implemented today (the `SAVE MEM` button at
  `App.tsx:771` is also a placeholder). When memory cells arrive, recall
  will overwrite `_itMode` / `_itRitHz` / `_itXitHz` from the stored
  cell. The clear-rule table above already lists this as a hook.
- **Configurable keyboard shortcuts.** Zeus has no hotkey-preferences UI
  today. Hardcoded defaults in this PR (Shift+ArrowUp/Down for step,
  Shift+Backspace for Clear). When a hotkey-prefs panel lands, RIT/XIT
  hotkeys join it.
- **~~Panadapter dual-marker for RIT/XIT visualisation.~~** DONE —
  `RitXitMarker.tsx` renders a red (`--tx`) vertical line with "RX" or "TX"
  label on the panadapter at the offset frequency. DOM overlay, same
  coordinate system as `PassbandOverlay`.
- **`Esc` global hotkey and `×` inline reset.** Provisional alternatives
  to the on-row `Clr` button. Skipped from v1; on-air smoke may surface
  the need.

## Open questions for the maintainer

This section is the "design notes" we'd quote in the PR description to
Doug. Each item is a judgement call we made; happy to revise on his read.

1. **~~Rename `VfoAHz` → `RxFreqAHz` + `TxFreqAHz` everywhere.~~**
   RESOLVED — done. 21 files, 74 occurrences renamed.

2. **~~`SetVfoAHz(long)` → `SetFreqs(long rxHz, long txHz)`.~~**
   RESOLVED — done. Combined setter, both freqs pushed atomically.
   5 call sites updated (4 in RadioService, 1 in DspPipelineService).

3. **REST replace-semantics, not PATCH.** The endpoint takes
   `{ mode, offsetHz }` as one indivisible state. We considered separate
   `/enable` and `/offset` paths, or PATCH with optional fields. The
   single replace endpoint is simpler and the operator action is always
   "this is the new combined state" (you don't typically toggle the mode
   without also implying what offset is in effect). Open to splitting if
   you prefer two narrower endpoints.

4. **Mutual exclusion (Zeus) vs both-on (Thetis).** This is the loudest
   divergence. Thetis allows RIT and XIT to both be on, since they apply
   in opposite MOX phases. We force a single-mode-active model so the UX
   is one cycle button and one sub-row display. The operator's *data*
   isn't lost (both `_itRitHz` and `_itXitHz` persist across cycling),
   only application. If you'd rather we keep Thetis parity here — two
   independent flags, two sub-rows, more UI — we'll redesign.

5. **TCI in this PR vs split-out.** The TCI handlers replace ignore-
   stubs and are ~12 commands (~15-20 lines each). Could ship in this PR
   or as a small follow-up to keep PR-A "substrate only". Your call.

6. **Band-edge clamping Thetis-strict.** When RIT pushes `rxWireHz` past
   the band edge, we clamp silently and let the displayed offset diverge
   from the effective offset (Thetis behavior). Alternatives: reject the
   offset set (cleaner state) or allow out-of-band (some radios cope,
   most don't). We picked Thetis-strict because it never blocks an
   operator action.

7. **Reset UX = on-row `Clr` only.** No `Esc` global hotkey, no `×`
   inline mini-button. Provisional; on-air smoke may say this is too
   buried in a fast QSO. Both alternatives are a few lines of code on
   top of the substrate — happy to add either if the smoke surfaces it.

8. **On-air validation envelope is lighter than Zero Beat's.** Zero Beat
   needed slow/fast/weak/fading because three baked-in constants gate
   real-radio correctness. RIT/XIT is wire plumbing — the math is
   `dial + offset`. The smoke checklist above plus 5 reality-checks
   feels right. If you'd rather we run a fuller envelope, name it.

## Notes / receipts

- **CTUN retired upstream (2026-05-25).** The frozen-NCO CTUN model
  (issue #427, commit `893b94e`) was reverted in `d25e32a` (#495) and
  `dcd414b` (#511). Root cause: `ControlFrame` writes a single `VfoAHz`
  to all five freq registers, so TX transmitted on the frozen NCO instead
  of the dial. `CtunEnabled` is gone from the contracts; `RadioLoHz`
  persists but tracks the dial unconditionally. The `VfoAHz` →
  `RxFreqAHz` + `TxFreqAHz` rename proposed by this design would have
  unblocked CTUN's TX problem — should CTUN return in the future, it
  can build on the two-field wire model RIT/XIT introduces.
- **Thetis reference**: `console.cs:31773-31787` for the canonical
  `rx_freq` / `tx_freq` independent calculation, `console.cs:7624-7633`
  for the filter-aware step (10 / 5 Hz), `console.cs:36052` for
  `btnRITReset_Click` (the canonical "clear value + turn off"),
  `console.Designer.cs:2522-2531` for the original ±99.999 kHz range,
  `CATCommands.cs:5904+` for the ZZRF wire format.
- **Range choice**: ±3 kHz — tighter than the original ±9.999 kHz draft
  and far narrower than Thetis's ±99.999. Beyond ±3 kHz, the operator
  really wants SPLIT (issue #96, separate PR). `RitXitMath.MaxOffsetHz`
  is a single constant to adjust if operator feedback surfaces the need.
- **Upstream SPLIT VFO**: issue
  [#96](https://github.com/Kb2uka/openhpsdr-zeus/issues/96) (open, P3,
  by Brian). The `RxFreqAHz` + `TxFreqAHz` wire rename is the substrate
  that #96 will build on — SPLIT just means `TxFreqAHz` can diverge
  arbitrarily from `RxFreqAHz`, not just by ±3 kHz.
- **Step choice**: 10 Hz default, 5 Hz when current filter bandwidth ≤
  250 Hz. Lifted verbatim from Thetis. Provisional pending on-air.
- **Cycle-button UX precedent**: not Thetis (which has two independent
  toggles); more like Yaesu's CLAR button on some FT-series radios.
  Documented as a deliberate divergence above.
- **Personal operating experience**: Simo (iu3qez) on CW / SSB DX work.
  Source of the "operator wants the Clr at hand" and "Esc/× as fallback
  if Clr is buried" intuitions.
