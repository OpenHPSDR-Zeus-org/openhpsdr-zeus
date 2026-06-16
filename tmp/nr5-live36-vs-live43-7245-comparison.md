# DSP Live Diagnostics Trace Comparison

- Ready for review: False
- Candidate: live43-7245-signal-valley
- Baseline: live36-7245-before
- Regressions: 2
- Gate failures: 0
- Missing values: 0

## NR5 Weak-Signal Summary

| Metric | Baseline | Candidate | Delta |
|---|---:|---:|---:|
| Weak input samples | 59 | 57 | -2 |
| Weak recovered samples | 59 | 57 | -2 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 100 | 100 | 0 |
| Output movement dB | 2.7 | 2.9 | 0.2 |
| Makeup movement dB | 7.9 | 6.9 | -1 |
| Makeup max dB | 9.4 | 8.4 | -1 |
| Recovery-drive movement | 0.309 | 0.718 | 0.409 |
| Texture-fill average | 0.045 | 0.046 | 0.001 |

## Regression Safety Classes

| Safety class | Regressions |
|---|---:|
| front-end | 1 |
| pumping | 1 |

## Metric Regressions

| Metric | Baseline | Candidate | Direction | Delta |
|---|---:|---:|---|---:|
| NR5 recovery-drive movement | 0.309 | 0.718 | lower | -0.409 |
| Minimum ADC headroom dB | 50.2 | 46.2 | higher | -4 |

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
| NR5 output movement dB | 2.7 | 2.9 | lower | tie |
| NR5 makeup movement dB | 7.9 | 6.9 | lower | tie |
| NR5 maximum makeup dB | 9.4 | 8.4 | lower | tie |
| NR5 recovery-drive movement | 0.309 | 0.718 | lower | regression |
| NR5 texture-fill average | 0.045 | 0.046 | informational | informational |
| Audio RMS movement dB | 1.6 | 2.6 | lower | tie |
| Maximum audio peak dBFS | -0.7 | -0.3 | lower | tie |
| Minimum ADC headroom dB | 50.2 | 46.2 | higher | regression |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 7.55 | 6.517 | lower | tie |
| Trace status severity | 70 | 0 | lower | improvement |
