using System.Threading.Channels;

namespace Zeus.Server.Hosting.Remote;

/// <summary>
/// Bridges <see cref="Zeus.Server.StreamingHub"/>'s broadcast frames onto a
/// remote session's WebRTC frames channel. Registered only after the SPAKE2+
/// password unlocks (ADR-0008), and the actual send is gated again by the
/// session's egress guard, so a frame can never leave a locked session.
///
/// A bounded drop-oldest queue drained on a background task keeps the DSP thread
/// (which calls <see cref="TryEnqueue"/> via the hub fan-out) from ever blocking
/// on the data channel — mirroring the WebSocket ClientSession's backpressure.
/// </summary>
internal sealed class RemoteFrameSink : Zeus.Server.IClientSink, IDisposable
{
    // Small bound: stale spectrum/meter frames are worthless, so drop-oldest.
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly Func<byte[], bool> _send;
    private readonly CancellationTokenSource _cts = new();

    /// <param name="send">The session's gated send (returns false while locked/closed).</param>
    public RemoteFrameSink(Func<byte[], bool> send)
    {
        _send = send;
        _ = DrainAsync();
    }

    public bool WantsDisplay => true;

    public bool TryEnqueue(byte[] payload) => _queue.Writer.TryWrite(payload);

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var payload in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                _send(payload);
        }
        catch (OperationCanceledException) { /* disposed */ }
        catch { /* session torn down underneath us */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
    }
}
