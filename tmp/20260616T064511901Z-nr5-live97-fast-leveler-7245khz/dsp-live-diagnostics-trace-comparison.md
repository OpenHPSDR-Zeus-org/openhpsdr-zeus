# DSP Live Diagnostics Trace Comparison

- Ready for review: False
- Candidate: candidate-under-test
- Baseline: current-zeus
- Regressions: 1
- Gate failures: 0
- Missing values: 0

## NR5 Weak-Signal Summary

| Metric | Baseline | Candidate | Delta |
|---|---:|---:|---:|
| Weak input samples | 51 | 58 | 7 |
| Weak recovered samples | 51 | 58 | 7 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 100 | 100 | 0 |
| Output movement dB | 5.1 | 5.3 | 0.2 |
| Makeup movement dB | 3.9 | 1.4 | -2.5 |
| Makeup max dB | 4.4 | 2.2 | -2.2 |
| Recovery-drive movement | 0.833 | 0.667 | -0.166 |
| Texture-fill average | 0.043 | 0.042 | -0.001 |

## Regression Safety Classes

| Safety class | Regressions |
|---|---:|
| clipping | 1 |

## Metric Regressions

| Metric | Baseline | Candidate | Direction | Delta |
|---|---:|---:|---|---:|
| Maximum audio peak dBFS | -3.9 | -2.2 | lower | -1.7 |

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
| NR5 output movement dB | 5.1 | 5.3 | lower | tie |
| NR5 makeup movement dB | 3.9 | 1.4 | lower | improvement |
| NR5 maximum makeup dB | 4.4 | 2.2 | lower | improvement |
| NR5 recovery-drive movement | 0.833 | 0.667 | lower | improvement |
| NR5 texture-fill average | 0.043 | 0.042 | informational | informational |
| NR5 maximum peak reduction dB | 0 | 0 | lower | tie |
| NR5 maximum output peak dBFS | -7.1 | -8.6 | lower | improvement |
| Audio RMS movement dB | 3.1 | 3.6 | lower | tie |
| Maximum audio peak dBFS | -3.9 | -2.2 | lower | regression |
| RX audio leveler output RMS movement dB | 3.1 | 3.6 | lower | tie |
| RX audio leveler applied gain movement dB | 5.3 | 5.1 | lower | tie |
| RX audio leveler boost-slew limited samples | 1 | 0 | lower | improvement |
| RX audio leveler peak-limited samples | 0 | 0 | lower | tie |
| Minimum ADC headroom dB | 38.1 | 46.7 | higher | improvement |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 8.3 | 7.633 | lower | tie |
| Trace status severity | 0 | 0 | lower | tie |
