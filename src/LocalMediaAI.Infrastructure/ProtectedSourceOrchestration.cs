using System.Text;
using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure;

public enum ProcessingSourceSessionPhase
{
    Acquired,
    SourceStagesCompleted,
    DerivedOrPublication,
    PublicationBoundaryCompleted,
    Aborted,
    Disposed
}

public enum ProcessingPublicationResolution { Published, Rejected }

public sealed class ProcessingSourceLeaseUnavailableException : IOException
{
    public ProcessingSourceLeaseUnavailableException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}

public sealed record UnpublishedProcessingArtifact(
    Guid SessionId,
    string RelativePath,
    string FullPath)
{
    public bool IsPublished { get; }
}

/// <summary>
/// A borrowed, non-disposable view of one authoritative processing source.
/// Stage code can read the protected path, but only ProcessingSourceSession owns
/// the lease and can advance or end its lifetime.
/// </summary>
public sealed class ProcessingSourceAccess
{
    private readonly ProcessingSourceSession _owner;

    internal ProcessingSourceAccess(ProcessingSourceSession owner) => _owner = owner;

    public Guid SessionId => GetOwner().SessionId;
    public Guid MediaFileId => GetOwner().MediaFileId;
    public Guid ExpectedMediaContentId => GetOwner().ExpectedMediaContentId;
    public string SourcePath => GetOwner().SourcePath;
    public long SourceLength => GetOwner().SourceLength;
    public DateTimeOffset SourceLastWriteTimeUtc => GetOwner().SourceLastWriteTimeUtc;
    public string? VolumeIdentity => GetOwner().VolumeIdentity;
    public string? FileIdentity => GetOwner().FileIdentity;

