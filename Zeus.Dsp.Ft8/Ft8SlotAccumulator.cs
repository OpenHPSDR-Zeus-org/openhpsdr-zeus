// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF),
//                         Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// Ft8SlotAccumulator — buffers 12 kHz mono audio into UTC-aligned transmit/
// receive slots (15 s FT8 / 7.5 s FT4 / 120 s WSPR) and hands a completed slot
// back when the wall clock crosses the next boundary. Time is passed in to Add
// so the accumulator is deterministic and unit-testable (no hidden clock).
//
// Alignment is block-granular: each arriving block is assigned to the slot of
// its arrival instant. FT8 only occupies ~12.64 s of the 15 s slot and the
// decoder tolerates ±2.5 s of time offset (it reports the residual as DT), so
// block-level alignment is sufficient; fine sample-accurate alignment is a
// later refinement.

namespace Zeus.Dsp.Ft8;

/// <summary>A finished slot of 12 kHz mono audio ready to decode.</summary>
public readonly record struct Ft8Slot(float[] Samples, DateTime SlotStartUtc, long SlotIndex);

public sealed class Ft8SlotAccumulator
{
    private readonly double _slotSeconds;
    private readonly int _capacity; // samples per slot at 12 kHz
    private float[] _buf;
    private int _count;
    private long _currentSlot = -1;

    public Ft8SlotAccumulator(double slotSeconds = 15.0, int sampleRate = Ft8Resampler.OutputRate)
    {
        _slotSeconds = slotSeconds;
        _capacity = (int)(slotSeconds * sampleRate);
        _buf = new float[_capacity];
        _count = 0;
    }

    private long SlotIndexOf(DateTime utc) =>
        (long)Math.Floor((utc - DateTime.UnixEpoch).TotalSeconds / _slotSeconds);

    /// <summary>
    /// Append a block of 12 kHz samples that arrived at <paramref name="nowUtc"/>.
    /// Returns the just-completed slot when this block belongs to a later slot
    /// than the one in progress, otherwise null. The completed slot's buffer is a
    /// fresh copy owned by the caller.
    /// </summary>
    public Ft8Slot? Add(ReadOnlySpan<float> samples, DateTime nowUtc)
    {
        long slot = SlotIndexOf(nowUtc);
        Ft8Slot? completed = null;

        if (_currentSlot < 0)
        {
            _currentSlot = slot; // first block ever
        }
        else if (slot != _currentSlot)
        {
            // Boundary crossed: emit the slot that just ended (if it has audio).
            if (_count > 0)
            {
                var copy = new float[_count];
                Array.Copy(_buf, copy, _count);
                completed = new Ft8Slot(copy, SlotStartUtc(_currentSlot), _currentSlot);
            }
            _count = 0;
            _currentSlot = slot;
        }

        // Append, capping at one slot's worth (excess within a block is dropped).
        int room = _capacity - _count;
        int take = Math.Min(room, samples.Length);
        if (take > 0)
        {
            samples[..take].CopyTo(_buf.AsSpan(_count));
            _count += take;
        }
        return completed;
    }

    private DateTime SlotStartUtc(long slotIndex) =>
        DateTime.UnixEpoch.AddSeconds(slotIndex * _slotSeconds);

    /// <summary>Discard the in-progress slot (e.g. on disable / band change).</summary>
    public void Reset()
    {
        _count = 0;
        _currentSlot = -1;
    }
}
