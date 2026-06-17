// SPDX-License-Identifier: GPL-2.0-or-later
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// On-demand <see cref="IRxAudioSink"/> for desktop/native-audio mode. The
/// host already plays RX audio through its native sink, so we normally send
/// nothing over the websocket. But a browser-side consumer (the DeepCW
/// decoder panel running in the Photino webview, which is itself a WS client)
/// needs the PCM stream — it raises <c>MsgType.AudioStreamRequest</c> while
/// active. While at least one client wants audio, this sink fans the same
/// single stream out via <see cref="StreamingHub.Broadcast(in AudioFrame)"/>;
/// otherwise it's inert (one volatile read per DSP tick, the same cost model
/// as the hub's empty-clients short-circuit).
///
/// Registered ONLY when <c>ShareOverLan</c> is off — with ShareOverLan on, an
/// ungated <see cref="WebSocketAudioSink"/> already broadcasts to every WS
/// client (including the local webview), so adding this gated sink too would
/// double the 0x02 frames.
/// </summary>
internal sealed class GatedWebSocketAudioSink : IRxAudioSink
{
    private readonly StreamingHub _hub;

    public GatedWebSocketAudioSink(StreamingHub hub) => _hub = hub;

    public void Publish(in AudioFrame frame)
    {
        if (!_hub.AudioStreamRequested) return;
        _hub.Broadcast(in frame);
    }
}
