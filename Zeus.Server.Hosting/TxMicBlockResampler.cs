// SPDX-License-Identifier: GPL-2.0-or-later
//
// Streaming mono float32 sample-rate converter for sources that feed the
// TX mic-block contract: 960 samples at 48 kHz.

namespace Zeus.Server;

internal delegate void TxMicBlockReady(ReadOnlySpan<float> block);

internal readonly record struct TxMicBlockResamplerResult(
    int OutputSamplesGenerated,
    int OutputSamplesEmitted);

internal sealed class TxMicBlockResampler
{
    public const int OutputSampleRate = DspPipelineService.AudioOutputRateHz;
    public const int OutputBlockSamples = 960;
    public const int OutputBlockBytes = OutputBlockSamples * sizeof(float);
    public const int MinInputSampleRate = 8_000;
    public const int MaxInputSampleRate = 768_000;

    private const int FilterTaps = 96;
    private const int FilterHalf = FilterTaps / 2;
    private const int PhaseCount = 2048;

    private readonly TxMicBlockReady _emit;
    private readonly float[] _outputBlock = new float[OutputBlockSamples];

    private float[] _inputBuffer = new float[4096];
    private int _inputStartIndex;
    private int _inputLength;
    private int _nextRealInputIndex;
    private long _nextOutputSampleIndex;
    private double _nextOutputInputPosition;
    private int _outputFill;
    private int _inputSampleRate;
    private Kernel? _kernel;

    public TxMicBlockResampler(TxMicBlockReady emit)
    {
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
    }

    public int PendingOutputSamples => _outputFill;

    public static bool IsSupportedInputSampleRate(int sampleRate)
        => sampleRate >= MinInputSampleRate && sampleRate <= MaxInputSampleRate;

    public TxMicBlockResamplerResult Accept(ReadOnlySpan<float> samples, int inputSampleRate)
    {
        if (samples.Length == 0) return default;
        if (!IsSupportedInputSampleRate(inputSampleRate))
            throw new ArgumentOutOfRangeException(nameof(inputSampleRate), inputSampleRate,
                $"Sample rate must be {MinInputSampleRate}..{MaxInputSampleRate} Hz.");

        if (_inputSampleRate != inputSampleRate)
            ResetForRate(inputSampleRate);

        if (inputSampleRate == OutputSampleRate)
        {
            return AppendOutputSamples(samples);
        }

        AppendInput(samples);
        return ProcessAvailable(final: false, generated: 0, emitted: 0);
    }

    public TxMicBlockResamplerResult FlushZeroPadded()
    {
        int generated = 0;
        int emitted = 0;

        if (_inputSampleRate != 0 && _inputSampleRate != OutputSampleRate && _inputLength > 0)
        {
            AppendPadding(FilterTaps);
            var result = ProcessAvailable(final: true, generated, emitted);
            generated += result.OutputSamplesGenerated;
            emitted += result.OutputSamplesEmitted;
        }

        if (_outputFill > 0)
        {
            Array.Clear(_outputBlock, _outputFill, OutputBlockSamples - _outputFill);
            _emit(_outputBlock);
            emitted += OutputBlockSamples;
            _outputFill = 0;
        }

        return new TxMicBlockResamplerResult(generated, emitted);
    }

    public void Reset()
    {
        _inputLength = 0;
        _inputStartIndex = 0;
        _nextRealInputIndex = 0;
        _nextOutputSampleIndex = 0;
        _nextOutputInputPosition = 0;
        _outputFill = 0;
        _inputSampleRate = 0;
        _kernel = null;
    }

    private void ResetForRate(int inputSampleRate)
    {
        _inputLength = 0;
        _inputStartIndex = -FilterHalf;
        _nextRealInputIndex = 0;
        _nextOutputSampleIndex = 0;
        _nextOutputInputPosition = 0;
        _outputFill = 0;
        _inputSampleRate = inputSampleRate;
        _kernel = inputSampleRate == OutputSampleRate ? null : Kernel.ForRate(inputSampleRate);

        if (inputSampleRate != OutputSampleRate)
        {
            EnsureInputCapacity(FilterHalf);
            Array.Clear(_inputBuffer, 0, FilterHalf);
            _inputLength = FilterHalf;
        }
    }

    private TxMicBlockResamplerResult AppendOutputSamples(ReadOnlySpan<float> samples)
    {
        int generated = 0;
        int emitted = 0;
        int pos = 0;

        if (_outputFill > 0)
        {
            int need = OutputBlockSamples - _outputFill;
            int take = Math.Min(need, samples.Length);
            CopyFinite(samples[..take], _outputBlock.AsSpan(_outputFill, take));
            _outputFill += take;
            generated += take;
            pos += take;
            if (_outputFill == OutputBlockSamples)
            {
                _emit(_outputBlock);
                emitted += OutputBlockSamples;
                _outputFill = 0;
            }
        }

        while (samples.Length - pos >= OutputBlockSamples)
        {
            var src = samples.Slice(pos, OutputBlockSamples);
            if (AllFinite(src))
            {
                _emit(src);
            }
            else
            {
                CopyFinite(src, _outputBlock);
                _emit(_outputBlock);
            }
            generated += OutputBlockSamples;
            emitted += OutputBlockSamples;
            pos += OutputBlockSamples;
        }

        int rem = samples.Length - pos;
        if (rem > 0)
        {
            CopyFinite(samples.Slice(pos, rem), _outputBlock.AsSpan(0, rem));
            _outputFill = rem;
            generated += rem;
        }

        return new TxMicBlockResamplerResult(generated, emitted);
    }

