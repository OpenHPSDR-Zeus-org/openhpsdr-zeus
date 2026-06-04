namespace Zeus.Plugins.Contracts.Audio;

/// <summary>
/// Lets a plugin play audio back through the host — either as a local monitor
/// (mixed into the operator's RX audio, like the built-in recorder's local
/// playback) or injected into the TX chain to go on the air. Surfaced on
/// <see cref="IPluginContext.Playback"/>; null when the host has no playback
/// path (e.g. browser mode has no local-monitor sink).
///
/// <para>This sink NEVER keys the radio. On-air playback only reaches the air
/// while the operator has MOX engaged — exactly the built-in recorder's
/// contract. A plugin should branch on <see cref="IsMoxOn"/> to choose between
/// <see cref="PlayOnAir"/> and <see cref="PlayLocal"/>.</para>
/// </summary>
public interface IAudioPlaybackSink
{
    /// <summary>True while the operator has the radio keyed (MOX/PTT).</summary>
    bool IsMoxOn { get; }

    /// <summary>
    /// Begin a local-monitor playback session: ensures subsequent
    /// <see cref="PlayLocal"/> samples are actually audible by enabling the
    /// host's local-monitor (audition) path for the duration. Returns a token
    /// whose <see cref="IDisposable.Dispose"/> restores the prior state. Always
    /// wrap a local-playback run in <c>using (sink.BeginLocalMonitor())</c>;
    /// the token is a harmless no-op when local monitoring is unavailable.
    /// </summary>
    IDisposable BeginLocalMonitor();

    /// <summary>
    /// Mix a block of mono float32 samples into the operator's local monitor
    /// (RX audio bus). Returns <c>false</c> when the host's monitor buffer is
    /// momentarily full — the caller should retry the SAME block rather than
    /// drop it, so the host's RX clock paces playback and it stays glitch-free.
    /// Always <c>true</c> when local monitoring is unavailable (samples sink
    /// harmlessly). Only audible inside a <see cref="BeginLocalMonitor"/> session.
    /// </summary>
    bool PlayLocal(ReadOnlySpan<float> samples, int sampleRate);

    /// <summary>Approximate samples still buffered for local monitor playback —
    /// lets a player wait out the tail before reporting playback finished.</summary>
    long LocalMonitorBacklog { get; }

    /// <summary>
    /// Inject a block of mono float32 samples into the TX audio chain so it is
    /// transmitted (processed by the normal TX path like live speech). Only
    /// reaches the air while <see cref="IsMoxOn"/>; the host does not key.
    /// Samples must be 48 kHz mono; the host packetises into TX mic blocks.
    /// </summary>
    void PlayOnAir(ReadOnlySpan<float> samples, int sampleRate);
}
