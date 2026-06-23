# Multi-DDC TX target + per-receiver audio mixer

Status: in progress (feat/legacy-zeusdb-migration working branch)
Owners: N9WAR (impl). TX-on-RX3+ and audio-routing defaults are operator-felt —
flagged for KB2UKA / EI6LF bench review before merge.

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
- `VfoPanel`: enumerate exposed receivers → N lanes (focus + TX-select + tune +
  audible toggle). RX1/RX2 retain swap/copy.
- `HeroPanel`: per-RX hear/mute chips for every exposed receiver.

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
