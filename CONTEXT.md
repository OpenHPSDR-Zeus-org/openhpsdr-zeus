# Zeus — Domain Context

Zeus is a cross-platform HPSDR client for original-protocol radios. This
glossary captures the domain vocabulary that operators, contributors, and
agents share when discussing the radio, its DSP, and its diagnostics.
Implementation lives in code; protocol/board specifics live in
`docs/references/`; this file is *only* the language.

## Language

### Radio plumbing

**Board**:
A specific HPSDR mainboard (Hermes-Lite 2, ANAN-G2, Apache OrionMkII,
Metis, …). Boards share a wire protocol but differ in drive resolution,
PA gain, calibration, and capability set.
_Avoid_: Radio (ambiguous — see below), rig.

**Radio**:
The whole on-air system: a **Board** plus its PA, antenna, and operator
context (band, mode, frequency). When the distinction matters, prefer
**Board** for the hardware and **Radio** for the on-air session.

**TXA**:
The transmit-side DSP channel inside WDSP. Owns the **Audio Chain**
stages from Mic to Out.

**RXA**:
The receive-side DSP channel inside WDSP. Symmetric to TXA on the input
side.

### The Audio Chain

**Audio Chain**:
The TX signal path from the operator's voice into the antenna, viewed as
a sequence of discrete **Stages**. Comprises WDSP TXA, the wire/framer,
and the PA/coupler. Operator-loaded **Plugin Slots** in `AudioChain` are
part of the on-air signal path but are NOT part of the monitored Audio
Chain — plugins handle their own diagnostics.
_Avoid_: TX path (too broad), pipeline (overloaded), processing chain.

**Stage**:
A discrete observable point in the **Audio Chain** with a stable
**Stage ID** (`tx.wdsp.mic`, `tx.wdsp.leveler`, `tx.wire.drive`,
`tx.pa.swr`, …). Each Stage has a reading (level / GR / SWR / …) and an
expected **Sweet Spot**.

**Stage ID**:
The opaque string identifier for a **Stage**. Stable across releases.
The join key between raw meter streams (`tx.stage-meters` etc.) and the
**Health Stream**.

**Sweet Spot**:
The operator-canonical target zone for a Stage's reading — the band of
values where the Stage is doing its job correctly for the current
**Mode** and **Board**. Sourced from Thetis-derived numerical recipes,
not from "what sounds good."
_Avoid_: Optimal range, correct setting, healthy zone.

### Diagnostics

**Audio Chain Monitor**:
The core diagnostic service that reads every **Stage**, evaluates rules,
and publishes **Verdicts**. Not a plugin — first-class core service.
Operators always have it; cannot uninstall.
_Avoid_: Chain analyzer, audio diagnostic, health checker.

**Verdict**:
The Monitor's derived interpretation of one **Stage**: a **Severity**, a
one-line human message, an optional **Apply Target**, and a
**Deeplink**. Slow-changing (~2 Hz); separate from the raw meter reading.

**Severity**:
One of `ok` / `info` / `warn` / `error`. `info` is the explicit "stage
idle by design" state (bypassed, awaiting MOX, mode-disabled). `error`
may carry an immediate-action flag for verdicts demanding the operator
stop transmitting.

**Apply Target**:
The absolute parameter value a **Verdict** recommends — e.g. "Mic Gain
22 → 28 dB." Always absolute, never delta. Idempotent: clicking Apply
twice doesn't double-correct.

**Deeplink**:
The stable identifier the **Factory Widget** uses to focus the operator
on the slider that owns a Stage's parameter. Resolves through the
frontend's chain-focus hook: open the right panel, scroll to the
control, pulse the existing accent outline. Teaches the operator where
the slider lives, rather than reproducing it inline.

**Factory Widget**:
The operator-facing visualisation of the **Audio Chain** as a
left-to-right pipeline of Stage tiles, each showing the raw reading
(from `tx.stage-meters` and friends) plus the **Verdict** pill (from
the **Health Stream**). Renders verdicts with the existing palette
tokens — `--power` yellow for warn, `--tx` red for error.

