// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server;

/// <summary>
/// Optional audition sink for the desktop-mode pre-MOX plugin-chain
/// audition feature. <see cref="AudioPluginBridge"/> pushes the
/// processed mic samples here (the output of the audio plugin chain)
/// when the operator has toggled "Audition" on in the Audio Suite
/// window, so they can hear what the chain is doing to their voice
/// without keying the radio. Implementations are expected to mix the
/// audition stream with the operator's existing RX audio so both share
/// the same playback path — the operator uses the regular RX mute /
/// volume controls to manage levels.
///
/// <para>Desktop mode binds this to <see cref="NativeAudioSink"/>;
/// browser mode binds <see cref="NoOpAuditionAudioSink"/> (audition
/// is desktop-only in v1).</para>
///
/// <para><see cref="PublishAudition"/> runs on the miniaudio capture
/// worker thread (the same thread that produces the mic samples being
/// previewed). Implementations must not block or allocate beyond what
/// the realtime audio path can absorb.</para>
/// </summary>
public interface IAuditionAudioSink
{
    /// <summary>True if the operator has audition turned on.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Turn audition on or off. When turning off, the implementation
    /// SHOULD drain any buffered audition samples so re-enabling
    /// doesn't replay the tail of the prior session.
    /// </summary>
    void SetEnabled(bool enabled);

    /// <summary>
    /// Publish a block of mono float32 samples produced by the audio
    /// plugin chain. Sample rate is supplied so implementations can
    /// assert format expectations (the canonical rate is 48 kHz from
    /// <see cref="NativeMicCapture"/>). When <see cref="IsEnabled"/>
    /// is false this call is a no-op.
    /// </summary>
    void PublishAudition(ReadOnlySpan<float> monoSamples, int sampleRate);
}