    private TxMicBlockResamplerResult ProcessAvailable(bool final, int generated, int emitted)
    {
        var kernel = _kernel;
        if (kernel is null) return new TxMicBlockResamplerResult(generated, emitted);

        int availableEnd = _inputStartIndex + _inputLength - 1;
        int realEnd = _nextRealInputIndex;
        long outputLimit = ((long)realEnd * OutputSampleRate + _inputSampleRate - 1) / _inputSampleRate;

        while (_nextOutputSampleIndex < outputLimit)
        {
            _nextOutputInputPosition = _nextOutputSampleIndex * _inputSampleRate / (double)OutputSampleRate;
            int center = (int)Math.Floor(_nextOutputInputPosition);
            double frac = _nextOutputInputPosition - center;
            int phase = (int)Math.Round(frac * PhaseCount);
            if (phase == PhaseCount)
            {
                phase = 0;
                center++;
            }

            int first = center - FilterHalf + 1;
            int last = first + FilterTaps - 1;
            if (last > availableEnd) break;
            if (first < _inputStartIndex) break;

            var coeffs = kernel.Coefficients.AsSpan(phase * FilterTaps, FilterTaps);
            int bufferOffset = first - _inputStartIndex;
            double sum = 0;
            for (int tap = 0; tap < FilterTaps; tap++)
                sum += _inputBuffer[bufferOffset + tap] * coeffs[tap];

            AppendOutputSample((float)Math.Clamp(sum, -1.0, 1.0));
            generated++;
            if (_outputFill == 0) emitted += OutputBlockSamples;
            _nextOutputSampleIndex++;
        }

        _nextOutputInputPosition = _nextOutputSampleIndex * _inputSampleRate / (double)OutputSampleRate;

        CompactInput(final);
        return new TxMicBlockResamplerResult(generated, emitted);
    }

    private void AppendOutputSample(float sample)
    {
        _outputBlock[_outputFill++] = float.IsFinite(sample) ? sample : 0f;
        if (_outputFill < OutputBlockSamples) return;

        _emit(_outputBlock);
        _outputFill = 0;
    }

    private void AppendInput(ReadOnlySpan<float> samples)
    {
        EnsureInputCapacity(_inputLength + samples.Length);
        CopyFinite(samples, _inputBuffer.AsSpan(_inputLength, samples.Length));
        _inputLength += samples.Length;
        _nextRealInputIndex += samples.Length;
    }

    private void AppendPadding(int samples)
    {
        EnsureInputCapacity(_inputLength + samples);
        Array.Clear(_inputBuffer, _inputLength, samples);
        _inputLength += samples;
    }

    private void CompactInput(bool final)
    {
        if (_inputLength == 0) return;
        int center = (int)Math.Floor(_nextOutputInputPosition);
        int keepFrom = final ? center - FilterHalf + 1 : center - FilterTaps;
        int drop = keepFrom - _inputStartIndex;
        if (drop <= FilterTaps || drop >= _inputLength) return;

        int remain = _inputLength - drop;
        Array.Copy(_inputBuffer, drop, _inputBuffer, 0, remain);
        _inputStartIndex += drop;
        _inputLength = remain;
    }

    private void EnsureInputCapacity(int required)
    {
        if (_inputBuffer.Length >= required) return;
        int newSize = _inputBuffer.Length;
        while (newSize < required) newSize *= 2;
        Array.Resize(ref _inputBuffer, newSize);
    }

    private static bool AllFinite(ReadOnlySpan<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            if (!float.IsFinite(samples[i])) return false;
        }
        return true;
    }

    private static void CopyFinite(ReadOnlySpan<float> src, Span<float> dst)
    {
        for (int i = 0; i < src.Length; i++)
        {
            float v = src[i];
            dst[i] = float.IsFinite(v) ? v : 0f;
        }
    }

    private sealed class Kernel
    {
        public readonly float[] Coefficients;

        private Kernel(float[] coefficients)
        {
            Coefficients = coefficients;
        }

        public static Kernel ForRate(int inputSampleRate)
        {
            var coeffs = new float[PhaseCount * FilterTaps];
            double cutoff = 0.475 * Math.Min(1.0, OutputSampleRate / (double)inputSampleRate);

            for (int phase = 0; phase < PhaseCount; phase++)
            {
                double frac = phase / (double)PhaseCount;
                double dc = 0;
                int phaseOffset = phase * FilterTaps;
                for (int tap = 0; tap < FilterTaps; tap++)
                {
                    double distance = frac + FilterHalf - 1 - tap;
                    double window = BlackmanHarris(tap);
                    double coeff = 2.0 * cutoff * Sinc(2.0 * cutoff * distance) * window;
                    coeffs[phaseOffset + tap] = (float)coeff;
                    dc += coeff;
                }

                if (Math.Abs(dc) < 1e-12) continue;
                double scale = 1.0 / dc;
                for (int tap = 0; tap < FilterTaps; tap++)
                    coeffs[phaseOffset + tap] = (float)(coeffs[phaseOffset + tap] * scale);
            }

            return new Kernel(coeffs);
        }

        private static double Sinc(double x)
        {
            if (Math.Abs(x) < 1e-12) return 1.0;
            double pix = Math.PI * x;
            return Math.Sin(pix) / pix;
        }

        private static double BlackmanHarris(int tap)
        {
            const double a0 = 0.35875;
            const double a1 = 0.48829;
            const double a2 = 0.14128;
            const double a3 = 0.01168;
            double x = 2.0 * Math.PI * tap / (FilterTaps - 1);
            return a0
                - a1 * Math.Cos(x)
                + a2 * Math.Cos(2.0 * x)
                - a3 * Math.Cos(3.0 * x);
        }
    }
}