**Health Frame**:
The new WebSocket binary frame type `AudioChainHealth` (`MsgType =
0x32`) carrying only **Verdicts** — no raw numbers. ~2 Hz, always-on.
The widget joins this with the existing `TxMetersV2` (0x16),
`PaTemp` (0x17), `RxMetersV2` (0x19) frames in the frontend by
**Stage ID**.

**Apply**:
The operator-initiated action that sets a Stage's parameter to its
**Apply Target**. One-click, visible, undoable. Distinguishes the
Monitor from an **Autopilot** — there is no autopilot.
_Avoid_: Auto-fix, auto-tune, self-heal.

**Autopilot**:
The deliberately-rejected v1 mode where the Monitor would mutate Stage
parameters without operator action. Out of scope. The Monitor diagnoses
and (on operator click) applies a single target value; it never nudges
unattended.

### Verdict state

**Sustained-Violation Window**:
The minimum duration a Stage's rule condition must hold before a
**Verdict** fires; symmetric on falling edge. Default 3 s for `warn`,
5 s for `error`. Per-rule overridable. Stops speech gaps and plosive
transients from flashing the widget.

**Hysteresis**:
The dB-wide gap between a rule's entry threshold and its exit threshold.
Default 2 dB on level rules. Stops flutter at the boundary.

**Dismissal**:
The operator's "I know, stop telling me" action on an active Verdict.
Session-only — does not persist across backend restart, does not
persist across the verdict clearing and re-firing. There is no
suppressed-warnings store.

### Modes and context

**Mode**:
The radio's modulation mode (SSB, AM, FM, DIGU/DIGL, CW). The
Audio Chain Monitor's rules switch on Mode: SSB is canonical, AM
adjusts headroom, FM disables most Stages, **Digital** inverts the
verdict on processing Stages (active processing = warn), CW reduces
the chain to wire and PA Stages only.

**Digital**:
DIGU / DIGL, where the operator intends a flat chain. Active Leveler /
CFC / Comp on Digital is a diagnosable defect, not a taste question.

### Rule organisation

**Base Rule Set**:
The canonical rule set, written for SSB and ANAN-class boards. The
default that per-context overrides extend.

**Per-Context Override**:
A small per-(Mode, Board) override class that shadows specific
thresholds, disables rules wholesale, or inverts verdicts. Mirrors the
existing `IRadioDriveProfile` / `PaDefaults` / `RadioCalibrations`
pattern in `Zeus.Server.Hosting`. HL2's drive ceiling, FM's bypassed
chain, Digital's invert-on-processing all live as overrides on the
**Base Rule Set**.

## Flagged ambiguities

- **"Audio chain"** has two meanings in this codebase. The capitalised
  domain term **Audio Chain** is the monitored TX signal path; the
  unrelated lowercase `AudioChain` class in `Zeus.Plugins.Host.Audio`
  is the 8-slot serial **Plugin Slot** chain. The Monitor does NOT
  observe `AudioChain`; the two share a name only.
- **"Stage"** in **Audio Chain** terminology is a Monitor concept.
  `Zeus.Dsp/TxStageMeters` already uses "stage" for WDSP TXA points —
  that usage is the source data for some Monitor Stages but not all.
  When in doubt, qualify: "WDSP stage" vs "monitored stage."

## Example dialogue

**Brian:** The factory widget should warn when the leveler is too hot.

**Agent:** Got it — a `warn` Verdict on `tx.wdsp.leveler` when its GR
reading sits outside its Sweet Spot for longer than the sustained-
violation window. The Apply Target would lower the leveler's max-gain
parameter to bring GR back into the band. On HL2 the per-context
override may pull the threshold tighter because of the drive ceiling.

**Brian:** And on digital?

**Agent:** Digital inverts: any leveler GR at all is a warn on DIGU/DIGL,
since the operator wants a flat chain. The Apply Target there is "set
Leveler off," not "tune the threshold."

**Brian:** What about CW?

**Agent:** CW reduces the Audio Chain to wire and PA Stages — the
operator sees an `info` verdict on all WDSP Stages ("not applicable in
CW") and live readings only on drive, fwd, rev, SWR. The factory widget
stays useful for "is my radio happy?" even when the audio half is dark.
