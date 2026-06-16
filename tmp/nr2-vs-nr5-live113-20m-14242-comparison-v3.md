# DSP Live Diagnostics Trace Comparison

- Ready for review: False
- Candidate: nr5-spnr-20m
- Baseline: nr2-emnr-20m
- Regressions: 0
- Gate failures: 0
- Missing values: 7

## NR5 Weak-Signal Summary

| Metric | Baseline | Candidate | Delta |
|---|---:|---:|---:|
| Weak input samples | 0 | 68 | 68 |
| Weak recovered samples | 0 | 68 | 68 |
| Weak dropout samples | 0 | 0 | 0 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 100 | 100 | 0 |
| Output movement dB | 0 | 5.4 | 5.4 |
| Makeup movement dB | 0 | 6.7 | 6.7 |
| Makeup max dB | 0 | 7.2 | 7.2 |
| Recovery-drive movement | 0 | 0.814 | 0.814 |
| Texture-fill average | 0 | 0.032 | 0.032 |

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
| NR5 output movement dB |  | 5.4 | lower | missing |
| NR5 makeup movement dB |  | 6.7 | lower | missing |
| NR5 maximum makeup dB |  | 7.2 | lower | missing |
| NR5 recovery-drive movement |  | 0.814 | lower | missing |
| NR5 texture-fill average |  | 0.032 | informational | missing |
| NR5 maximum peak reduction dB |  | 0 | lower | missing |
| NR5 maximum output peak dBFS |  | -9.4 | lower | missing |
| Audio RMS movement dB | 7.7 | 1.1 | lower | improvement |
| Maximum audio peak dBFS | -7 | -2.6 | lower | tie |
| RX audio leveler output RMS movement dB | 7.7 | 1.1 | lower | improvement |
| RX audio leveler applied gain movement dB | 3.2 | 5.6 | lower | tie |
| RX audio leveler boost-slew limited samples | 1 | 0 | lower | improvement |
| RX audio leveler peak-limited samples | 0 | 1 | lower | tie |
| RX audio leveler output-limited blocks | 0 | 0 | lower | tie |
| RX audio leveler max crest-cap reduction dB | 0 | 0 | lower | tie |
| RX audio leveler max shaped samples per block | 0 | 0 | lower | tie |
| Minimum ADC headroom dB | 47.4 | 51.8 | higher | improvement |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 7.256 | 7.144 | lower | tie |
| Trace status severity | 35 | 0 | lower | improvement |
