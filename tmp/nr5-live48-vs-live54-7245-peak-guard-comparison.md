# DSP Live Diagnostics Trace Comparison

- Ready for review: False
- Candidate: live54-7245-peak-guard
- Baseline: live48-7245-before-peak-guard
- Regressions: 3
- Gate failures: 0
- Missing values: 0

## NR5 Weak-Signal Summary

| Metric | Baseline | Candidate | Delta |
|---|---:|---:|---:|
| Weak input samples | 60 | 56 | -4 |
| Weak recovered samples | 60 | 56 | -4 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 100 | 100 | 0 |
| Output movement dB | 3.1 | 3.2 | 0.1 |
| Makeup movement dB | 3.4 | 6.6 | 3.2 |
| Makeup max dB | 4.9 | 7.8 | 2.9 |
| Recovery-drive movement | 0.766 | 0.811 | 0.045 |
| Texture-fill average | 0.046 | 0.045 | -0.001 |

## Regression Safety Classes

| Safety class | Regressions |
|---|---:|
| front-end | 1 |
| pumping | 2 |

## Metric Regressions

| Metric | Baseline | Candidate | Direction | Delta |
|---|---:|---:|---|---:|
| NR5 makeup movement dB | 3.4 | 6.6 | lower | -3.2 |
| NR5 maximum makeup dB | 4.9 | 7.8 | lower | -2.9 |
| Minimum ADC headroom dB | 48.7 | 40.9 | higher | -7.8 |

## Metric Summary

| Metric | Baseline | Candidate | Direction | Verdict |
|---|---:|---:|---|---|
| Failed samples | 0 | 0 | lower | tie |
| Hard blocker samples | 0 | 0 | lower | tie |
| Ready sample percent | 100 | 100 | higher | tie |
| Average readiness score | 92 | 92 | higher | tie |
| AGC gain movement dB | 0 | 0 | lower | tie |
| NR5 weak-input dropout samples | 0 | 0 | lower | tie |
| NR5 weak-input recovery percent | 100 | 100 | higher | tie |
| NR5 hot makeup samples | 0 | 0 | lower | tie |
| NR5 output movement dB | 3.1 | 3.2 | lower | tie |
| NR5 makeup movement dB | 3.4 | 6.6 | lower | regression |
| NR5 maximum makeup dB | 4.9 | 7.8 | lower | regression |
| NR5 recovery-drive movement | 0.766 | 0.811 | lower | tie |
| NR5 texture-fill average | 0.046 | 0.045 | informational | informational |
| Audio RMS movement dB | 2.7 | 2 | lower | tie |
| Maximum audio peak dBFS | -2.5 | -2.3 | lower | tie |
| Minimum ADC headroom dB | 48.7 | 40.9 | higher | regression |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 8.05 | 7.517 | lower | tie |
| Trace status severity | 0 | 0 | lower | tie |
