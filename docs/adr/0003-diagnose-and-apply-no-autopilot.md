---
status: accepted
---

# Audio Chain Monitor diagnoses and one-click-applies; never autopilots

The Monitor produces **Verdicts** with two operator actions: a
**Deeplink** that focuses the relevant slider with a transient
highlight, and an **Apply** button that sets the parameter to the
verdict's absolute **Apply Target** ("Mic Gain 22 → 28 dB"). Both are
fully operator-initiated and visible. We deliberately rejected
opt-in-per-stage auto-trim and global auto-trim: during MOX the
operator hears every parameter nudge on-air, so even mathematically
correct autonomy reads as "the radio is messing with my signal mid-QSO"
and burns operator trust the first time a rule misfires at the wrong
moment. `CLAUDE.md` classifies operator-facing default values as
red-light maintainer territory; an autopilot mutates exactly those
values dynamically. The diagnose-and-apply contract closes ~95% of the
"replace tribal knowledge" gap with zero autonomy risk.

## Considered Options

- **Diagnose only (no Apply button).** Cleanest, but loses the
  one-action correction path that makes the widget useful in real time.
- **Opt-in per-stage autopilot.** Rejected as above — even bounded
  per-stage, on-air nudging during MOX is a trust failure mode.
- **Global "self-heal my chain" toggle.** Rejected. Same as above plus
  blast radius.

## Consequences

- **Apply Targets are always absolute, never delta**, so the action is
  idempotent (clicking twice does not double-correct) and the operator
  sees the resulting value in advance.
- The rule engine must read current parameter values (to compute
  absolute targets); it does not need write authority outside the
  explicit Apply call.
- A future autopilot is not foreclosed, but is a separate decision
  requiring its own ADR.
