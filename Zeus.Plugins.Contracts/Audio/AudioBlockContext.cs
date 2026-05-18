namespace Zeus.Plugins.Contracts.Audio;

/// <summary>
/// Metadata passed to <see cref="Extensions.IAudioPlugin.Process"/>
/// alongside each audio block.
/// </summary>
public readonly ref struct AudioBlockContext
{
    public AudioBlockContext(int sampleRate, int channels, int frames, long sampleTime, bool mox)
    {
        SampleRate = sampleRate;
        Channels = channels;
        Frames = frames;
        SampleTime = sampleTime;
        Mox = mox;
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
}

/// <summary>
/// Sample-rate / channel / block-size negotiation values. <c>BlockSize</c>
/// is a sizing hint — the host may call <c>Process</c> with any block
/// length up to and including the declared value. Plugins should
/// iterate <c>input.Length</c> rather than caching the declared
/// <c>BlockSize</c> as a fixed-array dimension.
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
