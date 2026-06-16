# DSP Live Diagnostics Trace Comparison

- Ready for review: False
- Candidate: candidate-under-test
- Baseline: current-zeus
- Regressions: 4
- Gate failures: 0
- Missing values: 0

## NR5 Weak-Signal Summary

| Metric | Baseline | Candidate | Delta |
|---|---:|---:|---:|
| Weak input samples | 55 | 13 | -42 |
| Weak recovered samples | 55 | 11 | -44 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 100 | 84.615 | -15.385 |
| Output movement dB | 8.6 | 11 | 2.4 |
| Makeup movement dB | 1.3 | 2.1 | 0.8 |
| Makeup max dB | 1.9 | 3 | 1.1 |
| Recovery-drive movement | 0.483 | 0.813 | 0.33 |
| Texture-fill average | 0.038 | 0.008 | -0.03 |

## Regression Safety Classes

| Safety class | Regressions |
|---|---:|
| pumping | 3 |
| weak-signal | 1 |

## Metric Regressions

| Metric | Baseline | Candidate | Direction | Delta |
|---|---:|---:|---|---:|
| NR5 weak-input recovery percent | 100 | 84.615 | higher | -15.385 |
| NR5 output movement dB | 8.6 | 11 | lower | -2.4 |
| NR5 maximum makeup dB | 1.9 | 3 | lower | -1.1 |
| NR5 recovery-drive movement | 0.483 | 0.813 | lower | -0.33 |

## Metric Summary

| Metric | Baseline | Candidate | Direction | Verdict |
|---|---:|---:|---|---|
| Failed samples | 0 | 0 | lower | tie |
| Hard blocker samples | 0 | 0 | lower | tie |
| Ready sample percent | 100 | 100 | higher | tie |
| Average readiness score | 92 | 92 | higher | tie |
| AGC gain movement dB | 17.5 | 12 | lower | improvement |
| NR5 weak-input dropout samples | 0 | 0 | lower | tie |
| NR5 weak-input recovery percent | 100 | 84.615 | higher | regression |
| NR5 hot makeup samples | 0 | 0 | lower | tie |
| NR5 output movement dB | 8.6 | 11 | lower | regression |
| NR5 makeup movement dB | 1.3 | 2.1 | lower | tie |
| NR5 maximum makeup dB | 1.9 | 3 | lower | regression |
| NR5 recovery-drive movement | 0.483 | 0.813 | lower | regression |
| NR5 texture-fill average | 0.038 | 0.008 | informational | informational |
| NR5 maximum peak reduction dB | 0 | 0 | lower | tie |
| NR5 maximum output peak dBFS | -11.3 | -12.2 | lower | tie |
| Audio RMS movement dB | 3.8 | 4.2 | lower | tie |
| Maximum audio peak dBFS | -2 | -3.6 | lower | improvement |
| RX audio leveler output RMS movement dB | 3.8 | 4.2 | lower | tie |
| RX audio leveler applied gain movement dB | 9 | 8.2 | lower | tie |
| RX audio leveler boost-slew limited samples | 5 | 1 | lower | improvement |
| RX audio leveler peak-limited samples | 0 | 0 | lower | tie |
| Minimum ADC headroom dB | 31.1 | 39.9 | higher | improvement |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 7.633 | 7.517 | lower | tie |
| Trace status severity | 35 | 0 | lower | improvement |
