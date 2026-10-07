using LocalMediaAI.Infrastructure;

namespace ThroughputLab;

public sealed record DiskSample(double ElapsedSeconds, int DiskNumber, double BusyPercent, long ReadCount, long WriteCount, long BytesRead, long BytesWritten);

/// <summary>Samples per-disk driver counters at a fixed interval so busy time and write interruptions can be plotted over a run.</summary>
public sealed class DiskSampler : IAsyncDisposable
{
    private readonly int[] _disks;
    private readonly TimeSpan _interval;
    private readonly List<DiskSample> _samples = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private readonly Task _loop;

    public DiskSampler(IEnumerable<int> disks, TimeSpan interval)
    {
        _disks = disks.Distinct().ToArray();
        _interval = interval;
        Start = Snapshot();
        _loop = RunAsync();
    }

    public IReadOnlyDictionary<int, DiskPerformanceSnapshot> Start { get; }
    public IReadOnlyList<DiskSample> Samples { get { lock (_samples) return _samples.ToArray(); } }

    public IReadOnlyDictionary<int, DiskActivity> Totals()
    {
        var end = Snapshot();
        return end.Where(pair => Start.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value.Since(Start[pair.Key]));
    }

    private Dictionary<int, DiskPerformanceSnapshot> Snapshot()
    {
        var result = new Dictionary<int, DiskPerformanceSnapshot>();
        foreach (var disk in _disks)
            if (PhysicalDisk.TryRead(disk) is { } snapshot) result[disk] = snapshot;
        return result;
    }

    private async Task RunAsync()
    {
        var previous = Snapshot();
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                var current = Snapshot();
                var elapsed = (DateTimeOffset.Now - _started).TotalSeconds;
                lock (_samples)
                {
                    foreach (var (disk, snapshot) in current)
                    {
                        if (!previous.TryGetValue(disk, out var before)) continue;
                        var delta = snapshot.Since(before);
                        _samples.Add(new DiskSample(Math.Round(elapsed, 1), disk, Math.Round(delta.BusyPercent, 1), delta.ReadCount, delta.WriteCount, delta.BytesRead, delta.BytesWritten));
                    }
                }
                previous = current;
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }
}
