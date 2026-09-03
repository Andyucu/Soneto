using System;
using System.Numerics;

namespace s1b_audio;

/// <summary>
/// Minimal iterative radix-2 Cooley-Tukey FFT. Only used to validate the
/// resampler's anti-aliasing behaviour (magnitude spectrum), not for
/// production use.
/// </summary>
public static class Fft
{
    /// <summary>In-place FFT. Length of <paramref name="data"/> must be a power of two.</summary>
    public static void Forward(Complex[] data)
    {
        int n = data.Length;
        if (n == 0 || (n & (n - 1)) != 0)
            throw new ArgumentException("FFT length must be a power of two.", nameof(data));

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
                (data[i], data[j]) = (data[j], data[i]);
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            var wlen = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = data[i + k];
                    var v = data[i + k + len / 2] * w;
                    data[i + k] = u + v;
                    data[i + k + len / 2] = u - v;
                    w *= wlen;
                }
            }
        }
    }

    /// <summary>
    /// Returns magnitude spectrum (length n/2+1, DC..Nyquist) of a real
    /// signal, zero-padded/truncated to the next power of two, with a Hann
    /// window applied first to control spectral leakage.
    /// </summary>
    public static double[] MagnitudeSpectrum(ReadOnlySpan<float> real, out int fftSize, out double binHz, double sampleRate)
    {
        int n = 1;
        while (n < real.Length) n <<= 1;
        fftSize = n;
        binHz = sampleRate / n;

        var data = new Complex[n];
        for (int i = 0; i < real.Length; i++)
        {
            // Hann window.
            double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (real.Length - 1));
            data[i] = new Complex(real[i] * w, 0);
        }

        Forward(data);

        var mag = new double[n / 2 + 1];
        for (int i = 0; i <= n / 2; i++)
            mag[i] = data[i].Magnitude;

        return mag;
    }
}
