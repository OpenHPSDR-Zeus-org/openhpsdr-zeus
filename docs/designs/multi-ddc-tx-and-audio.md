# Multi-DDC TX target + per-receiver audio mixer

Status: PR against develop (branch feat/multi-ddc-tx-audio).
Owners: N9WAR (impl). TX-on-RX3+ is operator-felt — flagged for KB2UKA / EI6LF
bench review before merge.

> **Reconciliation with develop (read first).** This branch was rebuilt onto
> develop after develop independently shipped a per-RX **mute** feature
> (`ReceiverDto.Muted` / `StateDto.Rx1Muted`/`Rx2Muted` / `RadioService.
> SetReceiverMuted` / `POST /api/receivers/{i}/mute`). So the per-RX audio
> *backend* described below as a new `Audible` field is **not** added here — the
> UI mixer/VFO-panel mute toggles drive develop's existing `Muted` instead. What
> this PR actually lands:
> 1. **Receivers-menu fix** — `normalizeState` now carries `receivers[]` /
>    `maxReceivers` / `wireVersion` (develop still dropped them, so the exposed-
>    receiver control + multi-DDC panels never saw RX2+).
> 2. **TX-on-any-RX** — `StateDto.TxReceiverIndex`, `SetTxReceiver`,
>    `POST /api/tx/receiver`, generalized `TxFrequencyHz`.
> 3. **RX3+ relay-chatter fix** — idempotent `SetExtraReceiverFreqHz`.
> 4. **UI** — VFO master-detail panel, movable RX mixer popout, and the ≤4-stitch
>    waterfall grid (`RxWaterfallPane`), with mute wired to develop's `Muted`.
>
> The `Audible`-based audio-mix sections below are retained as the original design
> record; develop's `Muted` model supersedes them.

## Goal

Make the two dual-receiver UI surfaces first-class for **all** exposed DDC
receivers (RX1..RXN, N ≤ `WireContract.MaxReceivers`), not just the RX1/RX2
(VFO A/B) pair:

