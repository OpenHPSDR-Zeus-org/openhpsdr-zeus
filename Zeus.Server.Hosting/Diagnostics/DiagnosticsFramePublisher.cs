// SPDX-License-Identifier: GPL-2.0-or-later
//
// Live Diagnostics API v2 — push publisher.
//
// Background service that broadcasts the aggregate diagnostics health frame
// (MsgType 0x36) over the StreamingHub at a low, fixed rate. Subscriber-gated
// (skips the tick entirely when no clients are connected) and source-gen
// serialised so the push path stays allocation-light. Building the health frame
// runs the providers' self-checks — but only here, on this background thread,
// never on a request or DSP/realtime thread.

using System.Text.Json;
using Zeus.Contracts;

namespace Zeus.Server.Diagnostics;

public sealed class DiagnosticsFramePublisher : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(1);

    private readonly StreamingHub _hub;
    private readonly DiagnosticsSelfCheckCache _cache;
    private readonly ILogger<DiagnosticsFramePublisher> _log;

    public DiagnosticsFramePublisher(
        StreamingHub hub,
        DiagnosticsSelfCheckCache cache,
        ILogger<DiagnosticsFramePublisher> log)
    {
        _hub = hub ?? throw new ArgumentNullException(nameof(hub));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Encodes a DiagnosticsHealth (0x36) frame: [type:1][UTF-8 JSON]. Exposed for tests.</summary>
    public static byte[] EncodeFrame(DiagnosticsHealthDto health)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(health, DiagnosticsJsonContext.Default.DiagnosticsHealthDto);
        var frame = new byte[json.Length + 1];
        frame[0] = (byte)MsgType.DiagnosticsHealth;
        Buffer.BlockCopy(json, 0, frame, 1, json.Length);
        return frame;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // Subscriber gate: do no work (and run no self-checks) when nobody
                // is listening. Cheap int read on the hub.
                if (_hub.ClientCount == 0) continue;

                try
                {
                    var frame = EncodeFrame(_cache.BuildHealth());
                    _hub.BroadcastDiagnostics(frame);
                }
                catch (Exception ex)
                {
                    // A bad tick must not kill the publisher. Self-check probes are
                    // already individually guarded; this catches anything else.
                    _log.LogWarning(ex, "diagnostics frame publish tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }
}
