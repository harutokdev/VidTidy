namespace LocalMediaAI.WorkerProtocol;

/// <summary>Protocol v1 contract. The Worker never receives a SQLite path and never owns durable state.</summary>
public static class ProtocolVersion
{
    public const int Current = 1;
}

public sealed record WorkerInfo(int ProtocolVersion, string WorkerVersion, string PythonVersion, bool BackendAvailable, string? BackendDetail);
public sealed record HealthRequest(string SessionToken);
public sealed record HealthResponse(WorkerInfo Worker);
public sealed record WorkerVersion(WorkerInfo Worker, IReadOnlyList<string> Capabilities);
public enum WorkerAvailability { NotStarted, Starting, Available, Unavailable, Stopping, Stopped }
public sealed record WorkerRuntimeStatus(WorkerAvailability Availability, string? ErrorCode, string? UserMessage, int? ProcessId, int? Port, WorkerInfo? Worker, DateTimeOffset UpdatedAt);
public sealed record WorkerJobRequest(Guid JobId, string JobType, IReadOnlyDictionary<string, string> InputDescriptors, string ConfigJson, string ExpectedProcessorVersion, string SessionToken);
public sealed record WorkerJobProgress(Guid JobId, string Phase, long Current, long Total, string Unit, DateTimeOffset HeartbeatAt, IReadOnlyDictionary<string, double>? Metrics);
public sealed record ProducedArtifactDescriptor(string Kind, string RelativePath, string Sha256, long ByteSize, string MimeType);
public sealed record WorkerJobResult(Guid JobId, string Outcome, IReadOnlyList<ProducedArtifactDescriptor> Artifacts, string? StructuredResultJson, string? ErrorCode, string? ErrorMessage);
public sealed record CancelWorkerJobRequest(Guid JobId, string SessionToken);

public interface ILocalWorkerClient
{
    Task<WorkerInfo> HealthAsync(CancellationToken cancellationToken);
    Task<WorkerVersion> VersionAsync(CancellationToken cancellationToken);
    Task ShutdownAsync(CancellationToken cancellationToken);
    Task<WorkerJobResult> RunAsync(WorkerJobRequest request, IProgress<WorkerJobProgress>? progress, CancellationToken cancellationToken);
    Task CancelAsync(Guid jobId, CancellationToken cancellationToken);
}