1. **`vfo-dual-panel` (VfoPanel)** — one lane per exposed receiver, each with
   live frequency display + click-to-type + per-digit wheel tuning, band
   readout, focus, **and TX-select** (transmit on any receiver's VFO).
2. **`hero-rx-audio-switch` (HeroPanel)** — a per-RX listen/mute mixer: the
   operator chooses which receivers they hear, replacing the RX1/Both/RX2
   tri-state with N independent hear/mute toggles.

## Why this needed a contract change

The old model is binary and woven through the stack:

- TX target is `TxVfo {A,B}` — `RadioService.TxFrequencyHz` returns `VfoBHz`
  for B else `VfoHz`. There is no way to point TX at an RX3+ VFO.
- Audio routing is `Rx2AudioMode {Rx1,Both,Rx2}` — an RX1/RX2-only tri-state.
  The DSP mix (`DspPipelineService.Tick`) switches on it.

Both are extended (not replaced) so legacy callers keep working.

## Contract (`Zeus.Contracts/Dtos.cs`)

- `StateDto.TxReceiverIndex : int = 0` — **authoritative** TX target (0=RX1,
  1=RX2, ≥2=extra DDC). `TxVfo` stays as a back-compat projection
  (`index==1 ? B : A`); `TxFrequencyHz` now resolves off `TxReceiverIndex`.
- `ReceiverDto.Audible : bool` — whether this receiver is mixed into the
  monitor output. Projected from `RadioService._audible[]` (default all true).
  `Rx2AudioMode` is retained and projected from `audible[0]/audible[1]`
  (`rx1`=only RX1, `rx2`=only RX2, `both`=both) so legacy consumers and the
  `/api/rx2/audio` endpoint keep working.
- New request DTO `TxReceiverSetRequest(int Index)` for `POST /api/tx/receiver`.
  `ReceiverSetRequest` gains `bool? Audible`.

## Server

### TX target — `RadioService`
- `TxFrequencyHz(StateDto)`: index 0→VfoHz, 1→VfoBHz, ≥2→`Receivers[i].VfoHz`
  (snapshot path) / `_extraReceivers[i].VfoHz` (internal `_state` path via an
  instance overload, since `_state.Receivers` is null until projected).
- `SetTxReceiver(int index)`: clamps to an enabled receiver, sets
  `TxReceiverIndex` + projected `TxVfo`, recomputes PA on band change.
- The independent TX DUC (`OnRadioStateChanged → SetTxDucFrequency`) and CW/CTUN
  LO alignment all read `TxFrequencyHz`, so they follow automatically. The
  OrionMkII split-TX DUC guard (`AlignLoForTx`) generalizes from
  `TxVfo==B` to `TxReceiverIndex>=1`.

### Audio mixer — `DspPipelineService.Tick`
- Replace the `Rx2AudioMode` switch with a per-RX audible model:
  - RX1 (index 0) remains the audio **clock master** — always drained at full
    rate so the output ring is fed at exactly the RX sample rate (the #787
    constraint). "Muting RX1" means it contributes nothing to the mix, not that
    it stops clocking.
  - Every enabled+audible secondary contributes (read ≤ rx1Count, remainder
    buffered) exactly as the old `Both` path.
  - Mix via `MixRxAudioN(audioBuf, rx1Audible ? rx1Count : 0, audibleSlices)` —
    with `rx1Count==0` the existing averager already excludes RX1 and writes the
    secondary-only average into the output buffer. Hot path stays lock-free /
    zero-alloc (reuses `_mixSlices`).

## Frontend

- `connection-store`: widen focus from `rxFocus: 'A'|'B'` to a receiver index
  (`focusedRxIndex: number`), with `'A'/'B'` kept as derived helpers during the
  migration so the 31-file A/B blast radius (Panadapter/Waterfall/Filter/Mode/
  Band/keyboard tuning) flips incrementally. RX1/RX2 keep stitched-view identity.
- `VfoDisplay`: accept an optional `rxIndex` for ≥2 — reads
  `receivers[i].vfoHz`, posts `setReceiver(i,{vfoHz})`. A/B unchanged.
- `VfoPanel`: revamped to a **master-detail** — a compact chip rail (one chip per
  exposed receiver: id, freq, band, TX/mute dots) + a single active-receiver
  detail (full digits, listen/mute, TX-select, per-RX AF, A↔B copy/swap). Fixed
  footprint regardless of receiver count.
- `HeroPanel`: per-RX hear/mute + focus mixer, surfaced as a small **movable
  popout** (`RX MIX` trigger) instead of an inline header strip.

### Multi-DDC spectrum grid (waterfall stitching)

The hero panadapter and waterfall regions are a shared grid: **≤4 receivers
stitched across one row; beyond that the grid wraps so the last receivers stack
into a second row** (8 RX = two rows of 4). RX1/RX2 keep the interactive A/B
`Panadapter`/`WaterfallSurface`; RX3+ render read-only live panes —
`RxMonitorPane` (trace) and the new `RxWaterfallPane` (scrolling waterfall via
`createWfRenderer` + a per-rxId `planForFrame` key). While keyed the waterfall
collapses to the single full-width TX panafall (unchanged).

**Scaling note:** each pane owns a WebGL2 context, so 8 receivers = up to 16
contexts (8 trace + 8 waterfall) plus the chrome — near the browser's ~16-context
ceiling. The panes already handle context loss, but a future optimisation is a
shared/pooled renderer if 8-RX panafall proves context-starved on weak GPUs.

## Test plan

- Contract: `normalizeState` carries `txReceiverIndex` + `receivers[].audible`.
- Server: `TxFrequencyHz` index resolution; `SetTxReceiver` band/PA recompute;
  `MixRxAudioN` per-RX audible (RX1-muted → secondary-only average; all-muted →
  silence; single audible → passthrough).
- Frontend: VfoPanel renders a lane per exposed RX and tunes RX3+; HeroPanel
  mixer toggles audible per RX.

## Safety / bench notes (KB2UKA / EI6LF)

- TX-on-RX3+ reuses the existing single TX DUC/DUC-frequency mechanism; no new
  TX amplitude/drive path. Still: verify on HL2 + a P2 board that selecting TX
  on RX3 places the carrier on RX3's VFO and that un-key restores the RX LO.
- No change to PureSignal arm/disarm, drive bytes, or PA gain. Untouched.
