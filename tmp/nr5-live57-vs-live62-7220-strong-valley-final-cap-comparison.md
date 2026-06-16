# DSP Live Diagnostics Trace Comparison

- Ready for review: False
- Candidate: live62-7220-strong-valley-final-cap
- Baseline: live57-7220-peak-only
- Regressions: 4
- Gate failures: 0
- Missing values: 0

## NR5 Weak-Signal Summary

| Metric | Baseline | Candidate | Delta |
|---|---:|---:|---:|
| Weak input samples | 22 | 17 | -5 |
| Weak recovered samples | 22 | 17 | -5 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 100 | 100 | 0 |
| Output movement dB | 9.2 | 8 | -1.2 |
| Makeup movement dB | 4.4 | 5.3 | 0.9 |
| Makeup max dB | 5.1 | 6.6 | 1.5 |
| Recovery-drive movement | 0.839 | 0.801 | -0.038 |
| Texture-fill average | 0.016 | 0.014 | -0.002 |

## Regression Safety Classes

| Safety class | Regressions |
|---|---:|
| clipping | 1 |
| front-end | 1 |
| pumping | 2 |

## Metric Regressions

| Metric | Baseline | Candidate | Direction | Delta |
|---|---:|---:|---|---:|
| AGC gain movement dB | 10.2 | 18.9 | lower | -8.7 |
| NR5 maximum makeup dB | 5.1 | 6.6 | lower | -1.5 |
| Maximum audio peak dBFS | -3.7 | -2 | lower | -1.7 |
| Minimum ADC headroom dB | 38.5 | 33.7 | higher | -4.8 |

## Metric Summary

| Metric | Baseline | Candidate | Direction | Verdict |
|---|---:|---:|---|---|
| Failed samples | 0 | 0 | lower | tie |
| Hard blocker samples | 0 | 0 | lower | tie |
| Ready sample percent | 100 | 100 | higher | tie |
| Average readiness score | 92 | 92 | higher | tie |
| AGC gain movement dB | 10.2 | 18.9 | lower | regression |
| NR5 weak-input dropout samples | 0 | 0 | lower | tie |
| NR5 weak-input recovery percent | 100 | 100 | higher | tie |
| NR5 hot makeup samples | 0 | 0 | lower | tie |
| NR5 output movement dB | 9.2 | 8 | lower | improvement |
| NR5 makeup movement dB | 4.4 | 5.3 | lower | tie |
| NR5 maximum makeup dB | 5.1 | 6.6 | lower | regression |
| NR5 recovery-drive movement | 0.839 | 0.801 | lower | tie |
| NR5 texture-fill average | 0.016 | 0.014 | informational | informational |
| Audio RMS movement dB | 7.2 | 4.6 | lower | improvement |
| Maximum audio peak dBFS | -3.7 | -2 | lower | regression |
| Minimum ADC headroom dB | 38.5 | 33.7 | higher | regression |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 6.667 | 7.383 | lower | tie |
| Trace status severity | 35 | 35 | lower | tie |
