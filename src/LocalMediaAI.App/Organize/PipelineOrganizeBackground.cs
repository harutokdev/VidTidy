using System.Runtime.InteropServices;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.App.Organize;

/// <summary>Production background: scans go through the existing media pipeline; space comes from Windows.</summary>
public sealed class PipelineOrganizeBackground : IOrganizeBackground
{
    private static readonly JobState[] Unfinished = [JobState.Pending, JobState.Running, JobState.Waiting, JobState.RetryWaiting];
    private readonly MediaPipeline _pipeline;
    private readonly IJobStore _jobs;
    private readonly IStructuredLogger _log;
    private readonly ILibraryRepository? _library;

    public PipelineOrganizeBackground(MediaPipeline pipeline, IJobStore jobs, IStructuredLogger log, ILibraryRepository? library = null)
    {
        _pipeline = pipeline;
        _jobs = jobs;
        _log = log;
        _library = library;
    }

    public async Task<LocalMediaAI.Core.Organize.AnalysisProgress> GetProgressAsync(CancellationToken cancellationToken) =>
        _library is null ? IOrganizeBackground.FromCount(await GetRemainingWorkAsync(cancellationToken)) : await _library.GetAnalysisProgressAsync(cancellationToken);

    public async Task RequestChangeCheckAsync(IReadOnlyList<Guid> folderIds, CancellationToken cancellationToken)
    {
        foreach (var id in folderIds) await _pipeline.EnqueueScanAsync(id, cancellationToken);
        // The run outlives this request; the pipeline drains it on shutdown.
        _ = _pipeline.RequestRunAsync(CancellationToken.None);
    }

    public async Task<LocalMediaAI.Core.Processing.ProcessingSettings> LoadProcessingAsync(CancellationToken cancellationToken) =>
        _library is null ? _pipeline.Processing : await _library.GetProcessingSettingsAsync(cancellationToken);

    public async Task SaveProcessingAsync(LocalMediaAI.Core.Processing.ProcessingSettings settings, CancellationToken cancellationToken)
    {
        if (_library is not null) await _library.SaveProcessingSettingsAsync(settings, cancellationToken);
        _pipeline.Processing = settings;
    }

    public DateTimeOffset? RestingUntil => _pipeline.SourceRestingUntil;

    public bool WaitingForUser => _pipeline.SourceWaitingForUser;

    public async Task<int> GetRemainingWorkAsync(CancellationToken cancellationToken) =>
        (await _jobs.GetActiveAsync(cancellationToken)).Count(j => Unfinished.Contains(j.State));

    public IReadOnlyList<DriveSpace> GetDriveSpace(IEnumerable<string> folderPaths)
    {
        var result = new List<DriveSpace>();
        foreach (var drive in folderPaths.Select(System.IO.Path.GetPathRoot).OfType<string>().Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = new System.IO.DriveInfo(drive);
                if (!info.IsReady) continue;
                result.Add(new DriveSpace(drive, info.AvailableFreeSpace, info.TotalSize, RecycleBinSize(drive)));
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
            {
                _log.Write("Warning", "OrganizeDriveSpaceUnreadable", new { Drive = drive, ex.Message });
            }
        }
        return result;
    }

    private static long? RecycleBinSize(string drive)
    {
        var info = new ShQueryRbInfo { cbSize = Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBin(drive, ref info) == 0 ? info.i64Size : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string pszRootPath, ref ShQueryRbInfo pSHQueryRBInfo);
}
