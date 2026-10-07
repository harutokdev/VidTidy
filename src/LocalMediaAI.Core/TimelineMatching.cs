namespace LocalMediaAI.Core;

/// <summary>One frame sampled along a video's whole length, with its bar-free fingerprints.</summary>
public sealed record TimelineFrame(double Seconds, PreparedNearDuplicateFrame Frame);

/// <summary>A stretch of a video, in seconds from its start.</summary>
public sealed record TimelineSpan(double StartSeconds, double EndSeconds)
{
    public double Length => EndSeconds - StartSeconds;
}

/// <summary>
/// How two videos of possibly different lengths line up. <see cref="ShorterCoverage"/> is the share of the shorter
/// video's frames found, in order, in the longer one; <see cref="ExtraInLonger"/> are the parts of the longer video
/// with no counterpart (a longer start or end, or a scene the shorter one leaves out).
/// </summary>
public sealed record TimelineMatch(
    bool IsContained,
    bool AIsShorter,
    int MatchedFrames,
    double ShorterCoverage,
    double LongerCoverage,
    IReadOnlyList<TimelineSpan> ExtraInLonger,
    IReadOnlyList<TimelineSpan> ExtraInShorter,
    IReadOnlyList<(double ShorterSeconds, double LongerSeconds)> Pairs);

/// <summary>
/// Compares two videos as sets of frames sampled over their whole length, in the spirit of Meta's vPDQ: frames match
/// when their fingerprints are close, and only matches that keep both videos in the same order count, so a video that
/// repeats one scene, or two videos sharing an opening, do not pass. Pure, no I/O.
/// </summary>
public static class TimelineMatching
{
    public const string Version = "timeline-ordered-v1";

    /// <summary>Same per-frame limit as the five-sample rule (<see cref="NearDuplicateVerificationProfile.HighPrecisionV1"/>).</summary>
    public const int MaximumFrameDistance = 8;

    /// <summary>At least this many frames must line up, whatever the share.</summary>
    public const int MinimumMatchedFrames = 4;

    /// <summary>Share of the shorter video's informative frames that must be found in the longer one.</summary>
    public const double MinimumShorterCoverage = 0.8;

    public static TimelineMatch Compare(IReadOnlyList<TimelineFrame> a, double durationASeconds, IReadOnlyList<TimelineFrame> b, double durationBSeconds)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var aIsShorter = durationASeconds <= durationBSeconds;
        var shorter = Informative(aIsShorter ? a : b);
        var longer = Informative(aIsShorter ? b : a);
        var chain = OrderedChain(shorter, longer);
        var shorterCoverage = shorter.Count == 0 ? 0 : (double)chain.Count / shorter.Count;
        var longerCoverage = longer.Count == 0 ? 0 : (double)chain.Count / longer.Count;
        var contained = chain.Count >= MinimumMatchedFrames && shorterCoverage >= MinimumShorterCoverage;
        return new TimelineMatch(contained, aIsShorter, chain.Count, shorterCoverage, longerCoverage,
            Unmatched(longer, chain.Select(pair => pair.Longer).ToHashSet(), aIsShorter ? durationBSeconds : durationASeconds),
            Unmatched(shorter, chain.Select(pair => pair.Shorter).ToHashSet(), aIsShorter ? durationASeconds : durationBSeconds),
            chain.Select(pair => (shorter[pair.Shorter].Seconds, longer[pair.Longer].Seconds)).ToArray());
    }

    internal static IReadOnlyList<TimelineFrame> Informative(IReadOnlyList<TimelineFrame> frames) =>
        frames.Where(frame => !frame.Frame.IsFlat).OrderBy(frame => frame.Seconds).ToArray();

    /// <summary>
    /// The largest set of matching frame pairs that is increasing in both videos (each frame used once): a longest
    /// common subsequence where "equal" means close fingerprints.
    /// </summary>
    private static IReadOnlyList<(int Shorter, int Longer)> OrderedChain(IReadOnlyList<TimelineFrame> shorter, IReadOnlyList<TimelineFrame> longer)
    {
        var n = shorter.Count;
        var m = longer.Count;
        var best = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                best[i, j] = Matches(shorter[i], longer[j])
                    ? Math.Max(1 + best[i + 1, j + 1], Math.Max(best[i + 1, j], best[i, j + 1]))
                    : Math.Max(best[i + 1, j], best[i, j + 1]);
        var chain = new List<(int, int)>();
        for (int i = 0, j = 0; i < n && j < m;)
        {
            if (Matches(shorter[i], longer[j]) && best[i, j] == 1 + best[i + 1, j + 1])
            {
                chain.Add((i, j));
                i++;
                j++;
            }
            else if (best[i + 1, j] >= best[i, j + 1]) i++;
            else j++;
        }
        return chain;
    }

    internal static bool Matches(TimelineFrame x, TimelineFrame y) => NearDuplicatePreparation.Distance(x.Frame, y.Frame) <= MaximumFrameDistance;

    /// <summary>Runs of frames without a counterpart, widened halfway to their matched neighbours (or to the ends).</summary>
    private static IReadOnlyList<TimelineSpan> Unmatched(IReadOnlyList<TimelineFrame> frames, IReadOnlySet<int> matched, double durationSeconds)
    {
        var spans = new List<TimelineSpan>();
        for (var i = 0; i < frames.Count; i++)
        {
            if (matched.Contains(i)) continue;
            var start = i;
            while (i + 1 < frames.Count && !matched.Contains(i + 1)) i++;
            var from = start == 0 ? 0 : (frames[start - 1].Seconds + frames[start].Seconds) / 2;
            var to = i == frames.Count - 1 ? Math.Max(durationSeconds, frames[i].Seconds) : (frames[i].Seconds + frames[i + 1].Seconds) / 2;
            spans.Add(new TimelineSpan(from, to));
        }
        return spans;
    }
}
