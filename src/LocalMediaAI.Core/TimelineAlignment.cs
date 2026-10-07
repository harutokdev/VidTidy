namespace LocalMediaAI.Core;

/// <summary>Reads one video's frames at the given times, prepared with that video's bars, in the same order; null where a frame could not be read.</summary>
public delegate Task<IReadOnlyList<PreparedNearDuplicateFrame?>> TimelineFrameReader(IReadOnlyList<double> seconds, CancellationToken cancellationToken);

/// <summary>Reads one video's frames from <c>fromSeconds</c> to <c>toSeconds</c> at a steady rate, in one pass, with their times.</summary>
public delegate Task<IReadOnlyList<TimelineFrame>> TimelineWindowReader(double fromSeconds, double toSeconds, double framesPerSecond, CancellationToken cancellationToken);

/// <summary>Reading one video: a stretch at a steady rate, or single frames at given times.</summary>
public sealed record TimelineVideo(IReadOnlyList<TimelineFrame> Timeline, double Seconds, TimelineWindowReader ReadWindow, TimelineFrameReader ReadAt);

/// <summary>
/// Confirms that one video is contained in another when their sampled frames were not taken at matching moments.
/// Frames sampled every few seconds rarely fall on the same moment in a copy with a few seconds cut off, and a moving
/// picture looks different a second later, so the rough line-up (<see cref="TimelineMatching"/>) gives the offset only
/// approximately. For each run of frames sharing an offset, a short stretch of both videos is read in one pass and the
/// offset is taken where most of their frames agree, to a tenth of a second. Every frame of the shorter video is then
/// compared with the longer one's frame at exactly the matching moment. A scene pulled out of the middle gives two runs,
/// each with its own offset. Only pairs that already line up roughly are read further.
/// </summary>
public static class TimelineAlignment
{
    /// <summary>Length of the stretch of the shorter video the offset is measured on.</summary>
    public const double WindowSeconds = 20;

    private const double ShorterFramesPerSecond = 5;
    private const double LongerFramesPerSecond = 10;
    private const double OffsetStep = 1 / LongerFramesPerSecond;

    /// <summary>The offset is searched at least this far either side of the rough one, which can be off by more than the interval in a still scene.</summary>
    private const double MinimumReachSeconds = 5;

    /// <summary>Kept with each pair judged this way; a change to the method gives new evidence.</summary>
    public const string ThresholdVersion = "timeline-contained-v1";

    /// <summary>Lines up the shorter video (by length) inside the longer one.</summary>
    public static async Task<TimelineMatch> AlignAsync(TimelineVideo a, TimelineVideo b, double intervalSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(intervalSeconds, 0d);
        var aIsShorter = a.Seconds <= b.Seconds;
        var (shorter, longer) = aIsShorter ? (a, b) : (b, a);
        var rough = TimelineMatching.Compare(shorter.Timeline, shorter.Seconds, longer.Timeline, longer.Seconds);
        // One frame in common is enough to look closer: frames sampled apart rarely match, and the final check is strict.
        if (rough.Pairs.Count == 0) return rough with { IsContained = false, AIsShorter = aIsShorter };

        // A lone pair with its own offset is more likely a chance likeness than a cut, unless nothing else lines up.
        var runs = Runs(rough.Pairs, intervalSeconds).ToArray();
        var segments = new List<Segment>();
        foreach (var run in runs.Any(r => r.Count >= 2) ? runs.Where(r => r.Count >= 2) : runs)
            segments.Add(await MeasureAsync(run, shorter, longer, intervalSeconds, cancellationToken).ConfigureAwait(false));

        var informative = TimelineMatching.Informative(shorter.Timeline);
        var targets = informative.Select(frame => (Frame: frame, At: frame.Seconds + Nearest(segments, frame.Seconds).Offset))
            .Where(t => t.At >= 0 && t.At < longer.Seconds).ToArray();
        var read = targets.Length == 0 ? [] : await longer.ReadAt(targets.Select(t => t.At).ToArray(), cancellationToken).ConfigureAwait(false);
        var pairs = new List<(double ShorterSeconds, double LongerSeconds)>();
        for (var i = 0; i < targets.Length; i++)
            if (read[i] is { } frame && NearDuplicatePreparation.Distance(targets[i].Frame.Frame, frame) <= TimelineMatching.MaximumFrameDistance)
                pairs.Add((targets[i].Frame.Seconds, targets[i].At));

        // Black or flat frames say nothing either way; where the offset puts them is not counted as missing.
        var neutral = shorter.Timeline.Where(frame => frame.Frame.IsFlat).Select(frame => (frame.Seconds, At: frame.Seconds + Nearest(segments, frame.Seconds).Offset)).ToArray();
        var coverage = informative.Count == 0 ? 0 : (double)pairs.Count / informative.Count;
        return new TimelineMatch(
            pairs.Count >= TimelineMatching.MinimumMatchedFrames && coverage >= TimelineMatching.MinimumShorterCoverage,
            aIsShorter,
            pairs.Count,
            coverage,
            longer.Seconds <= 0 ? 0 : Math.Min(1, pairs.Count * intervalSeconds / longer.Seconds),
            Uncovered(Stretches(pairs.Select(p => (Segment: Nearest(segments, p.ShorterSeconds), p.LongerSeconds)), intervalSeconds).Concat(Ends(pairs, informative, shorter.Seconds)), longer.Seconds, intervalSeconds),
            Uncovered(Covered(pairs.Select(p => p.ShorterSeconds).Concat(neutral.Select(n => n.Seconds)), intervalSeconds), shorter.Seconds, intervalSeconds),
            pairs);
    }

