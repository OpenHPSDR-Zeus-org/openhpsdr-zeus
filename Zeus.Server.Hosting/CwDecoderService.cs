// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeus.Contracts;

namespace Zeus.Server;

/// <summary>
/// Server-side CW receive decoder. Subscribes to
/// <see cref="DspPipelineService.RxAudioAvailable"/> — the demodulated,
/// post-AGC RX audio that the DSP pipeline produces on every ~21 ms tick,
/// independently of any audio sink — runs <see cref="CwAudioDecoder"/> while
/// the radio is in a CW mode, and broadcasts decoded characters over the hub
/// as <see cref="CwDecodedTextFrame"/>.
///
/// Decoding lives here, not in the browser, on purpose: it then works in the
/// desktop/native-audio host (where the browser never sees the audio) and
/// headless (no sink required). The audio dependency is on the DSP *signal*,
/// not on any audio *output*.
/// </summary>
public sealed class CwDecoderService : BackgroundService
{
    private readonly DspPipelineService _pipeline;
    private readonly StreamingHub _hub;
    private readonly RadioService _radio;
    private readonly ILogger<CwDecoderService> _log;

    // Touched only on the DSP pipeline thread (RxAudioAvailable is single-fire
    // per tick from that thread), so no locking is needed.
    private CwAudioDecoder? _decoder;
    private int _decoderRate;
    private readonly StringBuilder _pending = new();

    public CwDecoderService(
        DspPipelineService pipeline,
        StreamingHub hub,
        RadioService radio,
        ILogger<CwDecoderService> log)
    {
        _pipeline = pipeline;
        _hub = hub;
        _radio = radio;
        _log = log;
        _pipeline.RxAudioAvailable += OnRxAudio;
    }

    // Event-driven: no background loop. The work happens in OnRxAudio.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

    private void OnRxAudio(int receiver, int sampleRateHz, ReadOnlyMemory<float> samples)
    {
        var mode = _radio.Snapshot().Mode;
        if (mode != RxMode.CWL && mode != RxMode.CWU)
        {
            // Drop decoder state when out of CW so the noise-floor followers
            // re-warm cleanly the next time CW is selected.
            _decoder = null;
            return;
        }

        if (_decoder is null || _decoderRate != sampleRateHz)
        {
            _decoder = new CwAudioDecoder(sampleRateHz);
            _decoderRate = sampleRateHz;
        }

        _pending.Clear();
        try
        {
            _decoder.Process(samples.Span, ch => _pending.Append(ch));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "cw-decoder: decode tick failed");
            return;
        }

        if (_pending.Length > 0)
        {
            _hub.Broadcast(new CwDecodedTextFrame(
                _pending.ToString(),
                _decoder.Wpm,
                (float)_decoder.SnrDb,
                (float)_decoder.Confidence));
        }
    }

    public override void Dispose()
    {
        _pipeline.RxAudioAvailable -= OnRxAudio;
        base.Dispose();
    }
}
