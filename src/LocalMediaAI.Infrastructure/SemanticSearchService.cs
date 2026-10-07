using System.Runtime.InteropServices;
using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.WorkerProtocol;

namespace LocalMediaAI.Infrastructure;

/// <summary>
/// C# owns persisted image embeddings and exact cosine ranking. The loopback Worker only
/// returns an in-memory text vector; it never receives a database path or writes SQLite.
/// </summary>
public sealed class SemanticSearchService : ISemanticSearchService
{
    private readonly ILibraryRepository _library;
    private readonly WorkerProcessManager _worker;
    private readonly AppPaths _paths;
    private readonly SemanticModelProfile _semanticModel;
    private readonly SemanticExecutionProfile _semanticExecution;
    private readonly IStructuredLogger _log;

    public SemanticSearchService(ILibraryRepository library, WorkerProcessManager worker, AppPaths paths, IStructuredLogger log, SemanticModelProfile? semanticModel = null, SemanticExecutionProfile? semanticExecution = null)
    {
        _library = library;
        _worker = worker;
        _paths = paths;
        _semanticModel = semanticModel ?? SemanticModelRegistry.ResolveDevelopmentCandidate();
        _semanticExecution = semanticExecution ?? SemanticExecutionProfile.Resolve(_semanticModel);
        _log = log;
    }

    public SemanticModelProfile SemanticModel => _semanticModel;
    public SemanticExecutionProfile SemanticExecution => _semanticExecution;

    public async Task<IReadOnlyList<SemanticSearchCandidate>> SearchAsync(string query, int topK, CancellationToken cancellationToken, Guid? libraryRootId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        topK = Math.Clamp(topK, 1, 100);
        var modelRoot = ResolveModelDirectory(_semanticModel.ModelDirectoryName);
        var adapterRoot = string.IsNullOrWhiteSpace(_semanticModel.AdapterDirectoryName) ? null : ResolveModelDirectory(_semanticModel.AdapterDirectoryName);

        var requestId = Guid.NewGuid();
        var descriptors = new Dictionary<string, string>
        {
            ["models_root"] = _paths.ModelsRoot,
            ["model_root"] = modelRoot,
            ["text"] = query.Trim()
        };
        if (adapterRoot is not null) descriptors["adapter_root"] = adapterRoot;
        var request = new WorkerJobRequest(requestId, "text-embedding-v1", descriptors, JsonSerializer.Serialize(new { model_id = _semanticModel.ModelId, revision = _semanticModel.ModelRevision, adapter = _semanticModel.Adapter, backend = _semanticExecution.Backend, dtype = _semanticExecution.Dtype }), _semanticModel.ProcessorVersion, string.Empty);
        var progress = new Progress<WorkerJobProgress>(update => _log.Write("Information", "SemanticSearchTextProgress", new { RequestId = requestId, update.Phase, update.Current, update.Total, update.Unit, update.Metrics }));
        var result = await _worker.RunAsync(request, progress, cancellationToken);
        if (!string.Equals(result.Outcome, "completed", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(result.StructuredResultJson))
            throw new SemanticSearchException(result.ErrorCode ?? "TEXT_EMBEDDING_FAILED", result.ErrorMessage ?? L.T("検索語のAI特徴量を生成できませんでした。"));

        var queryEmbedding = ParseEmbedding(result.StructuredResultJson);
        if (queryEmbedding.ModelName != _semanticModel.ModelId || queryEmbedding.ModelRevision != _semanticModel.ModelRevision || queryEmbedding.ProcessorVersion != _semanticModel.ProcessorVersion || queryEmbedding.Dtype != "float32" || queryEmbedding.Dimension != _semanticModel.Dimension || !queryEmbedding.Normalized)
            throw new SemanticSearchException("TEXT_EMBEDDING_RESULT_INVALID", L.T("検索語のAI特徴量のモデル契約が一致しません。"));
        var queryVector = DecodeAndValidate(queryEmbedding.VectorFloat32Base64, queryEmbedding.Dimension);
        var rows = await _library.GetCurrentSemanticSearchRowsAsync(_semanticModel, _semanticExecution, libraryRootId, cancellationToken);
        var candidates = new List<SemanticSearchCandidate>(rows.Count);
        foreach (var row in rows)
        {
            var score = ValidateAndCosine(queryVector, row.VectorFloat32, _semanticModel.Dimension);
            candidates.Add(new SemanticSearchCandidate(row.MediaFileId, row.FileName, row.Path, row.PresentationTimestampSeconds, score, row.VisualSampleId, row.VisualEmbeddingId, row.FrameRelativePath, row.DurationSeconds));
        }
        var ordered = candidates.OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.TimestampSeconds)
            .Take(topK)
            .ToArray();
        _log.Write("Information", "SemanticSearchCompleted", new { QueryLength = query.Length, CandidateCount = candidates.Count, ReturnedCount = ordered.Length, LibraryRootId = libraryRootId, _semanticModel.ModelId, _semanticModel.ModelRevision, _semanticModel.ProcessorVersion, _semanticExecution.Backend, _semanticExecution.Dtype });
        return ordered;
    }

