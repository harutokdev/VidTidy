namespace LocalMediaAI.Infrastructure;

/// <summary>
/// Provides a non-destructive availability check for a user-initiated media preview.
/// The service never opens a stream for writing and never changes Original Media.
/// </summary>
public interface IMediaPreviewService
{
    Task<MediaPreviewAvailability> CheckAvailabilityAsync(string path, CancellationToken cancellationToken);
}

public sealed record MediaPreviewAvailability(bool IsAvailable, string UserMessage)
{
    public static MediaPreviewAvailability Available { get; } = new(true, string.Empty);
}

public sealed class LocalMediaPreviewService : IMediaPreviewService
{
    public async Task<MediaPreviewAvailability> CheckAvailabilityAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new(false, "File unavailable. The result has no playable local media path.");

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Existence checking is intentionally read-only. MediaElement performs playback separately.
                if (!File.Exists(path))
                    return new MediaPreviewAvailability(false, "File unavailable. The cached matched frame can still be reviewed, but playback is not available.");
                return MediaPreviewAvailability.Available;
            }
            catch (UnauthorizedAccessException)
            {
                return new MediaPreviewAvailability(false, "File unavailable. Access to the local media is denied.");
            }
            catch (IOException)
            {
                return new MediaPreviewAvailability(false, "File unavailable. The local media cannot be accessed right now.");
            }
        }, cancellationToken).ConfigureAwait(false);
    }
}
