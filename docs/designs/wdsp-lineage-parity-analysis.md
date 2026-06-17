# WDSP Lineage and Parity Analysis

**Status:** Phase 1 foundation.
**Primary target:** ANAN G2 first-pass validation; cross-radio validation required before any
"best in class" DSP claim.
**Behavior authority:** Warren Pratt's WDSP Guide Rev 1.23 plus Thetis WDSP behavior.

## Sources

| Source | Role | Current observation |
|---|---|---|
| `Documentation/Radio/WDSP Guide, Rev 1.23.pdf` in the local Thetis tree | API and signal-chain authority | Rev 1.23, Warren C. Pratt, NR0V; documents channel lifecycle, RXA, TXA, analyzer, resamplers, PureSignal, CFC, VOX/DEXP, and external WDSP blocks. |
| `C:\Users\Admin\Desktop\Thetis-master\Project Files\Source\wdsp` | Reference implementation | Treat as the authoritative OpenHPSDR WDSP behavior when Zeus and Thetis differ unless Zeus has an explicit documented product-policy reason. |
| `https://github.com/g0orx/wdsp` | Historical lineage | Commit `49084f50c583a73644e03bcb56443fa9deb327de`, dated 2022-01-17. Useful for old Linux/Android port context, not an update source for Zeus. |
| `native/wdsp` | Zeus vendored WDSP | Contains Thetis-era components plus Zeus build/export shims and local experimental extensions. |
| `Zeus.Dsp/Wdsp` and `Zeus.Server.Hosting/DspPipelineService.cs` | Managed DSP integration | Defines the P/Invoke, channel lifecycle, pipeline policy, telemetry, and operator-facing behavior. |

## Delta Classification

| Area | Classification | Notes |
|---|---|---|
| RXA/TXA stage order | Thetis parity | Zeus must preserve WDSP's RXA and TXA stage order. Stage moves require measured evidence and an explicit compatibility decision. |
| `OpenChannel(... state: 0)` followed by explicit `SetChannelState(id, 1, 0)` | Thetis parity | Required by WDSP channel lifecycle. A stale `-400` RX meter is a regression signal that exchange/exec state did not run. |
| RX filter updates through `SetRXABandpassFreqs`, `RXANBPSetFreqs`, and `SetRXASNBAOutputBandwidth` | Thetis parity | `SetRXABandpassFreqs` alone is insufficient for the active SSB path. |
| AGC mode/custom controls, squelch, TX leveling, CFC, and high-rate sample ladder work | Thetis parity with Zeus integration | Existing parity work should be validated against the guide and Thetis before any modernization tuning. |
| CMake build, runtime native packaging, export wrappers, and fallback symbol probing | Port/build support | These are expected Zeus differences and should not be "reverted to Thetis" unless they are proven defective. |
| RX audio leveler, adaptive squelch, Auto-AGC policy, Smart NR, and frontend chain-health logic | Zeus product policy | These sit above WDSP. They may improve operation, but must be measured separately from WDSP parity. |
| SBNR/RNNR bindings and capability guards | Thetis parity plus portability | Thetis contains SBNR/RNNR-era code that the older g0orx tree lacks. Zeus should keep graceful fallback when native symbols are absent. |
| NR5/SPNR native stage and diagnostics | Experimental enhancement | Preserve as active local work. It remains opt-in/experimental until benchmark and on-air evidence prove it. |
| Broad native WDSP source drift from Thetis | Needs audit | Key files differ in line count and hash. Classify each delta before importing, deleting, or refactoring native code. |
| Crashing broad NR combinatorial test mode | Likely lifecycle/test defect | Keep focused lifecycle tests as gates; run broad combinatorics only as an opt-in diagnostic until the native lifecycle issue is isolated. |

## RXA Audit Map

The WDSP RXA chain is the main receive engine. Zeus must validate the following before tuning:

1. Channel lifecycle: create stopped, configure, start worker, then `SetChannelState(id, 1, 0)`.
2. Buffer/rate model: RXA input/dsp block sizes, sample-rate conversion, analyzer rate, and G2 high-rate DDC behavior.
3. Front-end stages: frequency shift, input resample, generators, ADC meter, pre-noise blankers, and first bandpass.
4. Demod and squelch: SSQL/AMSQ/FMSQ mode selection and adaptive squelch interaction.
5. Post-demod conditioning: SNBA, EQ, ANF/ANR, EMNR, RNNR, SBNR, SPNR, AGC, notches, carrier block, CW filters, patch panel, and output resample.
6. Meter contract: ADC, signal, AGC gain/envelope, NR diagnostics, and frontend telemetry must agree on units and sentinel behavior.

## TXA Audit Map

The TXA chain must be treated as a safety-critical path because level, ALC, and PureSignal changes
can affect transmitted spectral cleanliness.

1. Channel lifecycle and MOX state transitions, including RXA damp-down/re-arm.
2. Input block shape: mic sample rate, TXA profile, P2 high-rate CFIR path, and output IQ block sizing.
3. Gain stages: panel gain, phase rotator, mic meter, downward expander, EQ, leveler, CFC, compressor, overshoot control, ALC, modulation, and output meter.
4. PureSignal: `calcc`/`iqc` state, siphon/analyzer placement, feedback safety, and bypass behavior.
5. Verification: TX two-tone, voice-like audio, clipping, ALC movement, CFC behavior, and PureSignal-disabled fallback.

## Managed Seam Parity Matrix

| Subsystem | Zeus seam | Current parity posture | Next check |
|---|---|---|---|
| Channel lifecycle | `WdspDspEngine.OpenChannel`, `OpenTxChannel`, `SetMox`, `NativeMethods.OpenChannel`, `SetChannelState` | Matches WDSP's stopped-open/configure/start-state pattern; MOX toggles RXA/TXA state explicitly. | Add a lifecycle benchmark that records meter escape and audio drain after each state transition. |
| RX buffer/rate | RXA constants in `WdspDspEngine`, `FeedIq`, worker exchange path | Fixed RXA block sizing is intentional; high G2 DDC rates need bench proof. | Capture CPU, underrun, analyzer cadence, and audio drain at 384/768/1536 kHz on G2. |
| RX filters | `SetFilter`, `ApplyBandpassForMode`, `NativeMethods.SetRXABandpassFreqs`, `RXANBPSetFreqs`, `SetRXASNBAOutputBandwidth` | Uses the three active WDSP stages needed for SSB parity. | Audit tap count, min-phase, and per-mode width caps against Thetis. |
| AGC | `SetAgcMode`, `SetAgcTop`, Auto-AGC path in `RadioService` | Thetis mode/custom controls exist; Zeus adds an operator policy layer for Auto-AGC. | Add AGC pumping/loudness benchmark from the `AgcStep` fixture. |
| Squelch | `SetSquelch`, adaptive squelch in `DspPipelineService` | WDSP SSQL/AMSQ/FMSQ are mode-aware; Zeus adds adaptive server policy. | Use `SquelchTransition` fixture to measure open/close latency and false-open behavior. |
| NR/NB | `SetNoiseReduction`, NR1/NR2/NR4/NR5 bindings, NB1/NB2/SNB setters | Mutual exclusion is enforced in the engine; SBNR and SPNR are capability guarded. | Promote NR5 diagnostic tests into shared benchmark reports and add NR-off/NR-on comparisons. |
| RX audio policy | `DspPipelineService` audio leveler and sanitizer | Zeus policy above WDSP, not Thetis parity. | Measure post-WDSP level stability separately from WDSP AGC so tuning does not mask a WDSP regression. |
| TX leveler/CFC/compressor/ALC | `SetTxLeveling`, CFC setters, TXA processing path | Leveler/CFC wiring exists; TX compressor/ALC behavior must stay Thetis-compatible. | Add TX two-tone and voice-like benchmark runner through `ProcessTxBlock`. |
| PureSignal/CFIR | PureSignal setters, feedback display analyzer, TXA profile selection | Safety-sensitive parity area; no modernization should alter default PureSignal state. | Validate PureSignal disabled/enabled bypass behavior on G2 before any TX profile change. |
| Capability reporting | native export probes and diagnostics DTOs | SBNR/SPNR fallbacks are present; external engines not integrated. | Add an opt-in capability surface only after an external engine passes licensing/package review. |

## Benchmark Foundation

The first benchmark layer is deterministic and offline. It does not change runtime behavior and does
not require a radio. The acceptance producer now feeds those same deterministic fixtures through
`WdspDspEngine`, so offline review starts from real RXA/TXA behavior rather than synthetic
comparison values. The fixture set covers:

- Weak CW/carrier.
- SSB-like speech/formant content.
- Fading carrier.
- Impulse noise.
- Strong adjacent signal.
- Noise-only gating.
- AGC level step/pumping checks.
- Squelch open/close transitions.
- TX two-tone.
- TX voice-like audio.

Metrics captured by the foundation are intentionally primitive but stable: RMS, peak, crest factor,
DC offset, windowed RMS movement, coherent tone power, wanted SNR, signal SINAD, clipping count,
latency, per-fixture processing elapsed time, throughput ratio, and TX intermodulation proxy. The
WDSP-backed runner now also emits wrapper-level WDSP stage timing and managed allocation deltas;
`tools/summarize-dsp-native-stage-timing.ps1` rolls those into
`artifacts/native-stage-timing-report.json` for strict validation. Internal native C stage and
allocator probes remain a separate follow-up before claiming true native heap/timing coverage.

The benchmark acceptance surface is `/api/dsp/benchmark-plan`. It publishes the required scenario
catalog for live tools and scripts: frontend scene freshness, weak CW/carrier preservation,
SSB-like speech, fading carrier, impulse noise, strong adjacent signal, noise-only gating, AGC
level steps, squelch transitions, TX two-tone, TX voice-like audio, PureSignal-safe bypass, and
WDSP channel lifecycle. Each scenario lists required comparisons, metrics, artifacts, and failure
gates. The default source plan requires off, Thetis-parity, current-Zeus, and `nr5-spnr` evidence,
but not external-engine evidence. RX benchmark scenarios require `nr5-spnr` alongside off,
Thetis-parity, current-Zeus, and generic candidate-under-test comparisons; TX, PureSignal,
frontend-scene, and lifecycle scenarios keep the generic candidate contract without implying NR5
applies to those paths. This endpoint is the checklist for G2 capture sessions and later
cross-radio validation.
Strict validation checks this as an acceptance scenario-family contract, not merely a list of
whatever scenarios happen to be present. It surfaces `benchmarkPlanStatus`,
`benchmarkPlanMissingAcceptanceScenarioFamilyIds`, and per-scenario missing comparison/metric/gate
counts; validation triage exposes the same blocker as the `benchmark-plan-coverage` gate and the
`refresh-benchmark-plan-coverage` action.

The companion metric semantics surface is `/api/dsp/benchmark-metric-catalog`. It assigns each
required metric a normalized ID, direction (`higher`, `lower`, or `informational`), unit hint,
safety class, rationale, and related scenario IDs. Strict acceptance now treats the catalog as a
metric contract as well: each required metric must declare a threshold, comparator, unit, safety
class, and acceptance scope before fixture evidence can graduate beyond review-only status.
The endpoint emits `acceptanceThreshold`, `acceptanceComparator`, `acceptanceScopes`, and
`comparatorValues`; baseline-relative directional metrics use `no-regression`, hard safety metrics
such as clipping and native exceptions use zero-count contracts, and contextual metrics stay
explicitly `informational`. `relatedScenarios` is still accepted as a compatibility scope signal
while newer captures can use explicit `acceptanceScope`/`acceptanceScopes`. Capture bundles save the endpoint as
`benchmark-metric-catalog.json`; comparison tooling prefers that captured catalog before falling
back to built-in metric directions.

