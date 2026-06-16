# DSP Live Diagnostics Trace Comparison

- Ready for review: True
- Candidate: nr5-live114-long-20m
- Baseline: nr5-live113-return-20m
- Regressions: 0
- Gate failures: 0
- Missing values: 0

## NR5 Weak-Signal Summary

| Metric | Baseline | Candidate | Delta |
|---|---:|---:|---:|
| Weak input samples | 68 | 150 | 82 |
| Weak recovered samples | 68 | 150 | 82 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 100 | 100 | 0 |
| Output movement dB | 5.4 | 5.7 | 0.3 |
| Makeup movement dB | 6.7 | 5.7 | -1 |
| Makeup max dB | 7.2 | 5.9 | -1.3 |
| Recovery-drive movement | 0.814 | 0.823 | 0.009 |
| Texture-fill average | 0.032 | 0.039 | 0.007 |

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
| NR5 output movement dB | 5.4 | 5.7 | lower | tie |
| NR5 makeup movement dB | 6.7 | 5.7 | lower | tie |
| NR5 maximum makeup dB | 7.2 | 5.9 | lower | improvement |
| NR5 recovery-drive movement | 0.814 | 0.823 | lower | tie |
| NR5 texture-fill average | 0.032 | 0.039 | informational | informational |
| NR5 maximum peak reduction dB | 0 | 0 | lower | tie |
| NR5 maximum output peak dBFS | -9.4 | -7.6 | lower | tie |
| Audio RMS movement dB | 1.1 | 0.4 | lower | tie |
| Maximum audio peak dBFS | -2.6 | -4 | lower | improvement |
| RX audio leveler output RMS movement dB | 1.1 | 0.4 | lower | tie |
| RX audio leveler applied gain movement dB | 5.6 | 5.8 | lower | tie |
| RX audio leveler boost-slew limited samples | 0 | 0 | lower | tie |
| RX audio leveler peak-limited samples | 1 | 0 | lower | improvement |
| RX audio leveler output-limited blocks | 0 | 0 | lower | tie |
| RX audio leveler max crest-cap reduction dB | 0 | 0 | lower | tie |
| RX audio leveler max shaped samples per block | 0 | 0 | lower | tie |
| Minimum ADC headroom dB | 51.8 | 52.2 | higher | tie |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 7.144 | 6.48 | lower | tie |
| Trace status severity | 0 | 0 | lower | tie |
