# DSP Live Diagnostics Trace Comparison

- Ready for review: False
- Candidate: live46-7220-final-gate
- Baseline: live37-7220-before
- Regressions: 2
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
| Output movement dB | 14.1 | 7.3 | -6.8 |
| Makeup movement dB | 2.6 | 3.2 | 0.6 |
| Makeup max dB | 3.8 | 4.1 | 0.3 |
| Recovery-drive movement | 0.745 | 0.868 | 0.123 |
| Texture-fill average | 0.012 | 0.024 | 0.012 |

## Regression Safety Classes

| Safety class | Regressions |
|---|---:|
| clipping | 1 |
| pumping | 1 |

## Metric Regressions

| Metric | Baseline | Candidate | Direction | Delta |
|---|---:|---:|---|---:|
| NR5 recovery-drive movement | 0.745 | 0.868 | lower | -0.123 |
| Maximum audio peak dBFS | -4.5 | -2.1 | lower | -2.4 |

## Metric Summary

| Metric | Baseline | Candidate | Direction | Verdict |
|---|---:|---:|---|---|
| Failed samples | 0 | 0 | lower | tie |
| Hard blocker samples | 13 | 0 | lower | improvement |
| Ready sample percent | 45 | 100 | higher | improvement |
| Average readiness score | 79.417 | 92 | higher | improvement |
| AGC gain movement dB | 12.9 | 8.4 | lower | improvement |
| NR5 weak-input dropout samples | 0 | 0 | lower | tie |
| NR5 weak-input recovery percent | 34.783 | 100 | higher | improvement |
| NR5 hot makeup samples | 0 | 0 | lower | tie |
| NR5 output movement dB | 14.1 | 7.3 | lower | improvement |
| NR5 makeup movement dB | 2.6 | 3.2 | lower | tie |
| NR5 maximum makeup dB | 3.8 | 4.1 | lower | tie |
| NR5 recovery-drive movement | 0.745 | 0.868 | lower | regression |
| NR5 texture-fill average | 0.012 | 0.024 | informational | informational |
| Audio RMS movement dB | 4.7 | 3.9 | lower | tie |
| Maximum audio peak dBFS | -4.5 | -2.1 | lower | regression |
| Minimum ADC headroom dB | 38.9 | 44.3 | higher | improvement |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 7.233 | 9.9 | lower | tie |
| Trace status severity | 70 | 0 | lower | improvement |
