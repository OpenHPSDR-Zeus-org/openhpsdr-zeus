// SPDX-License-Identifier: GPL-2.0-or-later
//
// Read-only catalog of external DSP/ML engines that may be evaluated after
// WDSP parity, benchmark evidence, and radio safety gates are satisfied.

using Zeus.Contracts;

namespace Zeus.Server;

public static class DspExternalEngineCandidateCatalog
{
    private const string OptInGate = "candidate-only-opt-in-bakeoff";

    public static DspExternalEngineCandidateDto[] All() =>
    [
        new(
            SchemaVersion: 1,
            Id: "rnnoise",
            Name: "RNNoise",
            Family: "neural-speech-denoiser",
            IntegrationPoint: "post-demod-rx-audio-speech-only through RX Audio Suite receive VST insert",
            DefaultState: "off",
            RolloutPolicy: OptInGate,
            EvaluationStage: "rx-vst-plugin-path-supported-not-bundled",
            AllowedSignalPaths: ["post-demod-rx-audio-speech", "rx-audio-suite-post-demod-rx-audio-speech"],
            ForbiddenSignalPaths: ["raw-wdsp-iq", "cw-or-digital-non-speech", "tx-audio", "tx-monitor", "puresignal-feedback"],
            RequiredControls:
            [
                "operator-visible-opt-in",
                "clean-bypass-fallback",
                "speech-content-gate",
                "rnnoise-vad-confidence-gate",
                "rx-audio-suite-route",
                "original-filtered-blend-control",
                "48khz-frame-adapter-or-rx-vst-plugin-host",
                "official-xiph-runtime-only",
                "model-license-provenance-gate",
                "le9endary-training-reference-only",
                "werman-plugin-reference-only",
                "no-raw-wdsp-iq-replacement",
                "no-tx-or-puresignal-coupling",
            ],
            FallbackPolicy: "disabled/unavailable/model-load-failure path must fall back to current Zeus post-WDSP RX audio; non-speech/CW/digital/TX/PureSignal paths must bypass the engine",
            License: "BSD-3-Clause for official Xiph RNNoise runtime; le9endary/RNNoise has no repo license and is training-reference-only until provenance is cleared; werman/noise-suppression-for-voice is GPL-3.0 plugin reference only, not core-vendored runtime",
            PackagingStatus: "official-xiph-native-c-library-not-vendored; known RNNoise/noise-suppression VSTs can be operator-scanned into rx.post-demod; le9endary fork not vendorable until license/model provenance is cleared; werman plugin not vendored into core; model packaging and direct 48 kHz frame adapter required for a bundled runtime",
            RuntimeRisk: "medium",
            LatencyRisk: "low-medium",
            RadioSafetyRisk: "medium: speech-trained model may damage weak CW, digital, or non-speech HF content",
            Strengths:
            [
                "Small C runtime with established realtime speech-noise suppression use.",
                "Official Xiph runtime exposes a small frame API suitable for an opt-in RX Audio Suite post-demod adapter.",
                "Useful as a low-footprint post-demod speech benchmark candidate.",
                "The le9endary fork provides useful training workflow notes, but must not be treated as a shippable runtime or model source until licensing is cleared.",
                "The werman plugin is useful as a practical RNNoise behavior baseline on Windows, but its GPL plugin package remains reference-only for Zeus core.",
            ],
            RequiredBenchmarks:
            [
                "ssb-like-speech",
                "weak-ssb-volume-parity",
                "noise-only",
                "weak-cw-carrier",
                "fading-carrier",
                "agc-level-step",
                "rx-audio-suite-bypass",
            ],
            RequiredEvidence:
            [
                "Must preserve weak carrier/CW fixtures when bypassed or speech-gated.",
                "Must beat current Zeus post-demod audio on speech fixtures without pumping.",
                "Must show RX Suite RNNoise weak-speech level parity against no NR and NR2 without lifting noise-only floor.",
                "Must prove CPU, allocation, and latency bounds on G2-class hardware.",
                "Must document official Xiph source revision, BSD-3-Clause license text, model artifact origin, and model hash before packaging.",
                "Must prove le9endary/RNNoise remains a training-reference-only input unless repo license and model provenance are explicitly approved.",
            ],
            Blockers:
            [
                "No bundled native package or model artifact.",
                "No managed/native interop or direct 48 kHz frame-ring adapter in Zeus; audible RNNoise requires an installed RX VST plugin until a bundled runtime is approved.",
                "le9endary/RNNoise has no repo license in GitHub metadata; do not vendor its code or generated model artifacts until provenance is cleared.",
                "Speech-only training makes raw HF/IQ replacement unsafe.",
                "Needs explicit bypass for CW, digital, PureSignal, and TX monitor paths.",
            ],
            ReferenceUrls:
            [
                "https://github.com/xiph/rnnoise",
                "https://gitlab.xiph.org/xiph/rnnoise",
                "https://github.com/le9endary/RNNoise",
                "https://github.com/werman/noise-suppression-for-voice",
            ]),
        new(
            SchemaVersion: 1,
            Id: "rmnoise",
            Name: "RM Noise",
            Family: "remote-ai-speech-noise-filter-and-training-service",
            IntegrationPoint: "post-demod-rx-audio-speech-only training/reference service for RX Audio Suite receive VST/AI cleanup",
            DefaultState: "off",
            RolloutPolicy: OptInGate,
            EvaluationStage: "catalog-only-not-integrated",
            AllowedSignalPaths: ["offline-recorded-post-demod-rx-audio-speech", "rx-audio-suite-post-demod-rx-audio-speech-bakeoff"],
            ForbiddenSignalPaths: ["raw-wdsp-iq", "live-cloud-stream-by-default", "cw-or-digital-non-speech", "tx-audio", "tx-monitor", "puresignal-feedback"],
            RequiredControls:
            [
                "operator-visible-opt-in",
                "clean-bypass-fallback",
                "speech-content-gate",
                "recording-consent-gate",
                "privacy-and-terms-review-gate",
                "model-license-provenance-gate",
                "service-availability-fallback",
                "network-latency-budget-gate",
                "offline-training-only-until-runtime-approved",
                "rx-audio-suite-route",
                "no-live-cloud-stream-by-default",
                "no-raw-wdsp-iq-replacement",
                "no-tx-or-puresignal-coupling",
            ],
            FallbackPolicy: "disabled/unavailable/network-failure/training-unapproved path must bypass to current Zeus post-WDSP RX audio; non-speech/CW/digital/TX/PureSignal paths must bypass the service",
            License: "service terms, recording rights, trained-model ownership, and redistribution rights not reviewed; no RM Noise code, service API, recordings, or model artifacts are vendored",
            PackagingStatus: "service/model-training reference only; no local runtime, offline fixture export/import contract, consent workflow, or approved model package exists",
            RuntimeRisk: "high",
            LatencyRisk: "medium-high",
            RadioSafetyRisk: "high: service-trained speech filtering may erase weak non-speech HF content and live network paths can break realtime receiver safety",
            Strengths:
            [
                "Useful reference for collecting noisy/clean ham-radio speech recordings and scoring AI denoise results.",
                "Could help train or evaluate a future local RX Audio Suite model if recording consent, model provenance, and redistribution are approved.",
                "Service-style experiments can be kept offline and artifact-scored before any runtime path exists.",
            ],
            RequiredBenchmarks:
            [
                "ssb-like-speech",
                "weak-ssb-volume-parity",
                "noise-only",
                "weak-cw-carrier",
                "fading-carrier",
                "agc-level-step",
                "rx-audio-suite-bypass",
                "service-unavailable-bypass",
            ],
            RequiredEvidence:
            [
                "Must prove operator consent, privacy handling, recording retention, service terms, and trained-model rights before any upload or model use.",
                "Must preserve weak carrier/CW fixtures by deterministic bypass and must not touch raw WDSP IQ.",
                "Must beat no-NR/NR2 speech fixtures without pumping, false-open noise, or weak-speech deletion.",
                "Must prove service/network fallback behavior falls back instantly to current Zeus RX audio.",
                "Must produce reproducible offline fixture inputs/outputs, model hashes if any, and no live cloud-stream default.",
            ],
            Blockers:
            [
                "No reviewed service API, local runtime, or packaging path.",
                "No approved consent/privacy workflow for operator recordings.",
                "No reviewed service terms, trained-model ownership, or redistribution rights.",
                "No proof that RM Noise output preserves weak HF non-speech content.",
                "Network latency/availability is incompatible with default realtime RX behavior until bypass proof exists.",
            ],
            ReferenceUrls:
            [
                "https://ournetplace.com/rm-noise/",
            ]),
        new(
            SchemaVersion: 1,
            Id: "dpdfnet",
            Name: "DPDFNet",
            Family: "causal-neural-speech-enhancement",
            IntegrationPoint: "post-demod-rx-audio-speech-only through RX Audio Suite receive VST/AI insert",
            DefaultState: "off",
            RolloutPolicy: OptInGate,
            EvaluationStage: "catalog-only-not-integrated",
            AllowedSignalPaths: ["post-demod-rx-audio-speech", "rx-audio-suite-post-demod-rx-audio-speech", "offline-audio-bakeoff"],
            ForbiddenSignalPaths: ["raw-wdsp-iq", "cw-or-digital-non-speech", "tx-audio", "tx-monitor", "puresignal-feedback"],
            RequiredControls:
            [
                "operator-visible-opt-in",
                "clean-bypass-fallback",
                "speech-content-gate",
                "cpu-latency-budget-gate",
                "onnx-or-tflite-runtime-package-review",
                "48khz-frame-adapter",
                "original-filtered-blend-control",
                "model-license-provenance-gate",
                "no-raw-wdsp-iq-replacement",
                "no-tx-or-puresignal-coupling",
            ],
            FallbackPolicy: "disabled/unavailable/model-load-failure/latency-overrun path must fall back to current Zeus post-WDSP RX audio; non-speech/CW/digital/TX/PureSignal paths must bypass the model",
            License: "Apache-2.0 for repository code; ONNX/TFLite/pretrained model artifact provenance and redistribution rights must be reviewed before packaging",
            PackagingStatus: "python/onnx/tflite runtime not vendored; model package, 48 kHz streaming frame adapter, and latency guard required before any RX Audio Suite runtime path",
            RuntimeRisk: "medium-high",
            LatencyRisk: "medium",
            RadioSafetyRisk: "medium-high: speech enhancer may erase weak non-speech HF content or pull noise into speech unless gated by frontend signal evidence and explicit speech detection",
            Strengths:
            [
                "Modern causal DeepFilterNet-style speech enhancement with released 16 kHz and 48 kHz models.",
                "ONNX/TFLite paths and a streaming API make it a strong live RX Audio Suite candidate for high-quality speech-only experiments.",
                "Dual-path temporal/cross-band modeling is promising for faint SSB speech when blended behind frontend passband evidence.",
            ],
            RequiredBenchmarks:
            [
                "ssb-like-speech",
                "weak-ssb-volume-parity",
                "noise-only",
                "weak-cw-carrier-bypass",
                "fading-carrier",
                "agc-level-step",
                "rx-audio-suite-bypass",
                "realtime-latency-g2",
            ],
            RequiredEvidence:
            [
                "Must preserve weak CW/carrier/non-speech fixtures by deterministic bypass and must not touch raw WDSP IQ.",
                "Must beat no-NR/NR2 speech fixtures without pumping, false-open noise, clipped peaks, or weak-speech deletion.",
                "Must prove 48 kHz streaming latency, CPU, allocations, underrun behavior, and fallback on ANAN G2-class hardware.",
                "Must document ONNX/TFLite runtime package, code license, model artifact origin, model hash, and redistribution rights before packaging.",
            ],
            Blockers:
            [
                "No reviewed ONNX/TFLite runtime package or model artifact in Zeus.",
                "No managed streaming frame adapter, latency watchdog, or blend control in the live RX pipeline.",
                "No G2 live evidence yet proving faint HF speech improves without damaging CW, digital, or no-signal audio.",
            ],
            ReferenceUrls:
            [
                "https://github.com/ceva-ip/DPDFNet",
                "https://huggingface.co/Ceva-IP/DPDFNet",
                "https://arxiv.org/abs/2512.16420",
            ]),
        new(
            SchemaVersion: 1,
            Id: "deepfilternet",
            Name: "DeepFilterNet",
            Family: "neural-full-band-speech-enhancement",
            IntegrationPoint: "post-demod-rx-audio-speech-only",
            DefaultState: "off",
            RolloutPolicy: OptInGate,
            EvaluationStage: "catalog-only-not-integrated",
            AllowedSignalPaths: ["post-demod-rx-audio-speech"],
            ForbiddenSignalPaths: ["raw-wdsp-iq", "cw-or-digital-non-speech", "tx-audio", "tx-monitor", "puresignal-feedback"],
            RequiredControls:
            [
                "operator-visible-opt-in",
                "clean-bypass-fallback",
                "speech-content-gate",
                "cpu-latency-budget-gate",
                "no-raw-wdsp-iq-replacement",
                "no-tx-or-puresignal-coupling",
            ],
            FallbackPolicy: "disabled path must be bit-clean; model/package/runtime failure must fall back to current Zeus post-WDSP audio",
            License: "MIT OR Apache-2.0 for code; pretrained model artifact review required",
            PackagingStatus: "rust-python-stack-not-vendored; model/runtime packaging unresolved",
            RuntimeRisk: "high",
            LatencyRisk: "medium-high",
            RadioSafetyRisk: "medium-high: full-band speech enhancer must not touch raw IQ, CW, data, or TX safety paths",
            Strengths:
            [
                "Modern deep filtering approach for 48 kHz speech enhancement.",
                "Provides Rust/libDF and plugin paths that could support an offline bakeoff.",
                "Good candidate for artifact-scored speech readability experiments.",
            ],
            RequiredBenchmarks:
            [
                "ssb-like-speech",
                "noise-only",
                "strong-adjacent",
                "agc-level-step",
                "tx-voice-like-monitor-bypass",
            ],
            RequiredEvidence:
            [
                "Must show lower artifacts than current Zeus behavior on speech fixtures.",
                "Must prove weak CW/carrier bypass or neutral preservation before any live use.",
                "Must publish model license, package size, CPU, latency, and deterministic fallback evidence.",
                "Must remain opt-in and post-demod until cross-radio review proves safety.",
            ],
            Blockers:
            [
                "Model artifact licensing/package chain is not approved in Zeus.",
                "Runtime stack is heavier than WDSP and needs deployment design.",
                "No evidence yet for HF weak-signal preservation or G2 latency budget.",
            ],
            ReferenceUrls:
            [
                "https://github.com/Rikorose/DeepFilterNet",
            ]),
        new(
            SchemaVersion: 1,
            Id: "clearervoice-studio",
            Name: "ClearerVoice-Studio",
            Family: "offline-ai-speech-processing-suite",
            IntegrationPoint: "offline-recorded-post-demod-rx-audio-speech evidence and RX Audio Suite training/reference",
            DefaultState: "off",
            RolloutPolicy: OptInGate,
            EvaluationStage: "catalog-only-not-integrated",
            AllowedSignalPaths: ["offline-recorded-post-demod-rx-audio-speech", "offline-audio-bakeoff"],
            ForbiddenSignalPaths: ["raw-wdsp-iq", "live-realtime-default", "cw-or-digital-non-speech", "tx-audio", "tx-monitor", "puresignal-feedback"],
            RequiredControls:
            [
                "operator-visible-opt-in",
                "clean-bypass-fallback",
                "speech-content-gate",
                "recording-consent-gate",
                "offline-only-until-runtime-approved",
                "cpu-latency-budget-gate",
                "model-license-provenance-gate",
                "no-live-default",
                "no-raw-wdsp-iq-replacement",
                "no-tx-or-puresignal-coupling",
            ],
            FallbackPolicy: "offline-only/unavailable/model-load-failure path must leave current Zeus RX audio unchanged; live RX, non-speech/CW/digital/TX/PureSignal paths must bypass the suite",
            License: "Apache-2.0 for repository code; pretrained model artifact provenance and redistribution rights must be reviewed before packaging or fixture publication",
            PackagingStatus: "offline research/reference suite not vendored; no approved live runtime, model package, or operator recording workflow exists",
            RuntimeRisk: "high",
            LatencyRisk: "high",
            RadioSafetyRisk: "high: broad speech enhancement/separation/super-resolution models can alter HF content and are not safe as live receiver defaults",
            Strengths:
            [
                "Broad AI speech toolkit covering enhancement, separation, super-resolution, and target-speaker extraction.",
                "Useful for offline fixture restoration experiments and objective speech-quality scoring against no NR/NR2.",
                "Can help train or compare future local RX Audio Suite ideas without becoming a live default path.",
            ],
            RequiredBenchmarks:
            [
                "offline-ssb-like-speech",
                "weak-ssb-volume-parity",
                "noise-only",
                "weak-cw-carrier-bypass",
                "artifact-scoring",
                "offline-bypass",
            ],
            RequiredEvidence:
            [
                "Must preserve weak carrier/CW fixtures by deterministic bypass and must not touch raw WDSP IQ.",
                "Must beat no-NR/NR2 offline speech fixtures without pumping, over-smoothing, or weak-speech deletion.",
                "Must prove operator recording consent, dataset retention, model provenance, package size, and reproducible offline outputs.",
                "Must remain offline/reference-only until live latency, CPU, and cross-radio safety are separately approved.",
            ],
            Blockers:
            [
                "No reviewed pretrained model package or redistribution plan.",
                "No live streaming adapter or G2 latency evidence.",
                "No approved consent/privacy workflow for captured operator recordings.",
                "Offline restoration behavior may not preserve realtime HF receiver semantics.",
            ],
            ReferenceUrls:
            [
                "https://github.com/modelscope/ClearerVoice-Studio",
            ]),
        new(
            SchemaVersion: 1,
            Id: "speexdsp",
            Name: "SpeexDSP",
            Family: "classic-audio-dsp",
            IntegrationPoint: "post-demod-rx-audio-baseline-and-utilities",
            DefaultState: "off",
            RolloutPolicy: OptInGate,
            EvaluationStage: "catalog-only-not-integrated",
            AllowedSignalPaths: ["post-demod-rx-audio-speech", "offline-audio-bakeoff"],
            ForbiddenSignalPaths: ["raw-wdsp-iq", "speex-agc-by-default", "aec-by-default", "tx-audio", "puresignal-feedback"],
            RequiredControls:
            [
                "operator-visible-opt-in",
                "clean-bypass-fallback",
                "feature-level-enable-list",
                "speex-agc-disabled",
                "no-raw-wdsp-iq-replacement",
                "no-tx-or-puresignal-coupling",
            ],
            FallbackPolicy: "disabled path must be bit-clean; unavailable native package or unsafe feature selection must fall back to current Zeus audio",
            License: "BSD-style permissive license",
            PackagingStatus: "native-c-library-not-vendored; lower packaging risk than neural candidates",
            RuntimeRisk: "low-medium",
            LatencyRisk: "low",
            RadioSafetyRisk: "medium: AGC/noise suppressor must not fight WDSP AGC or radio squelch",
            Strengths:
            [
                "Mature C DSP library with preprocessing, resampling, echo/noise-related utilities.",
                "Useful as a non-neural baseline for post-demod speech/audio comparisons.",
                "Lower CPU and model-distribution risk than neural candidates.",
            ],
            RequiredBenchmarks:
            [
                "ssb-like-speech",
                "noise-only",
                "agc-level-step",
                "squelch-transition",
            ],
            RequiredEvidence:
            [
                "Must prove no pumping and no double-AGC behavior.",
                "Must prove meter correctness and squelch transition stability when enabled.",
                "Must beat or explain parity against current Zeus post-WDSP audio policy.",
            ],
            Blockers:
            [
                "No vendored package or managed interop yet.",
                "Need per-feature gating so AGC/AEC paths cannot be accidentally enabled.",
            ],
            ReferenceUrls:
            [
                "https://github.com/xiph/speexdsp",
                "https://gitlab.xiph.org/xiph/speexdsp",
            ]),
        new(
            SchemaVersion: 1,
            Id: "webrtc-apm",
            Name: "WebRTC Audio Processing",
            Family: "communications-audio-processing",
            IntegrationPoint: "post-demod-rx-audio-feature-gated-ns-vad-only",
            DefaultState: "off",
            RolloutPolicy: OptInGate,
            EvaluationStage: "catalog-only-not-integrated",
            AllowedSignalPaths: ["post-demod-rx-audio-speech", "offline-audio-bakeoff"],
            ForbiddenSignalPaths: ["raw-wdsp-iq", "aec-by-default", "agc-by-default", "high-pass-by-default", "tx-audio", "tx-monitor", "puresignal-feedback"],
            RequiredControls:
            [
                "operator-visible-opt-in",
                "clean-bypass-fallback",
                "ns-vad-only-enable-list",
                "webrtc-aec-disabled",
                "webrtc-agc-disabled",
                "webrtc-high-pass-disabled",
                "no-raw-wdsp-iq-replacement",
                "no-tx-or-puresignal-coupling",
            ],
            FallbackPolicy: "disabled path must be bit-clean; unavailable package or unsafe module state must fall back to current Zeus audio",
            License: "BSD-3-Clause lineage; package-specific license review required",
            PackagingStatus: "standalone packages exist; Zeus runtime package and ABI strategy unresolved",
            RuntimeRisk: "medium-high",
            LatencyRisk: "medium",
            RadioSafetyRisk: "high: AEC/AGC/high-pass defaults can corrupt receiver gain, meters, and weak-signal audio",
            Strengths:
            [
                "Widely deployed realtime audio processing module with noise suppression and VAD features.",
                "Standalone packaging exists in Linux/MSYS2 ecosystems, reducing initial spike risk.",
                "Useful as a feature-gated reference for speech-only post-demod comparisons.",
            ],
            RequiredBenchmarks:
            [
                "ssb-like-speech",
                "noise-only",
                "agc-level-step",
                "squelch-transition",
                "tx-voice-like-monitor-bypass",
            ],
            RequiredEvidence:
            [
                "Must start with NS/VAD-only experiments; AEC, AGC, and high-pass must stay disabled unless separately approved.",
                "Must prove no meter drift, no squelch false-open behavior, and no TX/PureSignal path coupling.",
                "Must document package ABI and platform support before integration.",
            ],
            Blockers:
            [
                "Default communications-processing assumptions conflict with radio receiver gain staging.",
                "ABI/package strategy is unresolved for Windows, macOS, and Linux builds.",
                "No G2 benchmark or on-air evidence yet.",
            ],
            ReferenceUrls:
            [
                "https://freedesktop.org/software/pulseaudio/webrtc-audio-processing/",
                "https://gitlab.freedesktop.org/pulseaudio/webrtc-audio-processing",
            ]),
    ];
}
