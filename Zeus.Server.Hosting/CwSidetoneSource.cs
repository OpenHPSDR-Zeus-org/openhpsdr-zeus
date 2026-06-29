// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF), Christian Suarez (N9WAR), and contributors.

namespace Zeus.Server;

/// <summary>
/// Host-side sidetone generator for CW keying. WDSP exports no sidetone
/// API in the libraries we ship (verified 2026-05-24: `nm -D libwdsp.so |
/// grep -i sidetone` is empty across linux-x64, linux-arm64, osx-arm64), so
/// — like the carrier itself in <see cref="CwEngine"/> — we synthesise the
/// monitor tone in C# and mix it into the RX audio buffer just before the
/// pipeline publishes the block. Sinking through the existing RX audio bus
/// means every output that already plays band RX (browser WebSocket, native
/// audio, TCI audio_stream) hears sidetone automatically; no per-sink
/// special case.
///
/// Single instance per process — registered as a singleton. Multiple
/// keying sources can drive it concurrently:
/// <list type="bullet">
///   <item><see cref="CwEngine"/> for macro / cw_msg / cw_macros / raw-key</item>
///   <item><c>ExternalPttService</c> for the HL2 KEY-jack and any other
///         hardware key the radio reports via its C&amp;C status path</item>
/// </list>
/// Calls to <see cref="Down"/> and <see cref="Up"/> are idempotent — two
/// <c>Down</c> in a row do not double the envelope, and an <c>Up</c> with
/// no preceding <c>Down</c> is a no-op. This lets the hardware-key path
/// fire transition events without bookkeeping about who else might be keying.
/// </summary>
public sealed class CwSidetoneSource
{
    /// <summary>Mono RX audio output rate — matches
    /// <c>DspPipelineService.AudioOutputRateHz</c>. The render path is
    /// always called against the RX audio buffer so the rate has to track
    /// the WDSP RXA output.</summary>
    public const int OutputRateHz = 48_000;

    /// <summary>Raised-cosine attack / release length in samples. 5 ms at
    /// 48 kHz = 240 samples. Same envelope shape and length as the on-air
    /// CW envelope in <see cref="CwEngine"/> so the sidetone the operator
    /// hears matches what's actually being transmitted; shorter than 2 ms
    /// produces audible monitor clicks, longer than 10 ms starts to round
    /// off short dits at 30+ WPM.</summary>
    private const int RampSamples = 240;

    private const int MinPitchHz = 200;
    private const int MaxPitchHz = 1200;
    private const double MinGainDb = -60.0;
    private const double MaxGainDb = 0.0;
    private const double SilenceLinear = 1e-4; // < -80 dBFS — operator can't hear

    // --- Hot-path state — read/written only by RenderInto (DSP thread) ---

    private double _phase;
    // Last keyed value rendered. Compared against _keyed on each call so
    // an external Down/Up between ticks triggers an envelope reset.
    private bool _renderedKeyed;
    // Samples since the last edge. Saturates at RampSamples (steady state
    // for plateau / full release). Reset on every edge. Initialised to
    // RampSamples so a freshly-constructed source reports as "release ramp
    // already complete" — the idle fast path returns immediately without
    // generating tone on the never-keyed-yet case.
    private int _envelopePos = RampSamples;

    // --- Cross-thread state ---

    // Latest commanded key state. Volatile so DSP thread sees writes from
    // CwEngine / ExternalPttService without a memory barrier.
    private volatile bool _keyed;

    // Pitch is a single 4-byte field — volatile read/write is atomic on
    // every platform we ship to.
    private volatile int _pitchHz = 600;

    // Gain is read on the hot path and written from REST/UI thread. Wrap
    // in a lock so DSP can't grab a half-updated float, even though the
    // tear window on .NET is theoretical. Reads are fast (uncontended).
    private float _gainLinear = (float)Math.Pow(10.0, -10.0 / 20.0);
    private readonly object _gainLock = new();

