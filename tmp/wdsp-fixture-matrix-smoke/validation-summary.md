# DSP Modernization Validation Triage

- Validation OK: False
- Triage status: validation-needs-attention
- Errors: 5
- Warnings: 1
- Referenced-file problems: 0
- Live-history trace source status: not-evaluated
- Live-history trace source problems: 0

## Recommendations

- Resolve validation errors before treating this bundle as DSP modernization evidence.
- Resolve required evidence gates before acceptance review: Strict bundle validation, G2 hardware evidence, WDSP native symbol audit, Live trace comparison.

## Evidence Gates

| Gate | Ready | Status | Required | Detail | Action |
|---|---:|---|---:|---|---|
| Strict bundle validation | False | failed | True | errors=5; warnings=1 | Resolve validation errors before using this bundle as DSP modernization evidence. |
| G2 hardware evidence | False | diagnostics-missing | True | target=G2; captureTarget=G2; diagnosticsPresent=False | Capture connected G2 hardware diagnostics and keep benchmark-plan/capture-manifest targets aligned. |
| WDSP native symbol audit | False | not-ready | True | present=False; imports=0; sourceMissing=0; signatureMismatches=0; binaryMissing=0 | Run audit-wdsp-native-symbols.ps1 with -RequireBinaryExports and resolve missing exports/signature drift. |
| WDSP runtime artifact audit | True | ready | True | present=True; artifacts=5; pendingRids=4; winX64Sha=d684676acdb803fa9912c64fe707317eadbf03a0ccde2f068119378507465ede | Run audit-wdsp-runtime-artifacts.ps1 and package the required win-x64 WDSP runtime artifact. |
| Benchmark metric catalog | True | ready | True | metrics=2; missingRequired=0 | Capture or update benchmark-metric-catalog.json so required metrics and directions are explicit. |
| External DSP/ML candidate catalog | False | missing | False | candidates=0; missing=0; unsafe=0; snapshotMismatch=0 | Keep RNNoise/DeepFilterNet/SpeexDSP/WebRTC entries opt-in, licensed, packaged, and safety-gated before any bakeoff. |
| External DSP/ML bakeoff report | True | not-required | False | requiredByScope=False; present=False; safe=0; blocked=0 | Generate external-engine-bakeoff-report only for scoped opt-in comparisons, then resolve unsafe/missing candidate evidence. |
| Offline fixture metric comparison | True | ready | True | present=True; sourceEngine=wdsp; comparisonEngine=wdsp; wdspBacked=True; sourceHash=match; runtimeHash=match; runtimeArtifactHash=match; scope=offline-fixture-ready; skippedNonFixture=0; regressions=0; gateFailures=0; missingScenarios=0; missingCurrent=0; missingThetis=0; missingCandidates=0; missingValues=0 | Run run-dsp-wdsp-fixture-matrix.ps1 and resolve fixture, runtime, or metric regressions before acceptance review. |
| Live trace comparison | False | not-ready | True | present=False; regressions=0; gateFailures=0; missingMetrics=0 | Compare baseline/current-Zeus and candidate live traces or matrices, then resolve regressions and gate failures. |
| Live diagnostics history provenance | False | not-evaluated | False | present=False; ready=False; traceSources=0; sourceProblems=0 | Regenerate live diagnostics history after watcher summaries and JSONL traces are finalized; fix source/hash mismatches before using history for tuning decisions. |
| Referenced artifact file provenance | True | ready | False | problemCount=0 | Inspect failedReferencedFiles for stale hashes, missing traces, or summary path mismatches. |

## Acceptance Readiness

- G2 first-pass ready: False
- Opt-in candidate comparison ready: False
- Default behavior change ready: False
- Cross-radio validation evidence: not-captured

| Stage | Ready | Status | Blocks Default Change | Detail | Next Action | Blocking Gates |
|---|---:|---|---:|---|---|---|
| Validation triage clean | False | validation-needs-attention | True | requiredGateProblems=4; referencedFileProblems=0; liveHistoryProvenanceNeedsAttention=False | Resolve the triage status and rerun validation before treating the bundle as acceptance evidence. | validation-report, g2-hardware, wdsp-native-symbol-audit, live-trace-comparison |
| G2 first-pass evidence | False | blocked-prerequisites | True | hardware=diagnostics-missing; fixtureReady=True; liveTraceReady=False | Complete G2 hardware, WDSP audit, fixture, and live-trace gates before first-pass review. | validation-report, g2-hardware, wdsp-native-symbol-audit, live-trace-comparison |
| Opt-in candidate comparison | False | blocked-benchmark-evidence | True | metricRegressions=0; liveRegressions=0; liveGateFailures=0; historyCandidatePromotionReady=False | Generate fixture and live trace comparisons that beat current Zeus/Thetis evidence before opt-in review. | validation-report, g2-hardware, wdsp-native-symbol-audit, live-trace-comparison |
| External DSP/ML bakeoff | True | not-in-scope | False | requiredByScope=False; present=False; ready=False; defaultBehaviorChangeReady=False; rawIqReplacementAllowed=False | No external-engine action is required unless an opt-in comparison is in scope. |  |
| Default DSP behavior graduation | False | blocked-g2-prerequisites | True | defaultBehaviorChangeReady=False; crossRadioValidationRequired=True; crossRadioValidationEvidenceStatus=not-captured; blockingGateCount=6 | Finish G2 first-pass evidence before planning cross-radio/default-graduation review. | validation-report, g2-hardware, wdsp-native-symbol-audit, external-engine-candidates, live-trace-comparison, live-history-provenance |

