using LocalMediaAI.Core;

namespace LocalMediaAI.Core.Tests;

public sealed class DomainTests
{
    [Fact]
    public void JobStateMachineAllowsExpectedRecoveryTransition()
    {
        Assert.True(JobStateMachine.CanTransition(JobState.Running, JobState.RetryWaiting));
        Assert.True(JobStateMachine.CanTransition(JobState.RetryWaiting, JobState.Running));
        Assert.False(JobStateMachine.CanTransition(JobState.Completed, JobState.Pending));
        Assert.Throws<InvalidOperationException>(() => JobStateMachine.EnsureTransition(JobState.Cancelled, JobState.Running));
    }

    [Fact]
    public void AnalysisKeyIsDeterministicAcrossPropertyOrder()
    {
        var a = new Dictionary<string, object> { ["seek"] = "00:00:01", ["width"] = 480, ["format"] = "jpeg" };
        var b = new Dictionary<string, object> { ["format"] = "jpeg", ["width"] = 480, ["seek"] = "00:00:01" };
        Assert.Equal(AnalysisKey.ConfigHash(a), AnalysisKey.ConfigHash(b));
        Assert.Equal(AnalysisKey.ArtifactCacheKey("input", "thumbnail", "v1", "ffmpeg-v1", AnalysisKey.ConfigHash(a)), AnalysisKey.ArtifactCacheKey("input", "thumbnail", "v1", "ffmpeg-v1", AnalysisKey.ConfigHash(b)));
    }

    [Fact]
    public void SearchRequestGateRejectsStaleResults()
    {
        var gate = new SearchRequestGate();
        var oldRequest = gate.Begin();
        var newRequest = gate.Begin();
        Assert.False(gate.IsCurrent(oldRequest));
        Assert.True(gate.IsCurrent(newRequest));
    }

    [Fact]
    public void PipelineConcurrencyOptionsAreBoundedAndStageSpecific()
    {
        var defaults = new PipelineConcurrencyOptions();
        Assert.Equal(1, defaults.ScanConcurrency);
        Assert.Equal(1, defaults.MetadataConcurrency);
        Assert.Equal(1, defaults.ThumbnailConcurrency);

        var configured = new PipelineConcurrencyOptions(scanConcurrency: 2, metadataConcurrency: 3, thumbnailConcurrency: 4);
        Assert.Equal(2, configured.For(JobType.Scan));
        Assert.Equal(3, configured.For(JobType.Metadata));
        Assert.Equal(4, configured.For(JobType.Thumbnail));
        Assert.Equal(1, configured.For(JobType.FullHash));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipelineConcurrencyOptions(0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipelineConcurrencyOptions(1, PipelineConcurrencyOptions.MaximumStageConcurrency + 1, 1));
    }

    [Fact]
    public void ResourceAdmissionPolicyIsConservativeWithoutInventingGpuAvailability()
    {
        var policy = new ConservativeResourceAdmissionPolicy();
        var cpuOnly = new ResourceCostEstimate(1, 1_024, false, 0, 1, 0, 0, 1);
        var gpuRequired = cpuOnly with { GpuRequired = true, VramBytesEstimate = 2_048 };
        var healthy = new ResourceSnapshot(10, 4_096, null, null, 10, 10, 10);

        var gpuUnknownForCpuJob = policy.Evaluate(cpuOnly, healthy);
        Assert.Equal(AdmissionDecisionKind.Admit, gpuUnknownForCpuJob.Kind);

        var ramInsufficient = policy.Evaluate(cpuOnly, healthy with { AvailableRamBytes = 1_023 });
        Assert.Equal(AdmissionDecisionKind.Wait, ramInsufficient.Kind);
        Assert.Equal(JobReason.ResourceRam, ramInsufficient.Reason);

        var gpuUnavailable = policy.Evaluate(gpuRequired, healthy with { GpuAvailable = false });
        Assert.Equal(AdmissionDecisionKind.Reject, gpuUnavailable.Kind);
        Assert.Equal(JobReason.ResourceGpu, gpuUnavailable.Reason);

        var databaseBusy = policy.Evaluate(cpuOnly, healthy with { DbWriterPressure = 80 });
        Assert.Equal(AdmissionDecisionKind.Wait, databaseBusy.Kind);
        Assert.Equal(JobReason.DatabaseBusy, databaseBusy.Reason);

        var ioBusy = policy.Evaluate(cpuOnly, healthy with { ReadIoPressurePercent = 80 });
        Assert.Equal(AdmissionDecisionKind.Wait, ioBusy.Kind);
        Assert.Equal(JobReason.ResourceIo, ioBusy.Reason);
    }
}
