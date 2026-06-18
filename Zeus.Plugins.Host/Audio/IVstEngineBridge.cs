using Zeus.Plugins.Contracts.Audio;

namespace Zeus.Plugins.Host.Audio;

/// <summary>
/// The realtime audio-plane surface of the VST engine bridge that
/// <see cref="VstEngineController"/> drives. Extracted from
/// <see cref="VstEngineBridge"/> so the controller's self-heal supervisor can be
/// unit-tested cross-platform (the real bridge is Windows-only named shared
/// memory) against a fake that simulates ready / degraded / passthrough.
/// </summary>
internal interface IVstEngineBridge : IDisposable
{
    /// <summary>Gate the realtime tap reads each block; false ⇒ pure passthrough.</summary>
    bool EngineReady { get; set; }

    /// <summary>Bounded per-block wait ceiling (ms).</summary>
    int WaitBudgetMs { get; set; }

    /// <summary>Blocks that fell through to passthrough (timeout / stale-seq).</summary>
    long DegradedBlocks { get; }

    /// <summary>Realtime tap. Returns true only when output came from the engine.</summary>
    bool Process(ReadOnlySpan<float> input, Span<float> output, AudioBlockContext ctx);

    /// <summary>
    /// Drain any stale handshake state left by a crashed engine and disarm the
    /// gate, so a freshly relaunched engine starts from a clean sequence. Safe to
    /// call only while the realtime tap is disarmed (engine down).
    /// </summary>
    void ResetForRelaunch();
}
