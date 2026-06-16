# DSP Live Diagnostics Trace Comparison

- Ready for review: True
- Candidate: soft-knee-7175
- Baseline: pre-soft-knee-7175
- Regressions: 0
- Gate failures: 0
- Missing values: 0

## NR5 Weak-Signal Summary

| Metric | Baseline | Candidate | Delta |
|---|---:|---:|---:|
| Weak input samples | 18 | 24 | 6 |
| Weak recovered samples | 18 | 24 | 6 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 100 | 100 | 0 |
| Output movement dB | 4.9 | 5.1 | 0.2 |
| Makeup movement dB | 3.9 | 2.7 | -1.2 |
| Makeup max dB | 4.5 | 3.1 | -1.4 |
| Recovery-drive movement | 0.794 | 0.674 | -0.12 |
| Texture-fill average | 0.036 | 0.027 | -0.009 |

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
| NR5 output movement dB | 4.9 | 5.1 | lower | tie |
| NR5 makeup movement dB | 3.9 | 2.7 | lower | improvement |
| NR5 maximum makeup dB | 4.5 | 3.1 | lower | improvement |
| NR5 recovery-drive movement | 0.794 | 0.674 | lower | improvement |
| NR5 texture-fill average | 0.036 | 0.027 | informational | informational |
| Audio RMS movement dB | 3.9 | 3.4 | lower | tie |
| Maximum audio peak dBFS | -1.3 | -3.7 | lower | improvement |
| Minimum ADC headroom dB | 45.6 | 47 | higher | improvement |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 8.967 | 8.82 | lower | tie |
| Trace status severity | 0 | 0 | lower | tie |
