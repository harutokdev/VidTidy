namespace LocalMediaAI.Core;

/// <summary>
/// A runtime-selectable semantic embedding model. This is intentionally separate from
/// commercial-release approval: a model can be approved for technical development while
/// distribution remains subject to legal review.
/// </summary>
public sealed record SemanticModelProfile(
    string ProfileId,
    string ModelId,
    string ModelRevision,
    string ModelDirectoryName,
    string? AdapterDirectoryName,
    string Adapter,
    string ProcessorVersion,
    int Dimension,
    string DefaultDtype,
    string RuntimeDirectoryName,
    SemanticModelTechnicalStatus TechnicalStatus,
    bool ApprovedForTechnicalUse,
    SemanticModelDistributionStatus DistributionStatus,
    string License)
{
    public static readonly SemanticModelProfile DevelopmentProductionCandidate = new(
        ProfileId: "llm-jp-clip-development-production-candidate-v1",
        ModelId: "llm-jp/llm-jp-clip-vit-base-patch16",
        ModelRevision: "e3b65fb4a871bacde924b3613dae0eccb7835e95",
        ModelDirectoryName: "llm-jp-clip-vit-base-patch16",
        AdapterDirectoryName: "llm-jp-clip-vit-base-patch16-openclip-adapter",
        Adapter: "openclip_local",
        ProcessorVersion: "llm-jp-openclip-2.31-local-adapter-v1",
        Dimension: 512,
        DefaultDtype: "float32",
        RuntimeDirectoryName: "python-openclip",
        TechnicalStatus: SemanticModelTechnicalStatus.DevelopmentCandidate,
        ApprovedForTechnicalUse: true,
        DistributionStatus: SemanticModelDistributionStatus.CommercialDistributionReviewRequired,
        License: "Apache-2.0");

    public string TechnicalStatusDisplay => TechnicalStatus switch
    {
        SemanticModelTechnicalStatus.DevelopmentCandidate => "Development Production Candidate",
        SemanticModelTechnicalStatus.ApprovedForTechnicalUse => "Approved for Technical Use",
        _ => TechnicalStatus.ToString()
    };

    public string TechnicalApprovalStatusDisplay => ApprovedForTechnicalUse ? "Approved for Technical Use" : "Technical Use Not Approved";

    public string DistributionStatusDisplay => DistributionStatus switch
    {
        SemanticModelDistributionStatus.CommercialDistributionReviewRequired => "LEGAL REVIEW REQUIRED",
        _ => DistributionStatus.ToString()
    };
}

public enum SemanticModelTechnicalStatus
{
    DevelopmentCandidate,
    ApprovedForTechnicalUse
}

public enum SemanticModelDistributionStatus
{
    CommercialDistributionReviewRequired
}

public enum SemanticIndexStatus
{
    NotIndexed,
    Queued,
    Indexing,
    Partial,
    Indexed,
    Failed
}

/// <summary>
/// Backend and dtype remain a runtime profile. FP16 is an evaluated GPU candidate profile,
/// not a permanent default; it must be explicitly selected for a run.
/// </summary>
public sealed record SemanticExecutionProfile(string Backend, string Dtype)
{
    public static SemanticExecutionProfile Resolve(SemanticModelProfile model)
    {
        var backend = NormalizeBackend(Environment.GetEnvironmentVariable("LOCAL_MEDIA_AI_SEMANTIC_BACKEND"));
        var dtype = NormalizeDtype(Environment.GetEnvironmentVariable("LOCAL_MEDIA_AI_SEMANTIC_DTYPE"), model.DefaultDtype, backend);
        return new SemanticExecutionProfile(backend, dtype);
    }

    public bool IsCpuFallback => string.Equals(Backend, "cpu", StringComparison.Ordinal);
    public static string CpuFallbackDisplay => "CPU fallback — indexing may be significantly slower";

    public SemanticExecutionProfile WithBackend(string backend) => new(NormalizeBackend(backend), NormalizeDtype(Dtype, "float32", NormalizeBackend(backend)));

    private static string NormalizeBackend(string? value) => string.Equals(value, "cpu", StringComparison.OrdinalIgnoreCase) ? "cpu" : "cuda";
    private static string NormalizeDtype(string? value, string defaultDtype, string backend)
    {
        if (string.Equals(backend, "cpu", StringComparison.Ordinal)) return "float32";
        return string.Equals(value, "float16", StringComparison.OrdinalIgnoreCase) ? "float16" : defaultDtype;
    }
}

public static class SemanticModelRegistry
{
    public static SemanticModelProfile ResolveDevelopmentCandidate() => SemanticModelProfile.DevelopmentProductionCandidate;
}
