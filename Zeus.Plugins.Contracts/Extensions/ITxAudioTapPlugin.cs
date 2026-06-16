using Zeus.Plugins.Contracts.Audio;

namespace Zeus.Plugins.Contracts.Extensions;

/// <summary>
/// Optional extension for plugins that OBSERVE the transmit-side audio without
/// being in the realtime insert chain — a read-only, non-destructive tap (a
/// recorder, a transmit-audio analyser). Companion to
/// <see cref="IRxAudioTapPlugin"/> for the TX direction.
///
/// Two sources are delivered, so a plugin can offer the operator a choice:
/// <list type="bullet">
/// <item><see cref="OnTxMicAudio"/> — the RAW microphone (pre-processing),
/// captured whether or not the operator is keyed.</item>
/// <item><see cref="OnTxAirAudio"/> — the PROCESSED transmit audio (post
/// EQ / compressor / leveler / CFC), demodulated back from the TX IQ — i.e.
/// what actually goes on the air. Only flows while the host TX monitor /
/// preview path is running.</item>
/// </list>
///
/// Both callbacks run on the audio thread and carry mono float32 at the host
/// rate. Exactly like <see cref="IAudioPlugin.Process"/> they MUST NOT
/// allocate, lock, perform IO, or block — copy into a pre-allocated buffer and
/// flush from a background thread.
/// </summary>
public interface ITxAudioTapPlugin
{
    /// <summary>Raw microphone audio (pre-processing). Read-only; realtime.</summary>
    void OnTxMicAudio(ReadOnlySpan<float> samples, AudioBlockContext ctx);

    /// <summary>Processed transmit audio — what goes on the air (post chain,
    /// demodulated from TX IQ). Only flows while the TX monitor is active.
    /// Read-only; realtime.</summary>
    void OnTxAirAudio(ReadOnlySpan<float> samples, AudioBlockContext ctx);
}