    /// <summary>Operator commanded key-down. Idempotent — repeated calls
    /// without an intervening <see cref="Up"/> are a no-op (do not
    /// re-trigger the attack ramp). Cheap; safe from any thread.</summary>
    public void Down() => _keyed = true;

    /// <summary>Operator commanded key-up. Idempotent — repeated calls
    /// without an intervening <see cref="Down"/> are a no-op. Cheap; safe
    /// from any thread.</summary>
    public void Up() => _keyed = false;

    /// <summary>Set the sidetone pitch. Clamped to a sane operator range
    /// (200..1200 Hz — narrower than WDSP's CW bandpass even at extremes
    /// so the sidetone always sits where the receiver expects it).</summary>
    public void SetPitchHz(int hz) => _pitchHz = Math.Clamp(hz, MinPitchHz, MaxPitchHz);

    /// <summary>Set sidetone gain. -60..0 dB. Above 0 risks clipping the
    /// mix with band RX; below -60 is effectively silent.</summary>
    public void SetGainDb(double db)
    {
        double clamped = Math.Clamp(db, MinGainDb, MaxGainDb);
        float linear = (float)Math.Pow(10.0, clamped / 20.0);
        lock (_gainLock) _gainLinear = linear;
    }

    /// <summary>True when the generator is producing non-silence — either
    /// currently keyed, with an unprocessed release edge pending render,
    /// or still inside the release ramp. Test seam.</summary>
    internal bool IsActive => _keyed || _renderedKeyed || _envelopePos < RampSamples;

    /// <summary>
    /// Mix sidetone samples into <paramref name="dst"/> (additive, not
    /// overwrite — caller's existing audio survives). When neither
    /// currently keyed nor mid-release-ramp, returns immediately without
    /// touching <paramref name="dst"/>.
    ///
    /// Called from <c>DspPipelineService.Tick</c> on every audio block.
    /// Must be cheap when idle (the no-keying common case) and must not
    /// allocate.
    /// </summary>
    /// <returns>True if any non-zero samples were written. Useful for the
    /// caller to decide whether to skip a publish that would otherwise be
    /// pure silence.</returns>
    public bool RenderInto(Span<float> dst)
    {
        bool wantKeyed = _keyed;
        if (wantKeyed != _renderedKeyed)
        {
            _renderedKeyed = wantKeyed;
            _envelopePos = 0;
        }
        // Fast path: not keying and the release ramp is done.
        if (!wantKeyed && _envelopePos >= RampSamples) return false;

        int pitch = _pitchHz;
        float gain;
        lock (_gainLock) gain = _gainLinear;
        // Don't synthesize a tone the operator can't hear — saves a sin()
        // per sample when sidetone is dialed to -60 dB. Mirror the spec
        // floor used by the gain setter.
        if (gain < SilenceLinear) return false;

        double phaseStep = 2.0 * Math.PI * pitch / OutputRateHz;
        bool wroteNonZero = false;

        for (int i = 0; i < dst.Length; i++)
        {
            double env;
            if (wantKeyed)
            {
                env = _envelopePos < RampSamples
                    ? 0.5 * (1.0 - Math.Cos(Math.PI * _envelopePos / RampSamples))
                    : 1.0;
            }
            else
            {
                // Release: 1 → 0 raised-cosine, then silent.
                env = _envelopePos < RampSamples
                    ? 0.5 * (1.0 + Math.Cos(Math.PI * _envelopePos / RampSamples))
                    : 0.0;
            }
            if (env > 0.0)
            {
                dst[i] += (float)(gain * env * Math.Sin(_phase));
                wroteNonZero = true;
            }
            _phase += phaseStep;
            if (_phase > 2.0 * Math.PI) _phase -= 2.0 * Math.PI;
            if (_envelopePos < RampSamples) _envelopePos++;
        }
        return wroteNonZero;
    }
}
