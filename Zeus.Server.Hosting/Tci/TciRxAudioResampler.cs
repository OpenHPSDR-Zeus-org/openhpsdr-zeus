// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server.Tci;

internal sealed class TciRxAudioResampler
{
    private float[] _inputBuffer = new float[4096];
    private int _inputStartIndex;
    private int _inputLength;
    private int _nextRealInputIndex;
    private long _nextOutputSampleIndex;
    private double _nextOutputInputPosition;
    private int _inputSampleRate;
    private int _outputSampleRate;
    private WindowedSincKernel? _kernel;

    public void Reset()
    {
        _inputLength = 0;
        _inputStartIndex = 0;
        _nextRealInputIndex = 0;
        _nextOutputSampleIndex = 0;
        _nextOutputInputPosition = 0;
        _inputSampleRate = 0;
        _outputSampleRate = 0;
        _kernel = null;
    }

    public float[] Convert(ReadOnlySpan<float> samples, int inputSampleRate, int outputSampleRate)
    {
        if (samples.Length == 0) return Array.Empty<float>();
        if (inputSampleRate <= 0 || outputSampleRate <= 0 || outputSampleRate > inputSampleRate)
            throw new ArgumentOutOfRangeException(nameof(outputSampleRate), outputSampleRate,
                "TCI RX audio resampling supports positive downsampling rates only.");

        if (_inputSampleRate != inputSampleRate || _outputSampleRate != outputSampleRate)
            ResetForRate(inputSampleRate, outputSampleRate);

        if (inputSampleRate == outputSampleRate)
        {
            var copy = new float[samples.Length];
            CopyFinite(samples, copy);
            return copy;
        }

        AppendInput(samples);
        return ProcessAvailable();
    }

    private void ResetForRate(int inputSampleRate, int outputSampleRate)
    {
        _inputLength = 0;
        _inputStartIndex = -WindowedSincKernel.Half;
        _nextRealInputIndex = 0;
        _nextOutputSampleIndex = 0;
        _nextOutputInputPosition = 0;
        _inputSampleRate = inputSampleRate;
        _outputSampleRate = outputSampleRate;
        _kernel = inputSampleRate == outputSampleRate
            ? null
            : WindowedSincKernel.ForRates(inputSampleRate, outputSampleRate);

        if (inputSampleRate != outputSampleRate)
        {
            EnsureInputCapacity(WindowedSincKernel.Half);
            Array.Clear(_inputBuffer, 0, WindowedSincKernel.Half);
            _inputLength = WindowedSincKernel.Half;
        }
    }

    private float[] ProcessAvailable()
    {
        var kernel = _kernel;
        if (kernel is null) return Array.Empty<float>();

        int availableEnd = _inputStartIndex + _inputLength - 1;
        int realEnd = _nextRealInputIndex;
        long outputLimit = ((long)realEnd * _outputSampleRate + _inputSampleRate - 1) / _inputSampleRate;
        int capacity = Math.Max(0, (int)(outputLimit - _nextOutputSampleIndex));
        if (capacity == 0) return Array.Empty<float>();

        var output = new float[capacity];
        int generated = 0;
        while (_nextOutputSampleIndex < outputLimit)
        {
            _nextOutputInputPosition = _nextOutputSampleIndex * _inputSampleRate / (double)_outputSampleRate;
            int center = (int)Math.Floor(_nextOutputInputPosition);
            double frac = _nextOutputInputPosition - center;
            int phase = (int)Math.Round(frac * WindowedSincKernel.PhaseCount);
            if (phase == WindowedSincKernel.PhaseCount)
            {
                phase = 0;
                center++;
            }

            int first = center - WindowedSincKernel.Half + 1;
            int last = first + WindowedSincKernel.Taps - 1;
            if (last > availableEnd || first < _inputStartIndex) break;

            var coeffs = kernel.Coefficients.AsSpan(phase * WindowedSincKernel.Taps, WindowedSincKernel.Taps);
            int bufferOffset = first - _inputStartIndex;
            double sum = 0;
            for (int tap = 0; tap < WindowedSincKernel.Taps; tap++)
                sum += _inputBuffer[bufferOffset + tap] * coeffs[tap];

            if (generated == output.Length) Array.Resize(ref output, output.Length * 2);
            output[generated++] = (float)Math.Clamp(sum, -1.0, 1.0);
            _nextOutputSampleIndex++;
        }

        _nextOutputInputPosition = _nextOutputSampleIndex * _inputSampleRate / (double)_outputSampleRate;
        CompactInput();

        if (generated == output.Length) return output;
        Array.Resize(ref output, generated);
        return output;
    }

    private void AppendInput(ReadOnlySpan<float> samples)
    {
        EnsureInputCapacity(_inputLength + samples.Length);
        CopyFinite(samples, _inputBuffer.AsSpan(_inputLength, samples.Length));
        _inputLength += samples.Length;
        _nextRealInputIndex += samples.Length;
    }

    private void CompactInput()
    {
        if (_inputLength == 0) return;
        int center = (int)Math.Floor(_nextOutputInputPosition);
        int keepFrom = center - WindowedSincKernel.Taps;
        int drop = keepFrom - _inputStartIndex;
        if (drop <= WindowedSincKernel.Taps || drop >= _inputLength) return;

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

    private static void CopyFinite(ReadOnlySpan<float> src, Span<float> dst)
    {
        for (int i = 0; i < src.Length; i++)
        {
            float v = src[i];
            dst[i] = float.IsFinite(v) ? v : 0f;
        }
    }
}