The capture manifest surface is `/api/dsp/benchmark-capture-manifest`. It combines the current live
diagnostics state with the benchmark plan into a concrete evidence checklist: scenario IDs to run,
required endpoint snapshots, offline fixture metrics, audio/spectrum artifacts, preflight checks,
stop conditions, and operator notes. A manifest can be saved with a G2 session to prove what evidence
was required at the time of capture.

The one-call evidence bundle is `/api/dsp/modernization-snapshot`. It nests the current Smart NR
condition, live diagnostics, benchmark plan, capture manifest, and external-engine candidate catalog
with an evidence completeness score and missing-evidence list. Capture tools should save this once
at the start and end of a G2 session so later review can reconstruct the exact readiness state.
The local helper `tools/capture-dsp-modernization-bundle.ps1` captures that endpoint plus the
supporting diagnostics endpoints into an ignored `captures/dsp-modernization/<timestamp>/` folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-dsp-modernization-bundle.ps1 -BaseUrl http://localhost:6060 -Label g2-nr5-before
powershell -NoProfile -ExecutionPolicy Bypass -File tools\watch-dsp-live-diagnostics.ps1 -BaseUrl http://localhost:6060 -Samples 60 -IntervalMs 1000 -Label g2-nr5-weak-cw
powershell -NoProfile -ExecutionPolicy Bypass -File tools\new-dsp-artifact-manifest.ps1 -BundleDir captures\dsp-modernization\<timestamp>
powershell -NoProfile -ExecutionPolicy Bypass -File tools\audit-wdsp-native-symbols.ps1 -ReportPath captures\dsp-modernization\<timestamp>\artifacts\wdsp-native-symbol-audit.json -RequireBinaryExports
powershell -NoProfile -ExecutionPolicy Bypass -File tools\audit-wdsp-runtime-artifacts.ps1 -ReportPath captures\dsp-modernization\<timestamp>\artifacts\wdsp-runtime-artifact-audit.json -FailOnMissingWinX64Nr5
powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ComparisonId off-baseline -IndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.off-baseline.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-matrix-report.off-baseline.json -Samples 60 -IntervalMs 1000
powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ComparisonId thetis-parity -IndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.thetis-parity.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-matrix-report.thetis-parity.json -Samples 60 -IntervalMs 1000
powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ComparisonId current-zeus -IndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.baseline.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-matrix-report.baseline.json -Samples 60 -IntervalMs 1000
powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ComparisonId nr5-spnr -IndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.candidate.json -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-matrix-report.candidate.json -Samples 60 -IntervalMs 1000
powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-live-diagnostics-history.ps1 -BundleDir captures\dsp-modernization\<timestamp> -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-history.json
powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -BaselineIndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.baseline.json -CandidateIndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.candidate.json -BaselineComparisonId current-zeus -CandidateComparisonId nr5-spnr -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-comparison.json -FailOnRegression
powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-dsp-live-diagnostics-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -BaselineIndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.thetis-parity.json -CandidateIndexPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-index.candidate.json -BaselineComparisonId thetis-parity -CandidateComparisonId nr5-spnr -ReportPath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace-comparison.thetis-parity.json -FailOnRegression
powershell -NoProfile -ExecutionPolicy Bypass -File tools\new-dsp-artifact-manifest.ps1 -BundleDir captures\dsp-modernization\<timestamp> -AcceptanceManifest -RequireLiveAcceptanceArtifacts -Force
powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-dsp-live-diagnostics-traces.ps1 -BundleDir captures\dsp-modernization\<timestamp> -BaselinePath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-baseline.jsonl -CandidatePath captures\dsp-modernization\<timestamp>\artifacts\live-diagnostics-trace.jsonl -FailOnRegression
powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-wdsp-fixture-matrix.ps1 -BundleDir captures\dsp-modernization\<timestamp> -Force
powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-dsp-modernization-bundle.ps1 -BundleDir captures\dsp-modernization\<timestamp>
powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-dsp-modernization-bundle.ps1 -BundleDir captures\dsp-modernization\<timestamp> -RequireArtifactFiles
powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-modernization-validation-report.ps1 -BundleDir captures\dsp-modernization\<timestamp> -FailOnOptInDspBuildOutBlocked
powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-modernization-validation-report.ps1 -BundleDir captures\dsp-modernization\<timestamp> -FailOnIssues
```

For the desktop HTTPS backend, use the same capture and matrix commands with
`-BaseUrl https://localhost:6443 -SkipCertificateCheck`; both tools pass that setting through to
live diagnostics watchers so self-signed local certificates do not break G2 evidence collection.

The scaffold generator writes `artifact-manifest.template.json` by default and includes required
non-endpoint artifacts; pass `-IncludeOptionalArtifacts` when a review needs optional evidence
placeholders too. The default NR5/SPNR source plan does not put
`candidate-external-engine-opt-in` in scope, so ordinary NR5 evidence bundles do not require an
external bakeoff report. If a custom benchmark plan, benchmark capture manifest, artifact manifest,
or captured comparison report includes that comparison, the scaffold preserves or emits
`external-engine-bakeoff-report` as a required artifact even without `-IncludeOptionalArtifacts`,
records `externalEngineBakeoffInScope`, and lists the trigger sources in
`externalEngineBakeoffScopeTriggers` because strict validation requires that report by explicit
external-bakeoff scope. For final G2 live acceptance review after the four matrix captures exist,
pass `-AcceptanceManifest -RequireLiveAcceptanceArtifacts` to promote
the current-Zeus and Thetis-parity `live-diagnostics-trace-comparison` reports, the four live matrix
trace-index/report pairs, and `live-diagnostics-history` to required artifacts without pulling
external-engine bakeoff into scope.
The same switch injects those live artifacts when an older or hand-built capture manifest omitted
the optional live matrix/history entries.
Fill the required paths with offline metrics, audio/spectrum evidence indexes, TX/PureSignal traces,
any scoped external bakeoff evidence, and operator notes, then copy or regenerate it as
`artifact-manifest.json` before treating a capture as acceptance evidence. Without
`-RequireArtifactFiles`, the validator
warns when this manifest is absent and still checks endpoint JSON; with `-RequireArtifactFiles`, it
fails missing required offline metrics, audio renders, spectrum captures, operator notes,
WDSP native symbol audits, and TX/PureSignal traces. The required
`wdsp-native-symbol-audit` artifact must be generated by
`tools/audit-wdsp-native-symbols.ps1` with binary export evaluation enabled; strict validation
rejects reports with missing required source/export symbols, signature mismatches, or
`readyForReview=false`. The required `wdsp-runtime-artifact-audit` artifact must be generated by
`tools/audit-wdsp-runtime-artifacts.ps1`; it records which packaged RIDs actually carry NR4/NR5
symbols, native/dependency SHA-256 hashes, and whether side-by-side FFTW dependencies are present,
so Win x64 NR5 readiness is not confused with pending Linux/macOS/ARM64 rebuild work. Use
`tools/compare-wdsp-source-drift.ps1 -ReferenceDir <local-thetis-wdsp> -CandidateDir native/wdsp`
before importing, deleting, or refactoring vendored WDSP source. It writes
`artifacts/wdsp-source-drift-report.json`, normalizes line endings before hashing, and classifies
each delta as Thetis parity, port/build support, Zeus product policy, experimental enhancement, or
likely defect. `likely-defect` drift means the source delta needs explicit parity classification
before it can be treated as safe modernization evidence. Strict validation recognizes the optional
`wdsp-source-drift-report` artifact, rejects ready claims with likely-defect drift, and triage
includes `wdsp-source-drift-report` as an opt-in DSP build-out prerequisite so native WDSP
modernization cannot skip Thetis/reference classification. The scaffold
also adds the generated
`fixture-metric-comparison-report` artifact at `dsp-fixture-metric-comparison.json`; strict
validation requires it and rejects reports with regressions, failed gates, missing current-Zeus or
Thetis baselines, missing candidate coverage, or missing metric values.

Use `tools/watch-dsp-live-diagnostics.ps1` during G2 scenario windows when the question is
runtime stability rather than a single snapshot. It samples `/api/dsp/live-diagnostics`, writes a
JSONL trace plus a summary report, and tracks hard blockers, AGC gain movement, final-audio RMS/peak
movement, minimum ADC headroom, squelch closed percentage, TX-monitor coupling, and monitor backlog.
The summary's `captureReadinessWatch` block is the first preflight surface to inspect after a run:
it names the top hard gate, the top overall constraint, the dominant status, and mapped action items
for constraints such as stale frontend DSP scene evidence or unmapped Smart NR profile publishing.
Its status distinguishes hard-blocked traces from `ready-with-advisory` traces that are benchmark
usable but still have soft capture hygiene issues to clear before acceptance evidence is final.
Use `-Preflight` for a short fail-fast capture gate; it exits nonzero when samples fail, hard gates
appear, or `readyForBenchmarkTrace=false`. Use `-FailOnHardGate` when an operator wants to continue
on soft advisories but still stop immediately on failed samples or hard blockers.
These traces are optional artifact evidence and do not approve changing defaults by themselves. Use
`tools/compare-dsp-live-diagnostics-traces.ps1` to compare candidate windows against a current-Zeus
or Thetis-parity baseline; pass `-BundleDir` when the comparison report is stored in a capture
bundle so input, JSON, and Markdown paths remain portable. The comparator rejects regressions in
blockers, readiness, AGC/audio movement, NR5 weak-input dropouts/recovery/hot-makeup counters,
low-evidence lift percent, audio-alignment mismatch percent, the composite NR5 artifact-risk score,
NR5 output movement, makeup-gain movement/max, recovery-drive movement, clipping-risk proxy peaks,
ADC headroom, monitor backlog, and diagnostic freshness before any on-air acceptance claim. Its JSON
and Markdown reports also include an NR5 weak-signal comparison summary with baseline/candidate
weak-input, recovered, dropout, hot-makeup, recovery-percent, output movement, makeup movement/max,
recovery-drive movement, texture-fill, low-evidence lift, audio-alignment mismatch, and
artifact-risk deltas for quick live tuning review.

