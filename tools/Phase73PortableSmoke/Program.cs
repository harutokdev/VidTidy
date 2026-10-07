using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Phase73PortableSmoke <package-root> <evidence-json>");
    return 2;
}

var packageRoot = Path.GetFullPath(args[0]);
var evidencePath = Path.GetFullPath(args[1]);
var started = DateTimeOffset.UtcNow;
var events = new List<string>();
var output = new Dictionary<string, object?>
{
    ["schema_version"] = 1,
    ["test_name"] = "phase73-portable-package-production-smoke",
    ["started_utc"] = started,
    ["package_root"] = packageRoot,
    ["evidence_path"] = evidencePath
};

try
{
    RequireFile(packageRoot, "portable.flag");
    RequireFile(packageRoot, "LocalMediaAI.App.exe");
    RequireFile(packageRoot, Path.Combine("runtime", "ffmpeg", "ffmpeg.exe"));
    RequireFile(packageRoot, Path.Combine("runtime", "ffmpeg", "ffprobe.exe"));
    RequireFile(packageRoot, Path.Combine("runtime", "python-openclip", "python.exe"));
    RequireFile(packageRoot, Path.Combine("runtime", "worker", "local_media_ai_worker.py"));

    var paths = AppPaths.Create(packageRoot);
    paths.EnsureDirectories();
    EnsureWithin(packageRoot, paths.DataRoot, "DataRoot");
    EnsureWithin(packageRoot, paths.CacheRoot, "CacheRoot");
    EnsureWithin(packageRoot, paths.ModelsRoot, "ModelsRoot");
    EnsureWithin(packageRoot, paths.RuntimeRoot, "RuntimeRoot");
    EnsureWithin(packageRoot, paths.TempRoot, "TempRoot");

    output["paths"] = new
    {
        paths.AppRoot,
        paths.DataRoot,
        paths.CacheRoot,
        paths.ModelsRoot,
        paths.RuntimeRoot,
        paths.TempRoot,
        paths.DatabasePath
    };
    output["standard_user_process"] = new
    {
        user = WindowsIdentity.GetCurrent().Name,
        is_administrator_in_current_token = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)
    };

    using var log = new JsonFileLogger(paths);
    var repository = new SqliteLibraryRepository(paths, log);
    await repository.InitializeAsync(CancellationToken.None);
    var schemaVersion = await ReadSchemaVersionAsync(paths.DatabasePath);
    if (schemaVersion != 13) throw new InvalidOperationException($"Expected schema v13, actual v{schemaVersion}.");
    output["sqlite"] = new { created = File.Exists(paths.DatabasePath), schema_version = schemaVersion, database_path = paths.DatabasePath };
    events.Add("SQLite v13 initialized at app-owned DataRoot.");

    var mediaRoot = Path.Combine(paths.TempRoot, "phase73-smoke-media");
    Directory.CreateDirectory(mediaRoot);
    var testMedia = Path.Combine(mediaRoot, "blue-sample.mp4");
    var ffmpegPaths = FfmpegPaths.Resolve(paths);
    if (ffmpegPaths.FfmpegPath is null || ffmpegPaths.FfprobePath is null)
        throw new InvalidOperationException("App-owned FFmpeg/FFprobe paths were not resolved.");
    await RunProcessAsync(ffmpegPaths.FfmpegPath, ["-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "color=c=blue:s=320x240:d=1", "-c:v", "mpeg4", testMedia]);
    if (!File.Exists(testMedia) || new FileInfo(testMedia).Length == 0) throw new InvalidOperationException("Bundled FFmpeg did not produce the isolated test media.");

    var library = await repository.AddOrGetLibraryRootAsync(mediaRoot, CancellationToken.None);
    var scanId = await repository.StartScanAsync(library.LibraryRootId, CancellationToken.None);
    var scanner = new FileSystemMediaScanner();
    var scanned = new List<ScannedMediaFile>();
    await foreach (var item in scanner.EnumerateAsync(library, CancellationToken.None)) scanned.Add(item);
    if (scanned.Count != 1) throw new InvalidOperationException($"Expected one isolated media scan result; actual {scanned.Count}.");
    var reconciled = await repository.ReconcileScannedFilesAsync(scanned, scanId, CancellationToken.None);
    if (reconciled.Count != 1) throw new InvalidOperationException("Scan reconciliation cardinality did not match the isolated media fixture.");
    await repository.CompleteScanAsync(scanId, new ScanCompletion("Completed", 1, reconciled[0].IsNew ? 1 : 0, reconciled[0].IsNew ? 0 : 1, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
    await repository.MarkAbsentFilesAfterCompletedScanAsync(library.LibraryRootId, scanId, CancellationToken.None);
    var media = reconciled[0].MediaFile;
    output["scan_persistence"] = new { library_root = library.ConfiguredPath, media.MediaFileId, media.MediaContentId, media.FileName, media.PresenceState, media.QuickFingerprint };
    events.Add("Production scanner and reconciliation persisted one isolated media file.");

    var adapter = new FfmpegAdapter(ffmpegPaths, paths, log);
    var probe = await adapter.ProbeAsync(media, CancellationToken.None);
    var frame = await adapter.ExtractFrameAsync(media, 0d, "phase73-blue-frame", CancellationToken.None);
    output["ffmpeg"] = new
    {
        ffmpeg_path = ffmpegPaths.FfmpegPath,
        ffprobe_path = ffmpegPaths.FfprobePath,
        probe = new { probe.Metadata.ContainerFormat, probe.Metadata.DurationSeconds, probe.Metadata.Width, probe.Metadata.Height, probe.Metadata.VideoCodec },
        representative_frame = new { frame.RelativePath, frame.ByteSize, frame.Sha256, frame.PresentationTimestampSeconds }
    };
    events.Add("App-owned FFprobe metadata probe and FFmpeg representative frame extraction completed.");

    var model = SemanticModelRegistry.ResolveDevelopmentCandidate();
    var execution = SemanticExecutionProfile.Resolve(model);
    var modelRoot = Path.Combine(paths.ModelsRoot, model.ModelDirectoryName);
    var adapterRoot = string.IsNullOrWhiteSpace(model.AdapterDirectoryName) ? null : Path.Combine(paths.ModelsRoot, model.AdapterDirectoryName);
    RequireDirectory(modelRoot, "production model root");
    if (adapterRoot is not null) RequireDirectory(adapterRoot, "production model adapter root");
    var provenancePath = Path.Combine(modelRoot, "provenance.json");
    RequireFile(packageRoot, Path.GetRelativePath(packageRoot, provenancePath));
    await repository.UpsertModelProvenanceAsync(new ModelProvenance(model.ModelId, model.ModelRevision, model.ProcessorVersion, await FileHash.Sha256Async(provenancePath, CancellationToken.None), "Apache-2.0", false, DateTimeOffset.UtcNow), CancellationToken.None);

    await using var worker = new WorkerProcessManager(paths, log, WorkerProcessOptions.Resolve(paths, model));
    var workerStatus = await worker.EnsureStartedAsync(CancellationToken.None);
    if (workerStatus.Availability != WorkerAvailability.Available) throw new InvalidOperationException(workerStatus.UserMessage ?? "App-owned Worker did not become available.");
    var textEmbedding = await RunTextEmbeddingAsync(worker, model, execution, paths.ModelsRoot, modelRoot, adapterRoot, "blue scene");
    var vector = Convert.FromBase64String(textEmbedding.VectorBase64);
    if (vector.Length != model.Dimension * sizeof(float)) throw new InvalidOperationException("Worker embedding dimension is invalid.");
    output["worker"] = new
    {
        python_executable = WorkerProcessOptions.Resolve(paths, model).PythonExecutablePath,
        worker_script = WorkerProcessOptions.Resolve(paths, model).WorkerScriptPath,
        worker_status = workerStatus,
        model_root = modelRoot,
        adapter_root = adapterRoot,
        model = new { model.ModelId, model.ModelRevision, model.ProcessorVersion, model.Dimension, execution.Backend, execution.Dtype },
        text_embedding = new { textEmbedding.ModelName, textEmbedding.ModelRevision, textEmbedding.ProcessorVersion, textEmbedding.InferenceMilliseconds, vector_bytes = vector.Length }
    };
    events.Add("App-owned Python Worker started over loopback and loaded the bundled model for text embedding.");

    var sample = new VisualSample(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, "phase73-isolated-input-v1", 0, 0d, frame.PresentationTimestampSeconds, VisualSamplingPolicy.FixedIntervalV1.Version, "phase73-blue-frame", frame.RelativePath, frame.Sha256, frame.ByteSize, VisualSampleStatus.Completed, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    await repository.ReplaceVisualSamplesAsync(media.MediaFileId, media.MediaContentId, "phase73-isolated-input-v1", VisualSamplingPolicy.FixedIntervalV1.Version, [sample], CancellationToken.None);
    var embedding = new VisualEmbedding(Guid.NewGuid(), sample.VisualSampleId, "phase73-isolated-input-v1", "phase73-semantic-vector", model.ModelId, model.ModelRevision, model.ProcessorVersion, VisualSamplingPolicy.FixedIntervalV1.Version, execution.Dtype, model.Dimension, true, vector, VisualEmbeddingStatus.Completed, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    await repository.UpsertVisualEmbeddingAsync(embedding, CancellationToken.None);

    var semantic = new SemanticSearchService(repository, worker, paths, log, model, execution);
    var candidates = await semantic.SearchAsync("blue scene", 10, CancellationToken.None, library.LibraryRootId);
    if (candidates.Count != 1 || candidates[0].MediaFileId != media.MediaFileId) throw new InvalidOperationException("Production Semantic Search did not return the package-seeded isolated media candidate.");
    var unified = await repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(library.LibraryRootId), CancellationToken.None);
    output["product_queries"] = new
    {
        semantic_search = new { query = "blue scene", candidate_count = candidates.Count, candidates[0].MediaFileId, candidates[0].FileName, candidates[0].TimestampSeconds, candidates[0].Score },
        unified_review = new { total_count = unified.TotalCount, returned_count = unified.Items.Count, unified.Offset, unified.Limit }
    };
    events.Add("Production Semantic Search returned the isolated candidate; basic Unified Review query completed.");

    output["passed"] = true;
    output["events"] = events;
    output["finished_utc"] = DateTimeOffset.UtcNow;
    await WriteEvidenceAsync(evidencePath, output);
    Console.WriteLine(evidencePath);
    return 0;
}
catch (Exception ex)
{
    output["passed"] = false;
    output["error"] = new { type = ex.GetType().FullName, ex.Message, ex.StackTrace };
    output["events"] = events;
    output["finished_utc"] = DateTimeOffset.UtcNow;
    await WriteEvidenceAsync(evidencePath, output);
    Console.Error.WriteLine(ex);
    return 1;
}

static void RequireFile(string root, string relative)
{
    var path = Path.GetFullPath(Path.Combine(root, relative));
    EnsureWithin(root, path, relative);
    if (!File.Exists(path)) throw new FileNotFoundException($"Required app-owned package artifact is missing: {relative}", path);
}

static void RequireDirectory(string path, string name)
{
    if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Required {name} is missing: {path}");
}

static void EnsureWithin(string root, string candidate, string name)
{
    if (!PathPolicy.IsWithinRoot(root, candidate)) throw new InvalidOperationException($"{name} resolves outside the package root.");
}

static async Task<int> ReadSchemaVersionAsync(string databasePath)
{
    var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly };
    await using var connection = new SqliteConnection(builder.ToString());
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migration;";
    return Convert.ToInt32(await command.ExecuteScalarAsync());
}

static async Task RunProcessAsync(string executable, IReadOnlyList<string> arguments)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = new Process { StartInfo = start };
    if (!process.Start()) throw new InvalidOperationException($"Could not start {executable}.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
    if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} failed ({process.ExitCode}): {await stderr}");
    _ = await stdout;
}

static async Task<WorkerEmbeddingResult> RunTextEmbeddingAsync(WorkerProcessManager worker, SemanticModelProfile model, SemanticExecutionProfile execution, string modelsRoot, string modelRoot, string? adapterRoot, string text)
{
    var descriptors = new Dictionary<string, string>
    {
        ["models_root"] = modelsRoot,
        ["model_root"] = modelRoot,
        ["text"] = text
    };
    if (adapterRoot is not null) descriptors["adapter_root"] = adapterRoot;
    var request = new WorkerJobRequest(Guid.NewGuid(), "text-embedding-v1", descriptors, JsonSerializer.Serialize(new { model_id = model.ModelId, revision = model.ModelRevision, adapter = model.Adapter, backend = execution.Backend, dtype = execution.Dtype }), model.ProcessorVersion, string.Empty);
    var result = await worker.RunAsync(request, null, CancellationToken.None);
    if (!string.Equals(result.Outcome, "completed", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(result.StructuredResultJson)) throw new InvalidOperationException($"Worker text embedding failed: {result.ErrorCode} {result.ErrorMessage}");
    using var document = JsonDocument.Parse(result.StructuredResultJson);
    var root = document.RootElement;
    return new WorkerEmbeddingResult(
        root.GetProperty("model_name").GetString() ?? string.Empty,
        root.GetProperty("model_revision").GetString() ?? string.Empty,
        root.GetProperty("processor_version").GetString() ?? string.Empty,
        root.GetProperty("vector_float32_base64").GetString() ?? string.Empty,
        root.GetProperty("metrics").GetProperty("inference_ms").GetDouble());
}

static async Task WriteEvidenceAsync(string path, object value)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}

internal sealed record WorkerEmbeddingResult(string ModelName, string ModelRevision, string ProcessorVersion, string VectorBase64, double InferenceMilliseconds);
