using Zeus.Plugins.Contracts.Audio;

namespace Zeus.Plugins.Contracts.Extensions;

/// <summary>
/// Optional extension for plugins that process audio blocks. Plugins
/// that only want to host a VST3 file should NOT implement this — they
/// declare <c>audio.vst3Path</c> in their manifest and the host wraps
/// the VST in a synthetic <c>IAudioPlugin</c>.
///
/// <see cref="Process"/> runs on the realtime audio thread. It MUST NOT
/// allocate, lock, perform IO, or call any blocking API.
/// </summary>
public interface IAudioPlugin
{
    /// <summary>Display string surfaced in chain UI (e.g. "1750 Hz tone").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Sample rate / channel count / block size the plugin needs.
    /// The host honours these and refuses to load the plugin if they
    /// cannot be satisfied by the current TX/RX path.
    /// </summary>
    AudioPluginRequirements Requirements { get; }

    /// <summary>
    /// Called once on the realtime thread before the first
    /// <see cref="Process"/> call. May allocate. Honour
    /// <paramref name="ct"/>; the host applies a 1-second timeout.
    /// </summary>
    Task InitializeAudioAsync(IAudioHost host, CancellationToken ct);

    /// <summary>
    /// Realtime audio processing. <paramref name="input"/> and
    /// <paramref name="output"/> are non-overlapping spans of the current
    /// block length (= <c>ctx.Frames * ctx.Channels</c>, planar). The frame
    /// count can vary by route; allocate for <see
    /// cref="IAudioHost.CurrentBlockSize"/> during initialization and
    /// iterate the span length here.
    /// In-place processing (input.CopyTo(output) then mutate output)
    /// is acceptable. Bypassed slots SHOULD <c>input.CopyTo(output)</c>
    /// rather than skipping the call — the host handles
    /// chain-disabled short-circuit.
    /// </summary>
    void Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx);

    /// <summary>Called once on the realtime thread after the last
    /// <see cref="Process"/>. May allocate / dispose.</summary>
    Task ShutdownAudioAsync(CancellationToken ct);
}
