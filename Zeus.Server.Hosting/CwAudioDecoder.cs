// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Brian Keating (EI6LF) and contributors.

namespace Zeus.Server;

/// <summary>
/// Headless, sample-driven CW receive decoder.
///
/// The RX audio reaching us is ALREADY bandpass-filtered around the CW pitch
/// by WDSP (the CW filter presets are centred on cw_pitch, typically
/// 250–1000 Hz wide), so a tone-frequency detector is the wrong tool: a
/// single-bin detector at exactly the pitch is brittle (it misses the signal
/// the moment the operator tunes the tone a few Hz off) and redundant with the
/// filter that already isolated the tone.
///
/// Instead we detect *keying* as energy rising above the noise floor — the
/// only thing that works for CW-on-SSB, where there is no true silence between
/// elements (it is signal / noise / signal), so an absolute energy threshold
/// is useless: there is always energy. We measure the *variation* over the
/// noise.
///
/// Pipeline per sample: envelope (one-pole LPF of |x|) → asymmetric noise-floor
/// and signal followers → Schmitt-trigger key decision → debounce → Morse FSM
/// with continuous dit-length estimation. Time is derived from the sample
/// index, never wall-clock.
/// </summary>
internal sealed class CwAudioDecoder
{
    private readonly int _sampleRate;

    // --- envelope key detector ---
    private double _env;
    private double _noiseFloor;
    private double _signal;
    private bool _keyedEnv;
    private long _warmup;
    private readonly double _envAlpha;
    private readonly double _nfRiseAlpha;
    private readonly double _nfFallAlpha;
    private readonly double _sigAttackAlpha;
    private readonly double _sigDecayAlpha;
    private readonly long _warmupSamples;

    // --- key debounce (consecutive same-side samples) ---
    private bool _keyed;
    private int _aboveCount;
    private int _belowCount;
    private readonly int _debounceSamples;

    // --- sample clock ---
    private long _sampleClock;
    private double _lastKeyUpMs;

    // Signal must exceed the noise floor by this factor for the channel to be
    // considered active (~3.4 dB). Below it, the key is forced down.
    private const double ActivityRatio = 2.2;
    // Keyed segments shorter than this are noise blips, not elements (a 50 WPM
    // dit is ~24 ms, so 15 ms is comfortably below any hand-sent speed).
    private const double MinElementMs = 15;

    // --- timing estimator ---
    private readonly List<double> _durations = new();
    private const int MaxDurations = 20;
    private double _ditMs = 24; // 20 WPM default
    private double _dahThr;
    private double _letterGapThr;
    private double _wordGapThr;

    // --- Morse FSM ---
    private string _pattern = string.Empty;
    private double _lastEdgeMs;
    private double _elementGapStartMs;
    private bool _inWord;

    public int Wpm => (int)Math.Round(1200.0 / (_ditMs * 2));
    public double SnrDb { get; private set; }
    public double Confidence { get; private set; }

    public CwAudioDecoder(int sampleRate)
    {
        _sampleRate = sampleRate;
        // One-pole smoothing coefficient for a time constant in ms:
        //   alpha = 1 − exp(−1 / (tau·fs))
        double Alpha(double tauMs) => 1 - Math.Exp(-1.0 / ((tauMs / 1000.0) * sampleRate));
        _envAlpha = Alpha(8);      // envelope tracks keying, smooths tone + noise spikes
        _nfRiseAlpha = Alpha(800); // noise floor creeps up slowly...
        _nfFallAlpha = Alpha(20);  // ...but drops fast to follow quiet
        _sigAttackAlpha = Alpha(5);   // signal jumps to the element level...
        _sigDecayAlpha = Alpha(500);  // ...and holds it through gaps
        _warmupSamples = (long)Math.Round(sampleRate * 0.25); // 250 ms to settle
        _debounceSamples = Math.Max(1, (int)Math.Round(sampleRate * 0.002)); // ~2 ms
        RecomputeThresholds();
    }

    /// <summary>Feed a block of audio samples; invokes <paramref name="onChar"/>
    /// for each decoded character (letters/digits/punctuation, and ' ' on a
    /// word gap), in order.</summary>
    public void Process(ReadOnlySpan<float> samples, Action<char> onChar)
    {
        foreach (var s in samples)
            ProcessOne(s, onChar);
    }

    private void ProcessOne(float sample, Action<char> onChar)
    {
        bool above = Envelope(sample);
        double now = (_sampleClock / (double)_sampleRate) * 1000.0;
        _sampleClock++;

        // Debounce: flip the key state only after enough consecutive samples
        // agree. A fixed debounce delays both edges equally, preserving
        // element/gap durations.
        if (above) { _aboveCount++; _belowCount = 0; }
        else { _belowCount++; _aboveCount = 0; }

        if (!_keyed && _aboveCount >= _debounceSamples)
        {
            _keyed = true;
            OnKeyDown(now, onChar);
        }
        else if (_keyed && _belowCount >= _debounceSamples)
        {
            _keyed = false;
            _lastKeyUpMs = now;
            OnKeyUp(now);
        }

        // End-of-letter flush: emit a pending character after a letter gap of
        // silence (the FSM otherwise only emits on the *next* key-down).
        if (!_keyed && _lastKeyUpMs > 0 && now - _lastKeyUpMs > _ditMs * 3)
        {
            var c = EmitChar();
            if (c.HasValue) { _lastKeyUpMs = 0; onChar(c.Value); }
        }
    }

