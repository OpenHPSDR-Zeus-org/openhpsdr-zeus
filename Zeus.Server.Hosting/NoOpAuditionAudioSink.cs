// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server;

/// <summary>
/// Browser-mode / fallback <see cref="IAuditionAudioSink"/> that swallows
/// every publish call. Audition is a desktop-mode-only feature in v1
/// because the browser-side audio path mixes RX in the AudioWorklet
/// rather than the server. When browser parity ships in a later phase,
/// this will be replaced with a streaming implementation that publishes
/// a separate audition frame type over the SignalR hub for the worklet
/// to mix client-side. Until then this no-op keeps DI happy in browser
/// mode without forcing call sites to branch.
/// </summary>
public sealed class NoOpAuditionAudioSink : IAuditionAudioSink
{
    public bool IsEnabled => false;
    public void SetEnabled(bool enabled) { /* no-op — audition not available in this host mode */ }
    public void PublishAudition(ReadOnlySpan<float> monoSamples, int sampleRate) { /* no-op */ }
}
