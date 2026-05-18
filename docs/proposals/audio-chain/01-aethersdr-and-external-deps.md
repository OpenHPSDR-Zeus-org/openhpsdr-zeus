# Audio Voice Chain — Phase 0: AetherSDR Reference, License Posture, External Dependencies

Status: Research output (no architecture commitments)
Authors: KB2UKA
Date: 2026-05-17
Issue: #332 (in-process voice audio chain — replaces the out-of-process VST host
proposal in `docs/proposals/vst-host.md` for Zeus's stock voice processing path)
Companion: `docs/proposals/audio-chain/03-ux-and-integration.md`

This document is **Phase 0 research only**. It catalogs AetherSDR's voice-DSP
subsystem as a reference design, fixes the license-compatibility posture, and
surveys external DSP libraries that the locked v1 block list might pull from.
Per the coordinator update of 2026-05-17 the v1 chain is locked at **EQ →
Compressor → Exciter → Bass Enhancer → Reverb**; gate, de-esser, tube, and
limiter are explicitly deferred (WDSP's upstream Leveler stays in place, and
the downstream ALC / brickwall path already exists). The analysis below is
scoped to those five blocks. No interface designs, no Phase 1 architecture,
no code.

Open questions raised by the research surface at the end of the file, prefixed
`> **Open question for sign-off:**`, for inclusion in the master PRD sign-off
list.

---

## 1. AetherSDR chain analysis (reference-only)

