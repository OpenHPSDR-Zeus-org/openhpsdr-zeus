# FreeDV fidelity — end-of-over tail + speech-band resampling

Two operator-reported FreeDV problems and the changes made to fix them. Read
this before touching `Zeus.Dsp.FreeDv` resamplers, the FreeDV TX un-key path, or
the FreeDV RX gain staging.

## Symptoms

1. **Random garbled artifacts at the END of an over.** Receiving stations heard
   noise/garbage on the last fraction of a second of every transmission.
2. **Voices sometimes sound "nasally."** Dull/boxy decoded speech, worse on some
   signals than others.

## Root causes

- **End-of-over garble = no TX tail.** The modem encodes whole OFDM frames. At
  un-key the wire MOX bit dropped immediately, so the radio stopped RF mid-frame
  and the buffered-but-untransmitted modem audio was discarded (`FlushTx`). The
  receiver saw a truncated final OFDM symbol → garbage. Confirmed by the existing
  code comment that RADE's EOO callsign "is not auto-fired" because "the TX tail
  needs to drain" — there was no post-unkey drain at all.
- **Nasal = the shared 48k⇄8k resampler ate the speech presence band.** The
  prototype low-pass was a short (96-tap) Hamming sinc at 3.4 kHz cutoff, whose
  wide transition band left the passband flat only to ~2.5 kHz. The 2.5–3.4 kHz
  speech presence band rolled off in BOTH speech paths (mic→codec2 analysis and
  decoded-speech→operator), so everyone sounded dull.
- **Nasal "sometimes" = level-dependent decoder-input clipping (suspected).**
  FreeDV forces WDSP RX AGC to **Fixed**; on strong signals that fixed level can
  drive the float→int16 conversion into `freedv_rx` toward clipping → harsh
  decode. Now *instrumented* (not yet auto-corrected) — see below.

## Changes

- **TX tail / PTT hang** (`TxService` → `DspPipelineService.DrainFreeDvTxTail` →
  `TxAudioIngest.DrainFreeDvTxTail`). On a genuine FreeDV un-key, BEFORE the wire
  MOX bit drops, the final modem frame is completed (`FinishTx`) and the queued
  modem audio is clocked out to the radio at the DAC rate, then PTT drops. The
  receiver now gets whole frames. The mic hot path yields TX exclusively during
  the drain (CAS `_tailDraining` flag + `_sync` barrier) so there is never a
  second feeder on WDSP `fexchange2`. **For RADE this also makes the EOO callsign
  actually transmit** (it rides the tail).
- **Resampler** (`FreeDvResampler`): 16→40 taps/phase (240-tap prototype), cutoff
  3.4→3.5 kHz. Passband now flat to ~3.2 kHz; stopband still below the 4 kHz
  (8 kHz-rate) Nyquist so decimation doesn't alias. Cost is trivial.
- **RX decoder-input health log** (`FreeDvModem`): when synced and the input to
  `freedv_rx` is clamping, a 1 Hz `freedv.rx.in clipping: peak=… clamps/s=…`
  warning fires. This is the evidence to decide whether the Fixed RX AGC needs
  headroom.

## Bench-tuning on the G2 (the open knobs)

The DSP changes are unit-tested (incl. against the real codec2/zeus_rade libs),
but the TX-tail TIMING needs on-air confirmation. Knobs:

- `TxAudioIngest.FreeDvTxTailMaxMs` (350) — hard ceiling on how long un-key blocks
  while the final frame clocks out.
- `TxAudioIngest.FreeDvTxTailGuardMs` (60) — extra hold after the last block so the
  radio FIFO finishes before PTT drops. **Tune this first:** if the very end is
  still clipped, raise it; if there is dead carrier at the end, lower it.
- `FreeDvResampler.TapsPerPhase` / `CutoffHz` — raise the cutoff toward 3.6–3.8 kHz
  if more brightness is wanted (keep stopband < 4 kHz).

Validation steps:
1. Key FreeDV (700D) on the G2 into a dummy load; have a second FreeDV RX decode.
   Confirm the end of each over decodes cleanly (no tail garble).