    // Envelope detector + Schmitt trigger. Returns the hysteretic keyed state.
    private bool Envelope(float sample)
    {
        double mag = Math.Abs(sample);
        _env += _envAlpha * (mag - _env);

        // Asymmetric followers: pick the rise/fall coefficient by direction.
        _noiseFloor += (_env < _noiseFloor ? _nfFallAlpha : _nfRiseAlpha) * (_env - _noiseFloor);
        _signal += (_env > _signal ? _sigAttackAlpha : _sigDecayAlpha) * (_env - _signal);

        SnrDb = _noiseFloor < 1e-9 ? 0 : 10 * Math.Log10(Math.Max(_signal, _noiseFloor) / _noiseFloor);
        Confidence = Math.Clamp(SnrDb / 10.0, 0, 1);

        // Hold off deciding until the followers settle (no cold-start element).
        if (_warmup < _warmupSamples) { _warmup++; return false; }

        // Activity gate: require the tracked signal to stand clearly above the
        // noise floor before treating the channel as carrying CW at all. Pure
        // band noise has signal ≈ noiseFloor (no keying structure), so this
        // suppresses noise-only periods; a genuine CW tone sits well above it.
        if (_signal < _noiseFloor * ActivityRatio)
        {
            _keyedEnv = false;
            return false;
        }

        double span = _signal - _noiseFloor;
        if (span <= 1e-6) { _keyedEnv = false; return false; }

        double hi = _noiseFloor + 0.62 * span;
        double lo = _noiseFloor + 0.4 * span;
        if (!_keyedEnv && _env > hi) _keyedEnv = true;
        else if (_keyedEnv && _env < lo) _keyedEnv = false;
        return _keyedEnv;
    }

    private void OnKeyDown(double now, Action<char> onChar)
    {
        if (_elementGapStartMs > 0)
        {
            double gap = now - _elementGapStartMs;
            if (gap >= _letterGapThr)
            {
                var c = EmitChar();
                if (c.HasValue) onChar(c.Value);
            }
            if (gap >= _wordGapThr && _inWord)
            {
                _inWord = false;
                onChar(' ');
            }
        }
        // Always mark the start of this element — including on the emit path,
        // or the first element after a gap would measure its duration from the
        // previous element's key-down and always classify as a dah.
        _lastEdgeMs = now;
    }

    private void OnKeyUp(double now)
    {
        double duration = now - _lastEdgeMs;
        // Reject sub-element noise blips: a keyed segment shorter than the
        // fastest plausible dit (well under hand-sent speeds) is noise, not an
        // element. Ignore it entirely so it neither adds a dot nor poisons the
        // dit-length estimator.
        if (duration < MinElementMs) return;
        char element = duration < _dahThr ? '.' : '-';
        Record(duration);
        _pattern += element;
        _elementGapStartMs = now;
        _inWord = true;
    }

    private char? EmitChar()
    {
        if (_pattern.Length == 0) return null;
        bool known = MorseDecode.TryGetValue(_pattern, out var ch);
        _pattern = string.Empty;
        // Drop unrecognised patterns rather than emitting '?', so noise that
        // slips through the detector doesn't spray garbage into the stream.
        return known ? ch : null;
    }

    // Continuous dit-length estimation from observed on-key durations.
    private void Record(double durationMs)
    {
        _durations.Add(durationMs);
        if (_durations.Count > MaxDurations) _durations.RemoveAt(0);
        if (_durations.Count < 5) return;

        var sorted = new List<double>(_durations);
        sorted.Sort();
        int half = sorted.Count / 2;
        if (half == 0) return;
        // Lower half are likely dits (operators send more dits than dahs).
        double median = sorted[half / 2];
        double clamped = Math.Clamp(median, 10, 120); // 5–60 WPM
        _ditMs = _ditMs * 0.8 + clamped * 0.2; // smooth
        RecomputeThresholds();
    }

    private void RecomputeThresholds()
    {
        _dahThr = _ditMs * 1.5;
        _letterGapThr = _ditMs * 3.0;
        _wordGapThr = _ditMs * 5.5;
    }

    private static readonly Dictionary<string, char> MorseDecode = new()
    {
        [".-"] = 'A', ["-..."] = 'B', ["-.-."] = 'C', ["-.."] = 'D', ["."] = 'E',
        ["..-."] = 'F', ["--."] = 'G', ["...."] = 'H', [".."] = 'I', [".---"] = 'J',
        ["-.-"] = 'K', [".-.."] = 'L', ["--"] = 'M', ["-."] = 'N', ["---"] = 'O',
        [".--."] = 'P', ["--.-"] = 'Q', [".-."] = 'R', ["..."] = 'S', ["-"] = 'T',
        ["..-"] = 'U', ["...-"] = 'V', [".--"] = 'W', ["-..-"] = 'X', ["-.--"] = 'Y',
        ["--.."] = 'Z',
        ["-----"] = '0', [".----"] = '1', ["..---"] = '2', ["...--"] = '3', ["....-"] = '4',
        ["....."] = '5', ["-...."] = '6', ["--..."] = '7', ["---.."] = '8', ["----."] = '9',
        [".-.-.-"] = '.', ["--..--"] = ',', ["..--.."] = '?', ["-..-."] = '/',
        [".--.-."] = '@', ["-...-"] = '=', ["-....-"] = '-', [".-.-."] = '+',
    };
}
