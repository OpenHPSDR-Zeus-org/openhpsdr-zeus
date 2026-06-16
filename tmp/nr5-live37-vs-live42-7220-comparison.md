# DSP Live Diagnostics Trace Comparison

- Ready for review: False
- Candidate: live42-7220-signal-valley
- Baseline: live37-7220-before
- Regressions: 5
- Gate failures: 0
- Missing values: 0

## NR5 Weak-Signal Summary

| Metric | Baseline | Candidate | Delta |
|---|---:|---:|---:|
| Weak input samples | 23 | 33 | 10 |
| Weak recovered samples | 8 | 33 | 25 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 34.783 | 100 | 65.217 |
| Output movement dB | 14.1 | 8 | -6.1 |
| Makeup movement dB | 2.6 | 6.3 | 3.7 |
| Makeup max dB | 3.8 | 6.9 | 3.1 |
| Recovery-drive movement | 0.745 | 0.843 | 0.098 |
| Texture-fill average | 0.012 | 0.024 | 0.012 |

## Regression Safety Classes

| Safety class | Regressions |
|---|---:|
| clipping | 1 |
| front-end | 1 |
| pumping | 3 |

## Metric Regressions

| Metric | Baseline | Candidate | Direction | Delta |
|---|---:|---:|---|---:|
| NR5 makeup movement dB | 2.6 | 6.3 | lower | -3.7 |
| NR5 maximum makeup dB | 3.8 | 6.9 | lower | -3.1 |
| Audio RMS movement dB | 4.7 | 6.5 | lower | -1.8 |
| Maximum audio peak dBFS | -4.5 | -2.1 | lower | -2.4 |
| Minimum ADC headroom dB | 38.9 | 37.4 | higher | -1.5 |

## Metric Summary

| Metric | Baseline | Candidate | Direction | Verdict |
|---|---:|---:|---|---|
| Failed samples | 0 | 0 | lower | tie |
| Hard blocker samples | 13 | 0 | lower | improvement |
| Ready sample percent | 45 | 100 | higher | improvement |
| Average readiness score | 79.417 | 92 | higher | improvement |
| AGC gain movement dB | 12.9 | 13.7 | lower | tie |
| NR5 weak-input dropout samples | 0 | 0 | lower | tie |
| NR5 weak-input recovery percent | 34.783 | 100 | higher | improvement |
| NR5 hot makeup samples | 0 | 0 | lower | tie |
| NR5 output movement dB | 14.1 | 8 | lower | improvement |
| NR5 makeup movement dB | 2.6 | 6.3 | lower | regression |
| NR5 maximum makeup dB | 3.8 | 6.9 | lower | regression |
| NR5 recovery-drive movement | 0.745 | 0.843 | lower | tie |
| NR5 texture-fill average | 0.012 | 0.024 | informational | informational |
| Audio RMS movement dB | 4.7 | 6.5 | lower | regression |
| Maximum audio peak dBFS | -4.5 | -2.1 | lower | regression |
| Minimum ADC headroom dB | 38.9 | 37.4 | higher | regression |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 7.233 | 7.667 | lower | tie |
| Trace status severity | 70 | 35 | lower | improvement |
