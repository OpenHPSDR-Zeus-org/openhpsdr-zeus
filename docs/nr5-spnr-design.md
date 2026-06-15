# NR5 / SPNR Design Note

NR5 is an experimental WDSP-native receive noise-reduction mode for weak-signal
copy. It is not a wrapper around RNNoise/NR3. The first implementation is a
self-contained `spnr` stage in `native/wdsp` that reuses the same post-RXA
placement model as EMNR and SBNR while keeping all NR modes mutually exclusive.

## Goals

- Preserve faint coherent signal structure before suppressing noise.
- Learn the current noise floor online without persisting operator/session data.
- Avoid broad whitening that can erase CW edges, weak carriers, or SSB formants.
- Normalize recovered weak-signal audio with a bounded post-NR output AGC.
- Degrade cleanly when an older bundled `libwdsp` does not export SPNR symbols.

## Algorithm

`spnr.c` runs an overlap-add real STFT over the WDSP RX audio/filter stream.
This is the full demodulated channel band that NR5 is responsible for cleaning,
not the separate panadapter display FFT.

1. Window and transform each frame with the existing WDSP FFTW path.
2. Measure per-bin power for the full frame.
3. Maintain a learned noise estimate with fast startup, slow protected updates,
   and faster updates when a bin looks noise-like.
4. Estimate signal presence from instantaneous SNR plus local spectral salience.
5. Track phase-acceleration stability so a weak coherent bin can be protected
   even when its instantaneous SNR is close to the learned floor.
6. Build a bounded ridge memory from coherence, local salience, neighboring
   presence, and neighboring coherent support so narrow carriers and weak
   formant-like structure survive deeper subtraction.
7. Require localized island support before a bin receives full signal
   protection. Isolated random spikes can no longer keep gain open merely
   because one FFT bin briefly looked phase-stable.
8. Increase floor pressure aggressively on unprotected stochastic bins while
   raising the gain floor around coherent/ridge-protected structure. When the
   prior frame shows sparse coherent islands, NR5 applies extra pressure to
   non-island bins so faint signals sit above a lower effective floor.
9. Apply a Wiener-like attenuation with the adaptive signal-preserving floor.
10. Smooth gain changes across frames to reduce musical-noise pumping.
11. Reconstruct with overlap-add.
12. Build a signal-confidence gate from presence, salience, coherence, and
   ridge memory. The output normalizer uses that gate to authorize leveling,
   then drives confirmed weak and strong signals toward the same target RMS.
   Its RMS envelope follows confirmed signal drops faster than noise-only
   drops, confirmed signals can use a wider makeup range, and explicit gain
   slew limits plus a held level-drive keep the leveling action from audibly
   pumping up and down. A final RMS limiter catches sudden powerful frames
   above target without feeding that limiter reduction back into the long-term
   AGC gain, avoiding fade-out/fade-in recovery cycles after loud bursts.
13. Report `dynamicRangeDb` against the effective post-NR floor, so diagnostics
    track usable recovered-signal range rather than only the pre-suppression
    learned input floor.

The "self learning" is intentionally local and volatile: NR5 adapts its per-bin
noise, presence, salience, phase coherence, ridge memory, floor pressure, and
output-normalizer gate while it runs, but it does not save learned state to disk
or train a model on operator audio.

## Integration Points

- Native WDSP:
  - `native/wdsp/spnr.c`
  - `native/wdsp/spnr.h`
  - `RXA.c`, `RXA.h`, `comm.h`, `wdsp.h`, and `CMakeLists.txt`
- Managed WDSP seam:
  - `NativeMethods.SetRXASPNR*`
  - `WdspDspEngine.Nr5SpnrAvailable`
  - `WdspDspEngine.SetNoiseReduction(... NrMode.Nr5 ...)`
- Diagnostics:
  - `wdspNr5SpnrAvailable`
  - `nr5Readiness`
  - `nr5SpnrDiagnostics.coherencePeak`
  - `nr5SpnrDiagnostics.ridgePeak`
  - `nr5SpnrDiagnostics.floorReductionDb`
  - `nr5SpnrDiagnostics.dynamicRangeDb`
  - `nr5SpnrDiagnostics.signalConfidence`
  - `nr5SpnrDiagnostics.agcGate`
  - requested/effective NR mode alignment through `/api/dsp/nr-condition`
- Frontend:
  - NR button cycle includes `NR5`
  - Smart NR may choose NR5 for telemetry-assisted weak signals or coherent
    subthreshold ridges, then downgrade to NR4/NR2 if SPNR is unavailable.

## Tradeoffs

- NR5 is conservative by default. It should favor intelligibility and signal
  preservation over the deepest possible noise subtraction.
- There are no public NR5 tunables in the first pass. The implementation should
  be judged against synthetic fixtures and on-air listening before exposing
  knobs.
- The output normalizer is bounded and presence-gated. It is not a replacement
  for the main WDSP AGC, and it intentionally leaves headroom.

## Verification Plan

- Build native WDSP with `spnr.c` in the shared library.
- Verify managed fallback when SPNR symbols are missing.
- Extend NR mutual-exclusion tests to include NR5.
- Run Smart NR tests for weak-signal, subthreshold, and capability-downgrade
  cases.
- Add synthetic audio fixtures for buried tone, fading carrier, weak SSB-like
  modulation, impulsive noise with NB off/on, and noise-only stability.
