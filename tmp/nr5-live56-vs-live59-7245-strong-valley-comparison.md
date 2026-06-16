# DSP Live Diagnostics Trace Comparison

- Ready for review: True
- Candidate: live59-7245-strong-valley
- Baseline: live56-7245-peak-only
- Regressions: 0
- Gate failures: 0
- Missing values: 0

## NR5 Weak-Signal Summary

| Metric | Baseline | Candidate | Delta |
|---|---:|---:|---:|
| Weak input samples | 59 | 58 | -1 |
| Weak recovered samples | 59 | 58 | -1 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 100 | 100 | 0 |
| Output movement dB | 3.6 | 3.2 | -0.4 |
| Makeup movement dB | 5.7 | 5.1 | -0.6 |
| Makeup max dB | 8 | 7.1 | -0.9 |
| Recovery-drive movement | 0.789 | 0.795 | 0.006 |
| Texture-fill average | 0.046 | 0.042 | -0.004 |

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
| NR5 output movement dB | 3.6 | 3.2 | lower | tie |
| NR5 makeup movement dB | 5.7 | 5.1 | lower | tie |
| NR5 maximum makeup dB | 8 | 7.1 | lower | tie |
| NR5 recovery-drive movement | 0.789 | 0.795 | lower | tie |
| NR5 texture-fill average | 0.046 | 0.042 | informational | informational |
| Audio RMS movement dB | 3.2 | 2.7 | lower | tie |
| Maximum audio peak dBFS | -2.5 | -2.9 | lower | tie |
| Minimum ADC headroom dB | 41.6 | 44.3 | higher | improvement |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 9.5 | 7.683 | lower | tie |
| Trace status severity | 0 | 0 | lower | tie |
