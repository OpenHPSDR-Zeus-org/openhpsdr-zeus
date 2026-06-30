// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Two small, allocation-free, lock-free dynamics blocks that kill the "random
// artifacts when a user stops talking" on FreeDV — one per direction. Both are
// owned exclusively by a modem hot path (RX tick / mic ingest) and are reset
// only while that path is gated out during reconfigure, so they need no
// synchronisation. Pure span math, no allocation — matching the realtime
// discipline of the rest of the Zeus audio bus (FreeDvResampler, FreeDvModem).
//
//   RxSquelchGate — gates DECODED speech by the modem's sync flag with a smooth
//   attack/release ramp and a short hold. When the far station unkeys, the
//   decoder keeps turning band-noise into speech-shaped output (codec2's SNR
//   squelch is per-frame and twitchy; RADE has NO squelch at all), so without a
//   sync gate you hear an R2D2/warble tail. The hold rides brief mid-over sync
//   flickers so good copy is never chopped; the ramp removes the click a hard
//   mute would make. Reports "fully closed" so the modem can flush its output
//   FIFO and start the next over clean (no stale-noise backlog plays on resync).
//
//   MicNoiseGate — gates the MIC by an envelope follower with hysteresis + hang
//   BEFORE the vocoder. When the operator pauses mid-over, background hiss fed
//   into freedv_tx / rade_tx encodes into vocoder garble at the far end. Gating
//   the mic to digital silence during pauses makes the encoder emit clean
//   silence frames instead (still valid modem frames, so far-end sync holds).
//   Opens fast so word onsets are never clipped; the hang holds it open through
//   inter-word gaps so speech isn't chopped.
//
// All time constants are bench-tunable; the defaults are deliberately
// conservative (long hold/hang, gentle thresholds) so the gates only ever act
// on genuine silence/noise, never on speech. See docs/lessons/freedv-fidelity.md.

namespace Zeus.Dsp.FreeDv;

/// <summary>
/// Sync-driven output squelch for decoded FreeDV/RADE speech. Applies a smooth
/// per-sample attack/release gain ramp toward open (synced) / closed (unsynced),
/// with a hold window that rides brief sync dropouts so marginal copy isn't
/// chopped. Single-threaded by contract (the RX hot path owns it).
/// </summary>
internal sealed class RxSquelchGate
{
    private readonly int _holdSamples;     // ride-through window after sync is lost
    private readonly float _attackCoef;    // per-sample slew when opening
    private readonly float _releaseCoef;   // per-sample slew when closing
    private readonly float _closedEps;     // gain below this counts as fully closed

    private float _gain;                   // current applied gain, 0..1
    private long _samplesSinceSync;        // 48 kHz samples since the last synced block
    private bool _open;

    /// <summary>Default RX squelch gate at the given output rate (48 kHz).</summary>
    internal static RxSquelchGate Default(int sampleRateHz) =>
        new(sampleRateHz, holdMs: 250.0, attackMs: 5.0, releaseMs: 60.0);

    internal RxSquelchGate(int sampleRateHz, double holdMs, double attackMs, double releaseMs)
    {
        _holdSamples = MsToSamples(sampleRateHz, holdMs);
        _attackCoef = OnePoleCoef(sampleRateHz, attackMs);
        _releaseCoef = OnePoleCoef(sampleRateHz, releaseMs);
        _closedEps = 1e-3f;
        Reset();
    }

    /// <summary>Current gain (0 closed .. 1 open). Telemetry/tests.</summary>
    internal float Gain => _gain;

    /// <summary>True while the gate is letting audio through (synced or within hold).</summary>
    internal bool IsOpen => _open;

    /// <summary>
    /// Apply the gate to a block of decoded 48 kHz speech in place, given the
    /// modem's live sync flag. Returns true when the gate is FULLY closed (gain
    /// has reached zero and the hold has expired) so the caller may flush its
    /// decoded-output FIFO — preventing a stale-noise backlog from playing when
    /// sync next returns.
    /// </summary>
    internal bool Process(Span<float> block, bool synced)
    {
        if (synced) _samplesSinceSync = 0;
        bool open = synced || _samplesSinceSync < _holdSamples;
        if (!synced) _samplesSinceSync += block.Length;
        _open = open;

        float target = open ? 1f : 0f;
        float g = _gain;
        for (int i = 0; i < block.Length; i++)
        {
            float coef = target > g ? _attackCoef : _releaseCoef;
            g += (target - g) * coef;
            block[i] *= g;
        }
        _gain = g;

        return !open && g < _closedEps;
    }

    /// <summary>Reset to a closed gate (next over fades in cleanly).</summary>
    internal void Reset()
    {
        _gain = 0f;
        _samplesSinceSync = long.MaxValue / 2;
        _open = false;
    }

    private static int MsToSamples(int fs, double ms) => (int)Math.Max(1, Math.Round(fs * ms / 1000.0));

    // One-pole smoothing coefficient for a target "reach" time constant in ms.
    private static float OnePoleCoef(int fs, double ms)
    {
        double tau = Math.Max(1e-4, ms / 1000.0) * fs; // time constant in samples
        return (float)(1.0 - Math.Exp(-1.0 / tau));
    }
}