2. Watch the server log for `freedv.tx.tail drained, dropping PTT` per un-key.
3. On RADE V1, confirm the decoding station shows the EOO callsign.
4. Work some real stations; watch for `freedv.rx.in clipping` warnings. If they
   appear on strong signals, add headroom to the Fixed RX AGC seed (red-light:
   that's a level an operator feels — change with bench data, not blind).

## Stop-talk artifacts — sync squelch (RX) + mic noise gate (TX)

A second operator pass targeted "random artifacts when a user stops talking,"
reported on **both** directions and primarily on **RADE V1**. Two new
allocation-free, lock-free dynamics blocks in `Zeus.Dsp.FreeDv/FreeDvGates.cs`,
wired into both `FreeDvModem` and `RadeModem`:

- **`RxSquelchGate` (RX).** When the far station unkeys, the decoder keeps
  turning band-noise into speech-shaped output. codec2's per-frame SNR squelch is
  twitchy, and **RADE has no squelch at all** (`RadeModem.SetSquelch` is inert),
  so you hear an R2D2/warble tail every un-key. The gate mutes decoded speech via
  a smooth attack/release ramp driven by the modem sync flag, with a **hold**
  window that rides brief mid-over sync dropouts so good copy is never chopped.
  When it goes fully closed it flushes the decode FIFO (`_rxOut48.Clear()`) so a
  stale-noise backlog can't play at the head of the next over. Applied at the end
  of `ProcessRxInPlace` in both modems.
- **`MicNoiseGate` (TX).** When the operator pauses mid-over, mic background hiss
  fed into `freedv_tx` / `rade_tx` vocodes into garble at the far end. The gate
  drives the mic to digital silence during pauses (the encoder then emits clean
  silence frames — still valid modem frames, so far-end sync holds). It is
  **adaptive**: the raw mic at this tap is pre-mic-gain, so its level varies
  wildly by interface; a fixed dBFS threshold would clip a quiet mic or miss a hot
  mic's hiss. Instead it tracks the noise floor (fast-down / slow-up follower so
  speech bursts don't drag the floor up) and gates relative to it (`openMargin` /
  `closeMargin` dB above floor) with hysteresis + hang. Resets to **open** each
  over so the first syllable is never clipped. Applied at the start of
  `ProcessTxInPlace` in both modems (before the mic decimation/encode).

Both gates reset whenever the modem quiesces (`RetireRx`/`RetireTx`/`FlushTx`),
so each over starts clean. Unit-tested in `FreeDvGatesTests.cs` (pure math, no
native lib); the native loopback/perf/zero-alloc tests still pass with the gates
inline.

### "Pinched / nasal" RADE speech — widen the RADE speech resampler

