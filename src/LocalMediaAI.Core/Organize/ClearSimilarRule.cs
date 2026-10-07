namespace LocalMediaAI.Core.Organize;

/// <summary>What the rule needs to know about one video of a similar group.</summary>
public sealed record VideoFacts(long PixelCount, long? Bitrate, double? DurationSeconds);

/// <summary>
/// A similar group is "clear" when choosing the kept file is not in doubt, so it can be approved together with others:
/// the videos match closely, have the same length (no scene only one of them has), and the kept file is at least as
/// sharp and as detailed as every other file. Anything unknown makes the group unclear.
/// </summary>
public static class ClearSimilarRule
{
    public const double MinimumMatch = 0.95;
    public const double MaximumLengthDifferenceSeconds = 1.0;

    public static bool IsClear(double? matchRatio, VideoFacts keep, IReadOnlyList<VideoFacts> others)
    {
        ArgumentNullException.ThrowIfNull(keep);
        ArgumentNullException.ThrowIfNull(others);
        if (matchRatio is not { } match || match < MinimumMatch || others.Count == 0) return false;
        if (keep.Bitrate is not { } keepBitrate || keep.DurationSeconds is not { } keepDuration || keep.PixelCount <= 0) return false;
        return others.All(o =>
            o.Bitrate is { } bitrate && o.DurationSeconds is { } duration
            && Math.Abs(duration - keepDuration) <= MaximumLengthDifferenceSeconds
            && o.PixelCount <= keep.PixelCount
            && bitrate <= keepBitrate);
    }
}
