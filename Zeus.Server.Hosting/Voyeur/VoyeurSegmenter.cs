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
// Zeus is distributed WITHOUT ANY WARRANTY; see ATTRIBUTIONS.md for provenance.

namespace Zeus.Server.Voyeur;

/// <summary>
/// Energy-based transmission ("over") segmenter for Voyeur Mode (zeus-la5
/// Phase 1). Runs on the Voyeur drain thread — NEVER the DSP/RX thread — over
/// the demodulated mono RX audio. Tracks a slow noise-floor follower and gates
/// a segment open when short-window RMS rises a margin above it, closing the
/// segment after a hang period of quiet so speech pauses don't chop one over
/// into many.
///
/// Phase 2 replaces this with Silero VAD (the Phase-0 spike showed even
/// un-gated whisper barely hallucinates on this audio, but VAD still cuts work
/// and sharpens the per-over boundaries). The interface here — feed blocks,
/// get start/continue/end transitions — is exactly what the ASR runner will
/// consume, so Phase 2 is a swap of the detector, not a rewrite of the caller.
/// </summary>
public sealed class VoyeurSegmenter
{
    public enum Transition { Idle, Started, Continuing, Ended }

    public readonly record struct Result(Transition Transition, int DurationMs, float PeakDbfs);

    private readonly int _sampleRate;
    private readonly double _openMarginDb;   // RMS must exceed floor by this to open
    private readonly double _closeMarginDb;  // ...and fall below this to start the hang
    private readonly int _hangSamples;       // quiet duration that ends an over
    private readonly int _minSegmentSamples; // ignore blips shorter than this

    private bool _active;
    private bool _floorSeeded;
    private double _noiseFloor = 1e-4; // linear RMS estimate; seeded to ambient on first block
    private long _activeSamples;
    private long _quietRun;
    private float _peak;

    public VoyeurSegmenter(
        int sampleRate,
        double openMarginDb = 8.0,
        double closeMarginDb = 4.0,
        double hangSeconds = 1.2,
        double minSegmentSeconds = 0.4)
    {
        _sampleRate = sampleRate;
        _openMarginDb = openMarginDb;
        _closeMarginDb = closeMarginDb;
        _hangSamples = (int)(hangSeconds * sampleRate);
        _minSegmentSamples = (int)(minSegmentSeconds * sampleRate);
    }

    /// <summary>True while inside an over (caller should be capturing audio).</summary>
    public bool IsActive => _active;

    /// <summary>
    /// Process one block of mono samples. Returns the transition for this block.
    /// On <see cref="Transition.Ended"/>, DurationMs/PeakDbfs describe the over
    /// that just closed. The caller appends the block to the segment's audio
    /// whenever the result is Started or Continuing.
    /// </summary>
    public Result Process(ReadOnlySpan<float> block)
    {
        if (block.IsEmpty) return new Result(_active ? Transition.Continuing : Transition.Idle, 0, 0);

        // Block RMS + peak.
        double sumsq = 0;
        float peak = 0;
        foreach (var s in block)
        {
            sumsq += (double)s * s;
            float a = s < 0 ? -s : s;
            if (a > peak) peak = a;
        }
        double rms = Math.Sqrt(sumsq / block.Length);

        // Seed the floor to the first block's ambient level so the open gate is
        // relative to THIS receiver's noise, not an arbitrary constant. Without
        // this, a quiet-but-above-1e-4 band floor would false-trigger an over on
        // the very first block. The seed block itself never opens.
        if (!_floorSeeded)
        {
            _noiseFloor = Math.Max(rms, 1e-6);
            _floorSeeded = true;
            return new Result(Transition.Idle, 0, 0);
        }

        // Noise-floor follower: track downward fast (find the quiet floor),
        // upward slowly (don't let a loud over raise the floor and self-close).
        if (rms < _noiseFloor)
            _noiseFloor += (rms - _noiseFloor) * 0.20;
        else
            _noiseFloor += (rms - _noiseFloor) * 0.0005;
        _noiseFloor = Math.Max(_noiseFloor, 1e-6);

        double overDb = 20.0 * Math.Log10(rms / _noiseFloor);

        if (!_active)
        {
            if (overDb >= _openMarginDb)
            {
                _active = true;
                _activeSamples = block.Length;
                _quietRun = 0;
                _peak = peak;
                return new Result(Transition.Started, 0, 0);
            }
            return new Result(Transition.Idle, 0, 0);
        }

        // Active.
        _activeSamples += block.Length;
        if (peak > _peak) _peak = peak;

        if (overDb < _closeMarginDb)
        {
            _quietRun += block.Length;
            if (_quietRun >= _hangSamples)
            {
                // Close the over. Subtract the trailing hang so the reported
                // duration is the speech, not the hang tail.
                long speechSamples = Math.Max(0, _activeSamples - _quietRun);
                bool tooShort = speechSamples < _minSegmentSamples;
                int durMs = (int)(speechSamples * 1000L / _sampleRate);
                float peakDb = _peak > 0 ? 20f * MathF.Log10(_peak) : -120f;
                Reset();
                // A sub-minimum blip (a click, a fragment) is dropped: report
                // Ended only for real overs so the caller doesn't store noise.
                return tooShort
                    ? new Result(Transition.Idle, 0, 0)
                    : new Result(Transition.Ended, durMs, peakDb);
            }
        }
        else
        {
            _quietRun = 0; // speech resumed within the hang window
        }
        return new Result(Transition.Continuing, 0, 0);
    }

    /// <summary>Force-close any in-flight over (session stop). Returns the
    /// Ended result if a real over was open, else Idle.</summary>
    public Result Flush()
    {
        if (!_active) return new Result(Transition.Idle, 0, 0);
        long speechSamples = Math.Max(0, _activeSamples - _quietRun);
        bool tooShort = speechSamples < _minSegmentSamples;
        int durMs = (int)(speechSamples * 1000L / _sampleRate);
        float peakDb = _peak > 0 ? 20f * MathF.Log10(_peak) : -120f;
        Reset();
        return tooShort ? new Result(Transition.Idle, 0, 0) : new Result(Transition.Ended, durMs, peakDb);
    }

    private void Reset()
    {
        _active = false;
        _activeSamples = 0;
        _quietRun = 0;
        _peak = 0;
    }
}
