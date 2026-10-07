namespace LocalMediaAI.Core;

/// <summary>
/// Dependency-free perceptual fingerprint primitives for Phase 5.2. Input is exactly a
/// 9 x 8 grayscale raster emitted by app-owned FFmpeg, laid out row-major. This utility has
/// no file, network, model, database, or filesystem responsibility.
/// </summary>
public static class PerceptualFrameFingerprint
{
    public const string Algorithm = "dhash-gray-9x8-v1";
    public const int Width = 9;
    public const int Height = 8;
    public const int InputByteCount = Width * Height;
    public const int FingerprintBitCount = 64;

    public static string ComputeDHashHex(ReadOnlySpan<byte> grayscale9x8)
    {
        if (grayscale9x8.Length != InputByteCount)
            throw new ArgumentException($"DHash expects exactly {InputByteCount} grayscale bytes.", nameof(grayscale9x8));

        ulong bits = 0;
        var bit = 0;
        for (var row = 0; row < Height; row++)
        {
            var offset = row * Width;
            for (var column = 0; column < Width - 1; column++)
            {
                if (grayscale9x8[offset + column] < grayscale9x8[offset + column + 1])
                    bits |= 1UL << bit;
                bit++;
            }
        }
        return bits.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static int HammingDistance(string firstHex, string secondHex)
    {
        if (!TryParse(firstHex, out var first) || !TryParse(secondHex, out var second))
            throw new ArgumentException("DHash values must be 16-digit hexadecimal strings.");
        return System.Numerics.BitOperations.PopCount(first ^ second);
    }

    /// <summary>Set bits at or below this (or at or above 64 minus this): the fingerprint alone cannot tell a flat frame from a smooth gradient.</summary>
    public const int FewEdgesBitLimit = 8;

    /// <summary>Brightness range (0..255) of the 9x8 grey frame below which the frame is flat.</summary>
    public const int FlatBrightnessRange = 32;

    /// <summary>
    /// True when the fingerprint has almost no edges. Such a frame may be black or flat (no evidence) or a smooth
    /// gradient such as a sky (real content); <see cref="IsFlat"/> on its pixels decides.
    /// </summary>
    public static bool HasFewEdges(string fingerprintHex)
    {
        if (!TryParse(fingerprintHex, out var value))
            throw new ArgumentException("DHash value must be a 16-digit hexadecimal string.", nameof(fingerprintHex));
        var set = System.Numerics.BitOperations.PopCount(value);
        return set <= FewEdgesBitLimit || set >= FingerprintBitCount - FewEdgesBitLimit;
    }

    /// <summary>
    /// True for a frame that says almost nothing about the video: black, one flat colour, or a dark screen with one
    /// small object. Different videos share such frames (fades, black intros), so they are no evidence either way.
    /// </summary>
    public static bool IsFlat(ReadOnlySpan<byte> grayscale9x8)
    {
        if (grayscale9x8.Length != InputByteCount)
            throw new ArgumentException($"Expected exactly {InputByteCount} grayscale bytes.", nameof(grayscale9x8));
        byte min = 255, max = 0;
        foreach (var value in grayscale9x8)
        {
            if (value < min) min = value;
            if (value > max) max = value;
        }
        return max - min < FlatBrightnessRange;
    }

    public static IReadOnlyList<string> CreateLshBands(string fingerprintHex)
    {
        if (!TryParse(fingerprintHex, out var value))
            throw new ArgumentException("DHash value must be a 16-digit hexadecimal string.", nameof(fingerprintHex));
        return Enumerable.Range(0, 4)
            .Select(index => ((value >> (index * 16)) & 0xffffUL).ToString("x4", System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static bool TryParse(string? value, out ulong parsed)
    {
        parsed = 0;
        return !string.IsNullOrWhiteSpace(value)
            && value.Length == 16
            && ulong.TryParse(value, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out parsed);
    }
}

public sealed record NearDuplicateVerificationProfile(
    string ThresholdVersion,
    int RequiredSampleCount,
    int RequiredMatchedSampleCount,
    int RequiredLongestConsecutiveMatchCount,
    int MaximumFrameDistance,
    int MaximumMeanDistance,
    int MaximumObservedDistance,
    double MaximumDurationDifferenceRatio)
{
    public static readonly NearDuplicateVerificationProfile HighPrecisionV1 = new(
        "near-duplicate-v1-high-precision", RequiredSampleCount: 5, RequiredMatchedSampleCount: 4,
        RequiredLongestConsecutiveMatchCount: 3, MaximumFrameDistance: 8, MaximumMeanDistance: 6,
        MaximumObservedDistance: 12, MaximumDurationDifferenceRatio: 0.03d);

    /// <summary>Frames with content that must remain once low-information frames are set aside.</summary>
    public const int MinimumInformativeSampleCount = 3;

    /// <param name="lowInformationSamples">
    /// Samples where both frames are flat (see <see cref="PerceptualFrameFingerprint.IsFlat"/>).
    /// They count neither for nor against a match; the rest must still agree. Null or empty keeps the original rule.
    /// </param>
    public NearDuplicateVerificationResult Verify(
        double durationASeconds,
        double durationBSeconds,
        IReadOnlyList<(int SampleIndex, int HammingDistance)> distances,
        IReadOnlyCollection<int>? lowInformationSamples = null)
    {
        ArgumentNullException.ThrowIfNull(distances);
        var maximumDuration = Math.Max(durationASeconds, durationBSeconds);
        var durationDifferenceRatio = maximumDuration <= 0d
            ? double.PositiveInfinity
            : Math.Abs(durationASeconds - durationBSeconds) / maximumDuration;
        if (distances.Count != RequiredSampleCount || durationDifferenceRatio > MaximumDurationDifferenceRatio)
            return new NearDuplicateVerificationResult(false, durationDifferenceRatio, distances.Count, 0, 0, double.NaN, 0, 0d, "InsufficientEvidence");

        var all = distances.OrderBy(item => item.SampleIndex).ToArray();
        if (all.Select(item => item.SampleIndex).Distinct().Count() != RequiredSampleCount)
            return new NearDuplicateVerificationResult(false, durationDifferenceRatio, distances.Count, 0, 0, double.NaN, 0, 0d, "InsufficientEvidence");

        var ordered = lowInformationSamples is { Count: > 0 } low ? all.Where(item => !low.Contains(item.SampleIndex)).ToArray() : all;
        if (ordered.Length < MinimumInformativeSampleCount)
            return new NearDuplicateVerificationResult(false, durationDifferenceRatio, ordered.Length, 0, 0, double.NaN, 0, 0d, "Rejected");
        var requiredMatched = Math.Max(MinimumInformativeSampleCount, Math.Min(RequiredMatchedSampleCount, ordered.Length - 1));
        var requiredRun = Math.Min(RequiredLongestConsecutiveMatchCount, requiredMatched);

        var matched = ordered.Where(item => item.HammingDistance <= MaximumFrameDistance).ToArray();
        // Consecutive among the frames that count: a set-aside frame between two matches does not break the run.
        var longest = 0;
        var current = 0;
        foreach (var item in ordered)
        {
            current = item.HammingDistance <= MaximumFrameDistance ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        var mean = ordered.Average(item => item.HammingDistance);
        var maximum = ordered.Max(item => item.HammingDistance);
        var score = 1d - (mean / PerceptualFrameFingerprint.FingerprintBitCount);
        var verified = matched.Length >= requiredMatched
            && longest >= requiredRun
            && mean <= MaximumMeanDistance
            && maximum <= MaximumObservedDistance;
        return new NearDuplicateVerificationResult(verified, durationDifferenceRatio, ordered.Length, matched.Length, longest, mean, maximum, score, verified ? "Verified" : "Rejected");
    }
}

public sealed record NearDuplicateVerificationResult(
    bool IsVerified,
    double DurationDifferenceRatio,
    int ComparedSampleCount,
    int MatchedSampleCount,
    int LongestConsecutiveMatchCount,
    double MeanHammingDistance,
    int MaxHammingDistance,
    double VisualMatchScore,
    string Status);

public static class NearDuplicateSamplingPolicy
{
    public const string Version = "near-duplicate-normalized-v1";
    public static readonly IReadOnlyList<double> NormalizedPositions = [0.05d, 0.25d, 0.50d, 0.75d, 0.95d];

    public static IReadOnlyList<(int SampleIndex, double NormalizedPosition, double RequestedTimestampSeconds)> CreateTimestamps(double? durationSeconds)
    {
        if (durationSeconds is not { } duration || !double.IsFinite(duration) || duration <= 0d)
            return [];
        return NormalizedPositions
            .Select((position, index) => (index, position, Math.Min(Math.Max(0d, duration * position), Math.Max(0d, duration - 0.001d))))
            .ToArray();
    }
}
