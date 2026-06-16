# DSP Live Diagnostics Trace Comparison

- Ready for review: False
- Candidate: live47-7245-final-gate
- Baseline: live36-7245-before
- Regressions: 3
- Gate failures: 0
- Missing values: 0

## NR5 Weak-Signal Summary

| Metric | Baseline | Candidate | Delta |
|---|---:|---:|---:|
| Weak input samples | 59 | 59 | 0 |
| Weak recovered samples | 59 | 59 | 0 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 100 | 100 | 0 |
| Output movement dB | 2.7 | 2.7 | 0 |
| Makeup movement dB | 7.9 | 6.5 | -1.4 |
| Makeup max dB | 9.4 | 8.6 | -0.8 |
| Recovery-drive movement | 0.309 | 0.621 | 0.312 |
| Texture-fill average | 0.045 | 0.043 | -0.002 |

## Regression Safety Classes

| Safety class | Regressions |
|---|---:|
| front-end | 1 |
| pumping | 2 |

## Metric Regressions

| Metric | Baseline | Candidate | Direction | Delta |
|---|---:|---:|---|---:|
| NR5 recovery-drive movement | 0.309 | 0.621 | lower | -0.312 |
| Audio RMS movement dB | 1.6 | 3.5 | lower | -1.9 |
| Minimum ADC headroom dB | 50.2 | 43.2 | higher | -7 |

## Metric Summary

| Metric | Baseline | Candidate | Direction | Verdict |
|---|---:|---:|---|---|
| Failed samples | 0 | 0 | lower | tie |
| Hard blocker samples | 16 | 0 | lower | improvement |
| Ready sample percent | 45 | 100 | higher | improvement |
| Average readiness score | 78.417 | 92 | higher | improvement |
| AGC gain movement dB | 0 | 0 | lower | tie |
| NR5 weak-input dropout samples | 0 | 0 | lower | tie |
| NR5 weak-input recovery percent | 100 | 100 | higher | tie |
| NR5 hot makeup samples | 0 | 0 | lower | tie |
| NR5 output movement dB | 2.7 | 2.7 | lower | tie |
| NR5 makeup movement dB | 7.9 | 6.5 | lower | improvement |
| NR5 maximum makeup dB | 9.4 | 8.6 | lower | tie |
| NR5 recovery-drive movement | 0.309 | 0.621 | lower | regression |
| NR5 texture-fill average | 0.045 | 0.043 | informational | informational |
| Audio RMS movement dB | 1.6 | 3.5 | lower | regression |
| Maximum audio peak dBFS | -0.7 | -3.7 | lower | improvement |
| Minimum ADC headroom dB | 50.2 | 43.2 | higher | regression |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 7.55 | 6.717 | lower | tie |
| Trace status severity | 70 | 0 | lower | improvement |
