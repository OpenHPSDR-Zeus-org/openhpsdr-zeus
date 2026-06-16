# DSP Live Diagnostics Trace Comparison

- Ready for review: False
- Candidate: candidate-under-test
- Baseline: current-zeus
- Regressions: 1
- Gate failures: 0
- Missing values: 0

## Metric Regressions

| Metric | Baseline | Candidate | Direction | Delta |
|---|---:|---:|---|---:|
| Minimum ADC headroom dB | 43.7 | 38.9 | higher | -4.8 |

## Metric Summary

| Metric | Baseline | Candidate | Direction | Verdict |
|---|---:|---:|---|---|
| Failed samples | 0 | 0 | lower | tie |
| Hard blocker samples | 0 | 0 | lower | tie |
| Ready sample percent | 100 | 100 | higher | tie |
| Average readiness score | 92 | 92 | higher | tie |
| AGC gain movement dB | 7.7 | 4.6 | lower | improvement |
| NR5 weak-input dropout samples | 0 | 0 | lower | tie |
| NR5 weak-input recovery percent | 100 | 97.778 | higher | tie |
| NR5 hot makeup samples | 0 | 0 | lower | tie |
| Audio RMS movement dB | 29.8 | 8.5 | lower | improvement |
| Maximum audio peak dBFS | -3.5 | -3.2 | lower | tie |
| Minimum ADC headroom dB | 43.7 | 38.9 | higher | regression |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 6.433 | 6.789 | lower | tie |
| Trace status severity | 35 | 35 | lower | tie |