    private sealed record Segment(double FromSeconds, double ToSeconds, double Offset);

    /// <summary>Runs of rough pairs sharing about the same offset; a jump larger than the interval starts a new run.</summary>
    private static IEnumerable<IReadOnlyList<(double Shorter, double Longer)>> Runs(IReadOnlyList<(double ShorterSeconds, double LongerSeconds)> pairs, double interval)
    {
        var current = new List<(double, double)>();
        foreach (var (s, l) in pairs.OrderBy(p => p.ShorterSeconds))
        {
            if (current.Count > 0 && Math.Abs(l - s - (current[^1].Item2 - current[^1].Item1)) > interval)
            {
                yield return current;
                current = [];
            }
            current.Add((s, l));
        }
        if (current.Count > 0) yield return current;
    }

    /// <summary>
    /// The run's offset: a stretch of the shorter video around the run's middle against the matching stretch of the
    /// longer one, widened by the interval either way; the offset (in tenths of a second) where most frames agree.
    /// A still picture agrees at every offset and a picture that changes every frame at none, so the frames in
    /// between decide.
    /// </summary>
    private static async Task<Segment> MeasureAsync(IReadOnlyList<(double Shorter, double Longer)> run, TimelineVideo shorter, TimelineVideo longer, double interval, CancellationToken cancellationToken)
    {
        var rough = run.Select(p => p.Longer - p.Shorter).Order().ElementAt(run.Count / 2);
        var middle = run[run.Count / 2].Shorter;
        var from = Math.Clamp(middle - WindowSeconds / 2, 0, Math.Max(0, shorter.Seconds - WindowSeconds));
        var to = Math.Min(shorter.Seconds, from + WindowSeconds);
        var reach = Math.Max(interval, MinimumReachSeconds);
        var source = (await shorter.ReadWindow(from, to, ShorterFramesPerSecond, cancellationToken).ConfigureAwait(false)).Where(f => !f.Frame.IsFlat).ToArray();
        var target = (await longer.ReadWindow(Math.Max(0, from + rough - reach), Math.Min(longer.Seconds, to + rough + reach), LongerFramesPerSecond, cancellationToken).ConfigureAwait(false))
            .OrderBy(f => f.Seconds).ToArray();
        var best = (Offset: rough, Agree: 0);
        if (source.Length > 0 && target.Length > 0)
        {
            var steps = (int)Math.Round(reach / OffsetStep);
            for (var k = -steps; k <= steps; k++)
            {
                var offset = rough + k * OffsetStep;
                var agree = source.Count(frame => At(target, frame.Seconds + offset) is { } other && NearDuplicatePreparation.Distance(frame.Frame, other) <= TimelineMatching.MaximumFrameDistance);
                if (agree > best.Agree || (agree == best.Agree && agree > 0 && Math.Abs(offset - rough) < Math.Abs(best.Offset - rough))) best = (offset, agree);
            }
        }
        return new Segment(run[0].Shorter, run[^1].Shorter, best.Offset);
    }

