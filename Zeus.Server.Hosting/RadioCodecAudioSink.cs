// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server;

/// <summary>
/// <see cref="IRxAudioSink"/> that routes demodulated RX audio (mono float32
/// 48 kHz) back to the radio's on-board audio codec via EP2 outbound L/R
/// bytes. Operators on Hermes / ANAN-class / OrionMkII boards plug
/// headphones into the radio's front-panel jack and hear receive audio
/// without any host-audio plumbing. Issue #426.
///
/// Joins the existing <c>IRxAudioSink</c> fan-out collection consumed by
/// <see cref="DspPipelineService"/> — sits alongside <see cref="WebSocketAudioSink"/>
/// / <see cref="NativeAudioSink"/> so the operator can hear audio in the
/// browser / desktop speakers AND on the radio's headphone jack
/// simultaneously.
///
/// Gating is by-board: this sink only writes to the ring when the
/// connected radio actually has a codec (i.e. <see cref="BoardCapabilities.HasOnboardCodec"/>
/// is true). HermesLite2 has no codec — the wire bytes are ignored by the
/// firmware either way, so skipping the work saves a few cycles. The
/// no-board (synthetic / pre-connect) case also short-circuits via
/// <c>UnknownDefaults</c>.
/// </summary>
internal sealed class RadioCodecAudioSink : IRxAudioSink
{
    private const int FrameRateHz = 48_000;

    private readonly RxCodecAudioRing _ring;
    private readonly RadioService _radio;
    private readonly ILogger<RadioCodecAudioSink> _log;

    public RadioCodecAudioSink(
        RxCodecAudioRing ring,
        RadioService radio,
        ILogger<RadioCodecAudioSink> log)
    {
        _ring = ring;
        _radio = radio;
        _log = log;
    }

    public void Publish(in AudioFrame frame)
    {
        // Skip the cost when the connected board has no codec to receive
        // these bytes. ConnectedBoardKind defaults to Unknown pre-connect,
        // and HL2 has HasOnboardCodec=false — both short-circuit here.
        var caps = BoardCapabilitiesTable.For(_radio.ConnectedBoardKind, _radio.EffectiveOrionMkIIVariant);
        if (!caps.HasOnboardCodec) return;

        // The DSP tick produces mono float32 @ 48 kHz. Anything else (e.g.
        // a future RX2 stereo path) is dropped silently — the codec is mono
        // anyway, so the right answer is for whoever owns that path to
        // downmix before publishing.
        if (frame.Channels != 1 || frame.SampleRateHz != FrameRateHz) return;

        _ring.Write(frame.Samples.Span);
    }
}
