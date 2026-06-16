# DSP Live Diagnostics Trace Comparison

- Ready for review: False
- Candidate: live58-7220-strong-valley
- Baseline: live57-7220-peak-only
- Regressions: 3
- Gate failures: 0
- Missing values: 0

## NR5 Weak-Signal Summary

| Metric | Baseline | Candidate | Delta |
|---|---:|---:|---:|
| Weak input samples | 22 | 22 | 0 |
| Weak recovered samples | 22 | 22 | 0 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 100 | 100 | 0 |
| Output movement dB | 9.2 | 7.7 | -1.5 |
| Makeup movement dB | 4.4 | 6.4 | 2 |
| Makeup max dB | 5.1 | 7.3 | 2.2 |
| Recovery-drive movement | 0.839 | 0.842 | 0.003 |
| Texture-fill average | 0.016 | 0.019 | 0.003 |

## Regression Safety Classes

| Safety class | Regressions |
|---|---:|
| front-end | 1 |
| pumping | 2 |

## Metric Regressions

| Metric | Baseline | Candidate | Direction | Delta |
|---|---:|---:|---|---:|
| NR5 makeup movement dB | 4.4 | 6.4 | lower | -2 |
| NR5 maximum makeup dB | 5.1 | 7.3 | lower | -2.2 |
| Minimum ADC headroom dB | 38.5 | 37.1 | higher | -1.4 |

## Metric Summary

| Metric | Baseline | Candidate | Direction | Verdict |
|---|---:|---:|---|---|
| Failed samples | 0 | 0 | lower | tie |
| Hard blocker samples | 0 | 0 | lower | tie |
| Ready sample percent | 100 | 100 | higher | tie |
| Average readiness score | 92 | 92 | higher | tie |
| AGC gain movement dB | 10.2 | 10.7 | lower | tie |
| NR5 weak-input dropout samples | 0 | 0 | lower | tie |
| NR5 weak-input recovery percent | 100 | 100 | higher | tie |
| NR5 hot makeup samples | 0 | 0 | lower | tie |
| NR5 output movement dB | 9.2 | 7.7 | lower | improvement |
| NR5 makeup movement dB | 4.4 | 6.4 | lower | regression |
| NR5 maximum makeup dB | 5.1 | 7.3 | lower | regression |
| NR5 recovery-drive movement | 0.839 | 0.842 | lower | tie |
| NR5 texture-fill average | 0.016 | 0.019 | informational | informational |
| Audio RMS movement dB | 7.2 | 4.8 | lower | improvement |
| Maximum audio peak dBFS | -3.7 | -3.7 | lower | tie |
| Minimum ADC headroom dB | 38.5 | 37.1 | higher | regression |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 6.667 | 7.8 | lower | tie |
| Trace status severity | 35 | 0 | lower | improvement |