Use `tools/run-dsp-g2-rx-peak-hunt.ps1` before the heavier matrix when recent G2 traces are weak-only
or off-signal. The helper queries the existing frontend top-peak evidence, can run a current-VFO-only
dry capture, and only retunes RX when `-AllowRetune` is supplied. Operators can bound intermittent
band hunting with `-PassCount` and `-PassDelaySec`, and can supply known active frequencies with
`-CandidateMHz` or `-CandidateFrequencyHz`; those operator frequencies are reported in dry runs but
are only captured when `-AllowRetune` is present. In retune mode it posts to `/api/vfo`, waits for RX
settling, delegates every window to `watch-dsp-live-diagnostics.ps1`, refreshes frontend/live
diagnostics each pass, and restores the original VFO in cleanup. Its report ranks candidate windows
by weak/strong NR5 sample coverage, weak/strong output or final-audio gap, AGC pumping risk, candidate
weak-loss rows, and hot makeup. This is an RX evidence-hunting tool, not a DSP behavior change and not
an acceptance shortcut: promote a found window into live history and baseline/candidate comparisons
before using it for tuning.
When `artifact-manifest.json` includes the optional `g2-rx-peak-hunt-report` artifact, strict
validation exposes the report as `g2RxPeakHunt*` fields, including pass counts and operator candidate
counts, and validation triage adds the advisory `g2-rx-peak-hunt` gate plus a Markdown evidence
section. Missing peak-hunt evidence does not block
acceptance, but present evidence must prove RX-only safety, no TX endpoint touch, VFO restore after
retune, no hard blockers, and no unexplained AGC pumping before its best window can be promoted into
live-history recapture. The report is only review-ready when every `runs[]` entry references
bundle-relative per-window watcher JSON and JSONL files inside the bundle; copied aggregate reports
with absolute or missing child paths are classified invalid and surfaced through
`g2RxPeakHuntReferencedWindow*` counters.

Use `tools/run-dsp-live-diagnostics-matrix.ps1` when a G2 session needs repeatable live windows
across several benchmark scenarios. It delegates to the single-window watcher, writes one JSONL trace
and summary per scenario, and produces `artifacts/live-diagnostics-trace-index.json` for optional
`trace` artifact validation. Matrix runs and trace-index entries carry NR5 weak-input, recovered,
dropout, and hot-makeup counts from the watcher summaries so review reports can see weak-signal
tradeoffs even before a baseline/candidate comparison is generated. Matrix schema v2 also copies
`strongInputSampleCount`, `weakStrongOutputGapDb`, `mixedWeakStrongEvidenceStatus`,
`mixedWeakStrongEvidenceReady`, `weakStrongOutputParityReady`, and
`nr5MixedWeakStrongHuntScore` into each run and trace-index entry, plus aggregate
`nr5MixedWeakStrongHuntReady`, mixed trace/ready/missing/gap-watch counts, status counts, and
`bestMixedWeakStrongRun`. These fields are capture-selection evidence: they help the G2 operator
find the missing mixed weak+strong speech window before running the heavier acceptance cycle, but
they do not change the DSP engine or acceptance defaults. Strict validation now rolls these fields up
as `liveMatrixMixedWeakStrong*`, and the triage report exposes an advisory
`live-matrix-mixed-weak-strong-hunt` gate plus a Markdown section that names the best matrix run to
feed into live-history and live-comparison evidence.
Matrix schema v3 adds advisory artifact-control rollups copied from watcher summaries:
`nr5ArtifactRiskScore`, `nr5ArtifactRiskStatus`, low-evidence lift counters, audio-alignment
mismatch, texture fill, and signal-probability averages. Strict validation exposes these as
`liveMatrixArtifactControl*`, and the triage report surfaces a non-required
`live-matrix-artifact-control` gate so a high-scoring mixed weak+strong window is not promoted for
tuning when it also shows speech-artifact risk. Watcher summaries also carry
optional `scenarioId`, `comparisonId`, and `label` metadata; the matrix runner passes these through
explicitly so history coverage does not depend only on bundle folder names. Matrix trace-index
entries now also copy `captureReadinessWatch` status, hard-gate pass, strict-preflight pass, and top
hard/soft capture constraints so a multi-scenario run exposes stale frontend scene or Smart NR
profile-publishing problems without opening each watch summary. Watcher summaries and matrix reports
also carry `comparisonStateReadiness`, requested/effective NR mode counts, and NR5 diagnostic
coverage counts. Required `nr5-spnr` traces must prove requested/effective `Nr5` state plus NR5
diagnostic coverage, and required `off-baseline` traces must prove requested/effective `Off` state
with no NR5 diagnostic samples; `current-zeus` and `thetis-parity` remain advisory state labels.
Use `-ComparisonId current-zeus`
or `thetis-parity` for baseline windows and candidate IDs such as `nr5-spnr` for opt-in candidate windows. Pass
separate `-IndexPath` and `-ReportPath` values for baseline and candidate matrix runs when reusing
the same bundle so one capture does not overwrite the other. Pass
`-PlanOnly` to print the full G2 acceptance command cycle, including the four comparison matrix
captures, live-history summary, current-Zeus and Thetis-parity matrix comparisons, required-live
manifest regeneration, strict bundle validation, and validation-triage report outputs.
Use `tools/run-dsp-live-acceptance-cycle.ps1 -PlanOnly` to print the same cycle as one executable
wrapper recipe, or run it against an existing modernization bundle when a G2 capture session is
ready. Strict mode remains the default and returns non-zero after writing
`artifacts/live-acceptance-cycle-summary.json` when capture preflight, comparison, or validation
fails; `-AllowCapturePreflight`, `-AllowRegression`, and `-AllowValidationPreflight` are
exploratory-only escape hatches.
When that summary is present in `artifact-manifest.json`, strict validation verifies its wrapper
identity, portable child artifact paths, G2 hardware evidence status, readiness math, comparison
counts, live trace metric-catalog alignment status for both current-Zeus and Thetis-parity
comparisons, live-history AGC stability status, mixed weak/strong volume-parity status, live matrix
mixed weak/strong hunt status, and triage counts, then validation triage
surfaces the wrapper status in a `Live Acceptance Cycle Summary` section. Validation triage also
keeps the current-Zeus and Thetis-parity live comparisons as separate evidence gates:
`live-trace-comparison` and `thetis-parity-live-comparison`, and the G2/opt-in readiness stages
require both before a candidate can move into review. A preflight validation run can still be useful for
exploration, but the wrapper summary cannot claim `liveAcceptanceEvidenceReady=true` unless the
computed hardware status is `g2-hardware-evidence-ready`, the current-Zeus and Thetis-parity live
comparisons are ready for review, and both captured live trace comparison metric-definition sets
align with `benchmark-metric-catalog.json`. Summary schema v6 also requires
`liveDiagnosticsHistoryAgcStabilityReady`, `liveDiagnosticsHistoryAgcStabilityStatus`, and the
AGC stability/pumping counters from strict validation, plus
`liveDiagnosticsHistoryMixedWeakStrongEvidenceReady`,
`liveDiagnosticsHistoryMixedWeakStrongEvidenceStatus`, and mixed trace/ready/missing/gap-watch
counters. Schema v6 also copies `liveDiagnosticsHistoryArtifactControlSignalCount` as an advisory
speech-artifact review signal. Schema v7 copies the advisory `liveMatrixMixedWeakStrong*` hunt
rollup, best run, and `liveMatrixArtifactControl*` matrix artifact-control rollups from strict
validation, plus the validation-triage `primaryAcceptance*` next-action fields, so the next G2
live-history target, artifact-risk status, and blocked-run command are visible in the wrapper
summary without making matrix hunt or artifact-control readiness a separate acceptance gate.
Schema-v14 live histories missing the
required AGC evidence surface as a
named `live-history-agc-stability-missing` or `live-history-agc-stability-not-ready` blocker instead
of hiding behind generic validation failure, and histories without bounded mixed weak/strong output
parity surface `live-history-mixed-weak-strong-missing`,
`live-history-mixed-weak-strong-gap-watch`, or `live-history-mixed-weak-strong-not-ready`.
`-IncludeOptionalArtifacts` to `tools/new-dsp-artifact-manifest.ps1` when the bundle should validate
the optional live matrix evidence; the scaffold emits separate off-baseline, Thetis-parity,
baseline, and candidate trace-index entries plus matching `live-diagnostics-matrix-report` entries,
each with scoped `comparisonIds`. If
an artifact captures only one comparison, add `comparisonIds` to that artifact-manifest entry so
strict validation checks the captured comparison instead of requiring every benchmark-plan
comparison in the same file.
Use `tools/compare-dsp-live-diagnostics-matrix.ps1` after capturing baseline and candidate trace
indexes; it matches scenario entries, reuses the single-trace comparator for each pair, and writes
one aggregate `live-diagnostics-trace-comparison` report for bundle validation. G2 acceptance now
requires two aggregate reports: `live-diagnostics-trace-comparison.json` for current-Zeus vs
candidate and `live-diagnostics-trace-comparison.thetis-parity.json` for Thetis-parity vs candidate,
so opt-in NR5/SPNR evidence cannot pass without preserving the WDSP behavior authority. Each aggregate report
also carries per-scenario metric, hard-constraint, gate, and missing-metric detail arrays so review
can identify the exact weak-signal, pumping, clipping, freshness, or latency regression without
opening every scenario report first. It aggregates the per-scenario NR5 weak-signal summaries into a
single baseline-vs-candidate rollup for weak recovery/dropout/hot-makeup plus output movement,
makeup movement/max, recovery-drive movement, texture-fill, low-evidence lift, audio-alignment
mismatch, and artifact-risk score. It also aggregates RX audio-leveler
constraint summaries for constrained samples/percent, boost-slew, peak-headroom, output-cap, output
RMS movement, and applied gain movement. Strict bundle validation surfaces those deltas and
regression counts in `liveTraceComparisonNr5*` and `liveTraceComparisonRxAudioLeveler*` fields.
Single-trace and matrix comparison reports also carry capture-readiness comparison evidence. Matrix
comparisons aggregate `captureReadinessComparisonSummary`; strict validation recomputes it from
scenario rows and surfaces it through `liveTraceComparisonCaptureReadiness*` fields, including
candidate hard-gate pass/fail counts, strict-preflight pass/fail counts, capture status counts, top
soft constraints, and top hard gates.
Strict validation now rejects live comparison reports that omit the RX audio-leveler comparison block,
so older or hand-edited reports cannot appear clean by defaulting those fields to zero.
Single-trace and matrix comparison reports also roll metric regressions and missing values up by
safety class so strict validation can expose top-level pumping, weak-signal, clipping, readiness,
freshness, front-end, audio-path, and tooling regression counts. Matrix comparison reports include SHA-256
hashes for the baseline/candidate trace indexes and each per-scenario comparison report; strict
bundle validation verifies those hashes, binds each scenario row back to exactly one
baseline/candidate trace-index entry, checks the selected input path and index-declared input hash,
reads the per-scenario reports, verifies each per-scenario report's baseline/candidate input hashes,
checks row/report path and count consistency, and recomputes aggregate comparison counts plus
`readyForReview` before trusting the matrix summary. Single-trace comparison reports also carry
baseline/candidate input SHA-256 hashes, and strict validation verifies those hashes for standalone
comparison artifacts before accepting the report. Single-trace reports now emit the live comparison
`metricDefinitions` used for verdicts, including direction, authored threshold, safety class, and
`acceptanceScope=live-diagnostics-trace-comparison`; matrix reports aggregate the same definitions
from their per-scenario reports. Strict bundle validation checks those definitions against
`benchmark-metric-catalog.json` and fails required live comparison artifacts when a metric is missing
from the catalog or when direction, numeric acceptance threshold, comparator, safety class, or scope
does not match. Reports created
with `-BaselineRoot` or `-CandidateRoot` carry portable root override paths so strict validation
resolves index entries through the same roots as the comparator. Pass
`-BundleDir` when the comparison report is stored in a capture bundle so index, input, Markdown, and
per-scenario report paths stay bundle-relative; strict bundle validation flags required single-trace
or matrix comparison reports that were generated without portable paths.
For acceptance review, regenerate `artifact-manifest.json` with
`-AcceptanceManifest -RequireLiveAcceptanceArtifacts` after the comparison report is captured so
strict validation fails the bundle on live trace regressions or incomplete live-history coverage
instead of treating them as optional evidence warnings.
If a `run-dsp-live-diagnostics-matrix` report is included as a bundle artifact, strict validation
recomputes its `runs[]` summary before trusting the report-level fields. It checks scenario,
failed/not-ready/hard-blocker counts, per-run sample totals, collection/acceptance readiness,
aggregated NR5 weak counters, and `indexSha256` against the referenced trace index. When the
referenced trace index is present, each report run must also match exactly one trace-index file entry
by scenario/comparison, JSONL path, summary path, sample count, and copied NR5 weak counters; for
matrix-generated indexes, missing sample or NR5 counter fields fail validation. This catches
hand-edited matrix reports before they can steer candidate comparison or on-air acceptance review.
For `live-diagnostics-trace-index` entries, bundle validation also reads each referenced
`summaryPath` and rejects acceptance evidence when the watcher summary is not benchmark-ready or an
NR5/SPNR window lacks complete `levelDrive`, `recoveryDrive`, and `makeupGainDb` diagnostics.
When a watcher summary includes explicit `scenarioId` or `comparisonId` metadata, strict validation
also checks that it matches the trace-index entry that points to the summary. Older watcher summaries
without this metadata remain legacy-compatible, but copied or swapped summaries now fail with
`live-trace-index-summary-scenario-mismatch` or `live-trace-index-summary-comparison-mismatch`.
The identity check uses only true comparison fields (`comparison`, `comparisonId`, `comparisons`,
and `comparisonIds`); broader artifact coverage aliases such as `candidate`, `mode`, and `backend`
still feed coverage expansion but do not authenticate the watch summary.
If the watcher summary records `jsonlPath` or `tracePath`, strict validation also confirms every
present trace path field points to the same JSONL evidence file as the trace-index entry. The
comparison tolerates moved bundles by matching the last `artifacts/...` suffix when an older summary
contains an absolute capture path, while a real mismatch fails with
`live-trace-index-summary-trace-path-mismatch`. Summaries without JSONL path metadata remain
legacy-compatible.
Strict validation also parses each referenced live trace JSONL, rejects corrupt or record-empty
trace files, and compares the JSONL record count with `files[].sampleCount` and watcher
`sampleCount` when those fields are present. Count mismatches fail with
`live-trace-index-file-sample-count-mismatch` or
`live-trace-index-summary-sample-count-mismatch`, so stale summaries and truncated traces cannot be
used as acceptance evidence.
Matrix trace-index entries also copy NR5 weak-input, recovered, dropout, hot-makeup, strong-input,
weak/strong output-gap, mixed-status, parity-ready, and hunt-score fields for quick review. When
those copied `nr5Weak*` counters are present, strict validation compares them
against the referenced watcher summary and fails stale weak-signal values with
`live-trace-index-nr5-weak-counter-mismatch`. Older indexes without copied counters remain
compatible, but they are reported as `legacy-missing-index` in `artifactReferencedFiles`.
New matrix trace indexes include SHA-256 hashes for the referenced JSONL (`sha256`) and watcher
summary (`summarySha256`). Strict validation recomputes both hashes when present and fails tampered
or stale evidence with `live-trace-index-hash-mismatch` or
`live-trace-index-summary-hash-mismatch`. Older indexes without hashes remain compatible and report
`hashStatus=legacy-missing-index` or `summaryHashStatus=legacy-missing-index`.
Matrix report schema v2 fields are also cross-checked against `runs[]` and matching trace-index
entries. Stale mixed weak/strong matrix evidence fails with
`live-matrix-report-mixed-weak-strong-*` or
`live-matrix-report-run-index-mixed-weak-strong-*` issue codes, while valid bundles expose
`liveMatrixMixedWeakStrongHuntReady`, status/count rollups, status counts, and
`liveMatrixMixedWeakStrongBestRun` for operator triage.
Matrix report schema v3 also cross-checks artifact-control copies between report runs and trace-index
entries, including risk status/score, low-evidence lift, alignment mismatch, texture fill, and signal
probability. Valid bundles expose `liveMatrixArtifactControl*` rollups; `artifact-review` is an
operator advisory for choosing a cleaner capture window, not a default-behavior blocker by itself.

