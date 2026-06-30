// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.VirtualRadio;
using Zeus.VirtualRadio.Observation;
using Zeus.VirtualRadio.P1;

[assembly: InternalsVisibleTo("Zeus.VirtualRadio.Tests")]

namespace Zeus.VirtualRadio.Host;

/// <summary>
/// Console host for the virtual HPSDR radio ("zeus-vradio"). Parses the CLI,
/// builds a <see cref="VirtualRadioProfile"/>, and runs the matching engine
/// under <see cref="Microsoft.Extensions.Hosting"/> (logging / config / lifetime
/// for free). No Photino / WDSP / native deps — runs anywhere Zeus does.
///
/// Usage:
///   zeus-vradio [--board HermesII] [--protocol P1] [--variant G2]
///               [--bind 127.0.0.1] [--rate 48]
///               [--tone 14074000:-73 [--tone …]] [--noise -110]
///               [--status-port 8073]
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        HostConfig config;
        try
        {
            config = ParseArgs(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"zeus-vradio: {ex.Message}");
            Console.Error.WriteLine(
                "usage: zeus-vradio [--board HermesII] [--protocol P1] [--variant G2] " +
                "[--bind 127.0.0.1] [--rate 48] [--tone 14074000:-73] [--noise -110] [--status-port 8073]");
            return 2;
        }

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(config.Profile);
        builder.Services.AddSingleton<IVirtualRadio>(sp =>
            // P1-only for Phase 1. When the P2 engine exists, branch on
            // config.Profile.Protocol in this lambda.
            new Protocol1Engine(config.Profile, sp.GetService<ILogger<Protocol1Engine>>()));
        builder.Services.AddHostedService<VirtualRadioHostedService>();

        using var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Parse the CLI into a validated <see cref="HostConfig"/>.
    /// Defaults: board HermesII, protocol P1, variant G2, bind 127.0.0.1,
    /// rate 48 kHz, noise floor -110 dBc, no tones, status port
    /// <see cref="StatusJsonServer.DefaultPort"/>.
    /// </summary>
    internal static HostConfig ParseArgs(string[] args)
    {
        var board = HpsdrBoardKind.HermesII;
        var protocol = ProtocolVersion.P1;
        var variant = OrionMkIIVariant.G2;
        var bind = IPAddress.Loopback;
        int rateKhz = 48;
        double noise = -110.0;
        int statusPort = StatusJsonServer.DefaultPort;
        var tones = new List<ToneSpec>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string Next(string name) =>
                i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{name} requires a value");

            switch (arg)
            {
                case "--board":
                    board = Enum.Parse<HpsdrBoardKind>(Next(arg), ignoreCase: true);
                    break;
                case "--protocol":
                    protocol = Enum.Parse<ProtocolVersion>(Next(arg), ignoreCase: true);
                    break;
                case "--variant":
                    variant = Enum.Parse<OrionMkIIVariant>(Next(arg), ignoreCase: true);
                    break;
                case "--bind":
                    bind = IPAddress.Parse(Next(arg));
                    break;
                case "--rate":
                    rateKhz = int.Parse(Next(arg), CultureInfo.InvariantCulture);
                    break;
                case "--noise":
                    noise = double.Parse(Next(arg), CultureInfo.InvariantCulture);
                    break;
                case "--tone":
                    tones.Add(ParseTone(Next(arg)));
                    break;
                case "--status-port":
                    statusPort = int.Parse(Next(arg), CultureInfo.InvariantCulture);
                    if (statusPort is < 1 or > 65535)
                        throw new ArgumentException($"--status-port out of range: {statusPort}");
                    break;
                default:
                    throw new ArgumentException($"unknown argument '{arg}'");
            }
        }

        var profile = VirtualRadioProfile.Create(board, protocol, variant) with
        {
            BindAddress = bind,
            SampleRateKhz = rateKhz,
            NoiseFloorDbc = noise,
            Tones = tones,
        };
        return new HostConfig(profile, statusPort);
    }

    /// <summary>Parse a "freqHz:dBc" tone spec, e.g. "14074000:-73".</summary>
    internal static ToneSpec ParseTone(string spec)
    {
        int colon = spec.IndexOf(':');
        if (colon <= 0)
            throw new ArgumentException($"--tone expects freqHz:dBc, got '{spec}'");
        long freq = long.Parse(spec[..colon], CultureInfo.InvariantCulture);
        double dbc = double.Parse(spec[(colon + 1)..], CultureInfo.InvariantCulture);
        return new ToneSpec(freq, dbc);
    }
}

/// <summary>Parsed CLI: the validated radio profile plus host-only options.</summary>
internal sealed record HostConfig(VirtualRadioProfile Profile, int StatusPort);

/// <summary>
/// Drives the engine's <see cref="IVirtualRadio.RunAsync"/> for the process
/// lifetime, alongside the read-only <see cref="StatusJsonServer"/>. Also wires
/// the structured command log: every decoded host command is logged edge-
/// triggered, and a ~1 Hz summary line mirrors Zeus's own <c>p1.tx.rate</c>
/// cadence.
/// </summary>
internal sealed class VirtualRadioHostedService : BackgroundService
{
    private readonly IVirtualRadio _radio;
    private readonly HostConfig _config;
    private readonly ILogger<VirtualRadioHostedService> _logger;
    private readonly StatusJsonServer _status;

    public VirtualRadioHostedService(
        IVirtualRadio radio,
        HostConfig config,
        ILoggerFactory loggerFactory)
    {
        _radio = radio;
        _config = config;
        _logger = loggerFactory.CreateLogger<VirtualRadioHostedService>();
        _status = new StatusJsonServer(
            _radio.Snapshot,
            config.StatusPort,
            loggerFactory.CreateLogger<StatusJsonServer>());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "vradio starting: board {Board} variant {Variant} protocol {Protocol} bind {Bind} rate {Rate}kHz tones {Tones} status :{StatusPort}",
            _config.Profile.Board, _config.Profile.Variant, _config.Profile.Protocol,
            _config.Profile.BindAddress, _config.Profile.SampleRateKhz,
            _config.Profile.Tones.Count, _config.StatusPort);

        _radio.CommandDecoded += OnCommandDecoded;
        try
        {
            await Task.WhenAll(
                _radio.RunAsync(stoppingToken),
                _status.RunAsync(stoppingToken),
                SummaryLoopAsync(stoppingToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            _radio.CommandDecoded -= OnCommandDecoded;
        }
    }

    private void OnCommandDecoded(DecodedHostCommand cmd) =>
        // Edge-triggered: one line per decoded host command, fully decoded.
        _logger.LogInformation("vradio.cmd {Kind}: {Summary}", cmd.CommandKind, cmd.Summary);

    private async Task SummaryLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                VirtualRadioStatus s = _radio.Snapshot();
                _logger.LogInformation(
                    "vradio.status host={Host} mox={Mox} fwd={Fwd:F1}W ref={Ref:F1}W swr={Swr:F2} ep6={Ep6} ep2={Ep2} gaps={Gaps}",
                    s.ConnectedHost ?? "-", s.Mox, s.FwdWatts, s.RefWatts, s.Swr,
                    s.Ep6PacketsSent, s.Ep2PacketsReceived, s.SeqGaps);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }
}
