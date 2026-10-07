using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace ThroughputLab;

public sealed record RunOptions(string Set, string Storage, string Label, bool Evict, bool KeepInstance, TimeSpan Timeout, LocalMediaAI.Core.Processing.ProcessingPace Pace = LocalMediaAI.Core.Processing.ProcessingPace.Fastest);

/// <summary>
/// Processes one lab media folder in a fresh, lab-confined instance composed exactly like the app,
/// and records wall time, per-stage job time, media subprocess counts and per-disk activity.
/// The result JSON never lists media file names.
/// </summary>
public sealed class ThroughputRun(LabLayout lab, TextWriter log)
{
    public async Task<string> ExecuteAsync(RunOptions options, CancellationToken cancellationToken)
    {
        var mediaFolder = lab.MediaFolder(options.Set);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var paths = LabInstance.CreatePaths(lab, options.Storage, $"{options.Label}-{stamp}");

        var mediaFiles = Directory.EnumerateFiles(mediaFolder, "*", SearchOption.AllDirectories).Select(path => new FileInfo(path)).ToArray();
        var mediaBefore = mediaFiles.ToDictionary(file => file.FullName, file => (file.Length, file.LastWriteTimeUtc), StringComparer.OrdinalIgnoreCase);
        var mediaDisk = PhysicalDisk.TryGetDiskNumber(mediaFolder) ?? throw new InvalidOperationException("Cannot resolve the media disk.");
        var dataDisk = PhysicalDisk.TryGetDiskNumber(paths.DataRoot) ?? throw new InvalidOperationException("Cannot resolve the data disk.");
        var systemDisk = PhysicalDisk.TryGetDiskNumber(Environment.SystemDirectory) ?? dataDisk;
        log.WriteLine($"run {options.Label}: {mediaFiles.Length} files, {mediaFiles.Sum(file => file.Length) / 1073741824.0:F1} GiB, media disk {mediaDisk}, data disk {dataDisk} ({options.Storage})");

        if (options.Evict) await CacheEviction.EvictAsync(lab, log, cancellationToken);

        var result = await ProcessAsync(paths, mediaFolder, [mediaDisk, dataDisk, systemDisk], options.Timeout, new LocalMediaAI.Core.Processing.ProcessingSettings(options.Pace, false), cancellationToken);

        var unchanged = mediaFiles.All(file =>
        {
            var current = new FileInfo(file.FullName);
            return current.Exists && mediaBefore[file.FullName] == (current.Length, current.LastWriteTimeUtc);
        });
        var report = new
        {
            label = options.Label,
            set = options.Set,
            storage = options.Storage,
            pace = options.Pace.ToString(),
            startedAt = result.StartedAt,
            media = new
            {
                files = mediaFiles.Length,
                totalBytes = mediaFiles.Sum(file => file.Length),
                under100MiB = mediaFiles.Count(file => file.Length < 100L * 1024 * 1024),
                atLeast1GiB = mediaFiles.Count(file => file.Length >= 1024L * 1024 * 1024),
                unchangedAfterRun = unchanged
            },
            disks = new { media = mediaDisk, data = dataDisk, system = systemDisk },
            wallSeconds = Math.Round(result.Wall.TotalSeconds, 1),
            secondsPerFile = Math.Round(result.Wall.TotalSeconds / Math.Max(1, mediaFiles.Length), 3),
            thumbnailsCompleteSeconds = result.ThumbnailsCompleteSeconds,
            sourceWorkCompleteSeconds = result.SourceWorkCompleteSeconds,
            evicted = options.Evict,
            result.Jobs,
            result.MediaProcesses,
            diskTotals = result.DiskTotals.Select(pair => new
            {
                disk = pair.Key,
                pair.Value.ReadCount,
                pair.Value.WriteCount,
                readMiB = Math.Round(pair.Value.BytesRead / 1048576.0, 1),
                writeMiB = Math.Round(pair.Value.BytesWritten / 1048576.0, 1),
                busyPercent = Math.Round(pair.Value.BusyPercent, 1),
                writesPerFile = Math.Round(pair.Value.WriteCount / (double)Math.Max(1, mediaFiles.Length), 1)
            }),
            result.IntegrityOk,
            result.SchemaVersion,
            result.VisualEmbeddings,
            diskSamples = result.Samples
        };
        Directory.CreateDirectory(lab.Measurements);
        var reportPath = Path.Combine(lab.Measurements, $"{options.Label}-{stamp}.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, Json.Indented), cancellationToken);
        log.WriteLine($"run {options.Label}: {result.Wall.TotalMinutes:F1} min, {report.secondsPerFile}s/file, media disk writes {result.DiskTotals.GetValueOrDefault(mediaDisk)?.WriteCount}, busy {result.DiskTotals.GetValueOrDefault(mediaDisk)?.BusyPercent:F0}%");
        log.WriteLine("report: " + reportPath);

        if (!options.KeepInstance) LabInstance.Delete(paths, log);
        return reportPath;
    }

    private sealed record ProcessResult(DateTimeOffset StartedAt, TimeSpan Wall, double? ThumbnailsCompleteSeconds, double? SourceWorkCompleteSeconds,
        object Jobs, object MediaProcesses, IReadOnlyDictionary<int, DiskActivity> DiskTotals, IReadOnlyList<DiskSample> Samples,
        bool IntegrityOk, long SchemaVersion, int VisualEmbeddings);

    private async Task<ProcessResult> ProcessAsync(AppPaths paths, string mediaFolder, int[] disks, TimeSpan timeout, LocalMediaAI.Core.Processing.ProcessingSettings processing, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyDictionary<int, DiskActivity> totals;
        IReadOnlyList<DiskSample> samples;
        bool integrity;
        int embeddings;
        await using (var instance = await LabInstance.OpenAsync(lab, paths, processing, cancellationToken))
        {
            var root = await instance.Library.AddOrGetLibraryRootAsync(mediaFolder, cancellationToken);
            startedAt = DateTimeOffset.UtcNow;
            stopwatch.Restart();
            await using var sampler = new DiskSampler(disks, TimeSpan.FromSeconds(2));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            await instance.Pipeline.EnqueueScanAsync(root.LibraryRootId, deadline.Token);
            await instance.DrainAsync(log, deadline.Token);
            stopwatch.Stop();
            totals = sampler.Totals();
            samples = sampler.Samples;
            integrity = await instance.Library.IntegrityCheckAsync(cancellationToken);
            embeddings = (await instance.Library.GetCompletedVisualEmbeddingsAsync(cancellationToken)).Count;
        }

        var (jobSummary, thumbnailsDone, sourceDone) = await SummarizeJobsAsync(paths.DatabasePath, startedAt, cancellationToken);
        var schema = await ScalarAsync(paths.DatabasePath, "SELECT COALESCE(MAX(version),0) FROM schema_migration;", cancellationToken);
        return new ProcessResult(startedAt, stopwatch.Elapsed, thumbnailsDone, sourceDone, jobSummary, SummarizeMediaProcesses(paths.LogRoot),
            totals, samples, integrity, schema, embeddings);
    }
    private static async Task<(object Summary, double? ThumbnailsDone, double? SourceDone)> SummarizeJobsAsync(string database, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(cancellationToken);
        var rows = new List<(string Type, string State, DateTimeOffset? Started, DateTimeOffset? Completed)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT job_type, state, started_at, completed_at FROM job;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((reader.GetString(0), reader.GetString(1), ParseTime(reader, 2), ParseTime(reader, 3)));
        }
        var summary = rows.GroupBy(row => row.Type).OrderBy(group => group.Key).Select(group =>
        {
            var measured = group.Where(row => row.Started.HasValue && row.Completed.HasValue).Select(row => (row.Completed!.Value - row.Started!.Value).TotalSeconds).ToArray();
            return new
            {
                type = group.Key,
                count = group.Count(),
                states = group.GroupBy(row => row.State).ToDictionary(state => state.Key, state => state.Count()),
                totalSeconds = Math.Round(measured.Sum(), 1),
                meanSeconds = measured.Length == 0 ? 0 : Math.Round(measured.Average(), 3),
                maxSeconds = measured.Length == 0 ? 0 : Math.Round(measured.Max(), 2)
            };
        }).ToArray();
        double? LastCompletion(string type) => rows.Where(row => row.Type == type && row.Completed.HasValue)
            .Select(row => (double?)Math.Round((row.Completed!.Value - startedAt).TotalSeconds, 1)).DefaultIfEmpty(null).Max();
        double? thumbnails;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT MAX(completed_at) FROM analysis WHERE analysis_type='Thumbnail' AND status='Completed';";
            var value = await command.ExecuteScalarAsync(cancellationToken) as string;
            thumbnails = value is null ? null : Math.Round((DateTimeOffset.Parse(value, CultureInfo.InvariantCulture) - startedAt).TotalSeconds, 1);
        }
        return (summary, thumbnails, LastCompletion(nameof(JobType.ProtectedSourceOrchestration)));
    }

    private static DateTimeOffset? ParseTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static async Task<long> ScalarAsync(string database, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static object SummarizeMediaProcesses(string logRoot)
    {
        var events = new List<(string Operation, double Milliseconds)>();
        foreach (var file in Directory.EnumerateFiles(logRoot, "*.jsonl"))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("EventName", out var name) || name.GetString() != "MediaProcessFinished") continue;
                var fields = root.GetProperty("Fields");
                events.Add((fields.GetProperty("Operation").GetString() ?? "unknown", fields.TryGetProperty("DurationMs", out var duration) ? duration.GetDouble() : 0));
            }
        }
        return events.GroupBy(item => item.Operation).OrderBy(group => group.Key).Select(group => new
        {
            operation = group.Key,
            count = group.Count(),
            totalSeconds = Math.Round(group.Sum(item => item.Milliseconds) / 1000, 1),
            meanMilliseconds = Math.Round(group.Average(item => item.Milliseconds), 1)
        }).ToArray();
    }
}
