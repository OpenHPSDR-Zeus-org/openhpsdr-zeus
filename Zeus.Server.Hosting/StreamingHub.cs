// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA), and contributors.
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU General Public License as published by the
// Free Software Foundation, either version 2 of the License, or (at your
// option) any later version. See the LICENSE file at the root of this
// repository for the full text, or https://www.gnu.org/licenses/.
//
// Zeus is an independent reimplementation in .NET — not a fork. Its
// Protocol-1 / Protocol-2 framing, WDSP integration, meter pipelines, and
// TX behaviour were informed by studying the Thetis project
// (https://github.com/ramdor/Thetis), the authoritative reference
// implementation in the OpenHPSDR ecosystem. Zeus gratefully acknowledges
// the Thetis contributors whose work made this possible:
//
//   Richard Samphire (MW0LGE), Warren Pratt (NR0V),
//   Laurence Barker (G8NJJ),   Rick Koch (N1GP),
//   Bryan Rambo (W4WMT),       Chris Codella (W2PA),
//   Doug Wigley (W5WC),        FlexRadio Systems,
//   Richard Allen (W5SD),      Joe Torrey (WD5Y),
//   Andrew Mansfield (M0YGG),  Reid Campbell (MI0BOT),
//   Sigi Jetzlsperger (DH1KLM).
//
// Thetis itself continues the GPL-governed lineage of FlexRadio PowerSDR
// and the OpenHPSDR (TAPR/OpenHPSDR) ecosystem; that lineage is preserved
// here. See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.
//
// Protocol-2 / PureSignal / Saturn-class behaviour was additionally informed
// by pihpsdr (https://github.com/dl1ycf/pihpsdr), maintained by Christoph
// Wüllen (DL1YCF); and by DeskHPSDR
// (https://github.com/dl1bz/deskhpsdr), maintained by Heiko (DL1BZ).
// Both are GPL-2.0-or-later.
//
// WDSP — loaded by Zeus via P/Invoke — is Copyright (C) Warren Pratt
// (NR0V), distributed under GPL v2 or later.
//
// Zeus is distributed WITHOUT ANY WARRANTY; see the GNU General Public
// License for details.

using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using Zeus.Contracts;

namespace Zeus.Server;

public sealed class StreamingHub
{
    private const int MaxBacklogPerClient = 4;

    // Matches MsgType.MicPcm; the client→server uplink type-byte.
    private const byte MsgTypeMicPcm = 0x20;

    // Largest client→server payload we'll reassemble. A mic PCM frame is
    // 1 + 960*4 = 3841 bytes; 16 KB leaves comfortable headroom if the
    // contract ever adds a control frame. Receives larger than this are
    // dropped to bound memory.
    private const int MaxInboundMessageBytes = 16 * 1024;

    private readonly ILogger<StreamingHub> _log;
    private readonly ConcurrentDictionary<Guid, ClientSession> _clients = new();
    // Latest WDSP wisdom phase + status string. Set by Program.cs wiring on
    // phase- and status-changed events; read on every AttachClientAsync so
    // late joiners see the current state without waiting for the next
    // transition. Volatile because the writer is on a worker thread and
    // readers can be on any hub caller.
    private volatile byte _wisdomPhase;
    private volatile string _wisdomStatus = string.Empty;

    // ---- Step 1 drop-counter probe for issue #299 -----------------------
    // Each per-client send queue is bounded to MaxBacklogPerClient=4 with
    // FullMode=DropOldest (lines 318-324). When the producer outruns
    // SendLoopAsync — e.g. under PS-armed CPU load or OBS-streaming load —
    // TryWrite silently discards the oldest frame. These counters surface
    // that loss so we can confirm the diagnosis before changing behaviour.
    //
    // Buckets:
    //   audio   — RX audio frames (the user-audible path)
    //   display — panadapter + waterfall display frames
    //   meter   — TX/RX/PS/PA-temp meter frames (5 Hz typical)
    //   other   — wisdom / alert / VST / band-plan / mic-priming
    //
    // A System.Threading.Timer fires every 1 s and logs deltas when any
    // bucket grew. Zero overhead when no drops are occurring. Single timer
    // lives the lifetime of the hub (singleton); no Dispose needed.
    private long _dropsAudio;
    private long _dropsDisplay;
    private long _dropsMeter;
    private long _dropsOther;
    private long _lastLoggedAudio;
    private long _lastLoggedDisplay;
    private long _lastLoggedMeter;
    private long _lastLoggedOther;
    private readonly System.Threading.Timer _dropLogTimer;

