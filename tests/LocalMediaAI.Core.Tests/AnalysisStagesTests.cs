using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.Core.Tests;

public sealed class AnalysisStagesTests
{
    private static ActiveAnalysisJob Job(JobType type) => new(Guid.NewGuid(), type, JobState.Pending, 0, 0, null, null);

    [Fact]
    public void JobTypesMapToTheFourUserSteps()
    {
        Assert.Equal(0, AnalysisStages.Of(JobType.Scan));
        Assert.Equal(1, AnalysisStages.Of(JobType.Thumbnail));
        Assert.Equal(2, AnalysisStages.Of(JobType.FullHash));
        Assert.Equal(3, AnalysisStages.Of(JobType.NearDuplicateFingerprint));
    }

    [Fact]
    public void CurrentStepIsTheEarliestWithWork()
    {
        Assert.Equal(1, AnalysisStages.Current([Job(JobType.FullHash), Job(JobType.Metadata)]));
        Assert.Equal(4, AnalysisStages.Current([]));
    }

    [Fact]
    public void QuickPassCountsAsVideoInformationAndDeepPassByWhatItStillDoes()
    {
        const ProtectedSourceStageEligibility quick = ProtectedSourceStageEligibility.Metadata | ProtectedSourceStageEligibility.Thumbnail;
        const ProtectedSourceStageEligibility deepWithHash = ProtectedSourceStageEligibility.Metadata | ProtectedSourceStageEligibility.VisualSampling | ProtectedSourceStageEligibility.FullHash;
        const ProtectedSourceStageEligibility deep = ProtectedSourceStageEligibility.Metadata | ProtectedSourceStageEligibility.VisualSampling | ProtectedSourceStageEligibility.NearDuplicateFingerprint;

        Assert.Equal(1, AnalysisStages.Of(Orchestration(quick)));
        Assert.Equal(2, AnalysisStages.Of(Orchestration(deepWithHash)));
        Assert.Equal(3, AnalysisStages.Of(Orchestration(deep)));
        Assert.Equal(3, AnalysisStages.Of(Orchestration(ProtectedSourceStageEligibility.None)));
        Assert.Equal(1, AnalysisStages.Current([Orchestration(deep), Orchestration(quick)]));
    }

    private static ActiveAnalysisJob Orchestration(ProtectedSourceStageEligibility stages) =>
        new(null, JobType.ProtectedSourceOrchestration, JobState.Pending, 0, 0, null, null, stages);
}
