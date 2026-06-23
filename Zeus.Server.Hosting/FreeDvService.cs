// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Server-side coordinator for FreeDV digital voice. Owns the single
// FreeDvModem instance, drives its active state from the operator's selected
// mode (RxMode.FreeDv), and exposes the /api/freedv status/config surface.
// The DSP pipeline taps ProcessRx (post-demod, RX0) and the TX mic-ingest
// taps ProcessTx (pre-WDSP) — both no-ops unless FreeDV is active. The radio
// itself runs USB underneath (WdspDspEngine.MapMode maps FreeDv -> USB), so
// no WDSP demod mode change is needed here.

using Microsoft.Extensions.Logging;
using Zeus.Contracts;
using Zeus.Dsp.FreeDv;

namespace Zeus.Server;

public sealed class FreeDvService : IDisposable
{
    private readonly FreeDvModem _modem;
    private readonly ILogger<FreeDvService> _log;
    private volatile string? _txText;

    public FreeDvService(ILoggerFactory loggerFactory)
    {
        _log = loggerFactory.CreateLogger<FreeDvService>();
        _modem = new FreeDvModem(loggerFactory.CreateLogger<FreeDvModem>());
    }

    /// <summary>True when FreeDV is engaged and the modem is processing audio.</summary>
    public bool Active => _modem.Active;

    /// <summary>
    /// Reconcile the modem's active state with the live RX0 mode. Called from
    /// the DSP tick — cheap no-op when already in the right state. Activating
    /// opens the native modem at the current submode; deactivating releases it.
    /// </summary>
    public void SyncMode(RxMode mode)
    {
        bool want = mode == RxMode.FreeDv;
        if (want && !_modem.Active)
        {
            _log.LogInformation("FreeDV: engaging (mode=FreeDv, submode={Submode})", _modem.Submode);
            _modem.Activate();
        }
        else if (!want && _modem.Active)
        {
            _log.LogInformation("FreeDV: disengaging (mode={Mode})", mode);
            _modem.Deactivate();
        }
    }

    /// <summary>RX0 post-demod insert: turn received modem audio into decoded speech, in place.</summary>
    public void ProcessRx(Span<float> block48k) => _modem.ProcessRxInPlace(block48k);

    /// <summary>TX mic insert: turn mic speech into the transmitted modem signal, in place.</summary>
    public void ProcessTx(Span<float> block48k) => _modem.ProcessTxInPlace(block48k);

    /// <summary>Drop buffered TX modem audio on a MOX falling edge so the next over starts clean.</summary>
    public void FlushTx() => _modem.FlushTx();

    public FreeDvStatusDto Status() => new(
        NativeAvailable: _modem.NativeAvailable,
        Active: _modem.Active,
        Submode: _modem.Submode,
        Synced: _modem.Synced,
        SnrDb: Math.Round(_modem.SnrDb, 1),
        SquelchEnabled: _modem.SquelchEnabled,
        SnrSquelchThreshDb: _modem.SnrSquelchThreshDb,
        SpeechSampleRateHz: _modem.SpeechSampleRateHz,
        ModemSampleRateHz: _modem.ModemSampleRateHz,
        // RX text sidechannel (callsign etc.) decoding is a follow-up — it needs
        // freedv_set_callback_txt wired through the modem. Reported as null for now.
        RxText: null,
        TxText: _txText,
        LibraryVersion: _modem.LibraryVersion);

    public FreeDvStatusDto ApplyConfig(FreeDvConfigRequest req)
    {
        if (req.Submode.HasValue) _modem.SetSubmode(req.Submode.Value);
        if (req.SquelchEnabled.HasValue || req.SnrSquelchThreshDb.HasValue)
            _modem.SetSquelch(req.SquelchEnabled, req.SnrSquelchThreshDb);
        if (req.TxText is not null) _txText = req.TxText;
        return Status();
    }

    public void Dispose() => _modem.Dispose();
}
