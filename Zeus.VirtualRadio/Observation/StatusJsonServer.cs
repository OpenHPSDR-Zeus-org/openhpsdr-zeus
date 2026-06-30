// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zeus.VirtualRadio.Observation;

/// <summary>
/// Minimal, read-only, localhost-only <c>GET /status</c> endpoint built on the
/// BCL <see cref="System.Net.HttpListener"/> (no ASP.NET dependency — keeps the
/// host binary lean and cross-platform). Serializes the engine's current
/// <see cref="VirtualRadioStatus"/> as JSON so an agent can poll and diff it
/// against Zeus's own <c>GET /api/state</c>.
/// </summary>
public sealed class StatusJsonServer
{
    private readonly Func<VirtualRadioStatus> _snapshot;
    private readonly int _port;
    private readonly ILogger<StatusJsonServer> _logger;

    /// <summary>Default loopback port for the status endpoint.</summary>
    public const int DefaultPort = 8073;

    /// <param name="snapshot">Supplier of the current status (typically
    /// <c>IVirtualRadio.Snapshot</c>).</param>
    /// <param name="port">Loopback TCP port to listen on.</param>
    /// <param name="logger">Optional logger.</param>
    public StatusJsonServer(Func<VirtualRadioStatus> snapshot, int port = DefaultPort, ILogger<StatusJsonServer>? logger = null)
    {
        _snapshot = snapshot;
        _port = port;
        _logger = logger ?? NullLogger<StatusJsonServer>.Instance;
    }

    /// <summary>The port the server listens on.</summary>
    public int Port => _port;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Serialize a <see cref="VirtualRadioStatus"/> into the <c>/status</c> JSON
    /// body. Projects to a flat, primitive-only shape (enums and the bind
    /// <c>IPAddress</c> as strings) so the output is stable and never depends on
    /// <see cref="System.Net.IPAddress"/> reflection quirks. Public + static so
    /// the shape can be asserted in a unit test without binding a socket.
    /// </summary>
    public static string Serialize(VirtualRadioStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        VirtualRadioProfile p = status.Profile;
        var payload = new
        {
            profile = new
            {
                board = p.Board.ToString(),
                variant = p.Variant.ToString(),
                protocol = p.Protocol.ToString(),
                tunedHz = p.TunedHz,
                sampleRateKhz = p.SampleRateKhz,
                bindAddress = p.BindAddress.ToString(),
                noiseFloorDbc = p.NoiseFloorDbc,
                pureSignalEnabled = p.PureSignalEnabled,
                tones = p.Tones.Select(t => new { freqHz = t.FreqHz, dbc = t.Dbc }).ToArray(),
            },
            connectedHost = status.ConnectedHost,
            mox = status.Mox,
            fwdWatts = status.FwdWatts,
            refWatts = status.RefWatts,
            swr = status.Swr,
            ep6PacketsSent = status.Ep6PacketsSent,
            ep2PacketsReceived = status.Ep2PacketsReceived,
            seqGaps = status.SeqGaps,
            lastCommands = status.LastCommands.Select(c => new
            {
                timestamp = c.Timestamp,
                protocol = c.Protocol.ToString(),
                commandKind = c.CommandKind,
                summary = c.Summary,
            }).ToArray(),
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>Start the listener and serve <c>/status</c> until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (!HttpListener.IsSupported)
        {
            _logger.LogWarning("vradio.status HttpListener is not supported on this platform; /status disabled.");
            return;
        }

        var listener = new HttpListener();
        // localhost-only: bind the loopback prefix so the read-only surface is
        // never exposed off-box and no Windows URL ACL is required.
        listener.Prefixes.Add($"http://localhost:{_port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            // Port already taken (or ACL denied) — log and continue; the status
            // surface is optional and must never take the emulator down.
            _logger.LogWarning(ex, "vradio.status could not bind port {Port}; /status disabled.", _port);
            return;
        }

        _logger.LogInformation("vradio.status serving GET /status on http://localhost:{Port}/status", _port);

        // Unblocks the GetContextAsync await when cancellation fires.
        using var reg = ct.Register(() =>
        {
            try { listener.Stop(); } catch { /* ignore: shutdown race */ }
        });

        try
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break; // listener stopped (cancellation)
                }
                catch (ObjectDisposedException)
                {
                    break; // listener closed (cancellation)
                }

                try
                {
                    HandleRequest(context);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "vradio.status request handling error.");
                }
            }
        }
        finally
        {
            try { listener.Close(); } catch { /* ignore: shutdown race */ }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        try
        {
            string path = request.Url?.AbsolutePath ?? "/";
            bool isStatus = path is "/status" or "/";

            if (!isStatus)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            string json = Serialize(_snapshot());
            byte[] body = Encoding.UTF8.GetBytes(json);
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json";
            response.ContentLength64 = body.Length;
            response.OutputStream.Write(body, 0, body.Length);
        }
        finally
        {
            try { response.OutputStream.Close(); } catch { /* ignore */ }
            try { response.Close(); } catch { /* ignore */ }
        }
    }
}