## Acceptance Action Plan

- Action count: 6
- Required action count: 4
- Manual action count: 0
- Primary action: rerun-strict-validation
- Primary command/manual action: powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-dsp-modernization-bundle.ps1 -BundleDir "$bundleDir" -RequireArtifactFiles

| Priority | Action | Stage | Gate | Required | Category | Steps | Command / Manual Action | Follow-up |
|---:|---|---|---|---:|---|---:|---|---|
| 10 | rerun-strict-validation | validation-triage-clean | validation-report | True | validation | 1 | powershell -NoProfile -ExecutionPolicy Bypass -File tools\validate-dsp-modernization-bundle.ps1 -BundleDir "$bundleDir" -RequireArtifactFiles | Rerun summarize-dsp-modernization-validation-report.ps1 with -FailOnIssues after validation passes. |
| 20 | capture-g2-hardware-evidence | g2-first-pass-evidence | g2-hardware | True | hardware-evidence | 1 | powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-dsp-modernization-bundle.ps1 -BaseUrl http://localhost:6060 -OutputRoot captures\dsp-modernization -Label g2-dsp-evidence | Confirm benchmark-plan firstHardwareTarget and capture manifest hardwareTarget both remain G2. |
| 30 | run-wdsp-native-symbol-audit | g2-first-pass-evidence | wdsp-native-symbol-audit | True | wdsp-audit | 1 | powershell -NoProfile -ExecutionPolicy Bypass -File tools\audit-wdsp-native-symbols.ps1 -ReportPath "$bundleDir\artifacts\wdsp-native-symbol-audit.json" -RequireBinaryExports | Resolve missing exports or signature drift before reviewing DSP parity. |
| 60 | capture-and-compare-live-matrix | opt-in-candidate-comparison | live-trace-comparison | True | live-diagnostics | 3 | powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -ComparisonId current-zeus -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.baseline.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.baseline.json" -Samples 60 -IntervalMs 1000; powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -ComparisonId nr5-spnr -IndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.candidate.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-matrix-report.candidate.json" -Samples 60 -IntervalMs 1000; powershell -NoProfile -ExecutionPolicy Bypass -File tools\compare-dsp-live-diagnostics-matrix.ps1 -BundleDir "$bundleDir" -BaselineIndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.baseline.json" -CandidateIndexPath "$bundleDir\artifacts\live-diagnostics-trace-index.candidate.json" -ReportPath "$bundleDir\artifacts\live-diagnostics-trace-comparison.json" -FailOnRegression | Mark live-diagnostics-trace-comparison required=true in artifact-manifest.json for acceptance review. |
| 70 | refresh-external-engine-catalog | external-dsp-ml-bakeoff | external-engine-candidates | False | external-dsp-ml | 1 | powershell -NoProfile -ExecutionPolicy Bypass -File tools\capture-dsp-modernization-bundle.ps1 -BaseUrl http://localhost:6060 -OutputRoot captures\dsp-modernization -Label external-candidate-refresh | Keep every external engine post-demod, opt-in, packaged, licensed, and off by default. |
| 80 | regenerate-live-diagnostics-history | opt-in-candidate-comparison | live-history-provenance | False | live-diagnostics | 1 | powershell -NoProfile -ExecutionPolicy Bypass -File tools\summarize-dsp-live-diagnostics-history.ps1 -BundleDir "$bundleDir" -ReportPath "$bundleDir\artifacts\live-diagnostics-history.json" | Use history only to choose candidate comparison windows; it does not approve defaults. |

## Validation Issue Codes

| Severity | Code | Count |
|---|---|---:|
| error | artifact-file-missing | 1 |
| error | benchmark-hardware-graduation-gates-incomplete | 1 |
| error | hardware-diagnostics-missing | 1 |
| error | live-diagnostics-missing | 1 |
| error | snapshot-missing | 1 |
| warning | external-engine-candidates-missing | 1 |

This triage report is review tooling only. It does not approve DSP defaults, raw WDSP IQ replacement, or on-air acceptance by itself.
