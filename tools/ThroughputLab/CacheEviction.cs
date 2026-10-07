using System.Diagnostics;

namespace ThroughputLab;

/// <summary>
/// Pushes previously read media out of the Windows file cache by reading a filler file larger than RAM,
/// so each run starts cold without needing elevation to purge the standby list.
/// </summary>
public static class CacheEviction
{
    public static async Task EvictAsync(LabLayout lab, TextWriter log, CancellationToken cancellationToken)
    {
        var ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var target = (long)(ram * 1.3);
        Directory.CreateDirectory(Path.GetDirectoryName(lab.EvictionFile)!);
        var buffer = new byte[4 * 1024 * 1024];
        var stopwatch = Stopwatch.StartNew();
        if (!File.Exists(lab.EvictionFile) || new FileInfo(lab.EvictionFile).Length < target)
        {
            var free = new DriveInfo(Path.GetPathRoot(lab.EvictionFile)!).AvailableFreeSpace;
            if (free < target + 50L * 1024 * 1024 * 1024)
                throw new IOException("Not enough free space on the lab drive for the cache-eviction filler.");
            log.WriteLine($"eviction: writing {target / 1073741824.0:F1} GiB filler (one time)");
            Random.Shared.NextBytes(buffer);
            await using var output = new FileStream(lab.EvictionFile, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length);
            for (long written = 0; written < target; written += buffer.Length)
                await output.WriteAsync(buffer, cancellationToken);
        }
        log.WriteLine("eviction: reading filler");
        await using (var input = new FileStream(lab.EvictionFile, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length))
            while (await input.ReadAsync(buffer, cancellationToken) > 0) { }
        log.WriteLine($"eviction: done in {stopwatch.Elapsed.TotalSeconds:F0}s");
    }
}
