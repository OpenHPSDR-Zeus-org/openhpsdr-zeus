# DSP Live Diagnostics Trace Comparison

- Ready for review: False
- Candidate: candidate-under-test
- Baseline: current-zeus
- Regressions: 3
- Gate failures: 0
- Missing values: 0

## NR5 Weak-Signal Summary

| Metric | Baseline | Candidate | Delta |
|---|---:|---:|---:|
| Weak input samples | 23 | 28 | 5 |
| Weak recovered samples | 16 | 18 | 2 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 69.565 | 64.286 | -5.279 |
| Output movement dB | 15.6 | 12.8 | -2.8 |
| Makeup movement dB | 6.3 | 5.9 | -0.4 |
| Makeup max dB | 7.3 | 6.6 | -0.7 |
| Recovery-drive movement | 0.845 | 0.794 | -0.051 |
| Texture-fill average | 0.014 | 0.013 | -0.001 |

## Regression Safety Classes

| Safety class | Regressions |
|---|---:|
| front-end | 1 |
| pumping | 1 |
| weak-signal | 1 |

## Metric Regressions

| Metric | Baseline | Candidate | Direction | Delta |
|---|---:|---:|---|---:|
| NR5 weak-input recovery percent | 69.565 | 64.286 | higher | -5.279 |
| Audio RMS movement dB | 6.6 | 7.8 | lower | -1.2 |
| Minimum ADC headroom dB | 40.3 | 31.9 | higher | -8.4 |

## Metric Summary

| Metric | Baseline | Candidate | Direction | Verdict |
|---|---:|---:|---|---|
| Failed samples | 0 | 0 | lower | tie |
| Hard blocker samples | 0 | 0 | lower | tie |
| Ready sample percent | 100 | 100 | higher | tie |
| Average readiness score | 92 | 92 | higher | tie |
| AGC gain movement dB | 10.5 | 10.4 | lower | tie |
| NR5 weak-input dropout samples | 0 | 0 | lower | tie |
| NR5 weak-input recovery percent | 69.565 | 64.286 | higher | regression |
| NR5 hot makeup samples | 0 | 0 | lower | tie |
| NR5 output movement dB | 15.6 | 12.8 | lower | improvement |
| NR5 makeup movement dB | 6.3 | 5.9 | lower | tie |
| NR5 maximum makeup dB | 7.3 | 6.6 | lower | tie |
| NR5 recovery-drive movement | 0.845 | 0.794 | lower | tie |
| NR5 texture-fill average | 0.014 | 0.013 | informational | informational |
| Audio RMS movement dB | 6.6 | 7.8 | lower | regression |
| Maximum audio peak dBFS | -1.7 | -3.9 | lower | improvement |
| Minimum ADC headroom dB | 40.3 | 31.9 | higher | regression |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 6.3 | 6.911 | lower | tie |
| Trace status severity | 35 | 35 | lower | tie |
