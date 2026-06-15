// SPDX-License-Identifier: GPL-2.0-or-later

namespace Zeus.Server;

internal sealed class WindowedSincKernel
{
    public const int Taps = 96;
    public const int Half = Taps / 2;
    public const int PhaseCount = 2048;

    public readonly float[] Coefficients;

    private WindowedSincKernel(float[] coefficients)
    {
        Coefficients = coefficients;
    }

    public static WindowedSincKernel ForRates(int inputSampleRate, int outputSampleRate)
    {
        var coeffs = new float[PhaseCount * Taps];
        double cutoff = 0.475 * Math.Min(1.0, outputSampleRate / (double)inputSampleRate);

        for (int phase = 0; phase < PhaseCount; phase++)
        {
            double frac = phase / (double)PhaseCount;
            double dc = 0;
            int phaseOffset = phase * Taps;
            for (int tap = 0; tap < Taps; tap++)
            {
                double distance = frac + Half - 1 - tap;
                double window = BlackmanHarris(tap);
                double coeff = 2.0 * cutoff * Sinc(2.0 * cutoff * distance) * window;
                coeffs[phaseOffset + tap] = (float)coeff;
                dc += coeff;
            }

            if (Math.Abs(dc) < 1e-12) continue;
            double scale = 1.0 / dc;
            for (int tap = 0; tap < Taps; tap++)
                coeffs[phaseOffset + tap] = (float)(coeffs[phaseOffset + tap] * scale);
        }

        return new WindowedSincKernel(coeffs);
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
        double x = 2.0 * Math.PI * tap / (Taps - 1);
        return a0
            - a1 * Math.Cos(x)
            + a2 * Math.Cos(2.0 * x)
            - a3 * Math.Cos(3.0 * x);
    }
}