Use `tools/summarize-dsp-live-diagnostics-history.ps1` after several NR5/NR2 live tuning attempts
have been captured. The history report scans watch summaries, ranks the latest, best weak-signal,
lowest-pumping, and best balanced NR5 traces, rolls all safety signals up by class, and emits
Markdown recommendations. This is tuning guidance rather than acceptance by itself; use it to choose
which live window should become the next candidate/baseline comparison and to avoid optimizing weak
recovery while reintroducing output-level pumping. Pass `-BundleDir` when writing the optional
`live-diagnostics-history` artifact so the report, Markdown path, scanned watch summaries, and trace
paths stay bundle-relative. Strict bundle validation surfaces
`liveDiagnosticsHistoryTraceCount`, `liveDiagnosticsHistoryNr5TraceCount`,
`liveDiagnosticsHistoryLatestReviewStatus`, best-trace IDs, aggregate pumping/weak-signal counts,
path portability fields, and `liveDiagnosticsHistoryPromotion*` fields when the artifact is present.
The report's `latestNr5Decision` is deliberately scoped to "candidate comparison only": a ready
decision means the latest trace is clean enough to compare against current-Zeus/Thetis evidence,
while a blocked decision carries blocker classes and recommends the best-balanced, best weak-signal,
or lowest-pumping trace as the next tuning reference. It never authorizes default DSP behavior
changes by itself. Each trace also carries `candidateComparisonReady`, `promotionBlockerClasses`,
and `promotionBlockerClassCounts`; the report rolls those into `readyNr5TraceCount` and
`candidateReadyNr5TraceCount` so review can distinguish "benchmark-ready capture" from "safe enough
to promote into the next candidate comparison." Strict validation recomputes best-balanced,
best-weak, and lowest-pumping trace selections from the full `traces` records, checks compact trace
decision fields against the full records, and derives `latestNr5Decision` from latest-trace safety
signals and trace-derived references. It verifies promotion status, ready flags, recommended and
reference trace IDs/roles, `nextAction`, blocker projections, blocker readiness summaries,
`latestIsBestBalanced`, and risk score deltas so a hand-edited decision cannot steer tuning or
experiment plans. It also recomputes review status counts, exported tuning thresholds,
latest-vs-previous NR5 numeric deltas, RX audio-leveler constrained-sample fields, and ordered
recommendations from the same trace evidence so advisory report fields cannot drift away from
producer semantics. Strict validation also resolves
each full `traces[].path` entry back to its referenced `watch-dsp-live-diagnostics` summary and
rebuilds the trace identity, source paths, scalar metrics, safety signals, readiness summaries, risk
score, and per-trace promotion blockers from that source summary before accepting the history. This
prevents hand-edited `traces` records from becoming the trusted substrate for later decisions.
Generated artifact manifests call out this provenance requirement so the history report is captured
after watcher summaries and JSONL traces are finalized.
Schema v2 history reports also quantify each safety
signal with `thresholdDirection`, `unit`, `readinessGap`, `readinessMargin`, and grouped
`readinessGapSummary`/`aggregateReadinessGaps` entries. These fields answer how far a trace missed a
threshold without mixing dB, percent, sample-count, and boolean gaps into one physical total. Strict
bundle validation recomputes those gaps and the per-class rollups, and surfaces
`liveDiagnosticsHistoryAggregateReadinessGaps`, `liveDiagnosticsHistoryReadinessGapSignalCount`,
`liveDiagnosticsHistoryReadinessGapNumericSignalCount`, and `liveDiagnosticsHistoryReadinessGapMax`
when the optional history artifact is present. Schema v3 adds
`latestVsPreviousNr5ReadinessGapTrend`, comparing the latest NR5 trace against the previous NR5 trace
by safety class, signal name, threshold direction, and readiness-gap unit. It reports `new-gap`,
`resolved-gap`, `gap-narrowed`, `gap-widened`, and `unchanged` details plus unit-safe
`readinessGapTrendBuckets`; strict validation recomputes the trend from the full `traces` records and
surfaces `liveDiagnosticsHistoryReadinessTrendStatus`,
`liveDiagnosticsHistoryReadinessTrendPreviousTraceId`,
`liveDiagnosticsHistoryReadinessTrendImprovedSignalCount`,
`liveDiagnosticsHistoryReadinessTrendRegressedSignalCount`, and
`liveDiagnosticsHistoryReadinessTrendGapMaxDelta`. Schema v4 adds
`latestVsReferenceNr5ReadinessGapTrend`, which compares the latest NR5 trace against the
trace-derived `promotionDecision.referenceTraceId` rather than the recommended trace. This keeps the tuning
reference stable when the latest trace is blocked or when the latest trace is promotable but still
needs comparison against the best-balanced reference. The reference trend includes
`comparisonScope=latest-vs-reference`, `referenceTraceId`, `referenceTraceRole`,
`latestIsReference`, and `referenceIsRecommended`; strict validation recomputes it from full trace
records, rejects role/id mismatches, and surfaces
`liveDiagnosticsHistoryReferenceTrendStatus`,
`liveDiagnosticsHistoryReferenceTrendReferenceTraceId`,
`liveDiagnosticsHistoryReferenceTrendImprovedSignalCount`,
`liveDiagnosticsHistoryReferenceTrendRegressedSignalCount`, and
`liveDiagnosticsHistoryReferenceTrendGapMaxDelta`. Schema v5 adds
`latestTuningActionPlan`, a deterministic review-only plan that converts the latest blocker set and
latest-vs-previous/reference readiness trends into ranked tuning actions. Each action names a
`safetyClass`, `signalName`, `controlFamily`, latest readiness gap, and guardrail so NR5/SPNR/AGC
tuning can focus on the most relevant control surface without changing operator defaults. Strict
validation checks that the plan matches the trace-derived `promotionDecision`, the latest trace,
the reference trace, and both trend statuses, derives the expected action list from trace safety signals and readiness
trends, verifies action counts, priority ordering, action IDs, control families, rationales,
guardrails, readiness gaps, trend deltas, and reference fields, and surfaces
`liveDiagnosticsHistoryTuningActionPlanStatus`,
`liveDiagnosticsHistoryTuningActionPlanDirectionStatus`,
`liveDiagnosticsHistoryTuningActionPlanPrimarySafetyClass`,
`liveDiagnosticsHistoryTuningActionPlanPrimarySignalName`,
`liveDiagnosticsHistoryTuningActionPlanTopControlFamily`, and
`liveDiagnosticsHistoryTuningActionPlanActionCount`. Schema v6 adds
`latestLiveExperimentPlan`, which turns the ranked tuning actions into concrete live matrix capture
guidance. It lists scenario IDs, purpose, control family, safety class, sample/interval settings,
quick tuning comparisons (`current-zeus`, `nr5-spnr`), full G2 RX acceptance comparisons
(`off-baseline`, `thetis-parity`, `current-zeus`, `nr5-spnr`), acceptance gates, and
off-baseline/Thetis/baseline/candidate/compare command templates for
`tools/run-dsp-live-diagnostics-matrix.ps1` and
`tools/compare-dsp-live-diagnostics-matrix.ps1`, with the compare command explicitly binding
`current-zeus` as the baseline comparison and `nr5-spnr` as the candidate comparison. This plan sets
`defaultBehaviorChangeReady=false`; it exists to make the next G2 evidence run repeatable and to
prevent NR5 tuning evidence from being mistaken for final Thetis/parity acceptance. Strict
validation checks that the experiment plan matches
`latestTuningActionPlan`, includes the required comparisons, keeps valid scenario priorities and
sampling settings, carries acceptance gates, and matches the generated scenario IDs, purposes,
source action fields, operator setup text, acceptance gates, required evidence, and matrix command
templates. It also rejects schema downgrades from the current history schema so v5-v12 plan and
coverage checks cannot be bypassed. Validation surfaces
`liveDiagnosticsHistoryLiveExperimentPlanStatus`,
`liveDiagnosticsHistoryLiveExperimentPlanDirectionStatus`,
`liveDiagnosticsHistoryLiveExperimentPlanPrimaryControlFamily`,
`liveDiagnosticsHistoryLiveExperimentPlanScenarioCount`,
`liveDiagnosticsHistoryLiveExperimentPlanRecommendedSampleCount`, and
`liveDiagnosticsHistoryLiveExperimentPlanRecommendedIntervalMs`. Schema v7 adds
`traceSequenceUtc` and `sortKeySource` to each trace and compact trace. The sequence is derived from
the nearest timestamped trace or capture ancestor first, then `completedUtc`, then file mtime
fallback, so live-history summaries are not misordered by a stale or future-skewed report timestamp.
When matrix summaries live under `artifacts/live-diagnostics-traces/<scenario>/<comparison>`, the
trace ID keeps the timestamped capture ancestor and appends the scenario/comparison path suffix, so
traces from the same bundle remain unique while sharing the capture sequence. Strict validation
checks that traces are ordered by `traceSequenceUtc`, `latestTrace` matches the highest NR5 sequence,
compact trace provenance matches the full record, and `previousNr5Trace` matches the second-highest
NR5 sequence. It surfaces `liveDiagnosticsHistoryLatestTraceSequenceUtc`,
`liveDiagnosticsHistoryLatestTraceSortKeySource`, `liveDiagnosticsHistoryTraceOrderingStatus`, and
`liveDiagnosticsHistoryTraceOrderingViolationCount`. Schema v8 adds
`latestLiveExperimentCoverage`, which compares the current trace records against
`latestLiveExperimentPlan.scenarios` and their required comparisons. Matrix traces use explicit
watcher `scenarioId`/`comparisonId` metadata when present and path-derived metadata for older
summaries; coverage counts only when the trace is benchmark-ready, so the history report can show
whether the next off-baseline, Thetis-parity, current-Zeus, and NR5/SPNR live matrix evidence is
complete, partial, or not started. Strict validation recomputes the coverage from `traces` against the generated live
experiment plan, not a hand-edited plan body, and surfaces
`liveDiagnosticsHistoryLiveExperimentCoverageStatus`,
`liveDiagnosticsHistoryLiveExperimentCoverageScenarioCount`,
`liveDiagnosticsHistoryLiveExperimentCoverageCoveredScenarioCount`,
`liveDiagnosticsHistoryLiveExperimentCoverageRequiredComparisonCount`,
`liveDiagnosticsHistoryLiveExperimentCoverageCoveredComparisonCount`, and
`liveDiagnosticsHistoryLiveExperimentCoverageMissingComparisonCount`. Strict validation also emits
`liveDiagnosticsHistoryLiveExperimentCoverageMissingComparisonIds` and flags
`live-history-live-experiment-coverage-incomplete` when the history is truthful but still missing
required scenario/comparison pairs. This keeps a `not-started` or partial history from being treated
as acceptance evidence just because the coverage rollup was internally consistent. Schema v9 adds per-trace
`summarySha256` and `jsonlSha256` fields for the exact watcher summary and JSONL trace used to build
each history record. Strict validation recomputes both hashes while performing source-summary
reconstruction and fails missing or stale hashes before accepting live-history provenance. It
surfaces `liveDiagnosticsHistoryTraceSourceStatus`,
`liveDiagnosticsHistoryTraceSourceCheckedCount`, source missing/invalid counts, JSONL missing count,
and per-source summary/JSONL hash present, missing, and mismatch counts for CI and review summaries.
The validator also emits one `artifactReferencedFiles` record per live-history trace source with
`sourceType=live-diagnostics-history-trace-source`, source/hash statuses, and the copied versus
recomputed watcher-summary/JSONL hashes so reviewers can drill into the exact failed trace.
Schema v10 adds RX audio-leveler constrained sample counts, constrained percent, boost-slew,
peak-headroom, output-cap, output RMS movement, and applied gain movement to full and compact
history traces. It also adds leveler thresholds and latest-vs-previous deltas so the lowest-pumping
selection and strict validation cannot treat downstream RX leveler limiting as acceptable NR5/SPNR
progress.
Schema v11 carries watcher `constraintCounts`, `hardConstraintCounts`, and `statusCounts` into full
history traces and copies `hardConstraintCounts` plus the top hard-constraint name/count into compact
traces. History recommendations now name the top hard gate, such as stale frontend scene evidence,
so the next capture action is clear before anyone tunes NR5 or the RX leveler from blocked traces.
Schema v12 expands `latestLiveExperimentPlan` from quick current-vs-NR5 tuning evidence to full G2
RX acceptance evidence. The plan now separates `tuningComparisons` from `acceptanceComparisons`,
requires each live scenario to cover `off-baseline`, `thetis-parity`, `current-zeus`, and
`nr5-spnr`, and emits explicit off-baseline and Thetis matrix commands. External engine bakeoff
comparisons remain absent from the default plan unless explicitly scoped elsewhere in the bundle.
Schema v13 carries `agcStabilityWatch` into full and compact history traces, adds
`agc-active-gain-movement-db`, `agc-voice-like-gain-movement-db`, and `agc-pumping-risk` as
pumping-class safety signals, adds AGC movement thresholds and latest-vs-previous deltas, and uses
active/voice-like AGC movement when selecting the lowest-pumping trace. Strict validation rejects
schema v13 histories that are missing `agcStabilityStatus` or `agcPumpingRisk` on any trace and
surfaces `liveDiagnosticsHistoryAgcStabilityStatus`,
`liveDiagnosticsHistoryAgcStabilityTraceCount`,
`liveDiagnosticsHistoryAgcStabilityMissingTraceCount`,
`liveDiagnosticsHistoryAgcPumpingRiskTraceCount`,
`liveDiagnosticsHistoryAgcActivePumpingSignalCount`, and
`liveDiagnosticsHistoryAgcVoiceLikePumpingSignalCount`. This prevents stale live-history artifacts
from advancing opt-in DSP work without the AGC pumping evidence required by the watcher and
comparator. The live acceptance-cycle wrapper carries those same fields in summary schema v5 and
requires `liveDiagnosticsHistoryAgcStabilityReady=true` before it can report
`liveAcceptanceEvidenceReady=true`.
Schema v14 adds the mixed weak/strong volume-parity contract. The live watcher now classifies each
NR5 trace inside `nr5WeakSignalWatch.mixedWeakStrongEvidenceStatus` as `ready`,
`missing-strong-input`, `missing-weak-input`, `missing-weak-and-strong-input`,
`missing-output-gap`, `weak-strong-output-gap-watch`, or `no-nr5-samples`, and its recommendations
call out the capture action needed before using the trace as volume-parity evidence. Full and compact
history traces carry `strongInputSampleCount`, `mixedWeakStrongEvidenceReady`,
`weakStrongOutputParityReady`, `mixedWeakStrongEvidenceStatus`, and `weakStrongOutputGapDb`. A trace
only counts as mixed evidence when it contains at least one weak and one strong NR5 input sample, and
it is parity-ready only when the absolute weak/strong output gap is within 6 dB. The history rollup exposes `mixedWeakStrongEvidenceReady`,
`mixedWeakStrongEvidenceStatus`, mixed trace/ready/missing/gap-watch counts, and
`mixedWeakStrongBestTrace`. Strict validation mirrors those fields as
`liveDiagnosticsHistoryMixedWeakStrong*`, validation triage emits a required
`live-history-mixed-weak-strong` evidence gate, and the live acceptance wrapper refuses
`liveAcceptanceEvidenceReady=true` until G2 mixed weak+strong evidence is ready.
Schema v15 adds advisory NR5 speech-artifact control evidence. Full and compact history traces now
carry `nr5ArtifactRiskScore`, `nr5ArtifactRiskStatus`, low-evidence lifted sample count/percent,
low-evidence alignment mismatch percent, audio-alignment mismatch percent, texture-fill average, and
signal-probability average. Strict validation exposes `liveDiagnosticsHistoryArtifactControlSignalCount`
and rejects stale schema-v15 histories that omit those fields or misreport the rollup. The live
acceptance-cycle wrapper carries the same count in summary schema v6 so blocked one-command G2 runs
still expose speech-artifact review signals next to AGC stability and mixed weak/strong evidence. The
`artifact-control` safety class is deliberately advisory: it triggers review of metallic, burbling,
speech-like, or unsupported texture-fill artifacts, but it is not a promotion blocker by itself unless
the same trace also fails a blocking class such as weak-signal loss, pumping, hard gating, clipping, or
freshness. When the same artifact-control evidence appears as a candidate-vs-baseline live comparison
metric regression, the comparison report is not clean and should be recaptured or reviewed before it
is used as opt-in tuning evidence; that still does not authorize a default behavior change without
the full G2, Thetis-parity, and cross-radio acceptance evidence.
Use `tools/summarize-dsp-modernization-validation-report.ps1` after strict bundle validation to
turn those per-trace records, issue codes, and referenced-file statuses into a compact JSON/Markdown
triage report. It also emits an evidence-gate matrix for G2 hardware, WDSP native/runtime audits,
metric catalog, external-engine catalog/bakeoff, offline fixture comparison, live trace comparison,
live-history provenance, and referenced-file provenance; `-FailOnIssues` makes CI fail when
validation, required gates, live-history provenance, or referenced-file provenance still need
attention, while `-FailOnOptInDspBuildOutBlocked` fails only when the earlier opt-in DSP build-out
prerequisites are blocked. Its acceptance-readiness section separates clean validation, opt-in DSP build-out
prerequisites, G2 first-pass evidence, opt-in candidate comparison review, external DSP/ML bakeoff
scope, and default DSP graduation. The summary also exposes `optInDspBuildOutReady`,
`optInDspBuildOutStatus`, and `optInDspBuildOutBlockingGateIds` so implementation work can be
scoped before any acceptance or default-behavior decision. A ready G2/candidate stage still reports
`defaultBehaviorChangeReady=false` and
`crossRadioValidationEvidenceStatus=not-captured` until non-G2 validation evidence exists. Strict
bundle validation now recognizes an optional `cross-radio-validation-report` artifact with kind
`cross-radio-validation-report-json`; when attached it must declare `schemaVersion`, a recognized
`tool`, `readyForReview=true`, at least one non-G2 `hardwareTargets` entry, non-empty `scenarioIds`,
non-empty `comparisonIds`, `defaultBehaviorChangeApproved=false`, and `sourceReports` proving at
least one non-G2 strict validation report with clean metric, Zeus live-trace, and Thetis live-trace
comparison readiness. Manual target/scenario/comparison declarations in
`tools/summarize-dsp-cross-radio-validation.ps1` are preflight only and now report
`sourceBackedEvidenceReady=false` until a source validation report is attached. Valid evidence is
surfaced as `crossRadioValidationEvidenceStatus=cross-radio-evidence-ready`, but
`defaultBehaviorChangeReady` remains `false` until a separate explicit default-change approval is
made after G2 and cross-radio review. Use
`tools/summarize-dsp-cross-radio-validation.ps1 -BundleDir <bundle>` to generate the artifact at
`artifacts/cross-radio-validation-report.json`; pass one or more `-ValidationReportPath` values from
the accepted non-G2 validation pass for review-ready evidence. `tools/new-dsp-artifact-manifest.ps1
-IncludeOptionalArtifacts` now includes the optional `cross-radio-validation-report` manifest entry.
The
triage report also emits `acceptanceActionPlan`, a prioritized list of command templates or manual
evidence actions for the remaining gates. Use it as the operator checklist for the next capture,
audit, comparison, external-engine bakeoff, or cross-radio validation pass. CI dashboards can read
`primaryAcceptanceActionId`, `primaryAcceptanceCommandTemplate` or `primaryAcceptanceManualAction`,
`primaryAcceptanceCommandSteps`, and `acceptanceActionCategoryCounts` to show the next concrete
evidence step without parsing the whole action table. Prefer the structured `commandSteps` array for
multi-step actions such as baseline capture, candidate capture, and comparison. Live acceptance
wrapper schema v8 mirrors the same primary-action fields as `triagePrimaryAcceptance*`, mirrors the
advisory `run-first-safe-external-engine-bakeoff` action as `triageExternalEngineBakeoff*`, and
mirrors the advisory `capture-puresignal-safe-bypass-bench` action as
`triagePureSignalSafeBypass*` when validation triage generated them. Strict validation cross-checks
those mirrors against `validation-triage-report.json`, so a blocked wrapper summary can be handed
directly to the operator or UI without losing the next G2 command, the next opt-in external DSP/ML
bakeoff command, or the G2 PureSignal disabled/enabled TX bench action. The wrapper console output
and the `Live Acceptance Cycle Summary` Markdown section print the primary action, command or manual
action, follow-up, the external bakeoff advisory, and the PureSignal safe-bypass advisory when
present so the G2 operator does not have to inspect nested JSON before the next capture.
Incomplete live-history coverage is surfaced through the `live-history-provenance` gate with
`coverage-*` status, missing comparison IDs, and a regenerate/capture action whose command steps
capture the required off-baseline, Thetis-parity, current-Zeus, and NR5/SPNR matrix before
regenerating history. The history cannot guide tuning or acceptance review until that coverage is
complete.
When the matrix hunt has already found a best mixed weak/strong window but schema-v14 live history
has not promoted it, triage now emits `promote-matrix-mixed-weak-strong-window`. That action names
the best scenario/comparison, hunt score, output-gap status, source artifact, targeted recapture
command, history regeneration, strict validation, and triage rerun. It remains a G2 evidence action:
the matrix hint does not satisfy acceptance until live history reports
`liveDiagnosticsHistoryMixedWeakStrongEvidenceReady=true` and the current-Zeus/Thetis comparisons
still pass.
If live history is blocked before any matrix-ready weak+strong candidate exists, triage points at
`tools/run-dsp-g2-rx-peak-hunt.ps1` as the targeted RX-only scan path. The command writes
`artifacts/g2-rx-peak-hunt-report.json` plus per-window watcher summaries/JSONL traces, and its
`-AllowRetune` mode is explicitly separated from `/api/dsp/live-diagnostics`: the endpoint remains
read-only, while the script may temporarily move RX VFO through `/api/vfo` and restore it afterward.
Peak-hunt reports now also preserve compact `mixedWeakStrongTuningFocus` steering fields from each
watcher window: best-run action, output/final-audio gap direction and excess, weak lift/trim needs,
and top weak/strong row counts. These fields decide whether the next live pass should retune/extend
dwell or inspect bounded weak-speech lift rows; they do not approve NR5/SPNR defaults without the
required live-history, comparison, and cross-radio evidence gates.
For operator-driven tuning, `tools/watch-dsp-manual-tune-observer.ps1` schema v2 now emits a
`primaryManualTuneAction` object and prints concise per-poll status in non-JSON mode. The action
collapses the observer result into one read-only next step such as `manual-tune-to-frontend-suggestion`,
`wait-for-nr5-capture-readiness`, `recapture-manual-observer-near-strong-vfo`,
`recapture-manual-observer-weak-only-vfo`, or
`promote-manual-observer-mixed-weak-strong-capture`. The optional `-RequireNr5CaptureReady` gate
keeps child watcher captures from being spent while requested/effective NR5 state, NR5 diagnostics,
or fresh runtime audio evidence are not ready. Manual captures also preserve the delegated watcher's
capture-readiness, comparison-state, NR5 tuning status, mixed-focus action, and AGC pumping-risk
fields so manual-tune evidence has the same steering vocabulary as peak-hunt evidence.
Frontend tune candidates and manual observer suggestions are dial-step aware: the operator-facing
`suggested*` fields are snapped to the configured step (`-TuneStepHz` or `-SuggestedVfoStepHz`,
1 kHz by default for G2 phone evidence), while `rawSuggested*`/`exactSuggested*` fields preserve
the FFT-bin-derived target for later audit.
The optional artifact-manifest entry is `kind=g2-rx-peak-hunt-report-json`; using a trace/index kind
is incorrect because the peak-hunt report is a summary JSON, not a `files[]` trace index.
If the same matrix window also carries `liveMatrixArtifactControlStatus=artifact-review`, triage
emits the advisory `recapture-matrix-artifact-control-window` action. That action is not a required
acceptance gate by itself, but it appears before the mixed weak/strong promotion action so operators
recapture or choose a cleaner active window before optimizing NR5/SPNR from low-evidence lift,
audio-alignment mismatch, or unsupported texture-fill artifacts.
The validation triage report also displays live-trace capture-readiness comparison gates and top
constraints so operators can clear setup hard gates before treating metric regressions as DSP
behavior. Its acceptance action plan now emits capture-readiness-specific live-diagnostics actions:
hard-gated candidates get a `clear-live-capture-readiness-hard-gates` action naming the top hard
gate before any recapture command, while soft strict-preflight failures get a
`clear-live-capture-readiness-advisories` action so advisory cleanup is not mixed with algorithm
regression work. The generic `rerun-strict-validation` action stays in the plan but is ordered after
specific evidence-repair actions, so the first action points at the concrete blocker rather than
asking the operator to rerun the same failing validation.
External DSP/ML gates follow the same rule: validation and triage carry exact missing candidate IDs,
unsafe candidate IDs/details, top external-engine issue counts, blocked-for-integration candidate
IDs/details, and snapshot mismatch IDs. The external-engine evidence gate, readiness stage, Markdown
summary, and acceptance action reason name those candidates directly, so an opt-in RNNoise,
DeepFilterNet, SpeexDSP, or WebRTC APM bakeoff cannot hide behind count-only evidence.

