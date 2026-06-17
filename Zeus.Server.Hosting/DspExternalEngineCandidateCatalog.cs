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
                "no-raw-wdsp-iq-replacement",
                "no-tx-or-puresignal-coupling",
            ],
            FallbackPolicy: "disabled path must be bit-clean; non-speech/CW/digital/TX/PureSignal paths must bypass the engine",
            License: "BSD-3-Clause",
            PackagingStatus: "native-c-library-not-vendored; model packaging and 48 kHz frame adapter required",
            RuntimeRisk: "medium",
            LatencyRisk: "low-medium",
            RadioSafetyRisk: "medium: speech-trained model may damage weak CW, digital, or non-speech HF content",
            Strengths:
            [
                "Small C runtime with established realtime speech-noise suppression use.",
                "Useful as a low-footprint post-demod speech benchmark candidate.",
                "Already modeled as gated NR3 lineage in native WDSP build flags.",
            ],
            RequiredBenchmarks:
            [
                "ssb-like-speech",
                "noise-only",
                "weak-cw-carrier",
                "fading-carrier",
                "agc-level-step",
            ],
            RequiredEvidence:
            [
                "Must preserve weak carrier/CW fixtures when bypassed or speech-gated.",
                "Must beat current Zeus post-demod audio on speech fixtures without pumping.",
                "Must prove CPU, allocation, and latency bounds on G2-class hardware.",
            ],
            Blockers:
            [
                "No bundled native package or model artifact.",
                "Speech-only training makes raw HF/IQ replacement unsafe.",
                "Needs explicit bypass for CW, digital, PureSignal, and TX monitor paths.",
            ],
            ReferenceUrls:
            [
                "https://github.com/xiph/rnnoise",
                "https://gitlab.xiph.org/xiph/rnnoise",
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
