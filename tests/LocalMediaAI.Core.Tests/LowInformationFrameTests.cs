namespace LocalMediaAI.Core.Tests;

public sealed class LowInformationFrameTests
{
    private static readonly NearDuplicateVerificationProfile Profile = NearDuplicateVerificationProfile.HighPrecisionV1;

    private static (int, int)[] Distances(params int[] values) => values.Select((d, i) => (i, d)).ToArray();

    [Theory]
    [InlineData("0000000000000000", true)]   // black, one flat colour, or a smooth gradient
    [InlineData("ffffffffffffffff", true)]   // a smooth gradient the other way
    [InlineData("0000000000180000", true)]   // black with one small bright object
    [InlineData("a5c3f00f5a3c0ff0", false)]  // an ordinary picture
    public void FewEdgesNeedsALookAtThePixels(string hash, bool few) => Assert.Equal(few, PerceptualFrameFingerprint.HasFewEdges(hash));

    [Fact]
    public void BlackAndDarkFramesAreFlatButASkyIsNot()
    {
        var black = new byte[72];
        var darkWithObject = new byte[72];
        darkWithObject[30] = 22;
        var sky = Enumerable.Range(0, 72).Select(i => (byte)(136 + i % 9 * 8)).ToArray();   // 136..200 across each row
        Assert.True(PerceptualFrameFingerprint.IsFlat(black));
        Assert.True(PerceptualFrameFingerprint.IsFlat(darkWithObject));
        Assert.False(PerceptualFrameFingerprint.IsFlat(sky));
    }

    [Fact]
    public void WithoutLowInformationFramesTheRuleIsUnchanged()
    {
        Assert.True(Profile.Verify(100, 100, Distances(0, 1, 2, 10, 1), []).IsVerified);
        Assert.False(Profile.Verify(100, 100, Distances(0, 30, 2, 30, 1), []).IsVerified);
    }

    [Fact]
    public void MostlyBlackVideosDoNotMatchOnBlackFramesAlone()
    {
        // Two different videos: three black frames match exactly, so the old rule accepted them on black alone.
        var distances = Distances(0, 0, 0, 1, 12);
        Assert.True(Profile.Verify(100, 100, distances).IsVerified);
        var result = Profile.Verify(100, 100, distances, [0, 1, 2]);
        Assert.False(result.IsVerified);
        Assert.Equal("Rejected", result.Status);
    }

    [Fact]
    public void TheSameVideoWithABlackIntroStillMatches()
    {
        // A black frame at 5% is ignored; the four frames with content all match.
        var result = Profile.Verify(100, 100, Distances(0, 2, 3, 1, 2), [0]);
        Assert.True(result.IsVerified);
        Assert.Equal(4, result.ComparedSampleCount);
    }

    [Fact]
    public void TooFewFramesWithContentIsNotEnoughEvidence()
    {
        var result = Profile.Verify(100, 100, Distances(0, 0, 0, 1, 1), [0, 1, 2]);
        Assert.False(result.IsVerified);
        Assert.Equal("Rejected", result.Status);
    }
}
