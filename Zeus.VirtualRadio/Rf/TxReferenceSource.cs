// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

namespace Zeus.VirtualRadio.Rf;

/// <summary>
/// The clean TX-DAC "reference" the radio loops back as the PureSignal TX
/// feedback. When the host streams TX-IQ on UDP 1029 (a transmitting Zeus) the
/// real exciter samples drive it; when no TX-IQ has arrived — a bench test that
/// keys without feeding IQ — it falls back to a synthesized clean carrier so PS
/// always has a finite, well-conditioned reference. Thread-safe: the 1029
/// receive loop ingests while the DDC0 send loop drains.
/// </summary>
internal sealed class TxReferenceSource
{
    private const double TwoPi = 2.0 * Math.PI;

    private readonly object _gate = new();
    private readonly double[] _ring;        // interleaved I/Q
    private readonly int _capacityComplex;
    private int _head;                      // next write (complex index)
    private int _count;                     // available complex samples

    // Synthesized fallback carrier: a clean tone at a small baseband offset so
    // the reference is a genuine signal, not DC. Phase-continuous.
    private readonly double _fallbackInc;
    private readonly double _fallbackAmp;
    private double _fallbackPhase;

    /// <param name="sampleRateHz">DDC rate the feedback is clocked at (for the
    /// synthesized fallback tone's baseband offset).</param>
    /// <param name="fallbackToneHz">Baseband offset of the fallback carrier.</param>
    /// <param name="fallbackAmp">Fallback carrier amplitude in full-scale units.</param>
    /// <param name="capacityComplex">Ring capacity in complex samples.</param>
    public TxReferenceSource(
        double sampleRateHz,
        double fallbackToneHz = 1_000.0,
        double fallbackAmp = 0.5,
        int capacityComplex = 8192)
    {
        _capacityComplex = Math.Max(256, capacityComplex);
        _ring = new double[_capacityComplex * 2];
        double rate = sampleRateHz <= 0 ? 192_000.0 : sampleRateHz;
        _fallbackInc = TwoPi * fallbackToneHz / rate;
        _fallbackAmp = fallbackAmp;
    }

    /// <summary>True once the host has streamed at least one TX-IQ packet.</summary>
    public bool HasHostIq { get { lock (_gate) return _count > 0; } }

    /// <summary>
    /// Ingest interleaved float I/Q from a TX-IQ packet (UDP 1029). Overwrites
    /// the oldest samples when the ring is full (a transmitting host outruns a
    /// slow drain — the freshest reference is what PS wants).
    /// </summary>
    public void Ingest(ReadOnlySpan<float> interleaved)
    {
        int complex = interleaved.Length / 2;
        if (complex <= 0) return;
        lock (_gate)
        {
            for (int i = 0; i < complex; i++)
            {
                _ring[_head * 2] = interleaved[2 * i];
                _ring[_head * 2 + 1] = interleaved[2 * i + 1];
                _head = (_head + 1) % _capacityComplex;
                if (_count < _capacityComplex) _count++;
            }
        }
    }

    /// <summary>
    /// Fill <paramref name="referenceInterleaved"/> with
    /// <paramref name="complexCount"/> reference samples, draining the host ring
    /// when available and synthesizing the fallback carrier otherwise.
    /// </summary>
    public void Fill(Span<double> referenceInterleaved, int complexCount)
    {
        if (referenceInterleaved.Length < complexCount * 2)
            throw new ArgumentException("reference buffer too small", nameof(referenceInterleaved));

        lock (_gate)
        {
            for (int i = 0; i < complexCount; i++)
            {
                if (_count > 0)
                {
                    // Oldest sample is (head - count) modulo capacity.
                    int tail = (_head - _count + _capacityComplex) % _capacityComplex;
                    referenceInterleaved[2 * i] = _ring[tail * 2];
                    referenceInterleaved[2 * i + 1] = _ring[tail * 2 + 1];
                    _count--;
                }
                else
                {
                    referenceInterleaved[2 * i] = _fallbackAmp * Math.Cos(_fallbackPhase);
                    referenceInterleaved[2 * i + 1] = _fallbackAmp * Math.Sin(_fallbackPhase);
                    _fallbackPhase += _fallbackInc;
                    if (_fallbackPhase > TwoPi) _fallbackPhase -= TwoPi;
                }
            }
        }
    }
}