    public StreamingHub(ILogger<StreamingHub> log)
    {
        _log = log;
        _dropLogTimer = new System.Threading.Timer(_ => LogDropsIfAny(), null, 1000, 1000);
    }

    private void LogDropsIfAny()
    {
        long a = System.Threading.Interlocked.Read(ref _dropsAudio);
        long d = System.Threading.Interlocked.Read(ref _dropsDisplay);
        long m = System.Threading.Interlocked.Read(ref _dropsMeter);
        long o = System.Threading.Interlocked.Read(ref _dropsOther);
        long da = a - _lastLoggedAudio;
        long dd = d - _lastLoggedDisplay;
        long dm = m - _lastLoggedMeter;
        long doo = o - _lastLoggedOther;
        if (da > 0 || dd > 0 || dm > 0 || doo > 0)
        {
            _log.LogWarning(
                "hub.drops audio={A} (+{Da}) display={D} (+{Dd}) meter={M} (+{Dm}) other={O} (+{Do})",
                a, da, d, dd, m, dm, o, doo);
            _lastLoggedAudio = a;
            _lastLoggedDisplay = d;
            _lastLoggedMeter = m;
            _lastLoggedOther = o;
        }
    }

    public int ClientCount => _clients.Count;

    /// <summary>Updates the hub's cached wisdom phase so clients attaching
    /// after the one-shot broadcast still receive the current state.</summary>
    public void SetWisdomPhase(Zeus.Contracts.WisdomPhase phase)
    {
        _wisdomPhase = (byte)phase;
    }

    /// <summary>Updates the hub's cached wisdom status string so clients
    /// attaching mid-build receive the current sub-step text immediately.</summary>
    public void SetWisdomStatus(string status)
    {
        _wisdomStatus = status ?? string.Empty;
    }

    /// <summary>
    /// Raised when a client sends a <c>MicPcm</c> binary frame. The handler
    /// receives the payload with the 1-byte type prefix stripped — plain
    /// f32le samples. Subscribers must be fast: the handler runs on the
    /// WS receive loop and blocking it will stall further uplink.
    /// </summary>
    public event Action<ReadOnlyMemory<byte>>? MicPcmReceived;

    public async Task AttachClientAsync(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var session = new ClientSession(id, ws, _log, this);
        _clients[id] = session;
        _log.LogInformation("ws.client.connected id={Id} total={Count}", id, _clients.Count);

        // Prime the new client with the current wisdom phase + status text.
        // Without this a client that joins after the ready event would sit
        // at default (Idle) and render the splash indefinitely; a client
        // joining mid-build needs both the phase byte and any status string
        // already accumulated so the body shows the current step.
        session.TryEnqueue(BuildWisdomPayload((Zeus.Contracts.WisdomPhase)_wisdomPhase, _wisdomStatus));

        try
        {
            await session.RunAsync(ct);
        }
        finally
        {
            _clients.TryRemove(id, out _);
            _log.LogInformation("ws.client.disconnected id={Id} total={Count}", id, _clients.Count);
        }
    }

