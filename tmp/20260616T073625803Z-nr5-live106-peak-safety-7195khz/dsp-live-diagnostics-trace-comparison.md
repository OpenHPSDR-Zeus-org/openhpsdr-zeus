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
| Weak input samples | 44 | 57 | 13 |
| Weak recovered samples | 44 | 57 | 13 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 100 | 100 | 0 |
| Output movement dB | 5.5 | 5.6 | 0.1 |
| Makeup movement dB | 2.2 | 3.4 | 1.2 |
| Makeup max dB | 3.1 | 4 | 0.9 |
| Recovery-drive movement | 0.518 | 0.823 | 0.305 |
| Texture-fill average | 0.046 | 0.039 | -0.007 |

## Regression Safety Classes

| Safety class | Regressions |
|---|---:|
| clipping | 1 |
| pumping | 2 |

## Metric Regressions

| Metric | Baseline | Candidate | Direction | Delta |
|---|---:|---:|---|---:|
| NR5 makeup movement dB | 2.2 | 3.4 | lower | -1.2 |
| NR5 recovery-drive movement | 0.518 | 0.823 | lower | -0.305 |
| NR5 maximum output peak dBFS | -10.2 | -8.6 | lower | -1.6 |

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
| NR5 output movement dB | 5.5 | 5.6 | lower | tie |
| NR5 makeup movement dB | 2.2 | 3.4 | lower | regression |
| NR5 maximum makeup dB | 3.1 | 4 | lower | tie |
| NR5 recovery-drive movement | 0.518 | 0.823 | lower | regression |
| NR5 texture-fill average | 0.046 | 0.039 | informational | informational |
| NR5 maximum peak reduction dB | 0 | 0 | lower | tie |
| NR5 maximum output peak dBFS | -10.2 | -8.6 | lower | regression |
| Audio RMS movement dB | 5.2 | 2.4 | lower | improvement |
| Maximum audio peak dBFS | -1.8 | -2.7 | lower | tie |
| RX audio leveler output RMS movement dB | 5.2 | 2.4 | lower | improvement |
| RX audio leveler applied gain movement dB | 6.4 | 5.3 | lower | improvement |
| RX audio leveler boost-slew limited samples | 0 | 0 | lower | tie |
| RX audio leveler peak-limited samples | 1 | 0 | lower | improvement |
| RX audio leveler output-limited blocks | 2 | 0 | lower | improvement |
| RX audio leveler max crest-cap reduction dB | 4 | 0 | lower | improvement |
| RX audio leveler max shaped samples per block | 10 | 0 | lower | improvement |
| Minimum ADC headroom dB | 43 | 44.2 | higher | improvement |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 7.444 | 7.617 | lower | tie |
| Trace status severity | 0 | 0 | lower | tie |
