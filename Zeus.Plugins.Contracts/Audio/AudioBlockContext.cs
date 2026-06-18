namespace Zeus.Plugins.Contracts.Audio;

/// <summary>
/// Metadata passed to <see cref="Extensions.IAudioPlugin.Process"/>
/// alongside each audio block.
/// </summary>
public readonly ref struct AudioBlockContext
{
    public AudioBlockContext(int sampleRate, int channels, int frames, long sampleTime, bool mox, int receiver = 0)
    {
        SampleRate = sampleRate;
        Channels = channels;
        Frames = frames;
        SampleTime = sampleTime;
        Mox = mox;
        Receiver = receiver;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int Frames { get; }

    /// <summary>Monotonic sample-frame counter from session start.</summary>
    public long SampleTime { get; }

    /// <summary>
    /// True if this block is part of the live TX audio path (the samples
    /// will be modulated and put on the air); false if this is a preview
    /// run for telemetry only — e.g. the host is calling Process so the
    /// plugin's IN/OUT/GR meter fields update from live mic input while
    /// MOX is off and nothing is being transmitted. Plugins that gate
    /// state-mutating behaviour on transmit (e.g. only writing to a
    /// shared resource while keyed) should branch on this field instead
    /// of assuming every Process call is on-air.
    /// </summary>
    public bool Mox { get; }

    /// <summary>
    /// Zero-based hardware receiver index this block came from: 0 = main RX,
    /// 1+ = SubRX / diversity / additional DDCs. Lets an <see
    /// cref="Extensions.IRxAudioTapPlugin"/> filter to a single receiver — a
    /// net monitor that only wants the main RX drops blocks where
    /// <c>Receiver != 0</c>, exactly as in-core RX consumers do. Meaningful
    /// only on the RX path; always 0 on TX mic/air blocks (no receiver).
    /// </summary>
    public int Receiver { get; }
}

/// <summary>
/// Sample-rate / channel / block-size negotiation values. <c>BlockSize</c>
/// is a compatibility hint from the plugin; the host reports the actual
/// route ceiling through <see cref="IAudioHost.CurrentBlockSize"/> during
/// <c>InitializeAudioAsync</c>. Plugins should preallocate from the host
/// ceiling and iterate <c>input.Length</c> / <c>ctx.Frames</c> rather than
/// caching <c>BlockSize</c> as a fixed-array dimension.
/// </summary>
public sealed record AudioPluginRequirements(
    int SampleRate,
    int Channels,
    int BlockSize);

/// <summary>What an <see cref="Extensions.IAudioPlugin"/> sees of its host.</summary>
public interface IAudioHost
{
    int CurrentSampleRate { get; }
    int CurrentChannels { get; }
    int CurrentBlockSize { get; }

    /// <summary>
    /// Where in the chain this plugin sits, copied from the manifest's
    /// <c>audio.slot</c>. Useful for plugins that branch on TX vs RX.
    /// </summary>
    string Slot { get; }
}
