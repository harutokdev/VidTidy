namespace LocalMediaAI.Core.Organize;

/// <summary>One scene found in both videos: the frame image of the first file and where it is in each file.</summary>
public sealed record MatchedScene(string? ImageRelativePath, IReadOnlyDictionary<Guid, double> SecondsByFile);

public static class MatchedScenes
{
    /// <summary>Up to <paramref name="count"/> matched frame pairs spread over the video (start, middle, end).</summary>
    public static IReadOnlyList<MatchedScene> Pick(NearDuplicateEvidence evidence, IEnumerable<NearDuplicateFrameMatch> matches,
        IEnumerable<NearDuplicateFrame> framesA, IEnumerable<NearDuplicateFrame> framesB, int count = 3)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var a = framesA.ToDictionary(f => f.NearDuplicateFrameId);
        var b = framesB.ToDictionary(f => f.NearDuplicateFrameId);
        var usable = matches.Where(m => m.IsMatch && a.ContainsKey(m.NearDuplicateFrameAId) && b.ContainsKey(m.NearDuplicateFrameBId))
            .OrderBy(m => m.SampleIndex).ToArray();
        if (usable.Length == 0 || count <= 0) return [];
        var picks = usable.Length <= count
            ? usable
            : Enumerable.Range(0, count).Select(i => usable[(int)Math.Round(i * (usable.Length - 1) / (double)(count - 1))]).Distinct().ToArray();
        return picks.Select(m =>
        {
            var fa = a[m.NearDuplicateFrameAId];
            var fb = b[m.NearDuplicateFrameBId];
            return new MatchedScene(fa.FrameRelativePath ?? fb.FrameRelativePath, new Dictionary<Guid, double>
            {
                [evidence.MediaFileAId] = fa.PresentationTimestampSeconds,
                [evidence.MediaFileBId] = fb.PresentationTimestampSeconds
            });
        }).ToArray();
    }
}