[AetherSDR](https://github.com/ten9876/AetherSDR) (ten9876, GPL-3.0-or-later)
is a Qt/C++ HPSDR client whose "Aetherial Audio Channel Strip" is the closest
public reference to what issue #332 wants Zeus to ship. Its block list, chain
ordering, and per-block parameter ranges inform Zeus's design even though no
AetherSDR source will be copied (see §2).

This section catalogs the full AetherSDR chain as a reference, then maps each
AetherSDR block to KB2UKA's locked v1 list — including the blocks Zeus is
explicitly **not** shipping in v1 — so the design decisions in §3 and the
sign-off list at the bottom are anchored against the same reference points.

### 1.1 Full block catalog and chain order

AetherSDR's TX and RX chains are defined as enums in
[`src/core/AudioEngine.h`](https://github.com/ten9876/AetherSDR/blob/main/src/core/AudioEngine.h)
(quoted verbatim from the file):

```cpp
enum class TxChainStage : uint8_t {
    None   = 0,   // sentinel / end-of-list marker
    Gate   = 1,
    Eq     = 2,
    DeEss  = 3,
    Comp   = 4,
    Tube   = 5,
    Enh    = 6,   // PUDU slot (Aphex / SX3040 exciter family)
    Reverb = 7,
};

enum class RxChainStage : uint8_t {
    None  = 0,
    Eq    = 1,
    Gate  = 2,
    Comp  = 3,
    Tube  = 4,
    Pudu  = 5,
    DeEss = 6,
};
```

The header comment on `applyClientTxDspFloat32()` calls out the **canonical
TX order** as `[Gate, Eq, DeEss, Comp, Tube, Enh]`, then `Reverb`, then
`FinalLimiter` (the brickwall is a fixed terminator outside the user-orderable
chain — see [`ClientFinalLimiter.h`](https://github.com/ten9876/AetherSDR/blob/main/src/core/ClientFinalLimiter.h)).

The user-visible block roster, mapped to its implementing file:

| Block | Header file | Role |
|---|---|---|
| Gate | `src/core/ClientGate.h` | Downward expander / hard gate (TX path only as shipped) |
| EQ | `src/core/ClientEq.h` | Up-to-16-band parametric EQ (default 10) |
| De-Esser | `src/core/ClientDeEss.h` | Single-band sidechain dynamics |
| Compressor | `src/core/ClientComp.h` | Feed-forward peak compressor w/ brickwall ceiling |
| Tube | `src/core/ClientTube.h` | Soft-clip waveshaper (3 selectable curves) |
| Exciter (`Enh` / `Pudu`) | `src/core/ClientPudu.h` | **Aphex Aural Exciter + Aphex 204 Big Bottom** (Mode A), or Behringer SX3040 (Mode B). Two-band: "Doo" (high) + "Poo" (low) |
| Reverb | `src/core/ClientReverb.h` | Freeverb (8 comb + 4 allpass) |
| Final Limiter | `src/core/ClientFinalLimiter.h` | Feed-forward peak limiter (TX terminator, not in user enum) |

Notes on the catalog:

- **There is no separate "leveler" or "AGC" block in AetherSDR's user chain.**
  Leveling is left to the upstream radio DSP (in WDSP's case the LEV/CFC
  blocks). The exciter / Pudu block is the closest analog to KB2UKA's
  "exciter + bass enhancer" requirement — and crucially it is **two-band by
  design**, with the low-band ("Poo") implementing an Aphex 204 Big Bottom
  model. AetherSDR fuses what Zeus's locked v1 list separates into "exciter"
  and "bass enhancer" into one combined block. This is the most important
  design tension in §3.
- The TX user-orderable chain ends at `Reverb`; the brickwall limiter is a
  fixed safety stage downstream of the user chain. Operators cannot reorder
  it, disable it, or change its position.
- The RX chain omits the Reverb and FinalLimiter stages — there is no point
  reverberating noise back at the receiver, and brickwall limiting downstream
  of the soundcard is the operator's headphone amp.
- Preset persistence is JSON, key-per-block, with a `"chain"` array recording
  the user's ordering. Quoted from [`ChannelStripPresets.h`](https://github.com/ten9876/AetherSDR/blob/main/src/core/ChannelStripPresets.h):

  ```json
  {
    "version": 1,
    "presets": {
      "PresetName": {
        "chain": ["Gate","Eq","DeEss","Comp","Tube","Pudu","Reverb"],
        "gate": {...}, "eq": {...}, "comp": {...},
        "deess": {...}, "tube": {...}, "pudu": {...}, "reverb": {...}
      }
    }
  }
  ```

  The example preset names called out in the header are `"Broadcast Voice"`
  and `"Contest Punch"`.

### 1.2 Per-block specifications (AetherSDR)

Tables below quote the parameter ranges and defaults visible in each
`Client*.h` header. These ranges inform Zeus's eventual control surfaces but
do not constrain them — KB2UKA may pick wider, narrower, or differently named
ranges in v1.

#### 1.2.1 ClientGate (TX gate / expander) — **NOT in Zeus v1**

| Parameter | Range | Default |
|---|---|---|
| Threshold | −80…0 dB | −40 dB |
| Ratio | 1.0…10.0 | 2.0 |
| Attack | 0.1…100 ms | 0.5 ms |
| Release | 5…2000 ms | 100 ms |
| Hold | 0…500 ms | 20 ms |
| Floor | −80…0 dB | −15 dB |
| Return (hysteresis) | 0…20 dB | 2 dB |
| Lookahead | 0…5 ms | 0 ms |
| Mode | `Expander` (2:1, −15 dB range) \| `Gate` (10:1, −40 dB range) | — |

Algorithm: Schmitt-trigger hysteresis (gate reopens when signal drops to
`threshold - return`), separate hold phase before release, lookahead via
delay line. Source: `src/core/ClientGate.h`.

#### 1.2.2 ClientEq (parametric EQ) — **MAPS to Zeus v1 EQ**

Per [`src/core/ClientEq.h`](https://github.com/ten9876/AetherSDR/blob/main/src/core/ClientEq.h):

| Aspect | Value |
|---|---|
| Maximum bands | 16 |
| Default band count | 10 |
| Default layout | HP / LowShelf / 6× Peak / HighShelf / LP, logarithmically spaced 40 Hz – 12 kHz |
| HP/LP cascade depth | Up to 4 biquad sections (12/24/36/48 dB/octave) |
| Per-band parameters | `freqHz` (default 1000), `gainDb` (default 0), `q` (default 0.707), `slopeDbPerOct` (12/24/36/48 for HP/LP), `enabled` |
| Filter families (HP/LP) | Butterworth / Chebyshev / Bessel / Elliptic |
| Filter types (per band) | Peak, LowShelf, HighShelf, LowPass, HighPass |
| Smoothing | One-pole per-block, ~15 ms time constant (prevents zipper noise on knob drag) |
| Threading | UI atomics + per-band version counters; audio thread recomputes coefficients on version bump only |

The 10-band default layout is the key reference point for KB2UKA's "8–10 band
parametric EQ" requirement. AetherSDR's choice — HP at the bottom, LP at the
top, and shelves anchoring the band cluster — is a sound voice-processing
default because it lets the operator low-cut rumble and high-cut hiss without
spending peaking bands on the extremes.

#### 1.2.3 ClientComp (TX compressor) — **MAPS to Zeus v1 Compressor**

Per [`src/core/ClientComp.h`](https://github.com/ten9876/AetherSDR/blob/main/src/core/ClientComp.h):

| Parameter | Range | Default |
|---|---|---|
| Threshold | dBFS (negative) | −18.0 dB |
| Ratio | 1.0…20.0 | 3.0 |
| Attack | (no explicit cap) | 20.0 ms |
| Release | (no explicit cap) | 200.0 ms |
| Knee | dB | 6.0 dB |
| Makeup | dB | 0.0 dB |

Algorithm notes (quoted from header comments):

- **Feed-forward topology** — the envelope is driven by `max(|L|, |R|)` for
  stereo-linked detection.
- **Peak detection in linear domain.** The header explicitly justifies this:
  *"log-domain smoothing of a sine would settle ~4 dB below the peak"* — i.e.
  log-domain envelope tracking is biased low for sine-like signals, so
  AetherSDR follows the linear-domain envelope and converts to dB only for
  the static curve calculation.
- **Stereo linking** — both channels receive the same gain multiplier to
  preserve phase coherence.
- **Brickwall peak limiter** ships in the same class with an independent
  ceiling parameter (`-12…0 dBFS`), but Zeus's downstream WDSP ALC plus
  CESSB and the existing brickwall make this redundant for our purposes.

This is the right baseline for Zeus's v1 compressor. Single-stage VCA-style,
feed-forward, linear-domain peak detection.

#### 1.2.4 ClientDeEss — **NOT in Zeus v1**

Single-band sidechain dynamics. Per `src/core/ClientDeEss.h`:

| Parameter | Range |
|---|---|
| Frequency (bandpass center) | 1000…12000 Hz |
| Q | 0.5…5.0 |
| Threshold | −60…0 dB |
| Amount (max attenuation) | −24…0 dB |
| Attack | 0.1…30 ms |
| Release | 10…500 ms |

Mode: input split through 2–10 kHz bandpass → envelope detector → broadband
attenuation capped at `Amount`. AetherSDR notes the design parallels Ableton
DeEsser and FabFilter Pro-DS.

#### 1.2.5 ClientTube (soft-clip waveshaper) — **NOT in Zeus v1** (exciter likely covers it)

Per `src/core/ClientTube.h`:

| Parameter | Range |
|---|---|
| Drive | 0–24 dB |
| Bias Amount | 0…1 (asymmetry — 0 = symmetric) |
| Output Gain | −24…+24 dB |
| Dry/Wet Mix | 0…1 |
| Tone Filter | −1…+1 (pre-saturation tilt) |
| Model | `A` (soft tanh) \| `B` (hard-clip + tanh hybrid, odd harmonics) \| `C` (asymmetric, even harmonics) |
| Envelope-modulated drive | −1…+1 bipolar |

Three selectable waveshaping curves (tanh / hybrid / asymmetric), per-sample,
no oversampling. The Aetherial Audio Channel Strip's tube block is
intentionally minimal — there is no per-stage filter network or transformer
model, just a `shape()` function with envelope-driven drive modulation.

#### 1.2.6 ClientPudu (Aphex-style exciter) — **MAPS to Zeus v1 Exciter + Bass Enhancer (combined)**

This is the most informative AetherSDR file for KB2UKA's locked v1 list,
because the **Pudu block is the only public open-source reference for both an
Aural-Exciter-style top-end enhancer and an Aphex 204 Big Bottom-style bass
psychoacoustic enhancer in a single file**. Per `src/core/ClientPudu.h`:

| Mode | Reference design |
|---|---|
| `Aphex` (0) | "Aural Exciter + Big Bottom" — original Aphex two-box model |
| `Behringer` (1) | "SX 3040 Sonic Exciter" — Behringer clone family |

Bands:

- **"Doo" (high band — Aural Exciter):** HPF → variable-gain amplifier →
  asymmetric soft-clip → DC block. Parameters: `dooTuneHz` 1–10 kHz,
  `dooHarmonicsDb` 0–24 dB, `dooMix` 0–1.
- **"Poo" (low band — Big Bottom):** LPF → envelope-follower **dynamic EQ**
  → saturation. Parameters: `pooDriveDb` 0–24 dB, `pooTuneHz` 50–160 Hz,
  `pooMix` 0–1.

For the Behringer mode the header notes a different topology: symmetric
soft-saturation HPF on the high band; frequency-selective compressor with
all-pass phase rotator on the low band.

**Critical observation:** AetherSDR's "Big Bottom" implementation is **a
dynamic-EQ + saturation hybrid in the 50–160 Hz band, parallel-mixed back to
dry**. It is *not* a psychoacoustic missing-fundamental synthesizer (the
MaxxBass / Larsen–Aarts approach). It is a sustain-and-density tool that
matches Aphex's own marketing description of the 204 ("dynamically contours
the bass response of a complex range of shapes in the 20 Hz to 120 Hz range"
— see Aphex 204 review, [Home Theater HiFi
2004](https://hometheaterhifi.com/volume_11_3/aphex-204-big-bottom-7-2004.html)),
and the patent claim that Big Bottom *"increases the perception of low
frequencies without significantly increasing the maximum peak output"* by
**stretching the existing low-frequency content in time and dynamics**, not
by synthesizing harmonics of frequencies the radio cannot transmit.

KB2UKA's coordinator-update brief, however, explicitly asks for a
**MaxxBass-style** algorithm — *"synthesizes upper-harmonics of low-frequency
content so the human ear perceives bass without the radio needing to transmit
the actual low frequencies (which the antenna can't radiate efficiently
anyway)."* That is the Waves MaxxBass / Larsen–Aarts virtual-bass approach,
which is a *different* algorithm family from the Aphex 204. This is the
single biggest decision the master PRD must resolve — see §3.4 and the open
question at the bottom of this file.

#### 1.2.7 ClientReverb (Freeverb) — **MAPS to Zeus v1 Reverb**

Per `src/core/ClientReverb.h`:

| Parameter | Range | Default |
|---|---|---|
| Size | 0…1 | 0.5 |
| DecayS | 0.3…5 s | 1.2 s |
| Damping | 0…1 | 0.5 |
| PreDelayMs | 0…100 ms | 20.0 ms |
| Mix | 0…1 | 0.15 |
| Enabled | bool | `false` |
| Width | fixed at 23 samples L↔R | (not exposed) |

Algorithm: classic Freeverb — 8 parallel lowpass-feedback comb filters summed
through 4 series allpass filters. The header explicitly calls this "a
voice-oriented design with no studio parameters" — wet/dry, size, and damping
only. Width is hardcoded.

The 0.15 mix default is voice-appropriate (subtle), not music-mastering-loud.
That's the right reference number for Zeus's default; the audible effect at
0.15 mix is a near-imperceptible room sense, not a perceptible reverb tail.

#### 1.2.8 ClientFinalLimiter (TX brickwall) — **NOT in Zeus v1** (downstream WDSP ALC + brickwall already covers it)

Per `src/core/ClientFinalLimiter.h`:

- Feed-forward peak limiter, per-block smoothed envelope, "fast attack,
  moderately fast release."
- **No lookahead, no oversampling, no inter-sample-peak handling.** The
  header explicitly states this is a per-block safety fence, not a mastering
  limiter.
- Parameters: Ceiling (−12…0 dBFS), Output Trim (−12…+12 dB), DC-block HPF
  (~25 Hz, optional). RMS metering with 300 ms smoothing for level display.
- Stereo-linked gain to preserve imaging.

This is the right minimum-viable brickwall reference, but Zeus has WDSP's
ALC + the existing TX-stage limiting path, so an explicit limiter block in
the new chain is redundant.

### 1.3 Mapping AetherSDR blocks to Zeus v1

| Zeus v1 block | AetherSDR analog | Notes |
|---|---|---|
| 8–10 band parametric EQ | `ClientEq` (10-band default of HP/LS/6×Peak/HS/LP, log spaced 40 Hz–12 kHz) | Layout is a good v1 default. **Band count tunability is an open question for sign-off.** |
| Single-stage VCA compressor | `ClientComp` | Feed-forward, linear-domain peak detection. Good baseline. |
| Exciter (top-end / "presence" / "air") | `ClientPudu` Doo band (Mode A: HPF→VGA→asymmetric soft-clip→DC block) | Aural Exciter reference is well-established. |
| Bass enhancer (psychoacoustic) | `ClientPudu` Poo band — but algorithm mismatch | AetherSDR's "Poo" is Aphex 204 (dynamic EQ + saturation) not MaxxBass (harmonic synthesis). **§3.4 and open question.** |
| Reverb | `ClientReverb` | Freeverb, voice-tuned defaults. |
| Gate | `ClientGate` | Deferred from v1. |
| De-esser | `ClientDeEss` | Deferred from v1. |
| Tube saturator | `ClientTube` | Deferred from v1 (exciter is the primary harmonic-content block). |
| Final limiter | `ClientFinalLimiter` | Deferred — WDSP ALC + brickwall covers it downstream. |

### 1.4 UI patterns worth noting

These are observed in the AetherSDR file inventory (`src/gui` directory
listing) and are reference data points for Zeus's eventual chain UI; no
visual decisions are made here.

- **Per-block "applet" pattern.** AetherSDR ships an "applet" per block
  (`ClientRxDspApplet`, `ClientChainApplet`, etc.) — small dockable panels
  with the block's own controls and metering. Inspired by Reaper's track FX
  paradigm. Zeus should not necessarily clone this; existing Zeus panel
  vocabulary (DspPanel, TxFilterPanel) already provides a similar "block of
  related controls" idiom.
- **Wall-clock-accurate scope per channel.** AetherSDR's channel strip
  exposes a scope view per side (RX, TX). This is mentioned in the README
  feature list. Zeus already has equivalent meter and waveform infrastructure
  in the TX-stage meter pipeline.
- **Preset library is global, not per-block.** AetherSDR snapshots the full
  chain — block order + every block's state — into one preset. Per-block
  preset libraries are not in evidence.
- **Two-tier parameter visibility is NOT visible in the headers.** AetherSDR
  exposes every parameter for every block. There is no "Basic / Advanced"
  split. This is a design choice Zeus could keep or change — flagged as open
  question.

---

## 2. License posture

### 2.1 Project license summary

| Project | License | Source |
|---|---|---|
| Zeus | GPL-2.0-or-later | [`/LICENSE`](../../../LICENSE) — header reads *"either version 2 of the License, or (at your option) any later version"* |
| AetherSDR | GPL-3.0-or-later | [AetherSDR `LICENSE`](https://github.com/ten9876/AetherSDR/blob/main/LICENSE) — *"GNU GENERAL PUBLIC LICENSE Version 3, 29 June 2007"* with §14 "any later version" clause |

### 2.2 The compatibility hazard, plainly stated

Zeus is GPL-2.0-**or**-later. AetherSDR is GPL-3.0-**or**-later. The "or
later" suffix on Zeus is what saves us: Zeus can be relicensed to GPL-3.0 by
any downstream consumer, so a GPL-3.0 codebase *can* legally consume Zeus
source. **The reverse is not true.** Code copied verbatim from AetherSDR
(GPL-3.0) into Zeus (GPL-2.0-or-later) would force Zeus to drop the "or
GPL-2" option for the resulting binary, because anyone exercising the GPL-2
option would be in violation of GPL-3's additional terms (anti-tivoization,
patent retaliation language, etc.). The cleanest answer is: **don't copy
AetherSDR source.**

This is consistent with how the existing VST host ADR
(`docs/proposals/vst-host.md` §6) handled JUCE: JUCE was rejected on license
grounds because its dual license is incompatible with GPL-2.0-or-later;
AetherSDR has the same hazard for the same reason. The fact that AetherSDR is
already GPL doesn't help — it's the *version* mismatch that bites.

### 2.3 The reference-only rule, what it means in practice

The rule for Zeus's v1 audio chain is: **read AetherSDR for ideas, copy
nothing.** Specifically:

1. **Algorithm ideas, parameter ranges, chain orderings, and default values
   are facts about how voice processing works.** These are not copyrightable.
   We can adopt AetherSDR's 10-band EQ default layout, its 0.15 reverb mix
   default, its `Doo`/`Poo` exciter band naming convention, and its 3.0:1
   compressor default ratio without copying a single line of code.
2. **Source code, comments, structure, and naming of internal types are
   copyrightable.** Even one verbatim `process()` loop, copy-pasted, taints
   the GPL-2 option. Do not paste.
3. **Every DSP line Zeus ships must be authored from one of:**
   - **Hand-written by a Zeus contributor** (clean-room, against the C++/Rust
     algorithm description in the AetherSDR header *comments*, not the
     implementation in the corresponding `.cpp`)
   - **Derived from WDSP primitives** (WDSP is GPL-2-compatible — `Zeus.Dsp`
     already P/Invokes it; see `native/wdsp/eq.c`, `native/wdsp/compress.c`,
     `native/wdsp/cfcomp.c`)
   - **Pulled in from a separately-license-cleared third-party library** —
     each candidate library passes the §3 evaluation gate below

4. **The clean-room boundary is the `.h` file.** Reading AetherSDR's `.h`
   files to learn parameter names and ranges is fine — those are interface
   facts about the algorithm. Reading the corresponding `.cpp` files is
   contamination risk. Zeus contributors implementing the v1 chain should
   **read the algorithm description in the header comments** (which describe
   *what* the block does) but **not the `.cpp` implementation** (which is
   *how* AetherSDR does it). The implementation is where copyright bites.

### 2.4 Tempting-to-copy files — explicit "do not copy" mapping

These are AetherSDR source files a Zeus contributor might be tempted to lift,
each paired with the legitimate alternative.

| AetherSDR file | Temptation | Do this instead |
|---|---|---|
| `src/core/ClientEq.cpp` | 10-band parametric EQ with BiQuad cookbook coefficients | Hand-author against [Audio EQ Cookbook (Bristow-Johnson)](https://webaudio.github.io/Audio-EQ-Cookbook/audio-eq-cookbook.html) — public-domain reference; or thin C# wrapper over WDSP `eq.c` |
| `src/core/ClientComp.cpp` | Feed-forward peak compressor, linear-domain envelope | Hand-author from textbook — Reiss/McPherson *Audio Effects* Ch. 6; or thin C# wrapper over WDSP `compress.c` |
| `src/core/ClientReverb.cpp` | Freeverb implementation | Use [verblib](https://github.com/blastbay/verblib) (single-file C89, MIT-or-public-domain) or [sinshu/freeverb](https://github.com/sinshu/freeverb) (Jezar's original, public domain) |
| `src/core/ClientPudu.cpp` | Aphex Aural Exciter + Big Bottom in one file | High band: hand-author from [Airwindows Exciter](https://github.com/airwindows/airwindows) reference (MIT). Low band: see §3.4 — algorithm choice is open question |
| `src/core/AudioEngine.cpp` | The TX/RX chain dispatcher loop | Hand-author Zeus's own block-chain dispatcher; the dispatcher is not novel and is straightforward to write |
| `src/core/ChannelStripPresets.cpp` | JSON preset serialization | Hand-author using Zeus's existing System.Text.Json patterns from `Zeus.Server.Hosting/RadioStateStore.cs` |

### 2.5 Plugin / weight licensing — not applicable to v1

The vst-host ADR (`docs/proposals/vst-host.md` §6) addresses the
operator-loads-a-VST-plugin-at-runtime case, citing FSF's host/plugin
position. That analysis does *not* apply here, because the v1 audio chain
ships its DSP **statically inside `Zeus.Server`**, not as runtime-loaded
plugins. Every byte of DSP code in the v1 chain is linked into the Zeus
binary at build time, so every byte must clear the GPL-2-or-later test
above. No neural-NR model weights (RNNoise, DeepFilterNet, etc.) are in v1
scope — model-weight licensing is deferred until a future NR block is
proposed.

---

## 3. External dependency evaluation

Per the coordinator update, RNNoise drops out (no NR in v1). The libraries
evaluated below are scoped to the five v1 blocks. The §3.4 bass-enhancer
section receives the most depth because it is the least-obvious algorithm to
implement and has the smallest body of FOSS prior art.

### 3.1 Quick license-compatibility primer for this section

| Third-party license | Compatible with Zeus GPL-2-or-later when… |
|---|---|
| Public domain / Unlicense / CC0 | Always |
| MIT, BSD-2/3-Clause, ISC, Apache-2.0 | Always |
| LGPL-2.1, LGPL-3.0 | **Conditionally** — fine if Zeus dynamically links to the LGPL lib (operator can swap it). Static-linking an LGPL-3 lib into a GPL-2-or-later binary forces the binary's effective license to GPL-3 (loses the GPL-2 option). For Zeus's deployment model (single-process .NET 8 server), "dynamic linking" means **a separate shared library distributed alongside Zeus**, not a NuGet/static-linked package. |
| GPL-2.0-only | Forces Zeus to drop "or-later" for distributed binaries — usually fine for end users but irritates downstream repackagers |
| GPL-3.0-only or GPL-3.0-or-later | Forces Zeus binary to GPL-3. Loses GPL-2 option but legal. |
| AGPL / commercial-source-available / "research only" / CC-BY-NC | **NO** — incompatible with Zeus's distribution model |

The decision matrix below uses these categories.

### 3.2 Block 1 — Parametric EQ (8–10 band)

**Recommendation: hand-author against WDSP primitives or the Audio EQ
Cookbook.** No external library is needed. EQ is the most-documented DSP
block in the world; the BiQuad coefficient formulas are in every audio-DSP
textbook and on every audio-engineering wiki. WDSP already ships `eq.c`
(graphic-style fixed-band EQ) and the cookbook is the canonical reference
for parametric biquads.

Reference libraries surveyed:

| Library | URL | License | Notes |
|---|---|---|---|
| WDSP `eq.c` | bundled in `native/wdsp/eq.c` | GPL-2-or-later (per WDSP) | Already in Zeus tree. Graphic, not parametric — would need extension or a sibling C source file. |
| NWaves (BiquadFilter, PeakFilter, ShelfFilter) | [github.com/ar1st0crat/NWaves](https://github.com/ar1st0crat/NWaves) | MIT | Pure C# / .NET. 100% managed. Includes BiQuad, Butterworth, Chebyshev, Bessel, Elliptic — all the filter families AetherSDR's EQ exposes. **Strongest external candidate** if "managed-only" is the design preference. |
| NAudio (BiQuadFilter) | [github.com/naudio/NAudio](https://github.com/naudio/NAudio) | MIT | Pure C#. Has BiQuad with low-pass / high-pass / peaking / shelving. No compressor, limiter, or specialized voice tools. Already a candidate for Zeus's audio I/O but not strictly necessary for EQ alone. |

> **Open question for sign-off:** Pure-managed (NWaves) or hand-authored
> against WDSP? NWaves gives us the full filter-family palette (Butterworth /
> Chebyshev / Bessel / Elliptic — matching AetherSDR's EQ — and is MIT,
> so the static-linking question is moot. Hand-authored gives us zero new
> dependencies and full control of cache layout / SIMD. Recommend NWaves for
> v1 (faster to ship, well-tested) but the choice is the maintainer's.

> **Open question for sign-off:** Operator-tunable band count (1–16, default
> 10) per AetherSDR, or fixed-10 to simplify the UI? Fixed-10 is the simpler
> UI; the cost is one operator who wants a single extra band finds the EQ
> insufficient. AetherSDR's 16-band cap with a 10-band default seems like a
> good compromise — the UI can ship "show 10 bands by default" with a
> "show all bands" toggle. Maintainer call.

### 3.3 Block 2 — Compressor (single-stage VCA)

**Recommendation: hand-author against the AetherSDR header comment
description, OR thin C# wrapper over WDSP `compress.c` / `cfcomp.c`.** No
external library is needed.

Reference libraries surveyed:

| Library | URL | License | Notes |
|---|---|---|---|
| WDSP `compress.c` | bundled in `native/wdsp/compress.c` | GPL-2-or-later | Already in Zeus tree. Hard-knee peak compressor — fine baseline. |
| WDSP `cfcomp.c` | bundled in `native/wdsp/cfcomp.c` | GPL-2-or-later | Continuous Frequency Compressor (multi-band, FFT-based). Out of scope for v1 (KB2UKA's brief: single-stage). |
| NWaves (`Compressor`, `Limiter`, `Expander`, `NoiseGate`) | [github.com/ar1st0crat/NWaves](https://github.com/ar1st0crat/NWaves) | MIT | Pure C#. Reasonable defaults; documented as "dynamics" group. Worth using if NWaves is adopted for EQ anyway. |
| FAUST `co.compressor_mono` | [github.com/grame-cncm/faustlibraries](https://github.com/grame-cncm/faustlibraries) | STK-4.3 / MIT | FAUST language; transpiles to C++. Useful as algorithm reference, not as a runtime dependency. |

> **Open question for sign-off:** Wrap WDSP `compress.c` (one fewer
> dependency, consistent with the rest of Zeus's DSP path which is already
> P/Invoke) or use NWaves' Compressor (managed, faster iteration)?
> Recommend WDSP wrap — keeps Zeus's "DSP lives in WDSP, C# is the
> orchestrator" architectural invariant intact.

### 3.4 Block 3 + Block 4 — Exciter and Bass Enhancer

This is the most-researched section of this document because (a) it
encompasses two of the five v1 blocks, (b) the algorithm choice for the bass
enhancer is the *only* algorithm in the v1 chain that doesn't have an
obvious "lift from WDSP" answer, and (c) the coordinator update of 2026-05-17
specifically called out psychoacoustic bass synthesis (MaxxBass / Aphex 204)
as the most valuable contribution of this Phase 0 doc.

#### 3.4.1 Two algorithm families — pick one

There are **two distinct algorithm families** described in the prior art,
and KB2UKA's brief asks for the second one but the only public FOSS
reference (AetherSDR's `ClientPudu`) implements the first one. The master
PRD must explicitly decide which family Zeus v1 targets.

**Family A — Aphex 204 "Big Bottom" model.** Time-domain dynamic processing
on the existing low-frequency content: low-pass-filtered sidechain →
envelope follower → dynamic gain / saturation → mixed back to dry. The
psychoacoustic effect comes from **stretching the existing bass content in
time** so the ear perceives it as louder without raising peak amplitude.

- Patent (now expired, ~1989–2008 window): Aphex 204 dynamics + phase
  filtering on the 20–120 Hz band.
- Marketing description: *"dynamically contours the bass response... in the
  20 Hz to 120 Hz range"* — [Home Theater HiFi 2004
  review](https://hometheaterhifi.com/volume_11_3/aphex-204-big-bottom-7-2004.html).
- Patent-level description: *"together the dynamics processor and time
  delay create sustained bass frequencies that are perceived as being louder
  yet do not noticeably increase peak output"* — quoted in same review.
- Public FOSS implementation: **AetherSDR `ClientPudu.cpp` (GPL-3, no copy)**.
  Algorithm description visible in the header comment block (see §1.2.6
  above). Hand-author-replicable from the header description.
- Requires: a 20-bit signal to operate on, i.e. the radio is actually
  transmitting energy at 80–120 Hz. **For SSB-on-an-HF-radio, that is
  exactly the case** — typical SSB bandwidth is 100–2700 Hz or wider, so
  there *is* low-frequency content to "stretch."

**Family B — MaxxBass / Larsen–Aarts "Virtual Bass" model.** Synthesize
*upper-harmonics* of low-frequency content. The fundamental at 50–80 Hz is
filtered out; harmonics at 100–500 Hz are synthesized via nonlinear
distortion and mixed back. The ear perceives the missing fundamental via
the [missing-fundamental
phenomenon](https://en.wikipedia.org/wiki/Missing_fundamental). The radio
never needs to transmit the actual low frequency.

- Patent (Meir Shashoua / Waves, mid-1999, **expired 2006–2008**):
  MaxxBass — "divides the audio signal, generates weighted harmonics for
  frequencies below the loudspeaker cutoff, and combines the signals to
  stimulate auditory sensation up to 1.5 octaves below the cutoff with no
  increase in size or power requirements."
- Foundational academic reference: Larsen & Aarts, *Audio Bandwidth
  Extension: Application of Psychoacoustics, Signal Processing and
  Loudspeaker Design*, Wiley 2005 — the textbook on nonlinear-device-based
  virtual bass.
- Survey paper: [Synthesis and Implementation of Virtual Bass System with
  a Phase-Vocoder
  Approach](https://www.researchgate.net/publication/228362764) (Bai &
  Lin, 2006) — phase-vocoder alternative to nonlinear-device approach.
- Most relevant for KB2UKA's brief: this is the algorithm that lets the
  operator's audio **sound bassy on an HF antenna that physically cannot
  radiate 60–80 Hz** because the HF amateur bands' antennas are
  high-pass-filtered far above the speech fundamental anyway. This is the
  Phase 0 brief's stated goal.
- Public FOSS implementations: **two candidates**, both surveyed below.

**The two families are not the same algorithm**, and the audible
differences matter. Family A (Aphex 204) only works if the radio is actually
transmitting low-frequency content; for an SSB voice mode with the HPF set
at 100 Hz, Family A still has plenty to work with. Family B (MaxxBass) is
the algorithm of choice when the **radio's transmit filter has already
removed the low-frequency content** before the bass enhancer runs — at
which point Family A has nothing to "stretch" and Family B is the only
option that produces an audible "bassy" voice.

> **Open question for sign-off:** Which family does Zeus v1 ship? KB2UKA's
> 2026-05-17 brief explicitly cites MaxxBass / missing-fundamental
> synthesis ("which the antenna can't radiate efficiently anyway"), so the
> stated preference is **Family B**. But Family B requires more DSP work
> (a real implementation of harmonic synthesis with proper
> psychoacoustic-loudness compensation) and is fundamentally a noisier
> algorithm than Family A. Recommend Family B for v1 to match the brief,
> with the explicit understanding that it is a 3–6 week implementation
> (vs. Family A's ~1 week). Final call is the maintainer's.

#### 3.4.2 Public FOSS prior art — Family B (MaxxBass-style) candidates

Two FOSS plugins implement the Family B (missing-fundamental synthesis)
approach.

##### Bankstown (Rust LV2)

[github.com/chadmed/bankstown](https://github.com/chadmed/bankstown) —
**MIT license**, Rust, LV2 plugin format.

Algorithm: three-stage psychoacoustic bass approximation. From
inspection of `src/lib.rs`:

1. **Band-pass split:** high-pass filter at `floor` (default 20 Hz) →
   low-pass at `ceil` (default 200 Hz). Selects the bass content to
   process.
2. **Nonlinear harmonic generation:** modified-Error-function saturation.
   The implementation comment says *"Saturation is performed with a
   modified Error function, which allows us to avoid hard clipping as
   x→inf."* Second-harmonic (asymmetric, negatives only) and
   third-harmonic (symmetric) are generated separately and crossfaded via
   a `blend` coefficient.
3. **Final reconstruction:** a `clamp_pass` filter at `ceil × 3` shapes
   the synthesized harmonics, then a final high-pass at `final_hp`
   (default 200 Hz) removes the original low-frequency band before
   summing back with dry.

LV2 ports (from
[`bankstown.ttl`](https://github.com/chadmed/bankstown/blob/main/bankstown.ttl)):

| Symbol | Name | Default | Min | Max | Unit |
|---|---|---|---|---|---|
| `bypass` | Bypass | 0 | 0 | 1 | toggle |
| `amt` | Amount | 1.0 | 0.0 | 15.0 | — |
| `floor` | Floor Frequency | 20 | 10 | 250 | Hz |
| `ceil` | Ceiling Frequency | 200 | 10 | 250 | Hz |
| `final_hp` | Output HPF | 200 | 10 | 250 | Hz |
| `sat_second` | Second Harmonic | 1.0 | 0.0 | 15.0 | — |
| `sat_third` | Third Harmonic | 1.0 | 0.0 | 15.0 | — |
| `blend` | Harmonic Ratio | 0.5 | 0.0 | 1.0 | — |

**Why Bankstown matters for Zeus.** The MIT license makes it the only
"copyable-with-attribution" FOSS reference for Family B that we have.
The algorithm is small (~150 lines of Rust per inspection) and well-
parameterized. Zeus could ship one of:

- A pure C# port of Bankstown's algorithm (license-clean under MIT
  attribution; preserves Zeus's managed-DSP preference)
- A direct FFI to a Rust `cdylib` build of Bankstown (preserves the
  original algorithm and the upstream maintenance burden)
- A C reimplementation of the same algorithm, dropped into
  `native/wdsp/` as a sibling source file

The algorithm is **demonstrably simple enough to hand-author from the
header comment + the LV2 port spec** — three stages, two saturation
functions, one mixer. The C# port path is the recommended one.

##### DeaDBeeF virtual-bass plugin (C)

[github.com/alpo/DeaDBeeF-virtual-bass-plugin](https://github.com/alpo/DeaDBeeF-virtual-bass-plugin)
— **MIT license**, C.

A research project; algorithm cites the academic literature directly:

- Oo, Gan & Lim (2010) — Arc-Tangent Square Root nonlinear processing
- Gan, Kuo & Toh (2001) — virtual bass for consumer electronics
- Bai & Lin (2006) — phase-vocoder methodology
- Arora, Moon & Jang (2006) — low-complexity algorithms
- Shi, Mu & Gan (2013) — psychoacoustical preprocessing

README quote: *"this algorithm adds some distortion to the low-frequency
part of the spectrum in order to simulate missing low frequencies... if
the distortion level is low then the bass enhancing effect is too subtle
and if the distortion level too high then this is perceived as a
distorted signal."*

The DeaDBeeF plugin is closer to a research prototype than a polished
production block (the author calls it "a research project"). It's most
useful as a reading reference for the underlying papers, not as a
direct source for Zeus to port.

##### Comparison

| Aspect | Bankstown | DeaDBeeF VB |
|---|---|---|
| License | MIT | MIT |
| Language | Rust | C |
| Maturity | Polished, in active use (Asahi Linux audio chain) | Research prototype |
| Lines of code | ~150 (algorithm core) | Larger, multi-algorithm |
| Algorithm | Three-stage nonlinear saturation + reconstruction | Multi-algorithm comparison harness from cited papers |
| Documentation | Inline + LV2 ttl ports | README + paper citations |
| Recommendation | **Use as v1 reference** | Use as academic reading; not for direct port |

#### 3.4.3 Public FOSS prior art — Family A (Aphex 204-style) candidates

For completeness, in case the maintainer chooses Family A for v1:

| Library | URL | License | Notes |
|---|---|---|---|
| AetherSDR `ClientPudu.cpp` | (GPL-3) | NO copy | Read header comment for algorithm description, hand-author |
| Calf Bass Enhancer | [github.com/calf-studio-gear/calf](https://github.com/calf-studio-gear/calf) | LGPL-2.1 | **The most-tempting public reference for Family A.** Calf's documentation describes the algorithm as "the distortion routine from TAP Tubewarmth... restricted in range and added to the original signal." Source files: implementation appears bandlimited-saturator-style; Calf's bass-enhancer plug is built atop their Saturator module. LGPL-2.1 means dynamic linking is required if used as-is. Recommend reading-only for algorithm guidance; the algorithm itself is small and well-described in the [Calf Bass Enhancer manual](https://calf-studio-gear.org/doc/Bass%20Enhancer.html). |

#### 3.4.4 Exciter (top-end / "presence") — separate-block candidates

The "exciter" block in Zeus v1 is the top-end Aural-Exciter-style harmonic
enhancement — separate from the bass enhancer. Candidates:

| Library | URL | License | Notes |
|---|---|---|---|
| Airwindows Exciter | [github.com/airwindows/airwindows](https://github.com/airwindows/airwindows) | MIT | Chris Johnson's plugin suite. "Aural Exciter that can be both subtle and extreme." MIT licensed, C++, no JUCE. The Exciter plugin is small enough to read and port. Also their **Pafnuty** is a Chebyshev-filter aliasing-free harmonic enhancer that's worth a look as a separate candidate. |
| Calf Exciter | [github.com/calf-studio-gear/calf](https://github.com/calf-studio-gear/calf) | LGPL-2.1 | Same TAP-Tubewarmth saturation-routine basis as their Bass Enhancer, but bandpass-filtered to the high band. Read-only for algorithm. |
| AetherSDR `ClientPudu.cpp` Doo band | (GPL-3) | NO copy | Read the Doo-band algorithm comment in `ClientPudu.h`: *"HPF → VGA → asymmetric soft-clip → DC block"* — that single sentence is the entire algorithm. Hand-authorable. |

> **Open question for sign-off:** The two-band combined-Pudu approach
> (AetherSDR's pattern) vs. two separate single-band blocks (Zeus v1's
> stated approach). KB2UKA's brief lists Exciter and Bass Enhancer as
> *two separate v1 blocks* — this is a small departure from AetherSDR's
> design and means the chain UI has one extra slot. Recommend Zeus's
> approach (two separate blocks) — it gives the operator independent
> enable/bypass for each, which is what an HF radio operator wants
> (most ops will want bass enhancer ON for SSB and exciter OFF for
> contesting, or vice versa). Maintainer call.

### 3.5 Block 5 — Reverb

**Recommendation: use [verblib](https://github.com/blastbay/verblib) as the
v1 reverb.** It's a single-file C89 implementation of Schroeder/Freeverb,
under MIT-No-Attribution-or-public-domain dual license, with no
dependencies. Compiles into Zeus's existing `native/wdsp/` build as a
sibling source file.

Surveyed candidates:

| Library | URL | License | Notes |
|---|---|---|---|
| verblib | [github.com/blastbay/verblib](https://github.com/blastbay/verblib) | MIT-No-Attribution or public domain (dual) | **Recommended.** Single-file C89, Schroeder reverb (i.e. Freeverb-style 8-comb + 4-allpass). Parameters: room size, damping, stereo width. No external deps. 22050+ Hz sample rates. |
| sinshu/freeverb | [github.com/sinshu/freeverb](https://github.com/sinshu/freeverb) | Public domain | Jezar's original C++ Freeverb. Slightly more code than verblib (separate Comb/Allpass classes), same algorithm. Either is acceptable. |
| irh/freeverb-rs | [github.com/irh/freeverb-rs](https://github.com/irh/freeverb-rs) | MIT | Rust port; adds FFI overhead for no algorithmic gain. Avoid. |
| Calf Reverb | (Calf project, LGPL-2.1) | LGPL-2.1 | Larger, more parameters, but LGPL-2.1 static-linking concern as noted in §3.1. Avoid for v1; verblib is simpler. |
| AetherSDR `ClientReverb.cpp` | (GPL-3) | NO copy | Reference for parameter defaults (size 0.5, decay 1.2 s, damping 0.5, mix 0.15). |
| NWaves reverb effects | [github.com/ar1st0crat/NWaves](https://github.com/ar1st0crat/NWaves) | MIT | NWaves doesn't ship a reverb effect (its dynamics group covers gate/compressor/limiter, not reverb). Excluded. |

verblib is the right answer for Zeus v1: it is **the same Freeverb algorithm
AetherSDR uses** (8 parallel lowpass-feedback comb filters → 4 series
allpass), it's MIT, it's single-file, and it slots into Zeus's existing
P/Invoke pattern with minimal effort.

> **Open question for sign-off:** Native (verblib C, P/Invoke from Zeus.Dsp)
> or pure-managed (port the algorithm to C# — Freeverb is ~200 lines)?
> The pure-managed route keeps the Zeus.Dsp project's dependency footprint
> minimal but adds a maintenance burden. The native route matches the
> existing WDSP pattern. Recommend native (verblib in `native/`) — it's the
> shortest path and matches the existing architecture. Maintainer call.

### 3.6 Libraries explicitly dropped from v1 consideration

| Library | URL | Reason for exclusion |
|---|---|---|
| RNNoise | [github.com/xiph/rnnoise](https://github.com/xiph/rnnoise) | No NR block in v1 (per 2026-05-17 brief). License (BSD-3) is fine; revisit if NR ships in v2. |
| libspecbleach | [github.com/lucianodato/libspecbleach](https://github.com/lucianodato/libspecbleach) | No NR block in v1. License (LGPL-2.1) is conditionally compatible; revisit if NR ships in v2. Requires FFTW3 which adds binary-size cost. |
| DeepFilterNet | [github.com/Rikorose/DeepFilterNet](https://github.com/Rikorose/DeepFilterNet) | No NR block in v1. Rust+Python, libDF dual MIT/Apache. Model weights have a separate license check that wasn't completed in this Phase 0 (the libDF Cargo.toml URL returned 404 on direct fetch — re-investigate in NR-Phase 0 if NR ever ships). |
| JUCE | [github.com/juce-framework/JUCE](https://github.com/juce-framework/JUCE) | Already rejected in `docs/proposals/vst-host.md` §6 on license-incompatibility grounds. Same answer here. |

### 3.7 Summary recommendation table

| v1 block | Recommended source | License | Pure-managed possible? |
|---|---|---|---|
| EQ | NWaves `BiquadFilter` (managed) **or** hand-authored against Audio EQ Cookbook | MIT / public-domain | Yes |
| Compressor | Thin C# wrapper over WDSP `compress.c` | GPL-2-or-later (Zeus-compatible) | No (P/Invoke) |
| Exciter | Hand-author from Airwindows Exciter algorithm | MIT (reference only) | Yes |
| Bass Enhancer | C# port of Bankstown's three-stage algorithm | MIT (Bankstown attribution) | Yes |
| Reverb | verblib (native C, P/Invoke from Zeus.Dsp) | MIT-No-Attribution / public-domain | No (native, by recommendation) |

If the maintainer prefers **maximum managed-DSP coverage**, four of five
blocks (all except the WDSP compressor wrapper) can be pure C#. If the
maintainer prefers **minimum new dependencies**, all five can route through
`native/wdsp/` (compressor via existing `compress.c`, reverb via verblib
dropped in `native/`, EQ via WDSP `eq.c` extension, exciter + bass enhancer
hand-authored into `native/wdsp/` as new sibling files). Either is
architecturally valid; the choice is the maintainer's.

---

## 4. Surprises and findings worth flagging to maintainer

These are observations from the research that the master PRD should incorporate:

1. **AetherSDR's "Big Bottom" is not MaxxBass.** This is the single most
   important finding. AetherSDR's only public bass-enhancer reference
   implements Family A (Aphex 204 dynamic-EQ + saturation), not Family B
   (MaxxBass missing-fundamental synthesis). KB2UKA's brief asks for
   Family B. The two are different algorithms with different audible
   behavior on an HF antenna. The design must explicitly pick one. See
   §3.4.1 open question.

2. **AetherSDR uses GPL-3-or-later, Zeus is GPL-2-or-later.** Copying
   AetherSDR source into Zeus is a license-tainting operation. The
   reference-only / clean-room rule (read `.h` headers and comments only,
   not `.cpp` implementations) is non-negotiable. See §2.

3. **AetherSDR fuses exciter and bass enhancer into one block (`ClientPudu`).**
   KB2UKA's v1 list splits them into two. This is a small but real design
   departure that gives operators independent enable/bypass. Recommended,
   but flagged. See §3.4.4 open question.

4. **Bankstown's algorithm is small enough to port directly.** ~150 lines
   of Rust for a complete three-stage Family B bass enhancer. MIT-licensed.
   This is the most-leverage finding for the bass-enhancer block: a
   well-defined, documented, license-clean reference implementation that
   Zeus can port to C# in a single day. See §3.4.2.

5. **verblib is a single-file MIT/public-domain Freeverb.** Zero
   dependencies, drop-in. The reverb block is essentially a solved
   problem. See §3.5.

6. **deskHPSDR has no bass-enhancer block.** Despite being the closest
   HPSDR cousin to Zeus's voice-DSP ambitions, deskHPSDR's TX audio chain
   stops at EQ + WDSP's stock compressor / leveler. Zeus's bass-enhancer
   block has no HPSDR-ecosystem precedent — Zeus would be the first
   HPSDR client to ship one. This affects the operator-facing
   communication strategy but does not affect the technical design.

7. **All five v1 blocks can be implemented in pure-managed C#** if the
   maintainer prefers — every external library candidate is either MIT
   (managed-friendly) or has a managed equivalent. The decision to wrap
   WDSP (per §3.3 recommendation) is an architectural preference, not a
   technical necessity.

8. **No model-weight licensing risk in v1.** All five v1 blocks are
   classical DSP — no neural NR weights, no model files. Weight-license
   complexity (CC-BY-NC research-only weights, etc.) is deferred to a
   future NR PRD.

---

## Open questions for sign-off

> **Open question for sign-off:** Bass-enhancer algorithm family — **Family
> A (Aphex 204 dynamic-EQ + saturation)** or **Family B (MaxxBass
> missing-fundamental harmonic synthesis)**? KB2UKA's 2026-05-17 brief
> explicitly cites Family B ("synthesizes upper-harmonics of low-frequency
> content so the human ear perceives bass without the radio needing to
> transmit the actual low frequencies"). Family B is the algorithm that
> matters for an HF antenna whose low-end is filtered out by physics.
> Family A is the algorithm AetherSDR ships, and is simpler to
> implement. Recommend Family B for v1 to match the brief; note that
> implementation is 3–6 weeks vs. ~1 week for Family A. See §3.4.1.

> **Open question for sign-off:** EQ band count — fixed-10 or
> operator-tunable (1–16, default 10)? AetherSDR allows 16. Fixed-10 is
> the simpler UI. Operator-tunable is the more powerful UI. Recommend
> AetherSDR's compromise (default 10 visible, "show all 16" toggle).
> See §3.2.

> **Open question for sign-off:** Compressor implementation — wrap
> existing WDSP `compress.c` (Zeus's "DSP in WDSP, C# orchestrates" pattern)
> or use NWaves's managed `Compressor` (faster iteration, no
> P/Invoke)? Recommend WDSP wrap to preserve the architectural invariant.
> See §3.3.

> **Open question for sign-off:** EQ implementation — wrap/extend WDSP
> `eq.c` (graphic, needs work to make parametric), use NWaves's managed
> BiQuad/Peak/Shelf filters (MIT, well-tested, full filter-family
> palette), or hand-author against the Audio EQ Cookbook? Recommend
> NWaves for v1 if the architecture allows new managed deps; recommend
> hand-author against the cookbook if "minimum new deps" is the
> preference. See §3.2.

> **Open question for sign-off:** Reverb implementation — native
> (verblib, P/Invoke) or pure-managed (C# port of Freeverb)? Recommend
> verblib (native) to match the existing architecture. See §3.5.

> **Open question for sign-off:** Exciter and Bass Enhancer — one
> combined block (AetherSDR's `ClientPudu` pattern, two-band exciter
> handles both) or two separate blocks (KB2UKA's v1 list)? Recommend
> two separate blocks per the v1 list — gives operators independent
> enable/bypass per side. See §3.4.4.

> **Open question for sign-off:** Bass-enhancer source — direct C# port
> of Bankstown's three-stage algorithm (MIT, ~150 lines), C
> reimplementation in `native/wdsp/`, or FFI to Bankstown as a Rust
> cdylib? Recommend C# port — preserves Zeus's managed-DSP preference,
> license-clean under MIT attribution, no new toolchain dependency.
> See §3.4.2.

> **Open question for sign-off:** AetherSDR's chain dispatcher exposes
> the block ordering to the operator (`"chain": ["Gate","Eq",...]` in
> the preset JSON). Zeus v1 — fixed order (EQ → Comp → Exciter → Bass →
> Reverb), or operator-reorderable? Fixed is simpler UI; reorderable is
> more flexible. **No recommendation** — pure visual/UX call. Brian's.

> **Open question for sign-off:** Two-tier parameter visibility
> ("Basic / Advanced" split) as is common in modern voice plugins, or
> all-parameters-always-visible (AetherSDR's pattern)? **No
> recommendation** — pure UX call. Brian's.

> **Open question for sign-off:** All five v1 blocks can be either pure-
> managed (C#) or native (P/Invoke into WDSP / verblib / hand-authored
> C). The architecture allows either. No technical answer is forced.
> Recommend a mixed approach (compressor via WDSP wrap, reverb via
> native verblib, EQ / exciter / bass-enhancer pure-managed C#) — but
> the choice is the maintainer's preference for codebase-shape.

---

## Citations

- AetherSDR repo: <https://github.com/ten9876/AetherSDR>
- AetherSDR audio engine: `src/core/AudioEngine.h`
- AetherSDR per-block headers: `src/core/ClientGate.h`, `ClientEq.h`,
  `ClientComp.h`, `ClientDeEss.h`, `ClientTube.h`, `ClientPudu.h`,
  `ClientReverb.h`, `ClientFinalLimiter.h`
- AetherSDR preset format: `src/core/ChannelStripPresets.h`
- AetherSDR LICENSE: <https://github.com/ten9876/AetherSDR/blob/main/LICENSE>
- Zeus LICENSE: `/LICENSE` at repo root
- Aphex 204 review (Home Theater HiFi, 2004):
  <https://hometheaterhifi.com/volume_11_3/aphex-204-big-bottom-7-2004.html>
- Aphex 204 owner's manual:
  <https://cdn.aphex.com/assets/pdf/Aphex_Exciter_OM.pdf>
- MaxxBass paper (AES, Ben-Tzur):
  *The Effect of MaxxBass Psychoacoustic Bass Enhancement on Loudspeaker
  Design* — <https://www.aes.org/e-lib/download.cfm?ID=8288>
- Larsen & Aarts, *Audio Bandwidth Extension*, Wiley 2005 (textbook)
- Missing-fundamental phenomenon (Wikipedia):
  <https://en.wikipedia.org/wiki/Missing_fundamental>
- Bankstown (Family B FOSS implementation, MIT):
  <https://github.com/chadmed/bankstown>
- DeaDBeeF virtual-bass plugin (academic-reference C plugin, MIT):
  <https://github.com/alpo/DeaDBeeF-virtual-bass-plugin>
- Calf Bass Enhancer documentation:
  <https://calf-studio-gear.org/doc/Bass%20Enhancer.html>
- Calf source repo: <https://github.com/calf-studio-gear/calf>
- Airwindows Exciter and Pafnuty:
  <https://github.com/airwindows/airwindows>
- verblib (Schroeder reverb, single-file C89, MIT/public-domain):
  <https://github.com/blastbay/verblib>
- sinshu/freeverb (Jezar's original Freeverb, public domain):
  <https://github.com/sinshu/freeverb>
- NWaves (.NET DSP library, MIT):
  <https://github.com/ar1st0crat/NWaves>
- NAudio (.NET audio I/O + BiQuad filters, MIT):
  <https://github.com/naudio/NAudio>
- Audio EQ Cookbook (Bristow-Johnson):
  <https://webaudio.github.io/Audio-EQ-Cookbook/audio-eq-cookbook.html>
- RNNoise (excluded from v1, BSD-3):
  <https://github.com/xiph/rnnoise>
- libspecbleach (excluded from v1, LGPL-2.1):
  <https://github.com/lucianodato/libspecbleach>
- DeepFilterNet (excluded from v1, MIT/Apache):
  <https://github.com/Rikorose/DeepFilterNet>
- deskHPSDR (HPSDR cousin, GPL-3):
  <https://github.com/dl1bz/deskhpsdr>
- WDSP source bundled at `native/wdsp/` — see `eq.c`, `compress.c`,
  `cfcomp.c`
- Zeus VST host ADR (companion document for plugin-route DSP):
  `docs/proposals/vst-host.md`
- Phase 0 sibling document on UX integration:
  `docs/proposals/audio-chain/03-ux-and-integration.md`
