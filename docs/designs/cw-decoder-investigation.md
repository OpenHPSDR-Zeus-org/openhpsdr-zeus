# CW Decoder Panel Design Investigation

**Issue:** zeus-cdn
**Date:** 2026-05-26
**Status:** Investigation Phase — Architecture Decisions

## Overview

Companion piece to the CW keyer panel (zeus-o27 / PR #479). This is a RECEIVE-side CW decoder that listens to RX audio and renders recovered Morse code in the same Telegraph Console visual language. The operator's mental model: "keyer above, decoder below" — both panels look like the same instrument's send and receive halves.

---

## 1. Architecture Decision: Where the DSP lives

### Selected Approach: **Client-side decoder via WebAudio**

**Reasoning:**

1. **Zero server cost** — Decoding runs only when the panel is mounted. In a typical operating session, the CW panel may be visible for a few minutes during a QSO or contest run. A server-side decoder would consume CPU 24/7 per connected radio, even when no operator is actively copying CW.

2. **CPU budget alignment** — The Zeus DSP pipeline (WDSP RXA/TXA) already runs on the server. Adding a decoder per client would multiply this load. Mobile clients have tighter CPU budgets; offloading decoding to the browser distributes the load appropriately.

3. **Privacy/simplicity** — Audio is already sent to clients via `AudioFrame` (0x02) for demodulated output. A client-side decoder can tap into this directly without new wire formats or additional streaming paths.

4. **Multi-client drift is acceptable** — In CW operation, copying is inherently subjective. Two operators listening to the same signal will transcribe slightly differently due to timing perception. Minor drift across browser windows is not a correctness problem.

### Why NOT server-side?

- **Extra CPU per connected radio** — Not justified for a feature used intermittently
- **Already have audio streaming** — No advantage to adding another stream just for text
- **WDSP has no CW decoder** — Confirmed: `native/wdsp/wdsp.h` exports only `SetRXASPCW*` functions for CW *filtering* (matched filter), not decoding. We would need to implement the decoder from scratch in C anyway.

### Why NOT hybrid?

- The hybrid approach (server emits envelope + key-state stream, client renders text) adds wire complexity without a clear win. Client-side Goertzel + FSM is well-understood and computationally cheap.

---

## 2. Decoder Algorithm

### Selected Approach: **Goertzel → Adaptive Threshold → Morse FSM**

**Reasoning:**

1. **Goertzel algorithm** — A single-tone detector that computes one FFT bin efficiently. For CW, we know the approximate pitch (600-800 Hz typical). Goertzel is O(N) vs O(N log N) for FFT, with minimal memory.

2. **Adaptive threshold** — Signal strength varies wildly. A static threshold fails at weak vs strong signals. Compute running average of envelope magnitude and set threshold as `noise_floor + margin`. Margin adjusts based on SNR.

3. **Morse FSM** — Classic state machine tracking on/off transitions. Adaptive timing estimation updates `dit_ms` continuously from observed durations.

### Why NOT other approaches?

- **FFT-bin peak tracking** — Heavier CPU for marginal gain. CW is fundamentally a single-tone problem; we don't need the full spectrum.
- **libcw / unixcw** — Adds a C dependency. The algorithm is simple enough to implement in JS; extra native binding is not worth it.
- **LSTM / ML** — Overkill for PR 1. A well-tuned classic decoder (Goertzel + adaptive timing) handles 5-60 WPM reliably. Revisit only if classic fails on edge cases.

---

## 3. Adaptive Timing

### Approach: **Continuous dit-length estimation from observed transitions**

**Reasoning:**

Human operators don't send PARIS-perfect timing. The decoder must estimate the reference unit duration from the signal itself:

1. Collect all on-key durations in a rolling window (last 10-20 characters)
2. Compute histogram of durations
3. The lower cluster is dits, upper cluster is dahs
4. `dit_ms` = median of lower cluster
5. `dah_threshold` = `1.5 × dit_ms` (standard: dah ≥ 3 dits, but use 1.5x for conservative discrimination)
6. Gap thresholds:
   - `element_gap_threshold` = `1.2 × dit_ms`
   - `letter_gap_threshold` = `3.0 × dit_ms` (standard: letter gap = 3 dits)
   - `word_gap_threshold` = `5.5 × dit_ms` (standard: word gap = 7 dits, but 5.5x catches shorter pauses)

### Estimation trigger

- Re-estimate `dit_ms` every N transitions (e.g., after every 10 elements)
- Clamp to reasonable range (for 5-60 WPM: `dit_ms` ∈ [20, 240] ms)

### Why not static PARIS timing?

- Fails on operators with "fist" — varying speed within a transmission
- Fails on beginner or contest CW where timing is intentionally compressed
- Adaptive approach self-calibrates to the current signal

---

## 4. Source Channel

### Selected Approach: **Tap WDSP RXA0 main output (post-filter, post-AGC)**

**Reasoning:**

1. **Post-filter** — The CW filter (typically 250 Hz bandwidth in Zeus) is already applied. This reduces noise and QRM before the decoder sees the signal, improving accuracy.

2. **Post-AGC** — AGC normalizes level, helping the adaptive threshold converge faster.

3. **Single RXA channel** — Multi-RX (RXA1..3) is a follow-up concern. First cut decodes the main receiver only, which covers the primary CW use case.

### Implementation

- The decoder runs in the browser on the demodulated audio stream already received via `AudioFrame` (0x02)
- Use WebAudio `AudioContext.createMediaStreamSource()` → `AnalyserNode` for time-domain data
- Alternatively, decode directly from the PCM buffer sent via WebSocket (bypass WebAudio for lower latency)

---

## 5. Wire Format

### Selected Approach: **No server-side wire format required**

**Reasoning:**

Since we chose client-side decoding, the decoder runs entirely in the browser. It consumes the existing `AudioFrame` stream and produces decoded text locally.

### Future server-side enhancement (optional)

If we later add server-side decoding (e.g., for unattended skimming), we would add:

- **MsgType**: `0x31` (next available after `0x30` = `CwEngineStatus`)
- **Frame**: `CwDecoderTextFrame`
  ```
  [type:1][char:u8][confidence:f32 LE][wpm:u16 LE]
  ```
  - Stream one frame per decoded character
  - Include confidence metric (0.0-1.0) for UI display
  - Include estimated WPM for status strip

But this is NOT required for PR 1.

---

## 6. UI Design: Telegraph Console Aesthetic

### Match the keyer layout from PR #479

```
┌─────────────────────────────────────────────────────────────┐
│ [●] READY                    ┌─────────────────────────────┐ │
│ CQ DE EI6LF TEST 73        │ +3                         │ │ ← HERO tape
└─────────────────────────────────────────────────────────────┘
┌───────────┬───────────┬───────────┬───────────┬─────────────┐
│ 22 WPM    │ +12 dB    │ ● 98%     │ [HOLD]    │ [CLEAR]    │ │ ← Status strip
└───────────┴───────────┴───────────┴───────────┴─────────────┘
┌─────────────────────────────────────────────────────────────┐
│ 14:23:01 CQ DE EI6LF TEST 73                                │ │
│ 14:23:12 TU 5NN QA                                          │ │
│ 14:23:18 GL 73                                              │ │ ← History pane
│ ...                                                          │ │
└─────────────────────────────────────────────────────────────┘
```

### Components

1. **HERO tape** — Decoded text scrolls left-to-right, latest char on right with cursor
2. **Status strip**:
   - WPM readout (chunky well, same as keyer)
   - SNR / signal bar (visual confidence)
   - Confidence dot (green = high, yellow = medium, red = low)
   - HOLD button (pause decoding)
   - CLEAR button (reset)
3. **History pane** — Last N lines with UTC timestamps (per convention from #486)

### Tokens

Reuse existing `.cw-*` tokens from `zeus-web/src/styles/tokens.css` and `.cw-wpm-*` classes:
- `.cw-wpm-readout` — WPM well styling
- `.cw-wpm-value` — Chunky mono number
- `.cw-wpm-label` — Small uppercase label
- `.mono` — JetBrains Mono for all decoded text

---

## 7. Implementation Plan (PR 1)

### Backend: None required

This is a frontend-only feature for PR 1.

### Frontend

1. **`zeus-web/src/cw/decoder.ts`** — CW decoder core
   - `GoertzelFilter` class (single-tone detector)
   - `AdaptiveThreshold` class (running average)
   - `MorseFsm` class (state machine)
   - `CwDecoder` class (orchestrator)

2. **`zeus-web/src/components/design/CwDecoder.tsx`** — Telegraph Console UI
   - Reuse `.cw-console` wrapper class
   - HERO tape rendering
   - Status strip
   - History pane with UTC timestamps

3. **Integration with existing audio stream**
   - Use `AudioContext` or decode directly from `AudioFrame` PCM
   - Wire into existing CW panel (possibly extend `CwPanel.tsx` or create separate `CwDecoderPanel.tsx`)

---

## 8. Out of Scope (Future)

- Multi-RX decoding (RXA1..3)
- Server-side decoder for unattended skimming
- ML / LSTM-based decoder (revisit only if classic fails)
- Multi-signal Skimmer-style decoding (different epic)

---

## 9. Reference Materials

### Studied

1. **WDSP CW functions** — `native/wdsp/matchedCW.c` exports matched filtering only, no decoder
2. **Goertzel algorithm** — Single-tone FFT bin computation, O(N)
3. **yuvadm/ditdah** — Rust CW decoder using Goertzel + adaptive timing
4. **fldigi morse.cxx** — Reference for adaptive timing (not directly accessed, but noted as canonical)
5. **Audio pipeline** — `IRxAudioSink.Publish(in AudioFrame)` is the existing seam for demodulated audio

### To read before implementation

1. **piHPSDR cw.c** — RX path reference (if accessible)
2. **CW Skimmer Tech Notes** — For advanced features (skipping for PR 1)

---

## 10. Acceptance Criteria

- [ ] Decoder runs in browser on demodulated RX audio
- [ ] Decodes standard CW (5-60 WPM) with >95% accuracy on test signals
- [ ] Telegraph Console UI matches keyer aesthetic (same tokens, layout)
- [ ] WPM readout, confidence dot, SNR bar render correctly
- [ ] HOLD/CLEAR buttons work
- [ ] History pane shows last N lines with UTC timestamps
- [ ] No new server endpoints or wire formats (PR 1 is client-side only)
- [ ] Vitest tests for decoder core (Goertzel, FSM, timing estimator)

---

## Appendix: Character Mapping

Standard International Morse Code (A-Z, 0-9, punctuation):

```
A: .-      B: -...    C: -.-.    D: -..     E: .       F: ..-.
G: --.     H: ....    I: ..      J: .---    K: -.-     L: .-..
M: --      N: -.      O: ---     P: .--.    Q: --.-    R: .-.
S: ...     T: -       U: ..-     V: ...-    W: .--     X: -..-
Y: -.--    Z: --..    0: -----   1: .----   2: ..---   3: ...--
4: ....-   5: .....   6: -....   7: --...   8: ---..   9: ----.
.: .-.-.-  ,: --..--  ?: ..--..  /: -..-.   @: .--.-.
=: -...-  +: .-.-.   _: ..--.-  "': .----.  (): -.--.-
```

Common prosigns (not in decoder scope for PR 1, but noted for future):
- AR: .-.-. (end of message)
- KN: -.--. (invite specific station)
- SK: ...-.- (end of contact)