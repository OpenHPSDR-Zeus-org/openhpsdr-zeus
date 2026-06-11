using Zeus.Plugins.Contracts.Audio;

namespace Zeus.Plugins.Contracts.Extensions;

/// <summary>
/// Optional extension for plugins that OBSERVE demodulated RX audio without
/// being in the realtime insert chain — a read-only, non-destructive tap
/// (e.g. a recorder, a decoder, an external meter). Unlike
/// <see cref="IAudioPlugin"/> a tap produces no output and cannot alter what
/// the operator hears; the host hands it a copy of each RX block after demod.
///
/// <para><see cref="OnRxAudio"/> runs on the RX audio thread. Exactly like
/// <see cref="IAudioPlugin.Process"/> it MUST NOT allocate, lock, perform IO,
/// or call any blocking API. A tap that needs to do IO — a recorder writing a
/// WAV file — must copy the samples into a pre-allocated lock-free buffer here
/// and flush them from its own background thread.</para>
///
/// A plugin may implement this alongside <see cref="IAudioPlugin"/>, but the
/// two are independent: the tap sees RX audio regardless of whether the plugin
/// also occupies an insert slot.
/// </summary>
public interface IRxAudioTapPlugin
{
    /// <summary>
    /// Sample rate / channel count the tap expects. The host delivers RX audio
    /// as 48 kHz mono float32; a tap that needs something else should refuse in
    /// <see cref="InitializeTapAsync"/>.
    /// </summary>
    AudioPluginRequirements Requirements { get; }

    /// <summary>
    /// Called once on the control thread before the first <see cref="OnRxAudio"/>.
    /// May allocate / open resources. Honour <paramref name="ct"/>; the host
    /// applies a 1-second timeout.
    /// </summary>
    Task InitializeTapAsync(IAudioHost host, CancellationToken ct);

    /// <summary>
    /// Read-only RX audio block (mono float32 at the host RX rate).
    /// <paramref name="samples"/> is valid only for the duration of the call —
    /// copy what you need. Realtime contract: no alloc, no lock, no IO, no throw.
    /// </summary>
    void OnRxAudio(ReadOnlySpan<float> samples, AudioBlockContext ctx);

    /// <summary>Called once after the last <see cref="OnRxAudio"/>. May
    /// allocate / dispose / flush.</summary>
    Task ShutdownTapAsync(CancellationToken ct);
}