RADE decodes **wideband 16 kHz** speech (FARGAN synthesizes energy up to ~8 kHz)
and that path uses `RadeResampler` (16↔48 kHz, ×3), which is SEPARATE from the
codec2 `FreeDvResampler` widened earlier. Its prototype was a 60-tap Hamming
sinc at 7.2 kHz cutoff — a ~2.6 kHz transition band that was only flat to
~5.5 kHz (rolling off FARGAN's presence/brilliance → dull, "nasal" decode) and
whose skirt spilled past the 8 kHz Nyquist, **aliasing on the 48→16 kHz mic
decimation** (so what we TRANSMIT was coloured too). Bumped to a 96-tap prototype
(32 taps/phase, cutoff unchanged at 7.2 kHz): transition ~1.6 kHz, flat to
~6.5 kHz, stopband below Nyquist. More presence survives on RX and the mic
anti-alias is clean on TX.

**This is the only code-level lever for RADE decoded-speech timbre.** Remaining
"nasal/underwater" quality on RADE is usually **low SNR** (the autoencoder
degrades characteristically near threshold) or, on TX, the operator's mic itself
— FreeDV deliberately bypasses the TX voice EQ/compressor (the vocoder wants flat
speech), so a thin mic sounds thin. Those are not Zeus DSP bugs; confirm SNR via
`/api/freedv/status` and compare against another FreeDV client on the same
signal before changing DSP.

### Bench-tuning the gates on the G2 (open knobs)

Both are always-on with conservative defaults in `MicNoiseGate.Default` /
`RxSquelchGate.Default`. If they ever over- or under-act, tune the `Default(...)`
arguments:

- **RX warble still audible after un-key** → shorten `holdMs` (250) or `releaseMs`
  (60). **RX chops the start/end of weak-but-good copy** → lengthen `holdMs`.
- **TX still passes background hiss during pauses** → lower `openMarginDb` (14) /
  `closeMarginDb` (8). **TX swallows soft speech / first words** → raise the
  margins or lengthen `hangMs` (250). If a very quiet shack lets the floor track
  too low, raise `minFloorDb` (−75).

Validate: key RADE/700D into a dummy load with a second station decoding; confirm
no R2D2 tail when you unkey, and no garble when you pause mid-over. Confirm soft
speech and word onsets are not clipped.

## Objective fidelity testing (RADE methodology)

RADE's upstream (drowe67/radae, "Testing RADE") insists results be **reproducible
via CLI/objective metrics, not GUI/on-air listening** — calibrated stored-file
channel sims scored by feature distortion (`loss.py`) and Whisper ASR WER, with
documented SNR thresholds (V1 ≈ −2 dB AWGN, ~0 dB MPP). We adopt that discipline
at the managed boundary so "nasal/artifacts" become measurable, not subjective:

- **`RadeResamplerFidelityTests`** (deterministic, no native lib): single-bin DFT
  magnitude response of the RADE 16↔48 kHz resampler. Asserts the passband is flat
  through the **6 kHz presence band** (guards the 96-tap widening), images are
  rejected (anti-imaging on RX), and out-of-band mic content doesn't alias
  (anti-aliasing on the TX decimation). A hard regression guard on the filter.
- **`RadeFidelityTests`** (native, skippable): end-to-end through the real
  zeus_rade + FARGAN decoder. (1) decoded output carries energy and **does not
  clip** — the objective answer to the "is the ×32768 shim scaling clipping?"
  question (it is not, in practice; matches lpcnet_demo). (2) **the stop-talk gate
  validated through the real decoder**: after a synced decode, pure band-noise
  drives the decoded output to silence instead of an R2D2 tail.

Gotcha encountered: `rade_sync()` is a debounced acquisition STATE, not a per-
frame flag (RADE re-primes FARGAN on each loss — `zeus_rade.c`), and it
deliberately drops sync on an **out-of-distribution** signal. A synthetic
harmonic excitation triggers that mid-stream, and the gate (correctly) mutes
there — so score "voice present" only over blocks where sync is actually held,
never a fixed time window. For fully deterministic clean-channel scoring,
`rade_api.h` exposes `rade_set_disable_unsync(seconds)` (radae's own test hook) —
not yet surfaced through the shim; wire it through if a no-unsync fidelity sweep
is wanted.

## End-of-over garble in Zeus's OWN audio — reset the RX modem on the MOX edge

A third operator report (zeus-japz): a garbled burst in **Zeus's own RX audio**
at the end of **every** FreeDV over, on **both** RADE and codec2. This is a
different failure from the stop-talk `RxSquelchGate` above — that gate mutes
decoded **noise** when the *far* station unkeys (their signal lost → *unsynced*).
Here the *local* operator unkeys, and the modem was genuinely **synced on its own
transmitted signal**, so the gate stays open.

Root cause: the FreeDV **receiver** was never reset across the MOX transition.
`DspPipelineService` drains WDSP RX every tick (`engine.ReadAudio`) regardless of
MOX — "RX is drained anyway so the audio ring doesn't back up" — and
`FreeDvService.ProcessRx` is gated only by `!txMonitorOn` + `audioSampleCount>0`,
**not** by `_keyed`. So while keyed the modem keeps decoding the RXA stream (the
operator's own TX bleed/residual), holds sync, and stockpiles decoded speech in
`_rxOut48` (capped ~250 ms). `OnRadioMoxChanged` only armed a 5 ms RX fade — it
never cleared that FIFO or the sync state — so at un-key the resuming receiver
dumped the self-decoded backlog into the output.

Fix: a lightweight `FlushRx()` on `FreeDvModem` and `RadeModem` (mirror of
`FlushTx`: seqlock the RX hot path out, clear `_rx8In`/`_rxOut48`, reset the
resamplers + `RxSquelchGate`, set `_synced=false`, **no** native close/reopen),
routed through `FreeDvService.FlushRx()` (flushes both modems so a submode change
across the edge can't strand a backlog), and called from `OnRadioMoxChanged` on
**both** edges. Key-down drops any pre-TX residual; key-up clears anything decoded
from TX bleed — so RX always resumes empty and unsynced (gate closed → silent
until it genuinely re-syncs on band audio). Validated end-to-end through the real
decoder in `RadeFidelityTests.FlushRx_AfterSync_ResumesSilentAndUnsynced`.

## Not yet changed (deliberately)

- **Fixed RX AGC headroom** — instrumented, not auto-adjusted. Needs the clipping
  log from a real on-air session first.
- **TX leveler end-of-over transient** — the tail draining real frames largely
  mitigates it; the residual is a WDSP-internal leveler ramp that can't be frozen
  per-block without a WDSP API change.
- **Auto-detect parking on a marginal submode** — possible secondary cause of
  "sometimes nasal"; correlate the nasal episodes with submode/SNR telemetry
  before changing the scanner.