    internal void DispatchInbound(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length == 0) return;
        byte type = frame.Span[0];
        switch (type)
        {
            case MsgTypeMicPcm:
                if (MicPcmReceived is { } handler)
                {
                    try { handler(frame.Slice(1)); }
                    catch (Exception ex) { _log.LogWarning(ex, "MicPcmReceived handler threw"); }
                }
                break;
            default:
                // Unknown uplink type — log once so a misaligned client is obvious,
                // but don't tear down the connection.
                _log.LogDebug("ws.inbound unknown type=0x{Type:X2} len={Len}", type, frame.Length);
                break;
        }
    }

    // Each Broadcast(...) below allocates the wire payload directly into a
    // fresh `byte[total]` and serialises into it via FixedBufferWriter. The
    // earlier shape rented from ArrayPool, serialised into the rented span,
    // then called `new ReadOnlyMemory<byte>(rented, 0, total).ToArray()` to
    // produce the broadcast payload — that .ToArray() was the #1 server-side
    // allocator under idle RX (36% of bytes allocated), and the rent/return
    // pair didn't save anything because the ToArray copy is the same size as
    // the rented buffer. Since each client gets the identical payload (queue
    // shares the byte[]), one `new byte[total]` per broadcast is both cheaper
    // and simpler. perf(server) follow-up to docs/performance_tuning.md.

    public void Broadcast(in DisplayFrame frame)
    {
        if (_clients.IsEmpty) return;

        int total = frame.TotalByteLength;
        var payload = new byte[total];
        var writer = new FixedBufferWriter(payload, total);
        frame.Serialize(writer);
        foreach (var client in _clients.Values)
        {
            if (!client.TryEnqueue(payload)) System.Threading.Interlocked.Increment(ref _dropsDisplay);
        }
    }

    public void Broadcast(in AudioFrame frame)
    {
        if (_clients.IsEmpty) return;

        int total = frame.TotalByteLength;
        var payload = new byte[total];
        var writer = new FixedBufferWriter(payload, total);
        frame.Serialize(writer);
        foreach (var client in _clients.Values)
        {
            if (!client.TryEnqueue(payload)) System.Threading.Interlocked.Increment(ref _dropsAudio);
        }
    }

    public void Broadcast(in TxMetersFrame frame)
    {
        if (_clients.IsEmpty) return;

        int total = TxMetersFrame.ByteLength;
        var payload = new byte[total];
        var writer = new FixedBufferWriter(payload, total);
        frame.Serialize(writer);
        foreach (var client in _clients.Values)
        {
            if (!client.TryEnqueue(payload)) System.Threading.Interlocked.Increment(ref _dropsMeter);
        }
    }

    public void Broadcast(in TxMetersV2Frame frame)
    {
        if (_clients.IsEmpty) return;

        int total = TxMetersV2Frame.ByteLength;
        var payload = new byte[total];
        var writer = new FixedBufferWriter(payload, total);
        frame.Serialize(writer);
        foreach (var client in _clients.Values)
        {
            if (!client.TryEnqueue(payload)) System.Threading.Interlocked.Increment(ref _dropsMeter);
        }
    }

    public void Broadcast(in PsMetersFrame frame)
    {
        if (_clients.IsEmpty) return;

        int total = PsMetersFrame.ByteLength;
        var payload = new byte[total];
        var writer = new FixedBufferWriter(payload, total);
        frame.Serialize(writer);
        foreach (var client in _clients.Values)
        {
            if (!client.TryEnqueue(payload)) System.Threading.Interlocked.Increment(ref _dropsMeter);
        }
    }

    public void Broadcast(in RxMeterFrame frame)
    {
        if (_clients.IsEmpty) return;

        int total = RxMeterFrame.ByteLength;
        var payload = new byte[total];
        var writer = new FixedBufferWriter(payload, total);
        frame.Serialize(writer);
        foreach (var client in _clients.Values)
        {
            if (!client.TryEnqueue(payload)) System.Threading.Interlocked.Increment(ref _dropsMeter);
        }
    }

    public void Broadcast(in RxMetersV2Frame frame)
    {
        if (_clients.IsEmpty) return;

        int total = RxMetersV2Frame.ByteLength;
        var payload = new byte[total];
        var writer = new FixedBufferWriter(payload, total);
        frame.Serialize(writer);
        foreach (var client in _clients.Values)
        {
            if (!client.TryEnqueue(payload)) System.Threading.Interlocked.Increment(ref _dropsMeter);
        }
    }

    public void Broadcast(in PaTempFrame frame)
    {
        if (_clients.IsEmpty) return;

        int total = PaTempFrame.ByteLength;
        var payload = new byte[total];
        var writer = new FixedBufferWriter(payload, total);
        frame.Serialize(writer);
        foreach (var client in _clients.Values)
        {
            if (!client.TryEnqueue(payload)) System.Threading.Interlocked.Increment(ref _dropsMeter);
        }
    }

    public void Broadcast(in WisdomStatusFrame frame)
    {
        SetWisdomPhase(frame.Phase);
        SetWisdomStatus(frame.Status);
        if (_clients.IsEmpty) return;
        var payload = BuildWisdomPayload(frame.Phase, frame.Status);
        foreach (var client in _clients.Values)
        {
            if (!client.TryEnqueue(payload)) System.Threading.Interlocked.Increment(ref _dropsOther);
        }
    }

    private static byte[] BuildWisdomPayload(Zeus.Contracts.WisdomPhase phase, string status)
    {
        var frame = new WisdomStatusFrame(phase, status ?? string.Empty);
        int total = frame.ByteLength;
        var buf = new byte[total];
        var writer = new FixedBufferWriter(buf, total);
        frame.Serialize(writer);
        return buf;
    }

    public void Broadcast(in AlertFrame frame)
    {
        if (_clients.IsEmpty) return;

        // AlertFrame is variable-length: 2-byte header + UTF-8 message bytes.
        // Compute the exact size up front so we can allocate the broadcast
        // payload directly (no rent/copy/return). Same shape as the other
        // Broadcast(...) overloads above.
        int total = 2 + System.Text.Encoding.UTF8.GetByteCount(frame.Message);
        if (total > AlertFrame.MaxByteLength) total = AlertFrame.MaxByteLength;
        var payload = new byte[total];
        var writer = new FixedBufferWriter(payload, total);
        frame.Serialize(writer);
        foreach (var client in _clients.Values)
        {
            if (!client.TryEnqueue(payload)) System.Threading.Interlocked.Increment(ref _dropsOther);
        }
    }

    /// Broadcast a small VST plugin-host event tag (utf-8 text). Used by
    /// <see cref="VstHostHostedService"/> to nudge connected clients to
    /// re-fetch <c>/api/plughost/state</c> or refresh a single slot. See
    /// <see cref="VstHostEventFrame"/> for the wire format.
    /// </summary>
    public void BroadcastVstHostEvent(string ev)
    {
        if (_clients.IsEmpty) return;
        var frame = new VstHostEventFrame(ev ?? string.Empty);
        int total = frame.ByteLength;
        var buf = new byte[total];
        var writer = new FixedBufferWriter(buf, total);
        frame.Serialize(writer);
        foreach (var client in _clients.Values)
        {
            if (!client.TryEnqueue(buf)) System.Threading.Interlocked.Increment(ref _dropsOther);
        }
    }

    /// <summary>
    /// Broadcasts a BandPlanChanged (0x1B) notification. Payload: type byte +
    /// UTF-8 region ID. Clients refetch /api/bands/current on receipt.
    /// </summary>
    public void BroadcastBandPlanChanged(string regionId)
    {
        if (_clients.IsEmpty) return;
        var regionBytes = System.Text.Encoding.UTF8.GetBytes(regionId);
        var payload = new byte[1 + regionBytes.Length];
        payload[0] = (byte)MsgType.BandPlanChanged;
        regionBytes.CopyTo(payload, 1);
        foreach (var client in _clients.Values)
        {
            if (!client.TryEnqueue(payload)) System.Threading.Interlocked.Increment(ref _dropsOther);
        }
    }

    private sealed class ClientSession
    {
        public Guid Id { get; }
        private readonly WebSocket _ws;
        private readonly ILogger _log;
        private readonly StreamingHub _hub;
        private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(MaxBacklogPerClient)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        public ClientSession(Guid id, WebSocket ws, ILogger log, StreamingHub hub)
        {
            Id = id; _ws = ws; _log = log; _hub = hub;
        }

        // Returns true if the frame was enqueued. False = the bounded queue's
        // DropOldest policy silently evicted the oldest frame (or this one)
        // — Broadcast(...) call sites attribute the drop to their kind via
        // the hub-level counters for the #299 Step 1 probe.
        public bool TryEnqueue(byte[] payload) => _queue.Writer.TryWrite(payload);

        public async Task RunAsync(CancellationToken ct)
        {
            var sendTask = SendLoopAsync(ct);
            var recvTask = ReceiveLoopAsync(ct);
            await Task.WhenAny(sendTask, recvTask);
            _queue.Writer.TryComplete();
            try { await Task.WhenAll(sendTask, recvTask); } catch { /* drained */ }
        }

        private async Task SendLoopAsync(CancellationToken ct)
        {
            // perf3 iter2: WaitToReadAsync+TryRead drain — see
            // DspPipelineService.StartIqPump for the rationale. At
            // ~60 frame/s per client (display+audio+meters combined),
            // burst arrivals are common (pipeline Tick enqueues display +
            // optional meters in the same iteration); batching them into
            // one TP dispatch + a tight TryRead loop is strictly fewer
            // wake-ups than one ReadAsync continuation per frame. Channel
            // is CreateBounded(DropOldest, SingleReader=true) so drop-oldest
            // back-pressure is unchanged; ChannelClosedException is replaced
            // by WaitToReadAsync returning false after Writer.TryComplete().
            var reader = _queue.Reader;
            try
            {
                while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var frame))
                    {
                        if (_ws.State != WebSocketState.Open) return;
                        await _ws.SendAsync(frame, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "ws send loop ended for {Id}", Id);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            // 8 KB receive window. A mic PCM frame (3841 B) arrives in one or
            // two ReceiveAsync calls depending on chunking; the accumulator
            // below stitches fragments up to MaxInboundMessageBytes before
            // dispatch.
            var buf = new byte[8 * 1024];
            // Reuse across messages; reset on each EndOfMessage.
            byte[]? accum = null;
            int accumLen = 0;
            try
            {
                while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
                {
                    var result = await _ws.ReceiveAsync(buf, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                        return;
                    }
                    if (result.MessageType != WebSocketMessageType.Binary)
                    {
                        // Ignore text frames — no textual control channel in the MVP.
                        continue;
                    }

                    int chunkLen = result.Count;
                    // Fast path: single-fragment message with no pending accumulator —
                    // dispatch the buffer view directly, no allocation.
                    if (result.EndOfMessage && accum is null)
                    {
                        _hub.DispatchInbound(new ReadOnlyMemory<byte>(buf, 0, chunkLen));
                        continue;
                    }

                    if (accum is null)
                    {
                        accum = ArrayPool<byte>.Shared.Rent(Math.Max(chunkLen, 4096));
                        accumLen = 0;
                    }
                    if (accumLen + chunkLen > MaxInboundMessageBytes)
                    {
                        _log.LogWarning("ws.inbound oversize id={Id} len={Len}", Id, accumLen + chunkLen);
                        ArrayPool<byte>.Shared.Return(accum);
                        accum = null;
                        accumLen = 0;
                        continue;
                    }
                    if (accumLen + chunkLen > accum.Length)
                    {
                        int newSize = Math.Min(MaxInboundMessageBytes, accum.Length * 2);
                        while (newSize < accumLen + chunkLen) newSize = Math.Min(MaxInboundMessageBytes, newSize * 2);
                        var grown = ArrayPool<byte>.Shared.Rent(newSize);
                        Buffer.BlockCopy(accum, 0, grown, 0, accumLen);
                        ArrayPool<byte>.Shared.Return(accum);
                        accum = grown;
                    }
                    Buffer.BlockCopy(buf, 0, accum, accumLen, chunkLen);
                    accumLen += chunkLen;

                    if (result.EndOfMessage)
                    {
                        _hub.DispatchInbound(new ReadOnlyMemory<byte>(accum, 0, accumLen));
                        ArrayPool<byte>.Shared.Return(accum);
                        accum = null;
                        accumLen = 0;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex)
            {
                _log.LogDebug(ex, "ws recv loop ended for {Id}", Id);
            }
            finally
            {
                if (accum is not null) ArrayPool<byte>.Shared.Return(accum);
            }
        }
    }

    private sealed class FixedBufferWriter : IBufferWriter<byte>
    {
        private readonly byte[] _buf;
        private readonly int _capacity;
        private int _written;

        public FixedBufferWriter(byte[] buf, int capacity) { _buf = buf; _capacity = capacity; }

        public void Advance(int count)
        {
            if (_written + count > _capacity) throw new InvalidOperationException("buffer overflow");
            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => _buf.AsMemory(_written, _capacity - _written);

        public Span<byte> GetSpan(int sizeHint = 0) => _buf.AsSpan(_written, _capacity - _written);
    }
}
