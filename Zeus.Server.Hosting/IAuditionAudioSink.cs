// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server;

/// <summary>
/// Optional local mono playback side-channel. Desktop implementations mix
/// published samples with the operator's existing RX audio so both share the
/// same playback path — the operator uses the regular RX mute / volume
/// controls to manage levels. Audio Suite audition itself is not published
/// here; it is an alias for the full WDSP TX-monitor path so the monitor
/// includes the complete TXA chain.
///
/// <para>Desktop mode binds this to <see cref="NativeAudioSink"/>;
/// browser mode binds <see cref="NoOpAuditionAudioSink"/> because this local
/// playback side-channel is only meaningful for native audio output.</para>
///
/// <para><see cref="PublishAudition"/> runs on the miniaudio capture
/// worker thread for native callers. Implementations must not block or
/// allocate beyond what the realtime audio path can absorb.</para>
/// </summary>
public interface IAuditionAudioSink
{
    /// <summary>True if the local playback side-channel is enabled.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Turn the local side-channel on or off. When turning off, the
    /// implementation SHOULD drain any buffered samples so re-enabling
    /// doesn't replay the tail of the prior session.
    /// </summary>
    void SetEnabled(bool enabled);

    /// <summary>
    /// Publish a block of mono float32 samples to the local side-channel.
    /// Sample rate is supplied so implementations can assert format
    /// expectations (the canonical rate is 48 kHz from
    /// <see cref="NativeMicCapture"/>). When <see cref="IsEnabled"/> is false
    /// this call is a no-op.
    /// </summary>
    void PublishAudition(ReadOnlySpan<float> monoSamples, int sampleRate);
}