Strict bundle validation also checks the hardware evidence chain. `benchmark-plan.json` must keep
`firstHardwareTarget` at `G2`, `benchmark-capture-manifest.json` must carry the same
`hardwareTarget`, and `hardware-diagnostics.json` must show a connected G2-class OrionMkII context
with a G2/G2_1K variant before the bundle is accepted as first-cycle hardware evidence. Use
`-AllowPreflight` only for dry-run captures; it downgrades readiness gaps to warnings but does not
turn them into acceptance evidence.

Example:

```json
{
  "schemaVersion": 1,
  "artifacts": [
    {
      "id": "offline-fixture-metrics",
      "kind": "metrics-json",
      "path": "artifacts/offline-fixture-metrics.json",
      "required": true,
      "scenarioIds": ["weak-cw-carrier", "ssb-like-speech"]
    },
    {
      "id": "wdsp-native-symbol-audit",
      "kind": "native-audit-json",
      "path": "artifacts/wdsp-native-symbol-audit.json",
      "required": true,
      "scenarioIds": ["weak-cw-carrier", "ssb-like-speech"]
    },
    {
      "id": "audio-render-before-after",
      "kind": "audio",
      "path": "artifacts/audio-render-before-after.json",
      "required": true,
      "scenarioIds": ["weak-cw-carrier"]
    },
    {
      "id": "operator-notes",
      "kind": "notes",
      "path": "artifacts/operator-notes.md",
      "required": true,
      "scenarioIds": ["weak-cw-carrier", "ssb-like-speech"]
    }
  ]
}
```

