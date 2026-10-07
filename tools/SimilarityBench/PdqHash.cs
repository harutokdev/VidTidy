using LocalMediaAI.Core;

namespace SimilarityBench;

/// <summary>A 256-bit PDQ fingerprint (64 hex digits) and how much picture detail it was made from (0–100).</summary>
public readonly record struct PdqFingerprint(string Hex, int Quality);

/// <summary>
/// PDQ perceptual hash, ported from Meta's ThreatExchange reference (pdq/cpp, BSD licence; Copyright (c) Meta Platforms,
/// Inc. and affiliates). Steps: luma, two passes of a Jarosz box filter, 64x64 decimation, 16x16 DCT without the DC term,
/// one bit per coefficient above the median. Float arithmetic follows the reference so hashes match it. Pure, no I/O.
/// </summary>
public static class PdqHasher
{
    public const string Algorithm = "pdq-256-v1";
    public const int HashBits = 256;
    public const int HexLength = 64;

    /// <summary>The reference treats frames scoring below this as too plain to compare reliably.</summary>
    public const int LowQualityBelow = 50;

    /// <summary>The reference's usual match limit: at most this many of the 256 bits differ.</summary>
    public const int MatchDistance = 31;

    private const int MinimumHashableDimension = 5;
    private const int BufferSize = 64;
    private const int DctSize = 16;
    private const int JaroszPasses = 2;

    private static readonly float[] DctMatrix = CreateDctMatrix();

    /// <summary>Luma of packed 8-bit RGB, with the reference's coefficients.</summary>
    public static float[] LumaFromRgb(ReadOnlySpan<byte> rgb, int width, int height)
    {
        Validate(rgb.Length, width * 3, height);
        var luma = new float[width * height];
        for (var i = 0; i < luma.Length; i++)
            luma[i] = 0.299f * rgb[i * 3] + 0.587f * rgb[i * 3 + 1] + 0.114f * rgb[i * 3 + 2];
        return luma;
    }

    /// <summary>Luma of the part of a grey frame inside <paramref name="crop"/> (for frames with black bars cut off).</summary>
    public static float[] LumaFromGray(ReadOnlySpan<byte> gray, int width, int height, FrameCrop crop)
    {
        Validate(gray.Length, width, height);
        var cropWidth = width - crop.Left - crop.Right;
        var cropHeight = height - crop.Top - crop.Bottom;
        if (crop.Left < 0 || crop.Top < 0 || crop.Right < 0 || crop.Bottom < 0 || cropWidth < 1 || cropHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(crop), "The crop leaves no picture.");
        var luma = new float[cropWidth * cropHeight];
        for (var y = 0; y < cropHeight; y++)
            for (var x = 0; x < cropWidth; x++)
                luma[y * cropWidth + x] = gray[(y + crop.Top) * width + x + crop.Left];
        return luma;
    }

