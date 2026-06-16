# DSP Live Diagnostics Trace Comparison

- Ready for review: True
- Candidate: live63-7245-strong-valley-final-cap
- Baseline: live56-7245-peak-only
- Regressions: 0
- Gate failures: 0
- Missing values: 0

## NR5 Weak-Signal Summary

| Metric | Baseline | Candidate | Delta |
|---|---:|---:|---:|
| Weak input samples | 59 | 56 | -3 |
| Weak recovered samples | 59 | 56 | -3 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 100 | 100 | 0 |
| Output movement dB | 3.6 | 2.4 | -1.2 |
| Makeup movement dB | 5.7 | 3.7 | -2 |
| Makeup max dB | 8 | 6.7 | -1.3 |
| Recovery-drive movement | 0.789 | 0.396 | -0.393 |
| Texture-fill average | 0.046 | 0.044 | -0.002 |

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
| NR5 output movement dB | 3.6 | 2.4 | lower | improvement |
| NR5 makeup movement dB | 5.7 | 3.7 | lower | improvement |
| NR5 maximum makeup dB | 8 | 6.7 | lower | improvement |
| NR5 recovery-drive movement | 0.789 | 0.396 | lower | improvement |
| NR5 texture-fill average | 0.046 | 0.044 | informational | informational |
| Audio RMS movement dB | 3.2 | 2.2 | lower | tie |
| Maximum audio peak dBFS | -2.5 | -3.1 | lower | tie |
| Minimum ADC headroom dB | 41.6 | 44.5 | higher | improvement |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 9.5 | 7.55 | lower | tie |
| Trace status severity | 0 | 0 | lower | tie |