    private string ResolveModelDirectory(string directoryName)
    {
        var resolved = Path.GetFullPath(Path.Combine(_paths.ModelsRoot, directoryName));
        if (!PathPolicy.IsWithinRoot(_paths.ModelsRoot, resolved))
            throw new SemanticSearchException("MODEL_PATH_INVALID", L.T("ローカルAIモデルのパスがModelsRoot外を指しています。"));
        if (!Directory.Exists(resolved))
            throw new SemanticSearchException("MODEL_NOT_FOUND", L.T("ローカルAIモデルが見つかりません。Portable ModelsRootを確認してください。"));
        return resolved;
    }

    private static WorkerEmbedding ParseEmbedding(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string RequiredString(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new SemanticSearchException("TEXT_EMBEDDING_RESULT_INVALID", L.T("検索語のAI特徴量に必須項目がありません。"));
        if (!root.TryGetProperty("dimension", out var dimensionElement) || !dimensionElement.TryGetInt32(out var dimension) ||
            !root.TryGetProperty("normalized", out var normalizedElement) || normalizedElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            throw new SemanticSearchException("TEXT_EMBEDDING_RESULT_INVALID", L.T("検索語のAI特徴量の形式が不正です。"));
        return new WorkerEmbedding(RequiredString("model_name"), RequiredString("model_revision"), RequiredString("processor_version"), RequiredString("dtype"), dimension, normalizedElement.GetBoolean(), RequiredString("vector_float32_base64"));
    }

    private static float[] DecodeAndValidate(string base64, int dimension) => DecodeAndValidate(Convert.FromBase64String(base64), dimension);

    private static float[] DecodeAndValidate(byte[] bytes, int dimension)
    {
        if (dimension <= 0 || bytes.Length != dimension * sizeof(float))
            throw new SemanticSearchException("VECTOR_FORMAT_INVALID", L.T("AI特徴量ベクトルの形式が不正です。"));
        var values = new float[dimension];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        var normSquared = 0d;
        foreach (var value in values)
        {
            if (!float.IsFinite(value)) throw new SemanticSearchException("VECTOR_FORMAT_INVALID", L.T("AI特徴量ベクトルに無効な値があります。"));
            normSquared += value * value;
        }
        var norm = Math.Sqrt(normSquared);
        if (norm is < 0.995d or > 1.005d) throw new SemanticSearchException("VECTOR_NOT_NORMALIZED", L.T("AI特徴量ベクトルが正規化されていません。"));
        return values;
    }

    private static double ValidateAndCosine(float[] queryVector, byte[] vectorBytes, int dimension)
    {
        if (dimension <= 0 || vectorBytes.Length != dimension * sizeof(float))
            throw new SemanticSearchException("VECTOR_FORMAT_INVALID", L.T("AI特徴量ベクトルの形式が不正です。"));
        var values = MemoryMarshal.Cast<byte, float>(vectorBytes.AsSpan());
        double normSquared = 0d;
        foreach (var value in values)
        {
            if (!float.IsFinite(value)) throw new SemanticSearchException("VECTOR_FORMAT_INVALID", L.T("AI特徴量ベクトルに無効な値があります。"));
            normSquared += value * value;
        }
        var norm = Math.Sqrt(normSquared);
        if (norm is < 0.995d or > 1.005d) throw new SemanticSearchException("VECTOR_NOT_NORMALIZED", L.T("AI特徴量ベクトルが正規化されていません。"));
        double total = 0d;
        for (var index = 0; index < queryVector.Length; index++) total += queryVector[index] * values[index];
        return Math.Clamp(total, -1d, 1d);
    }

    private sealed record WorkerEmbedding(string ModelName, string ModelRevision, string ProcessorVersion, string Dtype, int Dimension, bool Normalized, string VectorFloat32Base64);
}

public sealed class SemanticSearchException(string code, string userMessage) : Exception(userMessage)
{
    public string Code { get; } = code;
    public string UserMessage { get; } = userMessage;
}
