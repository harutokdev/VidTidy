namespace LocalMediaAI.Infrastructure;

/// <summary>
/// Type-specific irreversible filesystem boundary for Phase 6.5. It accepts one fully-qualified normal
/// file path only. It contains no directory, recursive, wildcard, batch, secure-erase, or Recycle Bin API.
/// SafeFileOperationService performs all authorization, StrongFullHash validation, confirmation, and
/// source-absence observation before publishing durable lifecycle state.
/// </summary>
public interface IWindowsPermanentDelete
{
    Task<WindowsPermanentDeleteResult> PermanentlyDeleteSingleFileAsync(string fullyQualifiedSourcePath, CancellationToken cancellationToken);
}

public sealed record WindowsPermanentDeleteResult(bool Succeeded, string? ErrorCode, string? ErrorMessage)
{
    public static WindowsPermanentDeleteResult Success() => new(true, null, null);
    public static WindowsPermanentDeleteResult Failure(string code, string message) => new(false, code, message);
}

public sealed class WindowsPermanentDeleteFileSystem : IWindowsPermanentDelete
{
    public Task<WindowsPermanentDeleteResult> PermanentlyDeleteSingleFileAsync(string fullyQualifiedSourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedSourcePath);
        if (!Path.IsPathFullyQualified(fullyQualifiedSourcePath))
            throw new ArgumentException("PermanentDelete source path must be fully qualified.", nameof(fullyQualifiedSourcePath));
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(fullyQualifiedSourcePath))
            return Task.FromResult(WindowsPermanentDeleteResult.Failure("PERMANENT_DELETE_DIRECTORY_REJECTED", "PermanentDelete accepts one normal file only; directory and recursive removal are prohibited."));
        if (!File.Exists(fullyQualifiedSourcePath))
            return Task.FromResult(WindowsPermanentDeleteResult.Failure("PERMANENT_DELETE_SOURCE_NOT_PRESENT", "PermanentDelete source is not present at the final call boundary."));
        try
        {
            File.Delete(fullyQualifiedSourcePath);
            return Task.FromResult(WindowsPermanentDeleteResult.Success());
        }
        catch (UnauthorizedAccessException ex)
        {
            return Task.FromResult(WindowsPermanentDeleteResult.Failure("PERMANENT_DELETE_ACCESS_DENIED", ex.Message));
        }
        catch (IOException ex)
        {
            return Task.FromResult(WindowsPermanentDeleteResult.Failure("PERMANENT_DELETE_IO_FAILURE", ex.Message));
        }
        catch (NotSupportedException ex)
        {
            return Task.FromResult(WindowsPermanentDeleteResult.Failure("PERMANENT_DELETE_PATH_UNSUPPORTED", ex.Message));
        }
    }
}
