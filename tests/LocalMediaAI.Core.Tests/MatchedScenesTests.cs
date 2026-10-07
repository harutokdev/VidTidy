using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.Core.Tests;

public sealed class MatchedScenesTests
{
    private static readonly Guid FileA = Guid.NewGuid(), FileB = Guid.NewGuid();

    private static NearDuplicateFrame Frame(Guid file, int index, double seconds) =>
        new(Guid.NewGuid(), file, Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch, index, 0, seconds, seconds, "v", "alg", "00", $"frames/{file:N}-{index}.jpg", null, null, AnalysisStatus.Completed, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static NearDuplicateEvidence Evidence() =>
        new(Guid.NewGuid(), FileA, Guid.NewGuid(), FileB, Guid.NewGuid(), "alg", "v", "t", "d", 100, 90, 0.1, 10, 7, 5, 3, 8, 0.9, "Verified", null, null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void PicksStartMiddleAndEndOfMatchedPairs()
    {
        var evidence = Evidence();
        var a = Enumerable.Range(0, 9).Select(i => Frame(FileA, i, i * 10)).ToArray();
        var b = Enumerable.Range(0, 9).Select(i => Frame(FileB, i, i * 10 - 2)).ToArray();
        var matches = Enumerable.Range(0, 9).Select(i => new NearDuplicateFrameMatch(evidence.NearDuplicateEvidenceId, i, a[i].NearDuplicateFrameId, b[i].NearDuplicateFrameId, 3, i != 4)).ToArray();

        var scenes = MatchedScenes.Pick(evidence, matches, a, b);

        Assert.Equal([0d, 50d, 80d], scenes.Select(s => s.SecondsByFile[FileA]));
        Assert.Equal(48d, scenes[1].SecondsByFile[FileB]);
        Assert.Equal(a[0].FrameRelativePath, scenes[0].ImageRelativePath);
    }

    [Fact]
    public void NoMatchesMeansNoScenes() =>
        Assert.Empty(MatchedScenes.Pick(Evidence(), [], [], []));
}
