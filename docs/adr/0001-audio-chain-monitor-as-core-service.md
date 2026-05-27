---
status: accepted
---

# Audio Chain Monitor lives in core, not as a plugin

The Audio Chain Monitor diagnoses the operator's TX signal path from mic
to power-out and surfaces verdicts that point at a specific slider to
adjust. We considered shipping it as an `IAudioPlugin` or a UI plugin,
and rejected both: the source telemetry (`tx.stage-meters`, the PA meter
pipeline, `Protocol1Client` drive bytes) is already core, the
"Sweet Spot" thresholds are operator-facing defaults that
`CLAUDE.md` classifies as red-light maintainer territory, and a future
**Apply** action mutates parameters a plugin must not reach. The
Monitor is therefore a first-class service in `Zeus.Server.Hosting`
that operators always have and cannot uninstall; the plugin surface
remains reserved for third-party audio processors that handle their own
diagnostics.

## Considered Options

- **Plugin (UI or audio).** Rejected: plugins can be uninstalled, can't
  carry the "default values" red-light authority, and would couple
  third-party code to internal `RadioService` / `Protocol1Client` reads.
- **Hybrid: core data, plugin UI.** Rejected as premature complication.
  Reserved as a v2 path *if* third parties want deeper-dive panels;
  the v1 widget is core.
