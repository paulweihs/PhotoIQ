using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PhotoWell.Services;

/// <summary>
/// Computes DCT-based perceptual hashes (pHash) for images.
/// Two photos with Hamming distance ≤ 8 are considered near-duplicates.
/// </summary>
public static class PerceptualHashService
{
    private const int DctSize  = 32;  // resize target
    private const int HashSize = 8;   // top-left DCT coefficients per axis

    /// <summary>Compute pHash from an image file. Returns null on any error.</summary>
    public static ulong? ComputeFromFile(string filePath)
    {
        try
        {
            using var img = Image.Load<L8>(filePath);
            return Compute(img);
        }
        catch { return null; }
    }

    /// <summary>Hamming distance between two pHash values (number of differing bits).</summary>
    public static int HammingDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    // ── Internal ──────────────────────────────────────────────────────────────

    private static ulong Compute(Image<L8> img)
    {
        img.Mutate(x => x.Resize(DctSize, DctSize));

        var pixels = new double[DctSize, DctSize];
        img.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < DctSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < DctSize; x++)
                    pixels[y, x] = row[x].PackedValue;
            }
        });

        var dct = Dct2D(pixels);

        // Top-left 8×8 low-frequency coefficients
        var lowFreq = new double[HashSize * HashSize];
        int idx = 0;
        for (int y = 0; y < HashSize; y++)
            for (int x = 0; x < HashSize; x++)
                lowFreq[idx++] = dct[y, x];

        double mean = lowFreq.Average();

        ulong hash = 0;
        for (int i = 0; i < 64; i++)
            if (lowFreq[i] > mean)
                hash |= 1UL << i;

        return hash;
    }

    private static double[,] Dct2D(double[,] input)
    {
        int n = input.GetLength(0);
        var temp   = new double[n, n];
        var output = new double[n, n];

        for (int y = 0; y < n; y++)
        {
            var row = new double[n];
            for (int x = 0; x < n; x++) row[x] = input[y, x];
            var d = Dct1D(row);
            for (int x = 0; x < n; x++) temp[y, x] = d[x];
        }

        for (int x = 0; x < n; x++)
        {
            var col = new double[n];
            for (int y = 0; y < n; y++) col[y] = temp[y, x];
            var d = Dct1D(col);
            for (int y = 0; y < n; y++) output[y, x] = d[y];
        }

        return output;
    }

    private static double[] Dct1D(double[] input)
    {
        int n = input.Length;
        var output = new double[n];
        for (int k = 0; k < n; k++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++)
                sum += input[i] * Math.Cos(Math.PI * k * (2 * i + 1) / (2.0 * n));
            output[k] = sum * (k == 0 ? Math.Sqrt(1.0 / n) : Math.Sqrt(2.0 / n));
        }
        return output;
    }
}