For `audio`, `spectrum`, and `trace` artifacts, the generated path is a JSON index. Its `files`
array must point to bundle-relative leaf evidence files and tag each file with `scenarioId` or
`scenarioIds` plus `comparison`, `comparisonId`, or `candidate`; strict validation checks that each
listed file exists, is non-empty, covers the scenario IDs required by the artifact manifest, and
covers the scenario's required benchmark comparisons from `/api/dsp/benchmark-plan`:

```json
{
  "schemaVersion": 1,
  "artifactId": "audio-render-before-after",
  "files": [
    {
      "path": "artifacts/audio/weak-cw-before.wav",
      "scenarioId": "weak-cw-carrier",
      "comparison": "off-baseline"
    },
    {
      "path": "artifacts/audio/weak-cw-thetis.wav",
      "scenarioId": "weak-cw-carrier",
      "comparison": "thetis-parity"
    },
    {
      "path": "artifacts/audio/weak-cw-current-zeus.wav",
      "scenarioId": "weak-cw-carrier",
      "comparison": "current-zeus"
    },
    {
      "path": "artifacts/audio/weak-cw-nr5.wav",
      "scenarioId": "weak-cw-carrier",
      "candidate": "nr5-spnr"
    }
  ]
}
```

The `offline-fixture-metrics` artifact is a metrics JSON file. For each required scenario and
comparison, strict validation checks that the metrics named by `/api/dsp/benchmark-plan` are present
and that at least one gate outcome is recorded. Metric names are normalized for punctuation/case, so
`wanted SNR`, `wantedSnr`, and `wanted-snr` all satisfy the same required metric:

```json
{
  "schemaVersion": 1,
  "scenarios": [
    {
      "scenarioId": "weak-cw-carrier",
      "comparisons": [
        {
          "comparison": "off-baseline",
          "metrics": {
            "coherentTonePower": -46.2,
            "wantedSnr": 8.5,
            "spectralPreservation": 1.0,
            "outputRms": 0.018,
            "latency": 0.0
          },
          "gates": [
            { "id": "weak-signal-preserved", "passed": true }
          ]
        }
      ]
    }
  ]
}
```

