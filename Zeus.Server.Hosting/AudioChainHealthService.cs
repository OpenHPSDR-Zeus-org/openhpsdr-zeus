// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Dsp;

namespace Zeus.Server;

/// <summary>
/// Audio Chain Monitor — core diagnostic service per ADR-0001. Ticks at
/// 2 Hz, reads the live WDSP TXA stage meters + wire/PA snapshot +
/// operator mode, evaluates the per-mode/per-board rule set, and
/// broadcasts <see cref="AudioChainHealthFrame"/> (MsgType 0x32) carrying
/// only verdicts. Raw stage numbers stay on the existing
/// <c>TxMetersV2</c> / <c>PaTemp</c> frames; the factory widget joins on
/// <see cref="AudioChainStageId"/> in the frontend.
///
/// This file is the skeleton: 2 Hz tick + broadcast wiring + 9-stage
/// stub that emits all-OK verdicts. The rule evaluator + sustained-
/// violation/hysteresis machinery + Thetis-seeded thresholds land in
/// follow-up issues (zeus-w7x → zeus-1x4 → zeus-y89). Apply targets are
/// kept in-process and consumed by the apply endpoint
/// (<c>POST /api/audio-chain/apply</c>, zeus-pgn).
/// </summary>
public sealed class AudioChainHealthService : BackgroundService
{
    // 2 Hz, matching the cadence committed to in CONTEXT.md and ADR-0002.
    // Verdicts are slow-changing (sustained-violation windows of 3-5 s
    // gate entry per the rule engine), so this is plenty.
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(500);

    private readonly StreamingHub _hub;
    private readonly RadioService _radio;
    private readonly TxService _tx;
    private readonly DspPipelineService _pipe;
    private readonly ILogger<AudioChainHealthService> _log;

    // The fixed nine-tile order the factory widget renders left-to-right.
    // Per the design handoff bundle (Direction A).
    private static readonly AudioChainStageId[] Stages =
    {
        AudioChainStageId.Mic,
        AudioChainStageId.Eq,
        AudioChainStageId.Leveler,
        AudioChainStageId.Cfc,
        AudioChainStageId.Comp,
        AudioChainStageId.Alc,
        AudioChainStageId.Out,
        AudioChainStageId.Wire,
        AudioChainStageId.Pa,
    };

    public AudioChainHealthService(
        StreamingHub hub,
        RadioService radio,
        TxService tx,
        DspPipelineService pipe,
        ILogger<AudioChainHealthService> log)
    {
        _hub = hub;
        _radio = radio;
        _tx = tx;
        _pipe = pipe;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Skeleton: tick at 2 Hz, build a stub frame with nine OK
        // verdicts, broadcast. Rule eval is wired in by zeus-w7x.
        var ticker = new PeriodicTimer(Tick);
        try
        {
            while (await ticker.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var frame = BuildSnapshot();
                    _hub.Broadcast(frame);
                }
                catch (Exception ex)
                {
                    // Per-tick failure should never take down the service —
                    // log and skip this tick; the next tick re-reads state
                    // fresh.
                    _log.LogWarning(ex, "audio-chain-health tick failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private AudioChainHealthFrame BuildSnapshot()
    {
        var state = _radio.Snapshot();
        // Read the WDSP stage meters once per tick. The engine returns
        // Silent (-∞ levels, 0 GR) when TXA is idle, which the rule
        // engine (next issue) will interpret as "awaiting mox" /
        // bypassed and surface as Info — not Warn.
        var stages = _pipe.CurrentEngine?.GetTxStageMeters() ?? TxStageMeters.Silent;
        bool mox = _tx.IsMoxOn || _tx.IsTunOn;

        // Stub: every tile reports OK. The rule engine (zeus-w7x) replaces
        // this with sustained-violation-gated verdicts that read `stages`,
        // `mox`, `state.Mode`, and the connected board kind.
        _ = stages;
        _ = mox;

        var verdicts = new AudioChainVerdict[Stages.Length];
        for (int i = 0; i < Stages.Length; i++)
        {
            verdicts[i] = AudioChainVerdict.Ok(Stages[i]);
        }
        return new AudioChainHealthFrame(state.Mode, verdicts);
    }
}
