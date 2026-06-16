// SPDX-License-Identifier: GPL-2.0-or-later
namespace Zeus.Server;

/// <summary>
/// Browser-mode / fallback <see cref="IPreviewAudioSink"/> that swallows
/// every publish call. Preview is a desktop-mode-only feature in v1
/// because the browser-side audio path mixes RX in the AudioWorklet
/// rather than the server. When browser parity ships in a later phase,
/// this will be replaced with a streaming implementation that publishes
/// a separate preview frame type over the SignalR hub for the worklet
/// to mix client-side. Until then this no-op keeps DI happy in browser
/// mode without forcing call sites to branch.
/// </summary>
public sealed class NoOpPreviewAudioSink : IPreviewAudioSink
{
    public bool IsEnabled => false;
    public void SetEnabled(bool enabled) { /* no-op — preview not available in this host mode */ }
    public void PublishPreview(ReadOnlySpan<float> monoSamples, int sampleRate) { /* no-op */ }
}
