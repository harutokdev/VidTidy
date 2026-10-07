using System.Globalization;
using System.Text;
using System.Text.Json;
using LocalMediaAI.Infrastructure;

namespace ThroughputLab;

public sealed record SoakOptions(string Set, TimeSpan Idle, TimeSpan Work, TimeSpan Rest);

public sealed record SoakPhase(string Phase, DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Thermal protocol for the HDD: idle baseline, continuous processing, then rest, with media-disk activity
/// sampled every 10 s. An elevated temperature logger runs alongside; the two CSVs are joined by timestamp.
/// </summary>
public sealed class SoakProtocol(LabLayout lab, TextWriter log)
{
    public async Task<string> ExecuteAsync(SoakOptions options, CancellationToken cancellationToken)
    {
        var mediaFolder = lab.MediaFolder(options.Set);
        var mediaDisk = PhysicalDisk.TryGetDiskNumber(mediaFolder) ?? throw new InvalidOperationException("Cannot resolve the media disk.");
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var phases = new List<SoakPhase>();
        await using var sampler = new DiskSampler([mediaDisk], TimeSpan.FromSeconds(10));
        var started = DateTimeOffset.Now;

        await PhaseAsync("idle", options.Idle, phases, token => Task.Delay(options.Idle, token), cancellationToken);
        await PhaseAsync("work", options.Work, phases, token => WorkAsync(mediaFolder, stamp, token), cancellationToken);
        await PhaseAsync("rest", options.Rest, phases, token => Task.Delay(options.Rest, token), cancellationToken);

        var samples = sampler.Samples;
        Directory.CreateDirectory(lab.Measurements);
        var csv = new StringBuilder("time,phase,busy_percent,reads,writes,read_mib,write_mib\n");
        foreach (var sample in samples)
        {
            var at = started.AddSeconds(sample.ElapsedSeconds);
            csv.Append(CultureInfo.InvariantCulture, $"{at:O},{PhaseAt(phases, at)},{sample.BusyPercent},{sample.ReadCount},{sample.WriteCount},{sample.BytesRead / 1048576.0:F1},{sample.BytesWritten / 1048576.0:F1}\n");
        }
        var csvPath = Path.Combine(lab.Measurements, $"soak-activity-{stamp}.csv");
        await File.WriteAllTextAsync(csvPath, csv.ToString(), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(lab.Measurements, $"soak-phases-{stamp}.json"),
            JsonSerializer.Serialize(new { mediaDisk, set = options.Set, phases }, Json.Indented), cancellationToken);
        log.WriteLine("soak activity: " + csvPath);
        return csvPath;
    }

    private async Task PhaseAsync(string name, TimeSpan duration, List<SoakPhase> phases, Func<CancellationToken, Task> body, CancellationToken cancellationToken)
    {
        var start = DateTimeOffset.Now;
        log.WriteLine($"{start:HH:mm:ss} phase {name} for {duration.TotalMinutes:F0} min");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(duration);
        try { await body(deadline.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        var remaining = duration - (DateTimeOffset.Now - start);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
        phases.Add(new SoakPhase(name, start, DateTimeOffset.Now));
    }

    private async Task WorkAsync(string mediaFolder, string stamp, CancellationToken cancellationToken)
    {
        for (var round = 1; !cancellationToken.IsCancellationRequested; round++)
        {
            var paths = LabInstance.CreatePaths(lab, "hdd", $"soak-{stamp}-{round}");
            try
            {
                await using var instance = await LabInstance.OpenAsync(lab, paths, new LocalMediaAI.Core.Processing.ProcessingSettings(LocalMediaAI.Core.Processing.ProcessingPace.Fastest, false), CancellationToken.None);
                var root = await instance.Library.AddOrGetLibraryRootAsync(mediaFolder, CancellationToken.None);
                await instance.Pipeline.EnqueueScanAsync(root.LibraryRootId, CancellationToken.None);
                await instance.DrainAsync(log, cancellationToken);
                log.WriteLine($"  round {round} finished the whole set before the work phase ended; evicting and repeating");
                await CacheEviction.EvictAsync(lab, log, cancellationToken);
            }
            finally
            {
                LabInstance.Delete(paths, log);
            }
        }
    }

    private static string PhaseAt(IEnumerable<SoakPhase> phases, DateTimeOffset at) =>
        phases.FirstOrDefault(phase => at >= phase.Start && at <= phase.End)?.Phase ?? "transition";
}
