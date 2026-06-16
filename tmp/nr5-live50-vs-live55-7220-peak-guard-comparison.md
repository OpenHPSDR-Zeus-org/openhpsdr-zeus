# DSP Live Diagnostics Trace Comparison

- Ready for review: False
- Candidate: live55-7220-peak-guard
- Baseline: live50-7220-before-peak-guard
- Regressions: 4
- Gate failures: 0
- Missing values: 0

## NR5 Weak-Signal Summary

| Metric | Baseline | Candidate | Delta |
|---|---:|---:|---:|
| Weak input samples | 22 | 20 | -2 |
| Weak recovered samples | 21 | 19 | -2 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 95.455 | 95 | -0.455 |
| Output movement dB | 10.6 | 10.9 | 0.3 |
| Makeup movement dB | 3.8 | 4 | 0.2 |
| Makeup max dB | 4.6 | 4.6 | 0 |
| Recovery-drive movement | 0.833 | 0.89 | 0.057 |
| Texture-fill average | 0.016 | 0.015 | -0.001 |

## Regression Safety Classes

| Safety class | Regressions |
|---|---:|
| clipping | 1 |
| front-end | 1 |
| hard-gate | 1 |
| pumping | 1 |

## Metric Regressions

| Metric | Baseline | Candidate | Direction | Delta |
|---|---:|---:|---|---:|
| AGC gain movement dB | 11.9 | 15.3 | lower | -3.4 |
| Maximum audio peak dBFS | -4.7 | -2.2 | lower | -2.5 |
| Minimum ADC headroom dB | 42 | 35.4 | higher | -6.6 |
| Trace status severity | 0 | 35 | lower | -35 |

## Metric Summary

| Metric | Baseline | Candidate | Direction | Verdict |
|---|---:|---:|---|---|
| Failed samples | 0 | 0 | lower | tie |
| Hard blocker samples | 0 | 0 | lower | tie |
| Ready sample percent | 100 | 100 | higher | tie |
| Average readiness score | 92 | 92 | higher | tie |
| AGC gain movement dB | 11.9 | 15.3 | lower | regression |
| NR5 weak-input dropout samples | 0 | 0 | lower | tie |
| NR5 weak-input recovery percent | 95.455 | 95 | higher | tie |
| NR5 hot makeup samples | 0 | 0 | lower | tie |
| NR5 output movement dB | 10.6 | 10.9 | lower | tie |
| NR5 makeup movement dB | 3.8 | 4 | lower | tie |
| NR5 maximum makeup dB | 4.6 | 4.6 | lower | tie |
| NR5 recovery-drive movement | 0.833 | 0.89 | lower | tie |
| NR5 texture-fill average | 0.016 | 0.015 | informational | informational |
| Audio RMS movement dB | 5.4 | 6.1 | lower | tie |
| Maximum audio peak dBFS | -4.7 | -2.2 | lower | regression |
| Minimum ADC headroom dB | 42 | 35.4 | higher | regression |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 7.817 | 6.783 | lower | tie |
| Trace status severity | 0 | 35 | lower | regression |
