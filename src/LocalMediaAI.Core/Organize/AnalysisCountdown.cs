namespace LocalMediaAI.Core.Organize;

/// <summary>One folder of a change check: files it had last time, files seen so far, and when its scan started and ended.</summary>
public sealed record ScanPart(int Expected, int Seen, bool Done, DateTimeOffset? StartedAt = null, DateTimeOffset? CompletedAt = null);

/// <summary>
/// Progress and time left of the "変更を確認" scan. Folders are scanned one at a time, so the files per second
/// measured so far are applied to the files the remaining folders had last time.
/// </summary>
public static class ChangeCheckEstimate
{
    private static readonly TimeSpan MinimumSample = TimeSpan.FromSeconds(2);

    public static double Percent(IReadOnlyList<ScanPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var expected = parts.Sum(Size);
        if (expected == 0) return parts.All(p => p.Done) ? 100 : 0;
        var seen = parts.Sum(p => p.Done ? Size(p) : Math.Min(p.Seen, Size(p)));
        return Math.Min(parts.All(p => p.Done) ? 100 : 99, Math.Floor(seen * 100.0 / expected));
    }

    /// <summary>Null until enough files were seen to measure a rate.</summary>
    public static TimeSpan? Remaining(IReadOnlyList<ScanPart> parts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.All(p => p.Done)) return TimeSpan.Zero;
        var seen = 0;
        var elapsed = TimeSpan.Zero;
        foreach (var part in parts.Where(p => p.StartedAt is not null && p.Seen > 0))
        {
            seen += part.Seen;
            elapsed += ((part.Done ? part.CompletedAt : null) ?? now) - part.StartedAt!.Value;
        }
        if (seen == 0 || elapsed < MinimumSample) return null;
        var left = parts.Where(p => !p.Done).Sum(p => Math.Max(0, Size(p) - p.Seen));
        return TimeSpan.FromSeconds(left * elapsed.TotalSeconds / seen);
    }

    /// <summary>A folder not scanned before has no count yet; what is seen so far is the best guess.</summary>
    private static int Size(ScanPart part) => Math.Max(part.Expected, part.Seen);
}

/// <summary>
/// Turns noisy estimates into a steady countdown: the finish time moves only part of the way toward each new
/// estimate, and between estimates the time left simply counts down.
/// </summary>
public sealed class FinishClock
{
    private const double Follow = 0.3;

    public DateTimeOffset? FinishAt { get; private set; }

    public void Observe(DateTimeOffset now, TimeSpan? remaining)
    {
        if (remaining is not { } left) return;
        var target = now + left;
        // A far-off first guess or a big change (new work added) is taken as is rather than crawled toward.
        if (FinishAt is not { } at || Math.Abs((target - at).TotalSeconds) > Math.Max(60, left.TotalSeconds * 0.5)) FinishAt = target;
        else FinishAt = at + TimeSpan.FromSeconds((target - at).TotalSeconds * Follow);
    }

    public TimeSpan? Left(DateTimeOffset now) => FinishAt is { } at ? at > now ? at - now : TimeSpan.Zero : null;

    public void Reset() => FinishAt = null;

    /// <summary>"約45秒", "約3分20秒", "約25分", "約1時間5分".</summary>
    public static string Format(TimeSpan left)
    {
        if (left < TimeSpan.FromMinutes(1)) return L.F("約{0}秒", Math.Max(1, (int)Math.Ceiling(left.TotalSeconds / 5) * 5));
        if (left < TimeSpan.FromMinutes(10)) return L.F("約{0}分{1:00}秒", (int)left.TotalMinutes, left.Seconds);
        return AnalysisEta.Format(left);
    }
}