Use `tools/run-dsp-wdsp-fixture-matrix.ps1` to create the offline WDSP fixture artifact set for a
capture bundle in one repeatable command. It runs the WDSP-backed fixture producer, native stage
timing/allocation summarizer, packaged runtime artifact audit, and fixture comparator, then writes
`artifacts/wdsp-fixture-matrix-summary.json`.
The underlying fixture producer reads `benchmark-plan.json` and `benchmark-metric-catalog.json`,
selects scenarios whose `fixtureStatus` is `offline-fixture-ready`, feeds the deterministic RX IQ and
TX audio fixtures through `WdspDspEngine`, and writes `artifacts/offline-fixture-metrics.json`,
`artifacts/audio-render-before-after.json`, and `artifacts/spectrum-before-after.json` with
bundle-relative evidence file paths. The matrix wrapper also writes
`artifacts/native-stage-timing-report.json`, `artifacts/wdsp-runtime-artifact-audit.json`, and
`artifacts/dsp-fixture-metric-comparison.json` so strict validation sees the same artifact paths
every time. This producer proves offline RXA/TXA
behavior through the same native WDSP engine Zeus ships, but it still does not prove G2, on-air,
PureSignal, or cross-radio acceptance. `tools/run-dsp-offline-fixture-evidence.ps1` remains
available as a deterministic schema fallback only; it should not be used as default-graduation proof.
The TXA fixture path now promotes the existing per-block stage meter snapshot into first-class
comparison metrics: TX leveler gain reduction, CFC gain reduction, compressor peak, ALC gain
reduction, output peak, and output average. That makes two-tone and voice-like TXA evidence
reviewable through the same metric catalog and comparator used for RX fixtures, while leaving
PureSignal enabled/disabled bypass proof in the G2 bench evidence path. That bench path now has a
first-class artifact: run `tools/summarize-dsp-puresignal-bench.ps1` after disabled and enabled G2
TX captures to write `artifacts/puresignal-safe-bypass-report.json`. Strict validation requires that
report to be generated by the summarizer, declare `tx-puresignal-safe-bypass`, preserve the
PureSignal default/bypass state, keep default behavior approval false, include ready disabled and
enabled paths, report zero clipping, keep feedback stability above threshold, and keep TX monitor
coupling below threshold.
The fixture-ready scenarios also promote explicit `signal SINAD`, `processing elapsed ms`, and
`throughput ratio` metrics into the catalog. `signal SINAD` applies to tone-bearing RX fixtures,
while the runtime-cost metrics apply across RX and TX offline fixtures so candidate NR, AGC, and
TXA changes cannot hide a processing-cost regression behind preservation gains.
`artifacts/native-stage-timing-report.json` is generated by
`tools/summarize-dsp-native-stage-timing.ps1`. Strict validation requires it to be WDSP-backed,
hash-match `artifacts/offline-fixture-metrics.json`, preserve the WDSP runtime hash, contain stage
records and allocation-probe metadata for every fixture run, and report zero budget failures. The
report intentionally says `nativeCStageInstrumentationStatus=not-instrumented` and
`nativeAllocationProbeStatus=managed-thread-delta-only`; this is acceptable wrapper-level fixture
evidence, not proof of internal WDSP C allocator behavior.

After `artifact-manifest.json` exists, pass `-ValidateBundle` to the matrix wrapper to run
`tools/validate-dsp-modernization-bundle.ps1` and copy strict validation status into
`artifacts/wdsp-fixture-matrix-summary.json`; use `-AllowValidationPreflight` for early bundles that
are still missing live or hardware artifacts. Summary schema v2 separates `readyForOfflineReview`
from `acceptanceEvidenceReady`: offline review only requires the comparison, native stage timing
report, packaged runtime audit, and runtime hashes to line up, while acceptance evidence also
requires `-ValidateBundle`,
`-RequireArtifactFiles`, clean strict validation, matched source/runtime/artifact hashes, and no
referenced-artifact provenance problems. Dashboards should show `acceptanceEvidenceStatus` and
`acceptanceEvidenceBlockers` before treating a fixture bundle as acceptance-grade evidence. A
single-scenario smoke can still be `readyForOfflineReview=true`, but it remains
`acceptanceEvidenceReady=false` until the benchmark plan covers every required scenario family and
strict validation passes with artifact-file provenance enabled.

WDSP-backed fixture metrics also carry native runtime identity fields:
`wdspRuntimeRid`, `wdspRuntimePath`, `wdspRuntimeLength`, `wdspRuntimeSha256`, and
`wdspRuntimeStatus`. The fixture comparator copies these into
`dsp-fixture-metric-comparison.json` and requires `wdspRuntimeStatus=found` plus a non-empty
`wdspRuntimeSha256` before setting `readyForReview=true`. This prevents a benchmark win from being
detached from the exact `wdsp.dll`/`libwdsp` binary that produced it.

`tools/run-dsp-wdsp-fixture-matrix.ps1` runs `tools/compare-dsp-fixture-metrics.ps1` after filling
`offline-fixture-metrics.json`. The comparator reads the captured benchmark plan, metric catalog,
and metrics artifact, compares every candidate against `current-zeus` and `thetis-parity`, writes
`artifacts/dsp-fixture-metric-comparison.json` plus a Markdown summary, and can fail a quality gate
with `-FailOnRegression`. Metric directions and acceptance-contract metadata come from
`benchmark-metric-catalog.json` when present; the script only falls back to built-in conservative
directions for older bundles. Each metric comparison row carries `acceptanceThreshold`,
`acceptanceComparator`, `unit`, `safetyClass`, `acceptanceScope`, and `contractReady` so a clean
candidate comparison is traceable to the declared metric contract. By default it compares only
`offline-fixture-ready` benchmark-plan scenarios, and `-IncludeNonFixtureScenarios` expands the
scope for special audits. The strict bundle
validator consumes the JSON report as generated evidence and now carries the comparator's missing
fixture coverage and provenance into top-level validation and
triage fields: `offlineFixtureMetricsEvidenceEngine`, `offlineFixtureMetricsWdspBackedEvidence`,
`offlineFixtureMetricsPath`, `offlineFixtureMetricsSha256`, `metricComparisonEvidenceEngine`,
`metricComparisonWdspBackedEvidence`, `metricComparisonSyntheticFallbackEvidence`,
`metricComparisonSourceMetricsPath`, `metricComparisonSourceMetricsSha256`,
`metricComparisonSourceMetricsHashStatus`, `offlineFixtureMetricsWdspRuntimeSha256`,
`metricComparisonWdspRuntimeSha256`, `metricComparisonWdspRuntimeHashStatus`,
`offlineFixtureMetricsRuntimeArtifactHashStatus`, `nativeRuntimeArtifactAuditWinX64NativeSha256`,
`metricComparisonReportReadyForReview`,
`metricComparisonMetricCoverageReadyForReview`, `metricComparisonFixtureScenarioScope`,
`metricComparisonSkippedNonFixtureScenarioCount`, `metricComparisonMissingScenarioCount`,
`metricComparisonMissingScenarios`, `metricComparisonMissingCurrentBaselineCount`,
`metricComparisonMissingThetisBaselineCount`, `metricComparisonMissingCandidateCount`,
`metricComparisonMissingMetricValueCount`, `metricComparisonCatalogAcceptanceContractReady`,
`metricComparisonCatalogContractProblemMetricCount`, and
`metricComparisonCatalogContractProblemMetricIds`. `metricComparisonReady` requires
`metricComparisonWdspBackedEvidence=true` and
`metricComparisonCatalogAcceptanceContractReady=true`; deterministic fallback output can prove
schema handoff, but strict validation emits `offline-fixture-metrics-not-wdsp-backed` or
`metric-comparison-not-wdsp-backed` and blocks acceptance until the WDSP-backed producer is used.
Strict validation also cross-checks `metricComparisonSourceMetricsPath` against the
artifact-manifest `offline-fixture-metrics` path and emits
`metric-comparison-source-metrics-path-mismatch` if the comparison was generated from a different
metrics file. It also compares `metricComparisonSourceMetricsSha256` with the current
`offlineFixtureMetricsSha256`; `metric-comparison-source-metrics-hash-missing` or
`metric-comparison-source-metrics-hash-mismatch` means the fixture metrics changed after comparison
or the report predates the hash chain and must be regenerated. The same strict path compares
`wdspRuntimeSha256` between the source metrics and comparison report, then matches Win x64 fixture
evidence against `wdsp-runtime-artifact-audit`'s `winX64NativeSha256`; runtime hash mismatches emit
`metric-comparison-runtime-hash-mismatch` or `offline-fixture-runtime-audit-hash-mismatch` and block
acceptance until evidence is regenerated from the packaged runtime under review. Passing candidates
still need G2 and cross-radio evidence before any default change is considered.

The live readiness surface is `/api/dsp/live-diagnostics`. It is read-only and combines the current
Smart NR condition, WDSP native capability probes, frontend DSP scene freshness, RX-chain health,
and NR5/SPNR diagnostics into a tool-friendly readiness score, constraint list, recommended action,
and candidate tool list for G2 benchmark sessions. It is an evidence aggregator only; it does not
change DSP defaults or promote experimental behavior.

The live diagnostics response now includes `runtimeEvidence`, derived from the DSP pipeline's
existing diagnostic caches rather than new realtime work. It records RXA meter freshness, ADC
headroom, AGC gain, final RX/TX-monitor audio freshness, RMS/peak dBFS, squelch open/tail/gate
state, monitor backlog, audio sink count, and a runtime recommendation. Acceptance captures should
treat `final-audio-not-fresh`, `final-audio-clipping-risk`, and `adc-headroom-low` as blockers
before tuning AGC, NR5/SPNR, squelch, or post-demod external engines.

`tools/watch-dsp-live-diagnostics.ps1` also summarizes post-NR RX audio-leveler constraints under
`rxAudioLevelerWatch`. That block preserves constrained-sample counts, percentages, thresholds, and
the top boost-slew, peak-headroom, and output-cap samples so `rx-leveler-settling-watch`,
`rx-leveler-headroom-watch`, and `rx-leveler-cap-watch` traces can be triaged from the summary
without manually mining the JSONL. `tools/compare-dsp-live-diagnostics-traces.ps1` consumes that
block when present, falls back to legacy top-level leveler counts when comparing older summaries, and
fails candidate review when constrained-sample count or percentage regresses. The live-history
summarizer carries the same fields into trace records and uses them in lowest-pumping selection, so
candidate tuning evidence cannot hide NR5/SPNR pumping behind the post-demod RX leveler.
The watcher also emits `agcStabilityWatch`, which splits AGC gain movement across total,
active-audio, voice-like, quiet/no-evidence, and RX-leveler-constrained buckets. A trace with active
or voice-like AGC movement above the pumping threshold gets `trendStatus=agc-pumping-watch`; wide
movement that is only quiet-floor or leveler-constrained remains distinguishable before anyone
retunes WDSP AGC or NR5 recovery/makeup from live captures.
`compare-dsp-live-diagnostics-traces.ps1` treats active AGC movement, voice-like AGC movement, and
the explicit pumping-risk flag as pumping-class metrics, so matrix and bundle validation can reject a
candidate that improves weak-signal recovery by reintroducing audible AGC movement.
The live-history summarizer carries the same `agcStabilityWatch` fields into full and compact trace
records, adds active/voice-like AGC movement as pumping-class safety signals, and uses those fields
when selecting the lowest-pumping reference and blocking promotion decisions. This keeps opt-in DSP
build-out readiness from advancing on traces where weak-signal recovery was bought with AGC pumping.
Strict bundle validation now treats those AGC history fields as part of the schema-v14 contract and flags
`live-history-agc-stability-missing` when a history artifact was generated from older watcher
summaries or hand-edited without AGC pumping evidence. The same schema also requires mixed
weak/strong fields and reports `live-history-mixed-weak-strong-*` blockers when a G2 history lacks
strong-input rows or exceeds the weak/strong output parity limit.

