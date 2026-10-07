using LocalMediaAI.Core;
using LocalMediaAI.Core.Processing;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace ThroughputLab;

/// <summary>A lab-confined catalog plus a pipeline composed the same way App.xaml.cs composes production.</summary>
public sealed class LabInstance : IAsyncDisposable
{
    private readonly JsonFileLogger _logger;
    private readonly WorkerProcessManager _worker;

    private LabInstance(AppPaths paths, JsonFileLogger logger, SqliteLibraryRepository library, SqliteJobStore jobs, WorkerProcessManager worker, MediaPipeline pipeline)
    {
        Paths = paths;
        _logger = logger;
        Library = library;
        Jobs = jobs;
        _worker = worker;
        Pipeline = pipeline;
    }

    public AppPaths Paths { get; }
    public SqliteLibraryRepository Library { get; }
    public SqliteJobStore Jobs { get; }
    public MediaPipeline Pipeline { get; }

    public static AppPaths CreatePaths(LabLayout lab, string storage, string name)
    {
        var parent = storage switch
        {
            "hdd" => lab.HddInstances,
            "ssd" => lab.SsdInstances,
            _ => throw new ArgumentException("storage must be hdd or ssd")
        };
        var instance = Path.Combine(parent, name);
        var overrides = new Dictionary<string, string>
        {
            ["DataRoot"] = Path.Combine(instance, "data"),
            ["CacheRoot"] = Path.Combine(instance, "cache"),
            ["TempRoot"] = Path.Combine(instance, "temp"),
            ["ModelsRoot"] = lab.ModelsRoot,
            ["RuntimeRoot"] = lab.RuntimeRoot
        };
        var paths = AppPaths.Create(instance, overrides, lab.Policy);
        paths.EnsureDirectories();
        return paths;
    }

    public static async Task<LabInstance> OpenAsync(LabLayout lab, AppPaths paths, ProcessingSettings processing, CancellationToken cancellationToken)
    {
        var logger = new JsonFileLogger(paths);
        var library = new SqliteLibraryRepository(paths, logger);
        await library.InitializeAsync(cancellationToken);
        var jobs = new SqliteJobStore(paths, logger);
        var ffmpeg = new FfmpegAdapter(FfmpegPaths.Resolve(paths), paths, logger);
        var semanticModel = SemanticModelRegistry.ResolveDevelopmentCandidate();
        var semanticExecution = SemanticExecutionProfile.Resolve(semanticModel);
        var resolution = await AiResourceCache.ResolveModelsRootAsync(paths, semanticModel, logger, cancellationToken, lab.AiResourceCacheRoot);
        var effectivePaths = paths with { ModelsRoot = resolution.ModelsRoot };
        var worker = new WorkerProcessManager(effectivePaths, logger, WorkerProcessOptions.Resolve(effectivePaths, semanticModel));
        var retention = new RetentionCleanupCoordinator(token => library.RunRetentionCleanupAsync(DateTimeOffset.UtcNow, token), logger);
        var pipeline = new MediaPipeline(library, jobs, new FileSystemMediaScanner(), ffmpeg, logger, PipelineConcurrencyOptions.FromEnvironment(),
            worker, effectivePaths, semanticModel: semanticModel, semanticExecution: semanticExecution, retentionCleanup: retention)
        {
            Processing = processing
        };
        return new LabInstance(paths, logger, library, jobs, worker, pipeline);
    }

    /// <summary>Runs until no job is left, waiting out resource-deferred retries, or until the token fires.</summary>
    public async Task DrainAsync(TextWriter log, CancellationToken cancellationToken)
    {
        var progress = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            await Pipeline.RunUntilIdleAsync(cancellationToken);
            var unfinished = (await Jobs.GetActiveAsync(cancellationToken)).Count(job => job.State is JobState.Pending or JobState.Running or JobState.Waiting or JobState.RetryWaiting);
            if (unfinished == 0) return;
            if (progress.Elapsed > TimeSpan.FromMinutes(1))
            {
                log.WriteLine($"  {unfinished} jobs waiting to retry");
                progress.Restart();
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Pipeline.RequestShutdownAndDrainAsync();
        Pipeline.Dispose();
        await _worker.DisposeAsync();
        _logger.Dispose();
        SqliteConnection.ClearAllPools();
    }

    public static void Delete(AppPaths paths, TextWriter log)
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(paths.AppRoot, true); }
        catch (IOException ex) { log.WriteLine("instance cleanup deferred: " + ex.Message); }
        catch (UnauthorizedAccessException ex) { log.WriteLine("instance cleanup deferred: " + ex.Message); }
    }
}