    public void EnsureMatches(MediaFile media)
    {
        ArgumentNullException.ThrowIfNull(media);
        var owner = GetOwner();
        if (media.MediaFileId != owner.MediaFileId || media.MediaContentId != owner.ExpectedMediaContentId ||
            !string.Equals(Path.GetFullPath(media.Path), owner.SourcePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The media item does not belong to this protected source session.");
    }

    private ProcessingSourceSession GetOwner()
    {
        _owner.EnsureBorrowIsValid();
        return _owner;
    }
}

/// <summary>
/// Foundation owner for one protected per-media source lifetime. This type is not
/// wired into the production scheduler yet. FileShare.Read keeps compatible readers
/// working while normal write/delete/rename/replace opens are excluded on qualified
/// current-PC local NTFS storage.
/// </summary>
public sealed class ProcessingSourceSession : IAsyncDisposable
{
    private const string StagingDirectoryName = "processing-source-sessions-v1";
    private const string OwnerMarkerName = ".localmediaai-processing-session-v1";
    private readonly object _gate = new();
    private readonly FileStream _lease;
    private readonly IStructuredLogger _log;
    private readonly ProcessingSourceAccess _access;
    private readonly List<UnpublishedProcessingArtifact> _artifacts = [];
    private int _activeStages;
    private bool _disposed;

    private ProcessingSourceSession(
        Guid sessionId,
        Guid mediaFileId,
        Guid expectedMediaContentId,
        string sourcePath,
        long sourceLength,
        DateTimeOffset sourceLastWriteTimeUtc,
        string? volumeIdentity,
        string? fileIdentity,
        string stagingRoot,
        string stagingDirectory,
        FileStream lease,
        IStructuredLogger log)
    {
        SessionId = sessionId;
        MediaFileId = mediaFileId;
        ExpectedMediaContentId = expectedMediaContentId;
        SourcePath = sourcePath;
        SourceLength = sourceLength;
        SourceLastWriteTimeUtc = sourceLastWriteTimeUtc;
        VolumeIdentity = volumeIdentity;
        FileIdentity = fileIdentity;
        StagingRoot = stagingRoot;
        StagingDirectory = stagingDirectory;
        _lease = lease;
        _log = log;
        _access = new ProcessingSourceAccess(this);
        Phase = ProcessingSourceSessionPhase.Acquired;
    }

    public Guid SessionId { get; }
    public Guid MediaFileId { get; }
    public Guid ExpectedMediaContentId { get; }
    public string SourcePath { get; }
    public long SourceLength { get; }
    public DateTimeOffset SourceLastWriteTimeUtc { get; }
    public string? VolumeIdentity { get; }
    public string? FileIdentity { get; }
    public string StagingRoot { get; }
    public string StagingDirectory { get; }
    public ProcessingSourceSessionPhase Phase { get; private set; }
    public ProcessingPublicationResolution? PublicationResolution { get; private set; }
    public bool IsLeaseHeld => !_disposed;
    public bool CleanupSucceeded { get; private set; } = true;
    public IReadOnlyList<UnpublishedProcessingArtifact> UnpublishedArtifacts
    {
        get { lock (_gate) return _artifacts.ToArray(); }
    }

    public static ValueTask<ProcessingSourceSession> AcquireAsync(
        AppPaths appPaths,
        Guid mediaFileId,
        Guid expectedMediaContentId,
        string sourcePath,
        IStructuredLogger log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(log);
        if (mediaFileId == Guid.Empty || expectedMediaContentId == Guid.Empty)
            throw new ArgumentException("A protected source session requires non-empty media and content identities.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new ProcessingSourceLeaseUnavailableException("SOURCE_LEASE_PLATFORM_UNSUPPORTED", "Protected source sessions are qualified only on the current Windows PC.");

        var fullPath = Path.GetFullPath(sourcePath);
        if (!Path.IsPathFullyQualified(fullPath) || fullPath.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ProcessingSourceLeaseUnavailableException("SOURCE_LEASE_PATH_UNSUPPORTED", "Protected source sessions require a local fully-qualified path.");
        var sourceInfo = new FileInfo(fullPath);
        if (!sourceInfo.Exists)
            throw new ProcessingSourceLeaseUnavailableException("SOURCE_LEASE_UNAVAILABLE", "The processing source is unavailable.");
        if (PathPolicy.IsReparsePoint(sourceInfo))
            throw new ProcessingSourceLeaseUnavailableException("SOURCE_LEASE_REPARSE_UNSUPPORTED", "Reparse-point processing sources are not supported.");

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
            throw new ProcessingSourceLeaseUnavailableException("SOURCE_LEASE_PATH_UNSUPPORTED", "The processing source volume could not be resolved.");
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new ProcessingSourceLeaseUnavailableException("SOURCE_LEASE_FILESYSTEM_UNSUPPORTED", "Protected source sessions are qualified only for a local fixed NTFS volume.");

        FileStream lease;
        string? createdStagingDirectory = null;
        Guid? createdSessionId = null;
        try
        {
            lease = new FileStream(fullPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.RandomAccess,
                BufferSize = 4096
            });
        }
        catch (IOException ex)
        {
            throw new ProcessingSourceLeaseUnavailableException("SOURCE_LEASE_CONFLICT", "The processing source is currently being changed or exclusively used.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ProcessingSourceLeaseUnavailableException("SOURCE_LEASE_ACCESS_DENIED", "The processing source lease could not be acquired.", ex);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceInfo.Refresh();
            if (!sourceInfo.Exists || PathPolicy.IsReparsePoint(sourceInfo))
                throw new ProcessingSourceLeaseUnavailableException("SOURCE_LEASE_SOURCE_CHANGED", "The processing source changed during lease acquisition.");
            var identity = WindowsFileIdentityReader.TryGet(fullPath);
            var sessionId = Guid.NewGuid();
            createdSessionId = sessionId;
            var stagingRoot = Path.GetFullPath(Path.Combine(appPaths.TempRoot, StagingDirectoryName));
            Directory.CreateDirectory(stagingRoot);
            EnsureSafeStagingRoot(appPaths.TempRoot, stagingRoot);
            var stagingDirectory = Path.GetFullPath(Path.Combine(stagingRoot, "session-" + sessionId.ToString("N")));
            if (!PathPolicy.IsWithinRoot(stagingRoot, stagingDirectory))
                throw new InvalidOperationException("The protected source staging directory escaped its app-owned root.");
            Directory.CreateDirectory(stagingDirectory);
            createdStagingDirectory = stagingDirectory;
            if (PathPolicy.IsReparsePoint(new DirectoryInfo(stagingDirectory)))
                throw new InvalidOperationException("The protected source staging directory is a reparse point.");
            var marker = Path.Combine(stagingDirectory, OwnerMarkerName);
            File.WriteAllText(marker, sessionId.ToString("D"), new UTF8Encoding(false));

            var session = new ProcessingSourceSession(
                sessionId,
                mediaFileId,
                expectedMediaContentId,
                fullPath,
                lease.Length,
                new DateTimeOffset(sourceInfo.LastWriteTimeUtc, TimeSpan.Zero),
                identity?.VolumeIdentity,
                identity?.FileIdentity,
                stagingRoot,
                stagingDirectory,
                lease,
                log);
            log.Write("Information", "ProtectedSourceSessionAcquired", new { session.SessionId, session.MediaFileId, session.ExpectedMediaContentId, Filesystem = "NTFS", Share = "Read" });
            return ValueTask.FromResult(session);
        }
        catch
        {
            lease.Dispose();
            if (createdStagingDirectory is not null && createdSessionId is Guid sessionId)
                TryDeleteOwnedStaging(Path.GetDirectoryName(createdStagingDirectory)!, createdStagingDirectory, sessionId);
            throw;
        }
    }

    public async Task<T> ExecuteSourceStageAsync<T>(
        string stageName,
        Func<ProcessingSourceAccess, CancellationToken, Task<T>> stage,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageName);
        ArgumentNullException.ThrowIfNull(stage);
        BeginStage(requireSourceStage: true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await stage(_access, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndStage();
        }
    }

    public async Task<T> ExecuteDerivedStageAsync<T>(
        string stageName,
        Func<CancellationToken, Task<T>> stage,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageName);
        ArgumentNullException.ThrowIfNull(stage);
        BeginStage(requireSourceStage: false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await stage(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndStage();
        }
    }

    public UnpublishedProcessingArtifact CreateUnpublishedArtifact(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        lock (_gate)
        {
            EnsureActiveLocked();
            if (PublicationResolution is not null)
                throw new InvalidOperationException("The publication boundary is already complete.");
            if (Path.IsPathRooted(relativePath))
                throw new ArgumentException("A staged artifact path must be relative.", nameof(relativePath));
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(StagingDirectory, normalized));
            if (!PathPolicy.IsWithinRoot(StagingDirectory, fullPath))
                throw new InvalidOperationException("The staged artifact path escaped its session directory.");
            var parent = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("The staged artifact parent could not be resolved.");
            Directory.CreateDirectory(parent);
            if (PathPolicy.HasReparsePointAncestor(StagingDirectory, fullPath))
                throw new InvalidOperationException("The staged artifact path crosses a reparse point.");
            var artifact = new UnpublishedProcessingArtifact(SessionId, normalized.Replace(Path.DirectorySeparatorChar, '/'), fullPath);
            _artifacts.Add(artifact);
            return artifact;
        }
    }

    public void CompleteSourceStaging()
    {
        lock (_gate)
        {
            EnsureNoActiveStageLocked();
            if (Phase != ProcessingSourceSessionPhase.Acquired)
                throw new InvalidOperationException("Source staging can be completed only once from the acquired phase.");
            Phase = ProcessingSourceSessionPhase.SourceStagesCompleted;
        }
    }

    public void EnterDerivedOrPublicationPhase()
    {
        lock (_gate)
        {
            EnsureNoActiveStageLocked();
            if (Phase != ProcessingSourceSessionPhase.SourceStagesCompleted)
                throw new InvalidOperationException("Derived/publication work requires completed source staging.");
            Phase = ProcessingSourceSessionPhase.DerivedOrPublication;
        }
    }

    /// <summary>
    /// B-2 records only the explicit future B-3 ownership boundary. It does not publish data.
    /// The lease remains held until the owner disposes the session after this call.
    /// </summary>
    public void CompletePublicationBoundary(ProcessingPublicationResolution resolution)
    {
        lock (_gate)
        {
            EnsureNoActiveStageLocked();
            if (PublicationResolution is not null)
                throw new InvalidOperationException("The publication boundary was already completed.");
            if (resolution == ProcessingPublicationResolution.Published &&
                Phase is not (ProcessingSourceSessionPhase.SourceStagesCompleted or ProcessingSourceSessionPhase.DerivedOrPublication))
                throw new InvalidOperationException("A source bundle cannot publish before source staging completes.");
            PublicationResolution = resolution;
            Phase = ProcessingSourceSessionPhase.PublicationBoundaryCompleted;
        }
    }

    public ValueTask DisposeAsync()
    {
        ProcessingSourceSessionPhase priorPhase;
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            if (_activeStages != 0)
                throw new InvalidOperationException("A protected source session cannot be disposed while a stage is active.");
            priorPhase = Phase;
            if (PublicationResolution is null) Phase = ProcessingSourceSessionPhase.Aborted;
            _disposed = true;
        }

        _lease.Dispose();
        CleanupSucceeded = TryDeleteOwnedStaging(StagingRoot, StagingDirectory, SessionId);
        lock (_gate) Phase = ProcessingSourceSessionPhase.Disposed;
        _log.Write(CleanupSucceeded ? "Information" : "Warning", "ProtectedSourceSessionDisposed", new { SessionId, MediaFileId, PriorPhase = priorPhase.ToString(), PublicationResolution = PublicationResolution?.ToString(), CleanupSucceeded });
        return ValueTask.CompletedTask;
    }

    internal void EnsureBorrowIsValid()
    {
        lock (_gate) EnsureActiveLocked();
    }

    private void BeginStage(bool requireSourceStage)
    {
        lock (_gate)
        {
            EnsureActiveLocked();
            if (requireSourceStage && Phase != ProcessingSourceSessionPhase.Acquired)
                throw new InvalidOperationException("Original-source stages must complete before leaving the acquired phase.");
            if (!requireSourceStage && Phase != ProcessingSourceSessionPhase.DerivedOrPublication)
                throw new InvalidOperationException("Derived work requires the explicit derived/publication phase.");
            if (PublicationResolution is not null)
                throw new InvalidOperationException("No stage may start after the publication boundary.");
            _activeStages++;
        }
    }

    private void EndStage()
    {
        lock (_gate) _activeStages--;
    }

    private void EnsureActiveLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed || Phase == ProcessingSourceSessionPhase.Disposed, this);
    }