    /// <summary>The frame read nearest to a moment, if one was read within half a step of it.</summary>
    private static PreparedNearDuplicateFrame? At(TimelineFrame[] frames, double seconds)
    {
        var low = 0;
        var high = frames.Length - 1;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (frames[mid].Seconds < seconds) low = mid + 1;
            else high = mid;
        }
        var nearest = low > 0 && Math.Abs(frames[low - 1].Seconds - seconds) < Math.Abs(frames[low].Seconds - seconds) ? low - 1 : low;
        return Math.Abs(frames[nearest].Seconds - seconds) <= OffsetStep / 2 + 1e-6 ? frames[nearest].Frame : null;
    }

    private static Segment Nearest(IReadOnlyList<Segment> segments, double seconds) =>
        segments.MinBy(s => seconds < s.FromSeconds ? s.FromSeconds - seconds : seconds > s.ToSeconds ? seconds - s.ToSeconds : 0)!;

    /// <summary>
    /// What the shorter video covers in the longer one: per offset, from its first to its last matched frame. A frame
    /// that did not match inside such a stretch leaves the shorter video unconfirmed there, not the longer one longer.
    /// </summary>
    private static IEnumerable<(double From, double To)> Stretches(IEnumerable<(Segment Segment, double LongerSeconds)> matched, double interval) =>
        matched.GroupBy(m => m.Segment).Select(g => (g.Min(m => m.LongerSeconds) - interval / 2, g.Max(m => m.LongerSeconds) + interval / 2));

    /// <summary>Each matched frame stands for half an interval either side of it.</summary>
    private static IEnumerable<(double From, double To)> Covered(IEnumerable<double> matchedSeconds, double interval) =>
        matchedSeconds.Select(t => (t - interval / 2, t + interval / 2));

    /// <summary>
    /// When the shorter video's first (last) frame matched, its start (end) is placed by the measured offset, so a start
    /// or end cut shorter than the interval still shows, to a tenth of a second.
    /// </summary>
    private static IEnumerable<(double From, double To)> Ends(IReadOnlyList<(double ShorterSeconds, double LongerSeconds)> pairs, IReadOnlyList<TimelineFrame> informative, double shorterSeconds)
    {
        if (pairs.Count == 0 || informative.Count == 0) yield break;
        var first = pairs.MinBy(p => p.ShorterSeconds);
        if (first.ShorterSeconds <= informative[0].Seconds) yield return (first.LongerSeconds - first.ShorterSeconds, first.LongerSeconds);
        var last = pairs.MaxBy(p => p.ShorterSeconds);
        if (last.ShorterSeconds >= informative[^1].Seconds) yield return (last.LongerSeconds, last.LongerSeconds + shorterSeconds - last.ShorterSeconds);
    }

    /// <summary>The parts of a video outside every covered stretch: inside it only gaps wider than the interval, at its ends any over a second.</summary>
    private static IReadOnlyList<TimelineSpan> Uncovered(IEnumerable<(double From, double To)> covered, double durationSeconds, double interval)
    {
        const double EndTolerance = 1;
        var spans = new List<TimelineSpan>();
        var cursor = 0d;
        foreach (var (from, to) in covered.Select(c => (Math.Max(0, c.From), Math.Min(durationSeconds, c.To))).Where(c => c.Item2 > c.Item1).OrderBy(c => c.Item1))
        {
            if (from - cursor > (cursor == 0 ? EndTolerance : interval)) spans.Add(new TimelineSpan(cursor, from));
            cursor = Math.Max(cursor, to);
        }
        if (durationSeconds - cursor > EndTolerance) spans.Add(new TimelineSpan(cursor, durationSeconds));
        return spans;
    }
}
