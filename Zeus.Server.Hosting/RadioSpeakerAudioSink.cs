// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using Zeus.Contracts;
using Zeus.Protocol1;

namespace Zeus.Server;

/// <summary>
/// Feeds demodulated RX audio into the Protocol-1 <see cref="RxAudioRing"/> so a
/// connected P1 radio's onboard codec drives its speaker / headphone / line-out
/// jacks. The ring is drained by <c>Protocol1Client</c>'s EP2 TX loop and packed
/// into the L/R slots of the frame it already sends continuously — no extra
/// socket, no platform gate. Works in every host mode and on every OS.
///
/// This is the Protocol-1 counterpart to <see cref="SaturnSpeakerAudioSink"/>,
/// which owns the Protocol-2 (Saturn/G2 appliance) speaker path. The two are
/// mutually exclusive at runtime: a P1 radio has no Saturn endpoint, and a P2
/// radio has no <c>RxAudioRing</c> consumer. This sink never touches P2.
///
/// Gating (all must hold, re-checked per frame so a mid-session toggle or MOX
/// transition takes effect immediately):
///   • operator opted in (RadioSpeakerSettingsStore.Enabled, default off)
///   • a Protocol-1 client is connected (so the ring is actually drained)
///   • the board has an onboard codec and is not the codec-less HL2
///   • not transmitting (don't push TX-monitor audio to the radio speaker)
///   • the frame is the expected 48 kHz mono RX audio
/// When any check fails the frame is dropped and the ring is left to drain to
/// silence, so the wire reverts to byte-identical "no RX audio" behaviour.
/// </summary>
public sealed class RadioSpeakerAudioSink : IRxAudioSink, IDisposable
{
    private const uint ExpectedSampleRateHz = 48_000;

    private readonly RadioService _radio;
    private readonly RxAudioRing _ring;
    private readonly RadioSpeakerSettingsStore _settings;

    public RadioSpeakerAudioSink(RadioService radio, RxAudioRing ring, RadioSpeakerSettingsStore settings)
    {
        _radio = radio;
        _ring = ring;
        _settings = settings;
        // Drop any buffered tail when the operator turns the feature off so a
        // later re-enable starts clean rather than replaying stale audio.
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>True when this sink would currently forward RX audio to the
    /// radio codec for the connected board. Surfaced via /api/radio/speaker-output
    /// so the UI can show the toggle only where it does something.</summary>
    public bool AvailableForConnectedBoard()
    {
        if (!_radio.IsProtocol1Active) return false;
        var board = _radio.ConnectedBoardKind;
        if (board == HpsdrBoardKind.HermesLite2) return false;
        return BoardCapabilitiesTable.For(board, _radio.EffectiveOrionMkIIVariant).HasOnboardCodec;
    }

    public void Publish(in AudioFrame frame)
    {
        if (frame.Channels != 1 || frame.SampleRateHz != ExpectedSampleRateHz) return;
        if (!_settings.Enabled) return;
        if (_radio.IsMox)
        {
            // While transmitting, the EP2 L/R slots carry no audio (WriteUsbFrame
            // only fills them during RX) and the TX-monitor frames arriving here
            // are not for the radio speaker. Drop the buffer so unkey resumes from
            // live RX rather than replaying the pre-key tail still in the ring.
            _ring.Clear();
            return;
        }
        if (!AvailableForConnectedBoard()) return;

        _ring.Write(frame.Samples.Span);
    }

    private void OnSettingsChanged()
    {
        if (!_settings.Enabled) _ring.Clear();
    }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;
}
