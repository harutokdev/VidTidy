using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using LocalMediaAI.Core;
using LocalMediaAI.WorkerProtocol;

namespace LocalMediaAI.Infrastructure;

/// <summary>Captures only directly observed local resource values. Missing values remain null/false.</summary>
public interface IResourceSnapshotProvider
{
    Task<ResourceSnapshot> CaptureAsync(CancellationToken cancellationToken);
}

public sealed class WorkerBackedResourceSnapshotProvider(WorkerProcessManager worker, IResourceSnapshotProvider? fallback = null) : IResourceSnapshotProvider
{
    private readonly IResourceSnapshotProvider _fallback = fallback ?? new WindowsResourceSnapshotProvider();

    public async Task<ResourceSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var baseline = await _fallback.CaptureAsync(cancellationToken);
        if (baseline.GpuAvailable is true && baseline.AvailableVramBytes is not null) return baseline;
        try
        {
            var result = await worker.RunAsync(new WorkerJobRequest(Guid.NewGuid(), "resource-snapshot-v1", new Dictionary<string, string>(), "{}", string.Empty, string.Empty), null, cancellationToken);
            if (!string.Equals(result.Outcome, "completed", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(result.StructuredResultJson)) return baseline;
            using var document = JsonDocument.Parse(result.StructuredResultJson);
            var root = document.RootElement;
            var available = root.TryGetProperty("gpu_available", out var availability) && availability.ValueKind == JsonValueKind.True;
            long? freeVram = root.TryGetProperty("available_vram_bytes", out var free) && free.TryGetInt64(out var bytes) && bytes >= 0 ? bytes : null;
            return baseline with { GpuAvailable = available, AvailableVramBytes = freeVram };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return baseline;
        }
    }
}

public sealed class WindowsResourceSnapshotProvider : IResourceSnapshotProvider
{
    private static readonly TimeSpan NvidiaSmiTimeout = TimeSpan.FromSeconds(3);

    public async Task<ResourceSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        long? availableRam = GlobalMemoryStatusEx(ref memory) ? checked((long)memory.AvailPhys) : null;
        var (gpuAvailable, freeVram) = await CaptureGpuAsync(cancellationToken);
        return new ResourceSnapshot(
            CpuPressurePercent: null,
            AvailableRamBytes: availableRam,
            GpuAvailable: gpuAvailable,
            AvailableVramBytes: freeVram,
            ReadIoPressurePercent: null,
            WriteIoPressurePercent: null,
            DbWriterPressure: null);
    }

    private static async Task<(bool Available, long? FreeVramBytes)> CaptureGpuAsync(CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo("nvidia-smi.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--query-gpu=memory.free");
            start.ArgumentList.Add("--format=csv,noheader,nounits");
            using var process = Process.Start(start);
            if (process is null) return (false, null);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(NvidiaSmiTimeout, cancellationToken);
            var stdout = await stdoutTask;
            _ = await stderrTask;
            if (process.ExitCode != 0) return (false, null);
            var first = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (!long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var freeMiB) || freeMiB < 0) return (false, null);
            return (true, checked(freeMiB * 1024L * 1024L));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            return (false, null);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}

/// <summary>
/// Reuses a resource reading for a short time. Admission runs before every embedding job, and each fresh reading is a
/// round trip to the worker; a few seconds of staleness does not change the decision for back-to-back jobs.
/// </summary>
public sealed class CachingResourceSnapshotProvider(IResourceSnapshotProvider inner, TimeSpan maxAge, Func<DateTimeOffset>? clock = null) : IResourceSnapshotProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    private (ResourceSnapshot Snapshot, DateTimeOffset At)? _last;

    public async Task<ResourceSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_last is { } last && _clock() - last.At < maxAge) return last.Snapshot;
            var snapshot = await inner.CaptureAsync(cancellationToken).ConfigureAwait(false);
            _last = (snapshot, _clock());
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }
}
