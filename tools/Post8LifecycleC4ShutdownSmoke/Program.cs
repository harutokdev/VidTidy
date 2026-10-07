using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Post8LifecycleC4ShutdownSmoke <isolated-package-root> <evidence-json>");
    return 2;
}

var packageRoot = Path.GetFullPath(args[0]);
var evidencePath = Path.GetFullPath(args[1]);
var startedUtc = DateTimeOffset.UtcNow;
var evidence = new Dictionary<string, object?>
{
    ["schema_version"] = 1,
    ["test_name"] = "post8-lifecycle-c4-actual-wpf-shutdown",
    ["started_utc"] = startedUtc,
    ["isolated_package_root"] = packageRoot
};

try
{
    RequireFile(packageRoot, "portable.flag");
    var appPath = RequireFile(packageRoot, "LocalMediaAI.App.exe");
    var paths = AppPaths.Create(packageRoot);
    if (Directory.Exists(paths.DataRoot) && Directory.EnumerateFileSystemEntries(paths.DataRoot).Any())
        throw new InvalidOperationException("Isolated DataRoot must be empty before the smoke.");
    paths.EnsureDirectories();
    EnsureWithin(packageRoot, paths.DataRoot, "DataRoot");
    EnsureWithin(packageRoot, paths.CacheRoot, "CacheRoot");
    EnsureWithin(packageRoot, paths.ModelsRoot, "ModelsRoot");
    EnsureWithin(packageRoot, paths.RuntimeRoot, "RuntimeRoot");
    EnsureWithin(packageRoot, paths.TempRoot, "TempRoot");
    evidence["isolation_gate"] = new
    {
        portable_flag = true,
        all_roots_within_isolated_package = true,
        production_catalog_inactive = true,
        synthetic_only = true,
        paths.DataRoot,
        paths.CacheRoot,
        paths.TempRoot
    };

    Guid jobId;
    Guid sentinelJobId;
    MediaFile media;
    string mediaPath;
    using (var log = new JsonFileLogger(paths))
    {
        var repository = new SqliteLibraryRepository(paths, log);
        var jobs = new SqliteJobStore(paths, log);
        await repository.InitializeAsync(CancellationToken.None);
        var schema = await ReadScalarIntAsync(paths.DatabasePath, "SELECT COALESCE(MAX(version),0) FROM schema_migration;");
        if (schema != 13) throw new InvalidOperationException($"Expected isolated schema v13, actual v{schema}.");

        var ffmpeg = FfmpegPaths.Resolve(paths).FfmpegPath ?? throw new InvalidOperationException("Isolated bundled FFmpeg was not resolved.");
        var mediaRoot = Path.Combine(paths.TempRoot, "c4-synthetic-media");
        Directory.CreateDirectory(mediaRoot);
        mediaPath = Path.Combine(mediaRoot, "c4-generated-video.mp4");
        await RunProcessAsync(ffmpeg,
        [
            "-hide_banner", "-nostdin", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=10",
            "-t", "120", "-c:v", "mpeg4", "-q:v", "8", mediaPath
        ]);

        var root = await repository.AddOrGetLibraryRootAsync(mediaRoot, CancellationToken.None);
        var scanId = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        ReconcileResult? reconcile = null;
        await foreach (var scanned in new FileSystemMediaScanner().EnumerateAsync(root, CancellationToken.None))
            if (string.Equals(scanned.FullPath, mediaPath, StringComparison.OrdinalIgnoreCase))
                reconcile = await repository.UpsertScannedFileAsync(scanned, scanId, CancellationToken.None);
        await repository.CompleteScanAsync(scanId, new ScanCompletion("Completed", 1, 1, 0, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        media = reconcile?.MediaFile ?? throw new InvalidOperationException("Synthetic media was not reconciled.");
        await repository.UpsertMetadataAsync(new MediaMetadata(media.MediaFileId, "mp4", 120d, 640, 360, "mpeg4", "10/1", 0, 0, 0, "{}", DateTimeOffset.UtcNow), CancellationToken.None);
        var job = await jobs.EnqueueUniqueAsync(JobType.ProtectedSourceOrchestration,
            new ProtectedSourceOrchestrationPayload(media.MediaFileId, media.MediaContentId, ProtectedSourceStageEligibility.VisualSampling), 2, null, CancellationToken.None);
        jobId = job.JobId;
        var sentinel = await jobs.EnqueueAsync(JobType.Scan, new ScanPayload(root.LibraryRootId), 2, null, CancellationToken.None);
        sentinelJobId = sentinel.JobId;
        evidence["isolated_catalog"] = new { schema_version = schema, protected_job_id = jobId, sentinel_job_id = sentinelJobId };
    }
    SqliteConnection.ClearAllPools();

    var baselineProcesses = SnapshotProcessIds();
    using var app = Process.Start(new ProcessStartInfo(appPath) { WorkingDirectory = packageRoot, UseShellExecute = false })
        ?? throw new InvalidOperationException("Actual application process did not start.");
    var appPid = app.Id;
    await WaitUntilAsync(async () => string.Equals(await ReadJobStateAsync(paths.DatabasePath, jobId), "Running", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
    await WaitUntilAsync(() =>
    {
        app.Refresh();
        return Task.FromResult(app.MainWindowHandle != IntPtr.Zero);
    }, TimeSpan.FromSeconds(15));

    app.Refresh();
    if (!app.CloseMainWindow()) throw new InvalidOperationException("WM_CLOSE could not be delivered to the actual WPF main window.");
    await WaitUntilAsync(() => Task.FromResult(LogContains(paths.LogRoot, "GracefulShutdownLinearized")), TimeSpan.FromSeconds(10));
    app.Refresh();
    var closeDeferred = !app.HasExited && !LogContains(paths.LogRoot, "GracefulShutdownFinalCloseAuthorized");
    if (!closeDeferred) throw new InvalidOperationException("The first WPF close was not observably deferred while protected work remained active.");

    await app.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
    var finalJobState = await ReadJobStateAsync(paths.DatabasePath, jobId);
    var sentinelState = await ReadJobStateAsync(paths.DatabasePath, sentinelJobId);
    var sampleCount = await ReadScalarIntAsync(paths.DatabasePath, "SELECT COUNT(1) FROM visual_sample;");
    var publicationCount = await ReadScalarIntAsync(paths.DatabasePath,
        $"SELECT COUNT(1) FROM job_event WHERE job_id='{jobId:D}' AND event_name='ProtectedPublicationCommitted';");
    var integrity = await ReadScalarTextAsync(paths.DatabasePath, "PRAGMA integrity_check;");
    if (!string.Equals(finalJobState, "Completed", StringComparison.Ordinal)) throw new InvalidOperationException($"Protected job ended as {finalJobState}.");
    if (!string.Equals(sentinelState, "Pending", StringComparison.Ordinal)) throw new InvalidOperationException($"Post-linearization sentinel was acquired: {sentinelState}.");
    if (sampleCount != VisualSamplingPolicy.FixedIntervalV1.MaximumSamples) throw new InvalidOperationException($"Expected {VisualSamplingPolicy.FixedIntervalV1.MaximumSamples} committed samples, actual {sampleCount}.");
    if (publicationCount != 1) throw new InvalidOperationException($"Expected one guarded publication event, actual {publicationCount}.");
    if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Isolated SQLite integrity_check returned {integrity}.");
    using (var released = new FileStream(mediaPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }

    var lifecycle = new[]
    {
        "GracefulShutdownLinearized",
        "GracefulShutdownProcessingDrainRequested",
        "GracefulShutdownProcessingDrainCompleted",
        "GracefulShutdownWorkerDisposed",
        "GracefulShutdownPipelineDisposed",
        "GracefulShutdownLifetimeDisposed",
        "GracefulShutdownFinalCloseAuthorized"
    };
    var logText = ReadLogs(paths.LogRoot);
    var indices = lifecycle.Select(value => logText.IndexOf(value, StringComparison.Ordinal)).ToArray();
    if (indices.Any(index => index < 0) || !indices.SequenceEqual(indices.Order()))
        throw new InvalidOperationException("Graceful shutdown lifecycle events were missing or out of order.");

    await Task.Delay(500);
    var residual = SnapshotProcessIds()
        .Where(pair => !baselineProcesses.TryGetValue(pair.Key, out var baseline) || !pair.Value.SetEquals(baseline))
        .ToDictionary(pair => pair.Key, pair => pair.Value.Except(baselineProcesses.GetValueOrDefault(pair.Key, [])).ToArray());
    if (residual.Any(pair => pair.Value.Length > 0))
        throw new InvalidOperationException("A smoke-owned process remained: " + JsonSerializer.Serialize(residual));

    evidence["result"] = new
    {
        app_pid = appPid,
        first_close_deferred = closeDeferred,
        protected_job_state = finalJobState,
        sentinel_job_state = sentinelState,
        committed_visual_samples = sampleCount,
        guarded_publication_commits = publicationCount,
        source_session_lease_released = true,
        lifecycle_order = lifecycle,
        app_residual = 0,
        worker_residual = 0,
        ffmpeg_residual = 0,
        ffprobe_residual = 0,
        sqlite_integrity = integrity
    };
    evidence["passed"] = true;
    evidence["finished_utc"] = DateTimeOffset.UtcNow;
    await WriteEvidenceAsync(evidencePath, evidence);
    Console.WriteLine(evidencePath);
    return 0;
}
catch (Exception ex)
{
    evidence["passed"] = false;
    evidence["error"] = new { type = ex.GetType().FullName, ex.Message, ex.StackTrace };
    evidence["finished_utc"] = DateTimeOffset.UtcNow;
    await WriteEvidenceAsync(evidencePath, evidence);
    Console.Error.WriteLine(ex);
    return 1;
}

static string RequireFile(string root, string relative)
{
    var path = Path.GetFullPath(Path.Combine(root, relative));
    EnsureWithin(root, path, relative);
    if (!File.Exists(path)) throw new FileNotFoundException($"Required isolated artifact is missing: {relative}", path);
    return path;
}

static void EnsureWithin(string root, string candidate, string name)
{
    var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!Path.GetFullPath(candidate).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"{name} resolves outside the isolated package.");
}

static async Task RunProcessAsync(string executable, IReadOnlyList<string> arguments)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
    if (process.ExitCode != 0) throw new InvalidOperationException($"Synthetic FFmpeg generation failed: {await stderr}");
    _ = await stdout;
}

static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (!await condition())
    {
        if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Expected isolated smoke condition was not observed.");
        await Task.Delay(50);
    }
}

static Dictionary<string, HashSet<int>> SnapshotProcessIds() =>
    SmokeProcess.Names.ToDictionary(
        name => name,
        name => Process.GetProcessesByName(name).Select(process =>
        {
            using (process) return process.Id;
        }).ToHashSet(),
        StringComparer.OrdinalIgnoreCase);

static bool LogContains(string logRoot, string eventName) => ReadLogs(logRoot).Contains(eventName, StringComparison.Ordinal);

static string ReadLogs(string logRoot) => string.Join('\n', Directory.EnumerateFiles(logRoot, "*.jsonl").Select(path =>
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}));

static async Task<string?> ReadJobStateAsync(string databasePath, Guid jobId) =>
    await ReadScalarTextAsync(databasePath, $"SELECT state FROM job WHERE job_id='{jobId:D}';");

static async Task<int> ReadScalarIntAsync(string databasePath, string sql) =>
    Convert.ToInt32(await ReadScalarAsync(databasePath, sql), CultureInfo.InvariantCulture);

static async Task<string?> ReadScalarTextAsync(string databasePath, string sql) =>
    Convert.ToString(await ReadScalarAsync(databasePath, sql), CultureInfo.InvariantCulture);

static async Task<object?> ReadScalarAsync(string databasePath, string sql)
{
    var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly };
    await using var connection = new SqliteConnection(builder.ToString());
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    return await command.ExecuteScalarAsync();
}

static async Task WriteEvidenceAsync(string path, object value)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}

internal static class SmokeProcess
{
    internal static readonly string[] Names = ["LocalMediaAI.App", "python", "ffmpeg", "ffprobe"];
}
