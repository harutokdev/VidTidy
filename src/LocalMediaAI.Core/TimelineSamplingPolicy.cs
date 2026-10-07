namespace LocalMediaAI.Core;

/// <summary>
/// Where along a video its timeline frames are taken (see <see cref="TimelineMatching"/>): every five seconds, and for
/// a video longer than half an hour no more than 360 frames, spread evenly (an hour every ten seconds). Agreed with the
/// user as plan A: on generated videos every five seconds found every shorter copy and ten seconds still did for long
/// videos, while each frame costs a seek into the file.
/// </summary>
public static class TimelineSamplingPolicy
{
    public const string Version = "timeline-5s-360-v1";

    /// <summary>The timeline as stored: sampling and the bar-free fingerprints taken of it.</summary>
    public const string TimelineVersion = Version + ":" + NearDuplicatePreparation.Version;

    public const double IntervalSeconds = 5;
    public const int MaximumFrames = 360;

    /// <summary>Shorter videos get no timeline: too few frames to tell a copy from a likeness.</summary>
    public const double MinimumDurationSeconds = 20;

    /// <summary>Longest stretch read in one FFmpeg run when a file is read from start to end.</summary>
    public const double SequentialChunkSeconds = 600;

    public static double Interval(double durationSeconds) => Math.Max(IntervalSeconds, durationSeconds / MaximumFrames);

    /// <summary>Times of the frames, in the middle of each interval; none for a short or unknown length.</summary>
    public static IReadOnlyList<double> Times(double? durationSeconds)
    {
        if (durationSeconds is not { } duration || !double.IsFinite(duration) || duration < MinimumDurationSeconds) return [];
        var interval = Interval(duration);
        var count = (int)Math.Floor(duration / interval);
        return Enumerable.Range(0, count).Select(i => interval / 2 + i * interval).ToArray();
    }

    /// <summary>
    /// Taking a frame decodes from the keyframe before it, so each read covers about half the keyframe spacing plus
    /// the keyframe itself (about a second's worth). When that is as long as the interval, frame by frame would read
    /// the file more than once and seek for every frame; reading it once from start to end is then lighter on the disk
    /// and no heavier on the processor. An unknown spacing keeps frame-by-frame reading.
    /// </summary>
    public static bool ReadSequentially(double? keyframeSpacingSeconds, double intervalSeconds) =>
        keyframeSpacingSeconds is { } spacing && spacing / 2 + 1 >= intervalSeconds;
}
