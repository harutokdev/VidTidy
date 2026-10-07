using LocalMediaAI.Core;
using LocalMediaAI.Core.Processing;

namespace LocalMediaAI.Core.Tests;

public sealed class ProcessingPaceTests
{
    private static readonly ProcessingSettings Paced = new(ProcessingPace.Standard, OnlyWhenIdle: false);

    [Theory]
    [InlineData(JobType.Scan, JobLaneKind.Source)]
    [InlineData(JobType.ProtectedSourceOrchestration, JobLaneKind.Source)]
    [InlineData(JobType.VisualEmbedding, JobLaneKind.Compute)]
    [InlineData(JobType.ContentSimilarityRepresentation, JobLaneKind.Compute)]
    [InlineData(JobType.TagSuggestion, JobLaneKind.Compute)]
    public void JobsThatReadOriginalsUseTheSourceLane(JobType type, JobLaneKind expected) => Assert.Equal(expected, JobLanes.KindOf(type));

    [Fact]
    public void EveryJobTypeHasExactlyOneLane()
    {
        foreach (var type in Enum.GetValues<JobType>())
            Assert.True(JobLanes.SourceTypes.Contains(type) ^ JobLanes.ComputeTypes.Contains(type), type.ToString());
    }

    [Fact]
    public void FullWorkIsSplitIntoAQuickPassAndADeepPass()
    {
        const ProtectedSourceStageEligibility all = ProtectedSourceStageEligibility.Metadata | ProtectedSourceStageEligibility.Thumbnail |
            ProtectedSourceStageEligibility.VisualSampling | ProtectedSourceStageEligibility.NearDuplicateFingerprint | ProtectedSourceStageEligibility.FullHash;

        var (quick, deep) = JobLanes.SplitPasses(all);

        Assert.Equal(ProtectedSourceStageEligibility.Metadata | ProtectedSourceStageEligibility.Thumbnail, quick);
        // The deep pass keeps Metadata so it can recover the duration itself if the quick pass has not run.
        Assert.Equal(ProtectedSourceStageEligibility.Metadata | ProtectedSourceStageEligibility.VisualSampling |
            ProtectedSourceStageEligibility.NearDuplicateFingerprint | ProtectedSourceStageEligibility.FullHash, deep);
        Assert.True(JobLanes.IsQuickPass(quick));
        Assert.False(JobLanes.IsQuickPass(deep));
    }

    [Theory]
    [InlineData(ProtectedSourceStageEligibility.Thumbnail, ProtectedSourceStageEligibility.Thumbnail, ProtectedSourceStageEligibility.None)]
    [InlineData(ProtectedSourceStageEligibility.FullHash, ProtectedSourceStageEligibility.None, ProtectedSourceStageEligibility.FullHash)]
    [InlineData(ProtectedSourceStageEligibility.Metadata | ProtectedSourceStageEligibility.Thumbnail, ProtectedSourceStageEligibility.Metadata | ProtectedSourceStageEligibility.Thumbnail, ProtectedSourceStageEligibility.None)]
    [InlineData(ProtectedSourceStageEligibility.None, ProtectedSourceStageEligibility.None, ProtectedSourceStageEligibility.None)]
    public void PartialWorkOnlyCreatesThePassesItNeeds(ProtectedSourceStageEligibility stages, ProtectedSourceStageEligibility quick, ProtectedSourceStageEligibility deep) =>
        Assert.Equal((quick, deep), JobLanes.SplitPasses(stages));

    [Fact]
    public void FastestNeverRests()
    {
        var clock = new FakeClock();
        var pacer = new SourcePacer(clock.Now, () => null);
        var fastest = new ProcessingSettings(ProcessingPace.Fastest, false);

        pacer.JobStarted();
        clock.Advance(TimeSpan.FromHours(6));

        Assert.Null(pacer.WaitBeforeNextJob(fastest));
    }

