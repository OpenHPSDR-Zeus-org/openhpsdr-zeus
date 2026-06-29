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

## Not yet changed (deliberately)

- **Fixed RX AGC headroom** — instrumented, not auto-adjusted. Needs the clipping
  log from a real on-air session first.
- **TX leveler end-of-over transient** — the tail draining real frames largely
  mitigates it; the residual is a WDSP-internal leveler ramp that can't be frozen
  per-block without a WDSP API change.
- **Auto-detect parking on a marginal submode** — possible secondary cause of
  "sometimes nasal"; correlate the nasal episodes with submode/SNR telemetry
  before changing the scanner.