The optional external-engine catalog is `/api/dsp/external-engine-candidates`. It classifies every
non-WDSP candidate as a post-demod bakeoff target with explicit blockers:

| Candidate | Role | Initial risk | Required gate |
|---|---|---|---|
| RNNoise | Small C neural speech denoiser | Speech-trained model can damage CW/data/non-speech HF content | Package/model review, speech fixture win, weak-carrier bypass proof |
| DeepFilterNet | Modern full-band neural speech enhancement | Heavy Rust/Python/model packaging and latency risk | Model license/package review, artifact score win, G2 CPU/latency proof |
| SpeexDSP | Classic DSP baseline/utilities | AGC/noise processing can fight WDSP AGC | Feature-level gating, no pumping, meter/squelch stability |
| WebRTC Audio Processing | Communications audio NS/VAD reference | AEC/AGC/high-pass defaults can corrupt radio gain staging | NS/VAD-only spike, AGC/AEC disabled, no TX/PureSignal coupling |

Strict bundle validation reads the captured external-engine catalog and rejects acceptance evidence
if a candidate is not `off` by default, is not opt-in bakeoff-only, is not post-demod, lacks
license/package/runtime/latency/radio-safety fields, has no blockers, omits benchmark/evidence
requirements, or drifts out of sync with the modernization snapshot. The source catalog now also
emits a machine-readable safety contract for each candidate: `evaluationStage`,
`allowedSignalPaths`, `forbiddenSignalPaths`, `requiredControls`, and `fallbackPolicy`. Validation
requires post-demod allowed paths, explicit raw-WDSP-IQ/PureSignal forbidden paths,
`operator-visible-opt-in`, `clean-bypass-fallback`, `no-raw-wdsp-iq-replacement`,
`no-tx-or-puresignal-coupling`, and a clean fallback/fall-back/bypass policy. This keeps RNNoise,
DeepFilterNet, SpeexDSP, WebRTC APM, or any future engine from silently bypassing the WDSP parity,
G2, on-air, and cross-radio gates.

Use `tools/summarize-dsp-external-engine-candidates.ps1` before any external DSP/ML bakeoff. The
tool reads `external-engine-candidates.json`, optionally cross-checks
`modernization-snapshot.json`, and writes `artifacts/external-engine-bakeoff-report.json` plus
Markdown. Schema v3 reports include SHA-256 hashes for the candidate catalog and any referenced
modernization snapshot; strict validation verifies those input hashes before accepting the report.
It also derives expected candidate summaries from the captured raw catalog, recomputes candidate
summary counts, missing required IDs, readiness, and bakeoff-plan candidate/scenario rollups from
the report body before trusting the header fields, and checks report candidate IDs against the
captured raw catalog. The report is
review evidence only: it counts safe-for-bakeoff candidates, explicit blockers,
missing candidates, unsafe catalog entries, snapshot mismatches, and any candidate that would still
need explicit approval before integration. Its JSON and Markdown now include the missing required
candidate IDs, safety-control counts, forbidden-path counts, fallback policy summaries, and compact
per-candidate blocker summaries; strict validation preserves those IDs, unsafe issue lists, and
blocker details in the top-level validation report for triage. Add
`external-engine-bakeoff-report` from `tools/new-dsp-artifact-manifest.ps1` when a bundle is used to
review RNNoise, DeepFilterNet, SpeexDSP, WebRTC APM, or another external engine; the scaffold marks
it required automatically when `benchmark-plan.requiredComparisons` or
`benchmark-capture-manifest.requiredComparisons` includes `candidate-external-engine-opt-in`, and
`-IncludeOptionalArtifacts` still adds it as optional for exploratory external-engine review. The
default source benchmark plan does not include this external comparison, so NR5/SPNR acceptance
captures remain independent of external ML/DSP bakeoff work. If
`candidate-external-engine-opt-in` appears in a benchmark plan, the benchmark capture manifest,
artifact comparison scope, or captured comparison-report content, strict validation requires the
bakeoff report before accepting the bundle for
external-engine review and records the exact source in `externalEngineBakeoffScopeTriggers`. Schema v2 bakeoff reports
also include `externalBakeoffPlan`, an opt-in post-demod bakeoff plan with candidate-specific
scenarios, required `current-zeus`,
`nr5-spnr`, and `candidate-external-engine-opt-in` comparisons, acceptance gates, required controls,
package/runtime gates, and fixture/live command templates. Strict validation checks that this plan
does not allow raw WDSP IQ replacement, TX/PureSignal coupling, or default behavior changes,
derives top-level scenario coverage from the summarized candidate IDs, and verifies each
candidate's generated scenario IDs, purposes, acceptance gates, required controls,
package/runtime gates, radio-safety gates, integration point, and default state. The validation
surfaces `externalEngineBakeoffPlanCandidateCount`,
`externalEngineBakeoffPlanScenarioCount`,
`externalEngineBakeoffPlanDefaultBehaviorChangeReady`, and
`externalEngineBakeoffPlanRawWdspIqReplacementAllowed`. Schema v4 bakeoff reports add a
deterministic opt-in evaluation order at both the report and `externalBakeoffPlan` levels:
`firstSafeBakeoffCandidateId`, `externalBakeoffEvaluationOrderCandidateIds`,
`externalBakeoffEvaluationOrder`, `externalBakeoffPlan.firstSafeBakeoffCandidateId`,
`externalBakeoffPlan.evaluationOrderCandidateIds`, and `externalBakeoffPlan.evaluationOrder`.
The order is recomputed by strict validation from `candidateSummaries`: safe-for-bakeoff candidates
come first, then lower combined runtime/latency/radio-safety risk, fewer blockers, fewer issues, and
candidate ID. This ranking only selects the safest first external bakeoff target; it does not
authorize integration, raw IQ processing, TX/PureSignal coupling, or default behavior changes.
Validation triage carries that ranking into an advisory `run-first-safe-external-engine-bakeoff`
action whenever a ready bakeoff report is present. The action names the first safe candidate, the
first-safe scenario IDs, an audited `run-dsp-external-engine-bakeoff.ps1 -PlanOnly` command,
cycle-summary artifacts, and a manual safety note requiring a post-demod, operator-opt-in candidate
path before any `-Execute` run is attempted. Live acceptance wrapper schema v8 copies that action into
`triageExternalEngineBakeoff*` fields even when a required G2 live-history action remains primary,
and strict validation rejects wrapper summaries whose copied advisory differs from the triage action
plan.
Use `tools/run-dsp-external-engine-bakeoff.ps1 -BundleDir <bundle> -PlanOnly` to turn the validated
first-safe action into a standalone execution plan. The runner writes
`artifacts/external-engine-bakeoff-cycle-summary.json` and Markdown with fixture comparison, live
matrix, strict validation, and triage-rerun commands. It is deliberately plan-first: live or fixture
execution requires explicit `-Execute`, missing candidate fixture metrics block execution unless the
operator chooses `-ContinueOnError`, and the summary repeats the safety policy that raw WDSP IQ, TX,
TX monitor, PureSignal, and default behavior changes are not authorized.
When external opt-in comparison scope is present, `tools/new-dsp-artifact-manifest.ps1` also emits
the optional `external-engine-bakeoff-cycle-summary` artifact. Strict validation checks the runner
schema, candidate/scenario alignment with the first-safe report, portable paths, command plan,
source triage action, and safety-policy booleans, then surfaces the result as
`externalEngineBakeoffCycle*` fields that validation summaries and Markdown copy forward. Validation
triage also exposes a non-required `external-engine-bakeoff-cycle` evidence gate so dashboards can
distinguish a ready first-safe bakeoff report from a captured, valid, ready-to-execute cycle summary
without turning exploratory external-engine work into a G2/default acceptance blocker. The triage
JSON carries `evidenceGateProblem*`, `requiredEvidenceGateProblem*`, and
`advisoryEvidenceGateProblem*` fields; dashboards should treat required gate problems as acceptance
blockers and advisory gate problems as next-level DSP/new-tech follow-up, with the external bakeoff
cycle gate expected to appear under advisory until its cycle summary is captured and valid.

The post-demod-only policy is intentional. RNNoise is a recurrent neural network noise-suppression
library for 48 kHz mono speech-style PCM examples and ships model files separately from the source
tree. DeepFilterNet is a 48 kHz full-band speech enhancement framework with Rust, Python, LADSPA,
and pretrained model paths. SpeexDSP's preprocessor includes denoise, residual echo suppression,
AGC, and VAD controls, so feature-level gating is required to avoid fighting WDSP AGC. WebRTC APM is
a VoIP speech-enhancement module whose examples include AEC, noise suppression, and AGC; Zeus must
keep AEC/AGC/high-pass behavior disabled unless a separate radio-safety review approves it.

## Modernization Policy

1. Stabilize parity first. Do not tune around an unknown Zeus/Thetis delta.
2. Keep all new DSP behavior opt-in until the measured result beats current Zeus behavior on the G2
   and survives on-air review.
3. Evaluate RNNoise, DeepFilterNet, SpeexDSP, and WebRTC Audio Processing only as optional post-demod
   speech/audio candidates unless a benchmark proves a lower-level integration is safe.
4. Preserve WDSP as the core DSP engine. External technology may augment receive/transmit audio, but
   it must have clean licensing, packageability, CPU/latency bounds, and fallback behavior.
5. No operator default changes without a separate approval backed by metrics and hardware evidence.

## Acceptance Matrix

| Gate | Required for Phase 1 | Required before completion |
|---|---:|---:|
| Lineage/parity document | yes | yes |
| Deterministic offline fixtures | yes | yes |
| WDSP-backed offline fixtures | yes | yes |
| Native WDSP build | targeted | all supported runtime targets |
| `dotnet build` / `dotnet test` | targeted | full solution |
| Frontend tests | no UI change in Phase 1 | required for UI/profile changes |
| G2 bench validation | design required | required |
| G2 hardware diagnostics bundle evidence | required | required |
| Cross-radio validation | optional artifact supported | required |
| Operator defaults unchanged | required | required unless separately approved |

## Immediate Follow-ups

- Run `compare-wdsp-source-drift.ps1` against the local Thetis WDSP source and resolve or explicitly classify any `likely-defect` source drift.
- Add internal WDSP C stage timing and allocator probes once wrapper-level `native-stage-timing-report` evidence is stable.
- Promote focused NR5/SPNR fixtures from diagnostic tests into the shared benchmark catalog.
- Run the G2 PureSignal disabled/enabled TX bench capture and summarize it with `tools/summarize-dsp-puresignal-bench.ps1` so `puresignal-safe-bypass-report` becomes ready in strict validation.
- Record G2 bench results alongside fixture metrics before any opt-in profile graduates.
