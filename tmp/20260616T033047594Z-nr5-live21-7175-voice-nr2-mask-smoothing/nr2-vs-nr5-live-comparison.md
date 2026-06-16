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
| Weak input samples | 0 | 32 | 32 |
| Weak recovered samples | 0 | 30 | 30 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 100 | 93.75 | -6.25 |

## Metric Regressions

| Metric | Baseline | Candidate | Direction | Delta |
|---|---:|---:|---|---:|
| AGC gain movement dB | 7.7 | 9.8 | lower | -2.1 |
| NR5 weak-input recovery percent | 100 | 93.75 | higher | -6.25 |
| Minimum ADC headroom dB | 43.7 | 35 | higher | -8.7 |

## Metric Summary

| Metric | Baseline | Candidate | Direction | Verdict |
|---|---:|---:|---|---|
| Failed samples | 0 | 0 | lower | tie |
| Hard blocker samples | 0 | 0 | lower | tie |
| Ready sample percent | 100 | 100 | higher | tie |
| Average readiness score | 92 | 92 | higher | tie |
| AGC gain movement dB | 7.7 | 9.8 | lower | regression |
| NR5 weak-input dropout samples | 0 | 0 | lower | tie |
| NR5 weak-input recovery percent | 100 | 93.75 | higher | regression |
| NR5 hot makeup samples | 0 | 0 | lower | tie |
| Audio RMS movement dB | 29.8 | 7.6 | lower | improvement |
| Maximum audio peak dBFS | -3.5 | -3.1 | lower | tie |
| Minimum ADC headroom dB | 43.7 | 35 | higher | regression |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 6.433 | 6.544 | lower | tie |
| Trace status severity | 35 | 35 | lower | tie |