    private void EnsureNoActiveStageLocked()
    {
        EnsureActiveLocked();
        if (_activeStages != 0)
            throw new InvalidOperationException("The source-session phase cannot change while a stage is active.");
    }

    private static void EnsureSafeStagingRoot(string tempRoot, string stagingRoot)
    {
        var fullTempRoot = Path.GetFullPath(tempRoot);
        if (!PathPolicy.IsWithinRoot(fullTempRoot, Path.Combine(stagingRoot, "containment-check")) ||
            PathPolicy.IsReparsePoint(new DirectoryInfo(stagingRoot)) ||
            PathPolicy.HasReparsePointAncestor(fullTempRoot, Path.Combine(stagingRoot, "containment-check")))
            throw new InvalidOperationException("The protected source staging root is not a contained app-owned directory.");
    }

    private static bool TryDeleteOwnedStaging(string stagingRoot, string stagingDirectory, Guid sessionId)
    {
        try
        {
            var root = Path.GetFullPath(stagingRoot);
            var directory = Path.GetFullPath(stagingDirectory);
            if (!PathPolicy.IsWithinRoot(root, directory)) return false;
            if (!string.Equals(Path.GetFileName(directory), "session-" + sessionId.ToString("N"), StringComparison.Ordinal)) return false;
            if (!Directory.Exists(directory) || PathPolicy.IsReparsePoint(new DirectoryInfo(directory))) return false;
            var marker = Path.Combine(directory, OwnerMarkerName);
            if (!File.Exists(marker) || PathPolicy.IsReparsePoint(new FileInfo(marker))) return false;
            if (!string.Equals(File.ReadAllText(marker, Encoding.UTF8), sessionId.ToString("D"), StringComparison.Ordinal)) return false;

            var directories = new List<string> { directory };
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(directory);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!PathPolicy.IsWithinRoot(root, current) || PathPolicy.IsReparsePoint(new DirectoryInfo(current))) return false;
                foreach (var entry in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!PathPolicy.IsWithinRoot(directory, entry)) return false;
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Add(entry);
                        pending.Push(entry);
                    }
                    else files.Add(entry);
                }
            }

            foreach (var file in files) File.Delete(file);
            foreach (var child in directories.OrderByDescending(item => item.Length)) Directory.Delete(child, recursive: false);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
