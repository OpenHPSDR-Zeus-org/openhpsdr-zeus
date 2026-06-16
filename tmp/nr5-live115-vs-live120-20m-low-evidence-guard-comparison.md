# DSP Live Diagnostics Trace Comparison

- Ready for review: False
- Candidate: new-nr5-low-evidence-guard
- Baseline: old-nr5-low-evidence-lift
- Regressions: 5
- Gate failures: 0
- Missing values: 0

## NR5 Weak-Signal Summary

| Metric | Baseline | Candidate | Delta |
|---|---:|---:|---:|
| Weak input samples | 90 | 80 | -10 |
| Weak recovered samples | 90 | 0 | -90 |
| Weak dropout samples | 0 | 2 | 2 |
| Hot makeup samples | 0 | 0 | 0 |
| Weak recovery percent | 100 | 0 | -100 |
| Output movement dB | 4.9 | 40.1 | 35.2 |
| Makeup movement dB | 1 | 0.1 | -0.9 |
| Makeup max dB | 1.2 | 0.1 | -1.1 |
| Recovery-drive movement | 0.838 | 0.199 | -0.639 |
| Texture-fill average | 0.04 | 0.04 | 0 |

## Regression Safety Classes

| Safety class | Regressions |
|---|---:|
| pumping | 3 |
| weak-signal | 2 |

## Metric Regressions

| Metric | Baseline | Candidate | Direction | Delta |
|---|---:|---:|---|---:|
| NR5 weak-input dropout samples | 0 | 2 | lower | -2 |
| NR5 weak-input recovery percent | 100 | 0 | higher | -100 |
| NR5 output movement dB | 4.9 | 40.1 | lower | -35.2 |
| Audio RMS movement dB | 0.4 | 4.2 | lower | -3.8 |
| RX audio leveler output RMS movement dB | 0.4 | 4.2 | lower | -3.8 |

## Metric Summary

| Metric | Baseline | Candidate | Direction | Verdict |
|---|---:|---:|---|---|
| Failed samples | 0 | 0 | lower | tie |
| Hard blocker samples | 0 | 0 | lower | tie |
| Ready sample percent | 100 | 100 | higher | tie |
| Average readiness score | 92 | 92 | higher | tie |
| AGC gain movement dB | 0 | 0 | lower | tie |
| NR5 weak-input dropout samples | 0 | 2 | lower | regression |
| NR5 weak-input recovery percent | 100 | 0 | higher | regression |
| NR5 hot makeup samples | 0 | 0 | lower | tie |
| NR5 low-evidence lifted samples | 73 | 0 | lower | improvement |
| NR5 output movement dB | 4.9 | 40.1 | lower | regression |
| NR5 makeup movement dB | 1 | 0.1 | lower | tie |
| NR5 maximum makeup dB | 1.2 | 0.1 | lower | improvement |
| NR5 recovery-drive movement | 0.838 | 0.199 | lower | improvement |
| NR5 texture-fill average | 0.04 | 0.04 | informational | informational |
| NR5 maximum peak reduction dB | 0 | 0 | lower | tie |
| NR5 maximum output peak dBFS | -6.8 | -28.3 | lower | improvement |
| Audio RMS movement dB | 0.4 | 4.2 | lower | regression |
| Maximum audio peak dBFS | -3.6 | -62.4 | lower | improvement |
| RX audio leveler output RMS movement dB | 0.4 | 4.2 | lower | regression |
| RX audio leveler applied gain movement dB | 4.9 | 0 | lower | improvement |
| RX audio leveler boost-slew limited samples | 0 | 0 | lower | tie |
| RX audio leveler peak-limited samples | 0 | 0 | lower | tie |
| RX audio leveler output-limited blocks | 0 | 0 | lower | tie |
| RX audio leveler max crest-cap reduction dB | 0 | 0 | lower | tie |
| RX audio leveler max shaped samples per block | 0 | 0 | lower | tie |
| Minimum ADC headroom dB | 59.9 | 62.7 | higher | improvement |
| Maximum monitor backlog samples | 0 | 0 | lower | tie |
| Audio fresh percent | 100 | 100 | higher | tie |
| RX meters fresh percent | 100 | 100 | higher | tie |
| Squelch closed percent | 0 | 0 | informational | informational |
| Average endpoint latency ms | 7.178 | 7.175 | lower | tie |
| Trace status severity | 35 | 0 | lower | improvement |