/// <summary>
/// Pre-vocoder mic noise gate that ADAPTS to the mic level. The raw mic at this
/// tap is pre-mic-gain, so its absolute level (and noise floor) varies wildly by
/// interface — a fixed dBFS threshold would clip a quiet mic's speech or fail to
/// gate a hot mic's hiss. Instead it tracks the noise floor with an asymmetric
/// follower (falls toward quiet quickly, rises toward loud very slowly, so speech
/// bursts don't drag the floor up) and gates relative to it: open at floor +
/// openMargin dB, close at floor + closeMargin dB. Opens fast (word onsets never
/// clipped) and holds open through inter-word gaps via the hang. Single-threaded
/// by contract (the mic-ingest hot path owns it).
/// </summary>
internal sealed class MicNoiseGate
{
    private readonly float _openMargin;     // linear ratio above the noise floor to open
    private readonly float _closeMargin;    // linear ratio above the noise floor to close
    private readonly float _envAttackCoef;  // envelope follower rise
    private readonly float _envReleaseCoef; // envelope follower fall
    private readonly float _floorDownCoef;  // noise-floor tracks DOWN toward quiet (fast)
    private readonly float _floorUpCoef;    // noise-floor tracks UP toward loud (very slow)
    private readonly float _gateAttackCoef; // gain slew opening
    private readonly float _gateReleaseCoef;// gain slew closing
    private readonly int _hangSamples;      // hold-open after level drops below close
    private readonly float _minFloor;       // absolute floor so digital silence can't collapse thresholds

    private float _env;                     // smoothed |x| envelope
    private float _floor;                   // tracked noise floor
    private float _gain;                    // applied gate gain, 0..1
    private int _hang;                      // samples remaining of hold-open
    private bool _openState;

    /// <summary>Default adaptive mic gate at the given mic rate (48 kHz).</summary>
    internal static MicNoiseGate Default(int sampleRateHz) =>
        new(sampleRateHz, openMarginDb: 14.0, closeMarginDb: 8.0,
            envAttackMs: 2.0, envReleaseMs: 40.0,
            floorDownMs: 50.0, floorUpMs: 3000.0, hangMs: 250.0,
            gateAttackMs: 3.0, gateReleaseMs: 80.0, minFloorDb: -75.0);

    internal MicNoiseGate(
        int sampleRateHz, double openMarginDb, double closeMarginDb,
        double envAttackMs, double envReleaseMs,
        double floorDownMs, double floorUpMs, double hangMs,
        double gateAttackMs, double gateReleaseMs, double minFloorDb)
    {
        _openMargin = DbToLinear(openMarginDb);
        _closeMargin = DbToLinear(closeMarginDb);
        _envAttackCoef = OnePoleCoef(sampleRateHz, envAttackMs);
        _envReleaseCoef = OnePoleCoef(sampleRateHz, envReleaseMs);
        _floorDownCoef = OnePoleCoef(sampleRateHz, floorDownMs);
        _floorUpCoef = OnePoleCoef(sampleRateHz, floorUpMs);
        _gateAttackCoef = OnePoleCoef(sampleRateHz, gateAttackMs);
        _gateReleaseCoef = OnePoleCoef(sampleRateHz, gateReleaseMs);
        _hangSamples = MsToSamples(sampleRateHz, hangMs);
        _minFloor = DbToLinear(minFloorDb);
        Reset();
    }

    /// <summary>Current gate gain (0 closed .. 1 open). Telemetry/tests.</summary>
    internal float Gain => _gain;

    /// <summary>True while the gate is passing the mic (speech detected or within hang).</summary>
    internal bool IsOpen => _openState;

    /// <summary>
    /// Gate a block of 48 kHz mic audio in place. The noise floor is tracked
    /// adaptively; speech (envelope well above the floor) opens the gate fast,
    /// and a pause that decays back to the floor closes it after the hang.
    /// Returns the end-of-block gain.
    /// </summary>
    internal float Process(Span<float> block)
    {
        float env = _env, floor = _floor, gain = _gain;
        int hang = _hang;
        bool open = _openState;
        for (int i = 0; i < block.Length; i++)
        {
            float a = block[i];
            if (a < 0f) a = -a;
            float envCoef = a > env ? _envAttackCoef : _envReleaseCoef;
            env += (a - env) * envCoef;

            // Asymmetric noise-floor tracker: chase the envelope down quickly,
            // creep up slowly — so transient speech never pulls the floor up.
            float floorCoef = env < floor ? _floorDownCoef : _floorUpCoef;
            floor += (env - floor) * floorCoef;
            if (floor < _minFloor) floor = _minFloor;

            float openThresh = floor * _openMargin;
            float closeThresh = floor * _closeMargin;
            if (env >= openThresh) { open = true; hang = _hangSamples; }
            else if (env < closeThresh)
            {
                if (hang > 0) hang--;
                else open = false;
            }
            // Between the two thresholds: hold the current state (hysteresis).

            float target = open ? 1f : 0f;
            float gCoef = target > gain ? _gateAttackCoef : _gateReleaseCoef;
            gain += (target - gain) * gCoef;
            block[i] *= gain;
        }
        _env = env;
        _floor = floor;
        _gain = gain;
        _hang = hang;
        _openState = open;
        return gain;
    }

    /// <summary>Reset to an OPEN gate so the first syllable of an over is never clipped.</summary>
    internal void Reset()
    {
        _env = 0f;
        _floor = _minFloor;
        _gain = 1f;
        _hang = _hangSamples;
        _openState = true;
    }

    private static int MsToSamples(int fs, double ms) => (int)Math.Max(1, Math.Round(fs * ms / 1000.0));

    private static float DbToLinear(double db) => (float)Math.Pow(10.0, db / 20.0);

    private static float OnePoleCoef(int fs, double ms)
    {
        double tau = Math.Max(1e-4, ms / 1000.0) * fs;
        return (float)(1.0 - Math.Exp(-1.0 / tau));
    }
}
