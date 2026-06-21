namespace Zeus.Server;

/// <summary>
/// A consumer of <see cref="StreamingHub"/>'s broadcast fan-out. Implemented by
/// the WebSocket <c>ClientSession</c> and by the remote-access WebRTC sink
/// (<c>RemoteFrameSink</c>), so a remote session rides the exact same fan-out as
/// <c>/ws</c> clients — the Broadcast methods are unchanged, and when no remote
/// sink is attached the <c>/ws</c> path behaves identically to before.
///
/// Only the two members the broadcast loops touch are abstracted.
/// </summary>
internal interface IClientSink
{
    /// <summary>Whether this consumer wants display frames (used to skip the heavy serialize).</summary>
    bool WantsDisplay { get; }

    /// <summary>Enqueue a serialized frame. Must be non-blocking (callers run on the DSP thread).</summary>
    bool TryEnqueue(byte[] payload);
}