    public static PdqFingerprint Compute(ReadOnlySpan<float> luma, int width, int height)
    {
        Validate(luma.Length, width, height);
        if (width < MinimumHashableDimension || height < MinimumHashableDimension)
            return new PdqFingerprint(new string('0', HexLength), 0);

        var small = width == BufferSize && height == BufferSize ? luma.ToArray() : Downscale(luma, width, height);
        var quality = Quality(small);
        var dct = Dct16(small);
        var median = Torben(dct);
        var words = new ushort[DctSize];
        for (var i = 0; i < DctSize; i++)
            for (var j = 0; j < DctSize; j++)
                if (dct[i * DctSize + j] > median)
                    words[i] |= (ushort)(1 << j);
        var hex = new System.Text.StringBuilder(HexLength);
        for (var w = DctSize - 1; w >= 0; w--)
            hex.Append(words[w].ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
        return new PdqFingerprint(hex.ToString(), quality);
    }

    public static int HammingDistance(string firstHex, string secondHex)
    {
        if (firstHex is null || secondHex is null || firstHex.Length != HexLength || secondHex.Length != HexLength)
            throw new ArgumentException($"PDQ values must be {HexLength}-digit hexadecimal strings.");
        var first = Convert.FromHexString(firstHex);
        var second = Convert.FromHexString(secondHex);
        var distance = 0;
        for (var i = 0; i < first.Length; i++)
            distance += System.Numerics.BitOperations.PopCount((uint)(first[i] ^ second[i]));
        return distance;
    }

    private static float[] Downscale(ReadOnlySpan<float> luma, int width, int height)
    {
        var buffer1 = luma.ToArray();
        var buffer2 = new float[buffer1.Length];
        var windowAlongRows = JaroszWindow(width);
        var windowAlongColumns = JaroszWindow(height);
        for (var pass = 0; pass < JaroszPasses; pass++)
        {
            for (var row = 0; row < height; row++)
                Box1D(buffer1, buffer2, row * width, width, 1, windowAlongRows);
            for (var column = 0; column < width; column++)
                Box1D(buffer2, buffer1, column, height, width, windowAlongColumns);
        }
        var result = new float[BufferSize * BufferSize];
        for (var outRow = 0; outRow < BufferSize; outRow++)
        {
            var inRow = (int)((outRow + 0.5) * height / BufferSize);
            for (var outColumn = 0; outColumn < BufferSize; outColumn++)
            {
                var inColumn = (int)((outColumn + 0.5) * width / BufferSize);
                result[outRow * BufferSize + outColumn] = buffer1[inRow * width + inColumn];
            }
        }
        return result;
    }

    private static int JaroszWindow(int dimension) => (dimension + 2 * BufferSize - 1) / (2 * BufferSize);

    /// <summary>Running box average over one row or column; the window narrows at both ends instead of padding.</summary>
    private static void Box1D(float[] input, float[] output, int start, int length, int stride, int window)
    {
        var halfWindow = (window + 2) / 2;
        var phase1 = halfWindow - 1;
        var phase2 = window - halfWindow + 1;
        var phase3 = length - window;
        var phase4 = halfWindow - 1;
        int left = start, right = start, outIndex = start, current = 0;
        var sum = 0f;
        for (var i = 0; i < phase1; i++)
        {
            sum += input[right];
            current++;
            right += stride;
        }
        for (var i = 0; i < phase2; i++)
        {
            sum += input[right];
            current++;
            output[outIndex] = sum / current;
            right += stride;
            outIndex += stride;
        }
        for (var i = 0; i < phase3; i++)
        {
            sum += input[right];
            sum -= input[left];
            output[outIndex] = sum / current;
            left += stride;
            right += stride;
            outIndex += stride;
        }
        for (var i = 0; i < phase4; i++)
        {
            sum -= input[left];
            current--;
            output[outIndex] = sum / current;
            left += stride;
            outIndex += stride;
        }
    }

    private static int Quality(float[] small)
    {
        var gradientSum = 0;
        for (var i = 0; i < BufferSize - 1; i++)
            for (var j = 0; j < BufferSize; j++)
                gradientSum += Math.Abs((int)((small[i * BufferSize + j] - small[(i + 1) * BufferSize + j]) * 100 / 255));
        for (var i = 0; i < BufferSize; i++)
            for (var j = 0; j < BufferSize - 1; j++)
                gradientSum += Math.Abs((int)((small[i * BufferSize + j] - small[i * BufferSize + j + 1]) * 100 / 255));
        return Math.Min(100, gradientSum / 90);
    }

    private static float[] CreateDctMatrix()
    {
        var matrix = new float[DctSize * BufferSize];
        var scale = (float)Math.Sqrt(2.0 / BufferSize);
        for (var i = 0; i < DctSize; i++)
            for (var j = 0; j < BufferSize; j++)
                matrix[i * BufferSize + j] = (float)(scale * Math.Cos(Math.PI / 2.0 / BufferSize * (i + 1) * (2 * j + 1)));
        return matrix;
    }

    /// <summary>D·A·Dᵀ: rows of the result are vertical frequencies 1–16, columns horizontal ones.</summary>
    private static float[] Dct16(float[] small)
    {
        var temp = new float[DctSize * BufferSize];
        for (var i = 0; i < DctSize; i++)
            for (var j = 0; j < BufferSize; j++)
            {
                var sum = 0f;
                for (var k = 0; k < BufferSize; k++)
                    sum += DctMatrix[i * BufferSize + k] * small[k * BufferSize + j];
                temp[i * BufferSize + j] = sum;
            }
        var result = new float[DctSize * DctSize];
        for (var i = 0; i < DctSize; i++)
            for (var j = 0; j < DctSize; j++)
            {
                var sum = 0f;
                for (var k = 0; k < BufferSize; k++)
                    sum += temp[i * BufferSize + k] * DctMatrix[j * BufferSize + k];
                result[i * DctSize + j] = sum;
            }
        return result;
    }

    /// <summary>Torben's median, as in the reference: the lower median, which is one of the values.</summary>
    private static float Torben(float[] values)
    {
        var n = values.Length;
        var half = (n + 1) / 2;
        float min = values[0], max = values[0];
        foreach (var value in values)
        {
            if (value < min) min = value;
            if (value > max) max = value;
        }
        float guess, maxLessThanGuess, minGreaterThanGuess;
        int less, greater, equal;
        while (true)
        {
            guess = (min + max) / 2;
            less = greater = equal = 0;
            maxLessThanGuess = min;
            minGreaterThanGuess = max;
            foreach (var value in values)
            {
                if (value < guess)
                {
                    less++;
                    if (value > maxLessThanGuess) maxLessThanGuess = value;
                }
                else if (value > guess)
                {
                    greater++;
                    if (value < minGreaterThanGuess) minGreaterThanGuess = value;
                }
                else equal++;
            }
            if (less <= half && greater <= half) break;
            if (less > greater) max = maxLessThanGuess;
            else min = minGreaterThanGuess;
        }
        if (less >= half) return maxLessThanGuess;
        return less + equal >= half ? guess : minGreaterThanGuess;
    }

    private static void Validate(int length, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (length != width * height) throw new ArgumentException($"Expected {width * height} values.");
    }
}