    [Fact]
    public void AfterAFullWorkWindowTheDiskRestsForTheRestPeriod()
    {
        var clock = new FakeClock();
        var pacer = new SourcePacer(clock.Now, () => null);
        var cycle = Paced.Cycle;

        Assert.Null(pacer.WaitBeforeNextJob(Paced));
        pacer.JobStarted();
        clock.Advance(cycle.Work - TimeSpan.FromSeconds(1));
        pacer.JobFinished();
        Assert.Null(pacer.WaitBeforeNextJob(Paced));
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(cycle.Rest, pacer.WaitBeforeNextJob(Paced));
        Assert.True(pacer.IsResting);
        clock.Advance(cycle.Rest / 2);
        Assert.Equal(cycle.Rest / 2, pacer.WaitBeforeNextJob(Paced));
        clock.Advance(cycle.Rest / 2);
        Assert.Null(pacer.WaitBeforeNextJob(Paced));
        Assert.False(pacer.IsResting);
    }

    [Fact]
    public void AnIdleGapAsLongAsTheRestStartsAFreshWorkWindow()
    {
        var clock = new FakeClock();
        var pacer = new SourcePacer(clock.Now, () => null);
        var cycle = Paced.Cycle;

        pacer.JobStarted();
        clock.Advance(cycle.Work / 2);
        pacer.JobFinished();
        clock.Advance(cycle.Rest);
        Assert.Null(pacer.WaitBeforeNextJob(Paced));
        pacer.JobStarted();
        clock.Advance(cycle.Work / 2 + TimeSpan.FromMinutes(1));

        Assert.Null(pacer.WaitBeforeNextJob(Paced));
    }

    [Fact]
    public void SwitchingToFastestEndsARestImmediately()
    {
        var clock = new FakeClock();
        var pacer = new SourcePacer(clock.Now, () => null);
        pacer.JobStarted();
        clock.Advance(Paced.Cycle.Work + TimeSpan.FromSeconds(1));
        Assert.NotNull(pacer.WaitBeforeNextJob(Paced));

        Assert.Null(pacer.WaitBeforeNextJob(new ProcessingSettings(ProcessingPace.Fastest, false)));
        Assert.False(pacer.IsResting);
    }

    [Fact]
    public void OnlyWhenIdleWaitsWhileTheUserIsActive()
    {
        var clock = new FakeClock();
        var idle = TimeSpan.FromSeconds(30);
        var pacer = new SourcePacer(clock.Now, () => idle);
        var settings = new ProcessingSettings(ProcessingPace.Fastest, OnlyWhenIdle: true);

        Assert.Equal(ProcessingSettings.IdleThreshold - idle, pacer.WaitBeforeNextJob(settings));
        idle = ProcessingSettings.IdleThreshold;
        Assert.Null(pacer.WaitBeforeNextJob(settings));
    }

    [Fact]
    public void UnknownUserActivityNeverBlocksWork()
    {
        var pacer = new SourcePacer(new FakeClock().Now, () => null);

        Assert.Null(pacer.WaitBeforeNextJob(new ProcessingSettings(ProcessingPace.Fastest, OnlyWhenIdle: true)));
    }

    [Fact]
    public void QuietRestsLongerAndMoreOftenThanStandard()
    {
        var standard = new ProcessingSettings(ProcessingPace.Standard, false).Cycle;
        var quiet = new ProcessingSettings(ProcessingPace.Quiet, false).Cycle;

        Assert.True(quiet.RestShare > standard.RestShare);
        Assert.True(new ProcessingSettings(ProcessingPace.Fastest, false).Cycle.IsContinuous);
    }

    [Theory]
    [InlineData(ProcessingPace.Standard)]
    [InlineData(ProcessingPace.Quiet)]
    public void PacedModesRestLongEnoughToCoolTheDrive(ProcessingPace pace)
    {
        var cycle = new ProcessingSettings(pace, false).Cycle;

        Assert.False(cycle.IsContinuous);
        Assert.True(cycle.Rest >= ProcessingSettings.MinimumUsefulRest);
    }

    private sealed class FakeClock
    {
        private DateTimeOffset _now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        public Func<DateTimeOffset> Now => () => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
