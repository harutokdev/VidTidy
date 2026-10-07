using System.Globalization;
using System.Text.Json;
using LocalMediaAI.Core;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure;

public sealed partial class SqliteLibraryRepository : ILibraryRepository
{
    private const int SchemaVersion = 14;
    private readonly AppPaths _paths;
    private readonly string _connectionString;
    private readonly IStructuredLogger _log;
    internal Func<int, Task>? MigrationAppliedBeforeCommitForTests { get; set; }

    public SqliteLibraryRepository(AppPaths paths, IStructuredLogger log)
    {
        _paths = paths;
        paths.EnsureDirectories();
        _connectionString = new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
        _log = log;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;", cancellationToken);
        return connection;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_migration(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);", cancellationToken);
        var current = await ScalarLongAsync(connection, transaction, "SELECT COALESCE(MAX(version), 0) FROM schema_migration;", cancellationToken);
        if (current > SchemaVersion) throw new InvalidOperationException($"Database schema {current} is newer than supported schema {SchemaVersion}.");
        if (current == 0)
        {
            await ExecuteNonQueryAsync(connection, transaction, SchemaV1, cancellationToken);
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO schema_migration(version, applied_at) VALUES (1, $at);", cancellationToken, ("$at", Now()));
            _log.Write("Information", "DatabaseMigrated", new { From = 0, To = 1 });
            current = 1;
        }
        if (current == 1)
        {
            await ExecuteNonQueryAsync(connection, transaction, SchemaV2, cancellationToken);
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO schema_migration(version, applied_at) VALUES (2, $at);", cancellationToken, ("$at", Now()));
            _log.Write("Information", "DatabaseMigrated", new { From = 1, To = 2 });
            current = 2;
        }
        if (current == 2)
        {
            await ExecuteNonQueryAsync(connection, transaction, SchemaV3, cancellationToken);
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO schema_migration(version, applied_at) VALUES (3, $at);", cancellationToken, ("$at", Now()));
            _log.Write("Information", "DatabaseMigrated", new { From = 2, To = 3 });
            current = 3;
        }
        if (current == 3)
        {
            await ExecuteNonQueryAsync(connection, transaction, SchemaV4, cancellationToken);
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO schema_migration(version, applied_at) VALUES (4, $at);", cancellationToken, ("$at", Now()));
            _log.Write("Information", "DatabaseMigrated", new { From = 3, To = 4 });
            current = 4;
        }
        if (current == 4)
        {
            await ExecuteNonQueryAsync(connection, transaction, SchemaV5, cancellationToken);
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO schema_migration(version, applied_at) VALUES (5, $at);", cancellationToken, ("$at", Now()));
            _log.Write("Information", "DatabaseMigrated", new { From = 4, To = 5 });
            current = 5;
        }
        if (current == 5)
        {
            await ExecuteNonQueryAsync(connection, transaction, SchemaV6, cancellationToken);
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO schema_migration(version, applied_at) VALUES (6, $at);", cancellationToken, ("$at", Now()));
            _log.Write("Information", "DatabaseMigrated", new { From = 5, To = 6 });
            current = 6;
        }
        if (current == 6)
        {
            await ExecuteNonQueryAsync(connection, transaction, SchemaV7, cancellationToken);
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO schema_migration(version, applied_at) VALUES (7, $at);", cancellationToken, ("$at", Now()));
            _log.Write("Information", "DatabaseMigrated", new { From = 6, To = 7 });
            current = 7;
        }
        if (current == 7)
        {
            await ExecuteNonQueryAsync(connection, transaction, SchemaV8, cancellationToken);
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO schema_migration(version, applied_at) VALUES (8, $at);", cancellationToken, ("$at", Now()));
            _log.Write("Information", "DatabaseMigrated", new { From = 7, To = 8 });
            current = 8;
        }
        if (current == 8)
        {
            await ExecuteNonQueryAsync(connection, transaction, SchemaV9, cancellationToken);
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO schema_migration(version, applied_at) VALUES (9, $at);", cancellationToken, ("$at", Now()));
            _log.Write("Information", "DatabaseMigrated", new { From = 8, To = 9 });
            current = 9;
        }
        if (current == 9)
        {
            await ExecuteNonQueryAsync(connection, transaction, SchemaV10, cancellationToken);
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO schema_migration(version, applied_at) VALUES (10, $at);", cancellationToken, ("$at", Now()));
            _log.Write("Information", "DatabaseMigrated", new { From = 9, To = 10 });
            current = 10;
        }
        if (current == 10)
        {
            await ExecuteNonQueryAsync(connection, transaction, SchemaV11, cancellationToken);
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO schema_migration(version, applied_at) VALUES (11, $at);", cancellationToken, ("$at", Now()));
            _log.Write("Information", "DatabaseMigrated", new { From = 10, To = 11 });
            current = 11;
        }
        if (current == 11)
        {
            await ExecuteNonQueryAsync(connection, transaction, SchemaV12, cancellationToken);
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO schema_migration(version, applied_at) VALUES (12, $at);", cancellationToken, ("$at", Now()));
            _log.Write("Information", "DatabaseMigrated", new { From = 11, To = 12 });
            current = 12;
        }
        if (current == 12)
        {
            await ExecuteNonQueryAsync(connection, transaction, SchemaV13, cancellationToken);
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO schema_migration(version, applied_at) VALUES (13, $at);", cancellationToken, ("$at", Now()));
            _log.Write("Information", "DatabaseMigrated", new { From = 12, To = 13 });
            current = 13;
        }
        if (current == 13)
        {
            await ExecuteNonQueryAsync(connection, transaction, SchemaV14, cancellationToken);
            if (MigrationAppliedBeforeCommitForTests is not null) await MigrationAppliedBeforeCommitForTests(14);
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO schema_migration(version, applied_at) VALUES (14, $at);", cancellationToken, ("$at", Now()));
            _log.Write("Information", "DatabaseMigrated", new { From = 13, To = 14 });
        }
        // Lane acquisition filters by job type and state on every pick. The index is outside the versioned schema,
        // so an older build can still open a catalog that has it.
        await ExecuteNonQueryAsync(connection, transaction, "CREATE INDEX IF NOT EXISTS ix_job_type_state_created ON job(job_type,state,created_at);", cancellationToken);
        await ExecuteNonQueryAsync(connection, transaction, NearDuplicatePreparationSchema, cancellationToken);
        await ExecuteNonQueryAsync(connection, transaction, NearDuplicateTimelineSchema, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<LibraryRoot> AddOrGetLibraryRootAsync(string configuredPath, CancellationToken cancellationToken)
    {
        _paths.TestLab?.EnsureMediaPathAllowed(configuredPath);
        var normalized = PathPolicy.NormalizeForIdentity(configuredPath);
        await using var connection = await OpenAsync(cancellationToken);
        var existing = await QuerySingleAsync(connection, null, "SELECT * FROM library_root WHERE normalized_path=$path AND registration_state='Active';", ReadRoot, cancellationToken, ("$path", normalized));
        if (existing is not null) return existing;
        if (Directory.Exists(configuredPath))
        {
            var identity = WindowsFileIdentityReader.TryGetDirectory(configuredPath);
            if (identity is not null)
            {
                var relocated = await QuerySingleAsync(connection, null, "SELECT * FROM library_root WHERE registration_state='Active' AND volume_identity=$volume AND root_identity=$identity LIMIT 1;", ReadRoot, cancellationToken, ("$volume", identity.VolumeIdentity), ("$identity", identity.FileIdentity));
                if (relocated is not null)
                {
                    await UpdateRootIdentityAsync(relocated.LibraryRootId, configuredPath, identity.VolumeIdentity, identity.FileIdentity, cancellationToken);
                    return (await GetLibraryRootAsync(relocated.LibraryRootId, cancellationToken))!;
                }
                var retained = await QuerySingleAsync(connection, null, "SELECT * FROM library_root WHERE registration_state='Unregistered' AND volume_identity=$volume AND root_identity=$identity ORDER BY unregistered_at DESC LIMIT 1;", ReadRoot, cancellationToken, ("$volume", identity.VolumeIdentity), ("$identity", identity.FileIdentity));
                if (retained is not null) return await ReregisterRootAsync(configuredPath, cancellationToken);
            }
        }
        var now = DateTimeOffset.UtcNow;
        var stable = Directory.Exists(configuredPath) ? WindowsFileIdentityReader.TryGetDirectory(configuredPath) : null;
        var kind = string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredPath)), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(Path.GetFullPath(configuredPath))!), StringComparison.OrdinalIgnoreCase) ? LibraryRootKind.Drive : LibraryRootKind.Folder;
        var root = new LibraryRoot(Guid.NewGuid(), configuredPath, normalized, Path.GetFileName(configuredPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), stable?.VolumeIdentity, RootAvailability.Unknown, now, null, RootRegistrationState.Active, kind, stable?.FileIdentity, null, 0);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO library_root(library_root_id,configured_path,normalized_path,display_name,volume_identity,availability,created_at,last_scan_at,registration_state,root_kind,root_identity,unregistered_at,registration_revision) VALUES($id,$configured,$normalized,$display,$volume,$availability,$created,NULL,'Active',$kind,$identity,NULL,0);", cancellationToken,
            ("$id", root.LibraryRootId.ToString("D")), ("$configured", root.ConfiguredPath), ("$normalized", root.NormalizedPath), ("$display", string.IsNullOrWhiteSpace(root.DisplayName) ? root.ConfiguredPath : root.DisplayName), ("$volume", (object?)root.VolumeIdentity ?? DBNull.Value), ("$availability", root.Availability.ToString()), ("$created", ToDb(root.CreatedAt)), ("$kind", root.RootKind.ToString()), ("$identity", (object?)root.RootIdentity ?? DBNull.Value));
        return root;
    }

    public async Task<LibraryRoot?> GetLibraryRootAsync(Guid rootId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, "SELECT * FROM library_root WHERE library_root_id=$id;", ReadRoot, cancellationToken, ("$id", rootId.ToString("D")));
    }

    public async Task<IReadOnlyList<LibraryRoot>> GetLibraryRootsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, "SELECT * FROM library_root WHERE registration_state='Active' ORDER BY display_name COLLATE NOCASE;", ReadRoot, cancellationToken);
    }

    public async Task SetRootAvailabilityAsync(Guid rootId, RootAvailability availability, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "UPDATE library_root SET availability=$availability WHERE library_root_id=$id;", cancellationToken, ("$availability", availability.ToString()), ("$id", rootId.ToString("D")));
    }

    public async Task<Guid> StartScanAsync(Guid rootId, CancellationToken cancellationToken)
    {
        var scanId = Guid.NewGuid();
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO library_scan(scan_id,library_root_id,state,started_at,completed_at,files_seen,files_added,files_updated,files_unavailable,error_count,registration_revision) SELECT $id,library_root_id,'Running',$started,NULL,0,0,0,0,0,registration_revision FROM library_root WHERE library_root_id=$root AND registration_state='Active';", cancellationToken, ("$id", scanId.ToString("D")), ("$root", rootId.ToString("D")), ("$started", Now()));
        return scanId;
    }

    public async Task CompleteScanAsync(Guid scanId, ScanCompletion completion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE library_scan SET state=$state,completed_at=$completed,files_seen=$seen,files_added=$added,files_updated=$updated,files_unavailable=$unavailable,error_count=$errors WHERE scan_id=$id;", cancellationToken,
            ("$state", completion.State), ("$completed", ToDb(completion.CompletedAt)), ("$seen", completion.FilesSeen), ("$added", completion.FilesAdded), ("$updated", completion.FilesUpdated), ("$unavailable", completion.FilesUnavailable), ("$errors", completion.ErrorCount), ("$id", scanId.ToString("D")));
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE library_root SET last_scan_at=$at,availability='Online' WHERE registration_state='Active' AND library_root_id=(SELECT library_root_id FROM library_scan WHERE scan_id=$id) AND registration_revision=(SELECT registration_revision FROM library_scan WHERE scan_id=$id);", cancellationToken, ("$at", ToDb(completion.CompletedAt)), ("$id", scanId.ToString("D")));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ReconcileResult> UpsertScannedFileAsync(ScannedMediaFile file, Guid scanId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var result = await ReconcileScannedFileInternalAsync(connection, transaction, file, scanId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<ReconcileResult>> ReconcileScannedFilesAsync(IReadOnlyList<ScannedMediaFile> files, Guid scanId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) return Array.Empty<ReconcileResult>();
        var rootId = files[0].LibraryRootId;
        if (files.Any(file => file.LibraryRootId != rootId)) throw new ArgumentException("A scan reconciliation batch must contain exactly one library root.", nameof(files));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var results = new List<ReconcileResult>(files.Count);
        foreach (var file in files)
            results.Add(await ReconcileScannedFileInternalAsync(connection, transaction, file, scanId, cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    private static async Task<ReconcileResult> ReconcileScannedFileInternalAsync(SqliteConnection connection, SqliteTransaction transaction, ScannedMediaFile file, Guid scanId, CancellationToken cancellationToken)
    {
        if (await ScalarLongAsync(connection, transaction, "SELECT COUNT(1) FROM library_scan s JOIN library_root r ON r.library_root_id=s.library_root_id AND r.registration_state='Active' AND r.registration_revision=s.registration_revision WHERE s.scan_id=$scan AND s.library_root_id=$root AND s.state='Running';", cancellationToken, ("$scan", scanId.ToString("D")), ("$root", file.LibraryRootId.ToString("D"))) != 1)
            throw new InvalidOperationException("The scan no longer belongs to the active root registration.");
        MediaFile? existing = null;
        if (!string.IsNullOrWhiteSpace(file.VolumeIdentity) && !string.IsNullOrWhiteSpace(file.FileIdentity))
            existing = await QuerySingleAsync(connection, transaction, "SELECT * FROM media_file WHERE volume_identity=$volume AND file_identity=$fileId AND cleanup_committed_at IS NULL ORDER BY CASE WHEN presence_state='Present' THEN 1 ELSE 0 END,last_seen_at DESC LIMIT 1;", ReadMediaFile, cancellationToken, ("$volume", file.VolumeIdentity), ("$fileId", file.FileIdentity));
        var pathOccupant = await QuerySingleAsync(connection, transaction, "SELECT * FROM media_file WHERE library_root_id=$root AND normalized_path=$path AND cleanup_committed_at IS NULL LIMIT 1;", ReadMediaFile, cancellationToken, ("$root", file.LibraryRootId.ToString("D")), ("$path", file.NormalizedPath));
        if (existing is null && pathOccupant is not null)
        {
            var identitiesMatch = !string.IsNullOrWhiteSpace(file.VolumeIdentity) && !string.IsNullOrWhiteSpace(file.FileIdentity)
                && string.Equals(pathOccupant.VolumeIdentity, file.VolumeIdentity, StringComparison.Ordinal)
                && string.Equals(pathOccupant.FileIdentity, file.FileIdentity, StringComparison.Ordinal);
            if (identitiesMatch) existing = pathOccupant;
            else
                await ExecuteNonQueryAsync(connection, transaction, "UPDATE media_file SET normalized_path=$tombstone,presence_state='Missing' WHERE media_file_id=$id;", cancellationToken, ("$tombstone", pathOccupant.NormalizedPath + "#retained-" + pathOccupant.MediaFileId.ToString("N")), ("$id", pathOccupant.MediaFileId.ToString("D")));
        }
        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            var content = new MediaContent(Guid.NewGuid(), null, file.SizeBytes, "Provisional", now);
            var media = new MediaFile(Guid.NewGuid(), file.LibraryRootId, content.MediaContentId, file.FullPath, file.NormalizedPath, file.FileName, file.Extension, file.SizeBytes, file.CreationTimeUtc, file.LastWriteTimeUtc, file.VolumeIdentity, file.FileIdentity, file.QuickFingerprint, null, PresenceState.Present, now, now);
            await InsertContentAsync(connection, transaction, content, cancellationToken);
            await InsertMediaFileAsync(connection, transaction, media, scanId, cancellationToken);
            return new ReconcileResult(media, true, true);
        }
        // Full SHA-256 is the sole Exact proof. Quick Fingerprint is only a scan-time source-version sentinel:
        // if identical stat fields conceal changed sampled bytes, never silently retain old FullHash evidence.
        var changed = existing.SizeBytes != file.SizeBytes
            || existing.LastWriteTimeUtc != file.LastWriteTimeUtc
            || !string.Equals(existing.QuickFingerprint, file.QuickFingerprint, StringComparison.Ordinal);
        var contentId = existing.MediaContentId;
        if (changed)
        {
            contentId = Guid.NewGuid();
            await InsertContentAsync(connection, transaction, new MediaContent(contentId, null, file.SizeBytes, "Provisional", now), cancellationToken);
            // media_metadata is keyed by MediaFileId in schema v13. Remove the prior
            // content generation in the same reconciliation transaction so it cannot
            // be presented as metadata for the new MediaContentId.
            await ExecuteNonQueryAsync(connection, transaction, "DELETE FROM media_metadata WHERE media_file_id=$id;", cancellationToken, ("$id", existing.MediaFileId.ToString("D")));
        }
        var updated = existing with { LibraryRootId = file.LibraryRootId, MediaContentId = contentId, Path = file.FullPath, NormalizedPath = file.NormalizedPath, FileName = file.FileName, Extension = file.Extension, SizeBytes = file.SizeBytes, CreationTimeUtc = file.CreationTimeUtc, LastWriteTimeUtc = file.LastWriteTimeUtc, VolumeIdentity = file.VolumeIdentity, FileIdentity = file.FileIdentity, QuickFingerprint = file.QuickFingerprint, FullSha256 = changed ? null : existing.FullSha256, PresenceState = PresenceState.Present, LastSeenAt = now };
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE media_file SET library_root_id=$root,media_content_id=$content,path=$path,normalized_path=$normalized,file_name=$name,extension=$extension,size_bytes=$size,creation_time_utc=$created,last_write_time_utc=$modified,volume_identity=$volume,file_identity=$fileId,quick_fingerprint=$quick,full_sha256=$full,presence_state='Present',last_seen_at=$seen,last_scan_id=$scan WHERE media_file_id=$id; DELETE FROM retention_state WHERE target_type='File' AND target_id=$id AND state='Retained';", cancellationToken,
            ("$root", updated.LibraryRootId.ToString("D")), ("$content", updated.MediaContentId.ToString("D")), ("$path", updated.Path), ("$normalized", updated.NormalizedPath), ("$name", updated.FileName), ("$extension", updated.Extension), ("$size", updated.SizeBytes), ("$created", ToDb(updated.CreationTimeUtc)), ("$modified", ToDb(updated.LastWriteTimeUtc)), ("$volume", (object?)updated.VolumeIdentity ?? DBNull.Value), ("$fileId", (object?)updated.FileIdentity ?? DBNull.Value), ("$quick", (object?)updated.QuickFingerprint ?? DBNull.Value), ("$full", (object?)updated.FullSha256 ?? DBNull.Value), ("$seen", ToDb(updated.LastSeenAt)), ("$scan", scanId.ToString("D")), ("$id", updated.MediaFileId.ToString("D")));
        if (existing.PresenceState == PresenceState.Recycled)
        {
            var lifecycle = await QuerySingleAsync(connection, transaction, "SELECT * FROM media_recycle_lifecycle WHERE media_file_id=$media;", ReadMediaRecycleLifecycle, cancellationToken, ("$media", existing.MediaFileId.ToString("D")));
            if (lifecycle is not null)
            {
                await ExecuteNonQueryAsync(connection, transaction, "UPDATE media_recycle_lifecycle SET lifecycle_state='RestoredExternally',source_available=1,restored_at=$restored,restored_path=$path,restored_normalized_path=$normalized WHERE media_file_id=$media;", cancellationToken,
                    ("$restored", ToDb(now)), ("$path", file.FullPath), ("$normalized", file.NormalizedPath), ("$media", existing.MediaFileId.ToString("D")));
                await AddFileOperationAuditInternalAsync(connection, transaction, new FileOperationAuditEntry(Guid.NewGuid(), lifecycle.RecycleOperationId, now, "ExternalRestoreReconciled", JsonSerializer.Serialize(new { existing.MediaFileId, PriorMediaContentId = lifecycle.MediaContentId, CurrentMediaContentId = contentId, file.FullPath, file.VolumeIdentity, file.FileIdentity, SourceVersionChanged = changed })), cancellationToken);
            }
        }
        else if (existing.PresenceState == PresenceState.PermanentlyDeleted)
        {
            var lifecycle = await QuerySingleAsync(connection, transaction, "SELECT * FROM media_permanent_delete_lifecycle WHERE media_file_id=$media;", ReadMediaPermanentDeleteLifecycle, cancellationToken, ("$media", existing.MediaFileId.ToString("D")));
            if (lifecycle is not null)
                await AddFileOperationAuditInternalAsync(connection, transaction, new FileOperationAuditEntry(Guid.NewGuid(), lifecycle.PermanentDeleteOperationId, now, "ExternalReappearanceObserved", JsonSerializer.Serialize(new { existing.MediaFileId, PriorMediaContentId = lifecycle.MediaContentId, CurrentMediaContentId = contentId, file.FullPath, file.VolumeIdentity, file.FileIdentity, SourceVersionChanged = changed, AutomaticallyClassifiedAsRestore = false })), cancellationToken);
        }
        return new ReconcileResult(updated, false, changed);
    }

    public async Task MarkAbsentFilesAfterCompletedScanAsync(Guid rootId, Guid scanId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var completedAt = await ScalarStringAsync(connection, transaction, "SELECT s.completed_at FROM library_scan s JOIN library_root r ON r.library_root_id=s.library_root_id AND r.registration_state='Active' AND r.registration_revision=s.registration_revision WHERE s.scan_id=$scan AND s.state='Completed' AND s.completed_at IS NOT NULL;", cancellationToken, ("$scan", scanId.ToString("D")));
        if (completedAt is null) { await transaction.CommitAsync(cancellationToken); return; }
        var ids = await QueryAsync(connection, transaction, "SELECT media_file_id FROM media_file WHERE library_root_id=$root AND (last_scan_id IS NULL OR last_scan_id<>$scan) AND presence_state='Present' AND cleanup_committed_at IS NULL;", r => r.GetString(0), cancellationToken, ("$root", rootId.ToString("D")), ("$scan", scanId.ToString("D")));
        foreach (var id in ids)
        {
            await ExecuteNonQueryAsync(connection, transaction, "UPDATE media_file SET presence_state='Missing' WHERE media_file_id=$id;", cancellationToken, ("$id", id));
            await SetRetentionAsync(connection, transaction, RetentionTargetType.File, id, RetentionCause.FileMissing, id, ParseTime(completedAt), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<MediaFile?> GetMediaFileAsync(Guid mediaFileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, "SELECT * FROM media_file WHERE media_file_id=$id;", ReadMediaFile, cancellationToken, ("$id", mediaFileId.ToString("D")));
    }

    public async Task<MediaContent?> GetMediaContentAsync(Guid mediaContentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, "SELECT * FROM media_content WHERE media_content_id=$id;", ReadContent, cancellationToken, ("$id", mediaContentId.ToString("D")));
    }

    public async Task<Analysis?> FindCompletedAnalysisAsync(Guid contentId, string analysisType, string processorName, string processorVersion, string configHash, string processingVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, "SELECT * FROM analysis WHERE media_content_id=$content AND analysis_type=$type AND status='Completed' AND processor_name=$processor AND processor_version=$version AND config_hash=$hash AND processing_version=$processing ORDER BY completed_at DESC LIMIT 1;", ReadAnalysis, cancellationToken,
            ("$content", contentId.ToString("D")), ("$type", analysisType), ("$processor", processorName), ("$version", processorVersion), ("$hash", configHash), ("$processing", processingVersion));
    }

    public async Task<Analysis> StartAnalysisAsync(Guid contentId, string analysisType, string processorName, string processorVersion, string configJson, string configHash, string inputIdentityHash, string processingVersion, CancellationToken cancellationToken)
    {
        var analysis = new Analysis(Guid.NewGuid(), contentId, analysisType, AnalysisStatus.Running, processorName, processorVersion, null, null, configJson, configHash, inputIdentityHash, processingVersion, DateTimeOffset.UtcNow, null, null, null);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO analysis(analysis_id,media_content_id,analysis_type,status,processor_name,processor_version,model_name,model_version,config_json,config_hash,input_identity_hash,processing_version,started_at,completed_at,error_code,error_message) VALUES($id,$content,$type,'Running',$processor,$version,NULL,NULL,$config,$hash,$input,$processing,$started,NULL,NULL,NULL);", cancellationToken,
            ("$id", analysis.AnalysisId.ToString("D")), ("$content", contentId.ToString("D")), ("$type", analysisType), ("$processor", processorName), ("$version", processorVersion), ("$config", configJson), ("$hash", configHash), ("$input", inputIdentityHash), ("$processing", processingVersion), ("$started", ToDb(analysis.StartedAt!.Value)));
        return analysis;
    }

    public async Task CompleteAnalysisAsync(Guid analysisId, AnalysisStatus status, CancellationToken cancellationToken)
    {
        if (status is not (AnalysisStatus.Completed or AnalysisStatus.Partial or AnalysisStatus.Cancelled))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Only terminal successful analysis states may be completed.");
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "UPDATE analysis SET status=$status,completed_at=$at,error_code=NULL,error_message=NULL WHERE analysis_id=$id;", cancellationToken, ("$status", status.ToString()), ("$at", Now()), ("$id", analysisId.ToString("D")));
    }

    public async Task FailAnalysisAsync(Guid analysisId, string errorCode, string userMessage, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "UPDATE analysis SET status='Failed',completed_at=$at,error_code=$code,error_message=$message WHERE analysis_id=$id;", cancellationToken, ("$at", Now()), ("$code", errorCode), ("$message", userMessage), ("$id", analysisId.ToString("D")));
    }

    public async Task UpsertMetadataAsync(MediaMetadata metadata, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO media_metadata(media_file_id,container_format,duration_seconds,width,height,video_codec,fps_text,bitrate,audio_stream_count,subtitle_stream_count,raw_summary_json,updated_at) VALUES($id,$format,$duration,$width,$height,$codec,$fps,$bitrate,$audio,$subtitle,$raw,$updated) ON CONFLICT(media_file_id) DO UPDATE SET container_format=excluded.container_format,duration_seconds=excluded.duration_seconds,width=excluded.width,height=excluded.height,video_codec=excluded.video_codec,fps_text=excluded.fps_text,bitrate=excluded.bitrate,audio_stream_count=excluded.audio_stream_count,subtitle_stream_count=excluded.subtitle_stream_count,raw_summary_json=excluded.raw_summary_json,updated_at=excluded.updated_at;", cancellationToken,
            ("$id", metadata.MediaFileId.ToString("D")), ("$format", (object?)metadata.ContainerFormat ?? DBNull.Value), ("$duration", (object?)metadata.DurationSeconds ?? DBNull.Value), ("$width", (object?)metadata.Width ?? DBNull.Value), ("$height", (object?)metadata.Height ?? DBNull.Value), ("$codec", (object?)metadata.VideoCodec ?? DBNull.Value), ("$fps", (object?)metadata.FpsText ?? DBNull.Value), ("$bitrate", (object?)metadata.Bitrate ?? DBNull.Value), ("$audio", metadata.AudioStreamCount), ("$subtitle", metadata.SubtitleStreamCount), ("$raw", metadata.RawSummaryJson), ("$updated", ToDb(metadata.UpdatedAt)));
    }

    public async Task AddArtifactAsync(Artifact artifact, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO artifact(artifact_id,analysis_id,artifact_kind,relative_path,sha256,byte_size,mime_type,durability_class,created_at) VALUES($id,$analysis,$kind,$path,$hash,$bytes,$mime,$durability,$created);", cancellationToken,
            ("$id", artifact.ArtifactId.ToString("D")), ("$analysis", artifact.AnalysisId.ToString("D")), ("$kind", artifact.ArtifactKind), ("$path", artifact.RelativePath), ("$hash", artifact.Sha256), ("$bytes", artifact.ByteSize), ("$mime", artifact.MimeType), ("$durability", artifact.DurabilityClass.ToString()), ("$created", ToDb(artifact.CreatedAt)));
    }

    public async Task ReplaceVisualSamplesAsync(Guid mediaFileId, Guid mediaContentId, string inputIdentityHash, string samplingVersion, IReadOnlyList<VisualSample> samples, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputIdentityHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(samplingVersion);
        ArgumentNullException.ThrowIfNull(samples);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var sample in samples)
        {
            if (sample.MediaFileId != mediaFileId || sample.MediaContentId != mediaContentId || sample.InputIdentityHash != inputIdentityHash || sample.SamplingVersion != samplingVersion)
                throw new InvalidOperationException("Visual sample does not match the persistence scope.");
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO visual_sample(visual_sample_id,media_file_id,media_content_id,input_identity_hash,sample_index,requested_timestamp_seconds,presentation_timestamp_seconds,sampling_version,cache_key,frame_relative_path,frame_sha256,frame_byte_size,status,error_code,error_message,created_at,updated_at) VALUES($id,$file,$content,$input,$index,$requested,$presentation,$version,$cache,$path,$sha,$bytes,$status,$code,$message,$created,$updated) ON CONFLICT(cache_key) DO UPDATE SET media_file_id=excluded.media_file_id,media_content_id=excluded.media_content_id,frame_relative_path=excluded.frame_relative_path,frame_sha256=excluded.frame_sha256,frame_byte_size=excluded.frame_byte_size,status=excluded.status,error_code=excluded.error_code,error_message=excluded.error_message,updated_at=excluded.updated_at;", cancellationToken,
                ("$id", sample.VisualSampleId.ToString("D")), ("$file", sample.MediaFileId.ToString("D")), ("$content", sample.MediaContentId.ToString("D")), ("$input", sample.InputIdentityHash), ("$index", sample.SampleIndex), ("$requested", sample.RequestedTimestampSeconds), ("$presentation", sample.PresentationTimestampSeconds), ("$version", sample.SamplingVersion), ("$cache", sample.CacheKey), ("$path", (object?)sample.FrameRelativePath ?? DBNull.Value), ("$sha", (object?)sample.FrameSha256 ?? DBNull.Value), ("$bytes", (object?)sample.FrameByteSize ?? DBNull.Value), ("$status", sample.Status.ToString()), ("$code", (object?)sample.ErrorCode ?? DBNull.Value), ("$message", (object?)sample.ErrorMessage ?? DBNull.Value), ("$created", ToDb(sample.CreatedAt)), ("$updated", ToDb(sample.UpdatedAt)));
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<VisualSample>> GetVisualSamplesAsync(Guid mediaFileId, string inputIdentityHash, string? samplingVersion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputIdentityHash);
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, "SELECT * FROM visual_sample WHERE media_file_id=$id AND input_identity_hash=$input AND ($version IS NULL OR sampling_version=$version) ORDER BY sample_index;", ReadVisualSample, cancellationToken, ("$id", mediaFileId.ToString("D")), ("$input", inputIdentityHash), ("$version", (object?)samplingVersion ?? DBNull.Value));
    }

    public async Task<VisualSample?> GetVisualSampleAsync(Guid visualSampleId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, "SELECT vs.* FROM visual_sample vs JOIN media_file mf ON mf.media_file_id=vs.media_file_id AND mf.media_content_id=vs.media_content_id AND mf.presence_state='Present' WHERE vs.visual_sample_id=$id;", ReadVisualSample, cancellationToken, ("$id", visualSampleId.ToString("D")));
    }

    /// <summary>
    /// Reads the durable sample row without asserting that it still belongs to the
    /// current, present media version. Callers must classify currentness before use.
    /// This exists so stale/orphaned work can converge without weakening the guarded
    /// current-sample query above.
    /// </summary>
    public async Task<VisualSample?> GetVisualSampleRecordAsync(Guid visualSampleId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, "SELECT * FROM visual_sample WHERE visual_sample_id=$id;", ReadVisualSample, cancellationToken, ("$id", visualSampleId.ToString("D")));
    }

    public async Task UpsertModelProvenanceAsync(ModelProvenance provenance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO model_provenance(model_name,model_revision,processor_version,artifact_manifest_sha256,license,runtime_network_allowed,recorded_at) VALUES($name,$revision,$processor,$manifest,$license,$network,$recorded) ON CONFLICT(model_name,model_revision,processor_version) DO UPDATE SET artifact_manifest_sha256=excluded.artifact_manifest_sha256,license=excluded.license,runtime_network_allowed=excluded.runtime_network_allowed,recorded_at=excluded.recorded_at;", cancellationToken,
            ("$name", provenance.ModelName), ("$revision", provenance.ModelRevision), ("$processor", provenance.ProcessorVersion), ("$manifest", provenance.ArtifactManifestSha256), ("$license", provenance.License), ("$network", provenance.RuntimeNetworkAllowed ? 1 : 0), ("$recorded", ToDb(provenance.RecordedAt)));
    }

    public async Task<VisualEmbedding?> FindCompletedVisualEmbeddingAsync(string cacheKey, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, "SELECT * FROM visual_embedding WHERE cache_key=$cache AND status='Completed';", reader => ReadVisualEmbedding(reader), cancellationToken, ("$cache", cacheKey));
    }

    public async Task UpsertVisualEmbeddingAsync(VisualEmbedding embedding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "INSERT INTO visual_embedding(visual_embedding_id,visual_sample_id,input_identity_hash,cache_key,model_name,model_revision,processor_version,sampling_version,dtype,dimension,normalized,vector_float32,status,error_code,error_message,created_at,updated_at) VALUES($id,$sample,$input,$cache,$name,$revision,$processor,$sampling,$dtype,$dimension,$normalized,$vector,$status,$code,$message,$created,$updated) ON CONFLICT(cache_key) DO UPDATE SET visual_sample_id=excluded.visual_sample_id,vector_float32=excluded.vector_float32,status=excluded.status,error_code=excluded.error_code,error_message=excluded.error_message,updated_at=excluded.updated_at;", cancellationToken,
            ("$id", embedding.VisualEmbeddingId.ToString("D")), ("$sample", embedding.VisualSampleId.ToString("D")), ("$input", embedding.InputIdentityHash), ("$cache", embedding.CacheKey), ("$name", embedding.ModelName), ("$revision", embedding.ModelRevision), ("$processor", embedding.ProcessorVersion), ("$sampling", embedding.SamplingVersion), ("$dtype", embedding.Dtype), ("$dimension", embedding.Dimension), ("$normalized", embedding.Normalized ? 1 : 0), ("$vector", (object?)embedding.VectorFloat32 ?? DBNull.Value), ("$status", embedding.Status.ToString()), ("$code", (object?)embedding.ErrorCode ?? DBNull.Value), ("$message", (object?)embedding.ErrorMessage ?? DBNull.Value), ("$created", ToDb(embedding.CreatedAt)), ("$updated", ToDb(embedding.UpdatedAt)));
    }

    public async Task<IReadOnlyList<(VisualEmbedding Embedding, VisualSample Sample, MediaFile Media, MediaMetadata? Metadata)>> GetCompletedVisualEmbeddingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = "SELECT ve.visual_embedding_id ve_visual_embedding_id,ve.visual_sample_id ve_visual_sample_id,ve.input_identity_hash ve_input_identity_hash,ve.cache_key ve_cache_key,ve.model_name ve_model_name,ve.model_revision ve_model_revision,ve.processor_version ve_processor_version,ve.sampling_version ve_sampling_version,ve.dtype ve_dtype,ve.dimension ve_dimension,ve.normalized ve_normalized,ve.vector_float32 ve_vector_float32,ve.status ve_status,ve.error_code ve_error_code,ve.error_message ve_error_message,ve.created_at ve_created_at,ve.updated_at ve_updated_at,vs.*,mf.*,mm.media_file_id md_media_file_id,mm.container_format md_container_format,mm.duration_seconds md_duration_seconds,mm.width md_width,mm.height md_height,mm.video_codec md_video_codec,mm.fps_text md_fps_text,mm.bitrate md_bitrate,mm.audio_stream_count md_audio_stream_count,mm.subtitle_stream_count md_subtitle_stream_count,mm.raw_summary_json md_raw_summary_json,mm.updated_at md_updated_at FROM visual_embedding ve JOIN visual_sample vs ON vs.visual_sample_id=ve.visual_sample_id JOIN media_file mf ON mf.media_file_id=vs.media_file_id AND mf.media_content_id=vs.media_content_id LEFT JOIN media_metadata mm ON mm.media_file_id=mf.media_file_id WHERE ve.status='Completed' AND vs.status='Completed' AND mf.presence_state='Present' ORDER BY mf.file_name COLLATE NOCASE,vs.sample_index;";
        return await QueryAsync(connection, null, sql, reader =>
        {
            var media = ReadMediaFile(reader);
            return (ReadVisualEmbedding(reader, "ve_"), ReadVisualSample(reader), media, ReadJoinedMetadata(reader, media.MediaFileId));
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<SemanticSearchEmbeddingRow>> GetCurrentSemanticSearchRowsAsync(SemanticModelProfile model, SemanticExecutionProfile execution, Guid? libraryRootId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(execution);
        var where = new List<string>
        {
            "EXISTS(SELECT 1 FROM library_root scope_root WHERE scope_root.library_root_id=mf.library_root_id AND scope_root.registration_state='Active')", "mf.cleanup_committed_at IS NULL", "ve.status='Completed'",
            "vs.status='Completed'",
            "mf.presence_state='Present'",
            "mf.media_content_id=vs.media_content_id",
            "ve.vector_float32 IS NOT NULL",
            "ve.normalized=1",
            "ve.model_name=$model",
            "ve.model_revision=$revision",
            "ve.processor_version=$processor",
            "ve.dtype=$dtype",
            "ve.dimension=$dimension"
        };
        var parameters = new List<(string Name, object? Value)>
        {
            ("$model", model.ModelId),
            ("$revision", model.ModelRevision),
            ("$processor", model.ProcessorVersion),
            ("$dtype", execution.Dtype),
            ("$dimension", model.Dimension)
        };
        if (libraryRootId is { } rootId)
        {
            where.Add("mf.library_root_id=$root");
            parameters.Add(("$root", rootId.ToString("D")));
        }
        var sql = "SELECT ve.visual_embedding_id,vs.visual_sample_id,mf.media_file_id,mf.library_root_id,mf.file_name,mf.path,vs.presentation_timestamp_seconds,vs.frame_relative_path,mm.duration_seconds,ve.vector_float32 FROM visual_embedding ve JOIN visual_sample vs ON vs.visual_sample_id=ve.visual_sample_id JOIN media_file mf ON mf.media_file_id=vs.media_file_id LEFT JOIN media_metadata mm ON mm.media_file_id=mf.media_file_id WHERE " + string.Join(" AND ", where) + " ORDER BY mf.file_name COLLATE NOCASE,vs.sample_index;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, reader => new SemanticSearchEmbeddingRow(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetDouble(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetDouble(8),
            reader.GetFieldValue<byte[]>(9)), cancellationToken, parameters.ToArray());
    }

    public async Task<SemanticEmbeddingCompleteness> GetCurrentSemanticEmbeddingCompletenessAsync(
        SemanticModelProfile model,
        SemanticExecutionProfile execution,
        Guid? libraryRootId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(execution);
        const string sql = "SELECT COUNT(1) eligible_count, SUM(CASE WHEN EXISTS(SELECT 1 FROM visual_embedding ve WHERE ve.visual_sample_id=vs.visual_sample_id AND ve.status='Completed' AND ve.vector_float32 IS NOT NULL AND ve.normalized=1 AND ve.model_name=$model AND ve.model_revision=$revision AND ve.processor_version=$processor AND ve.dtype=$dtype AND ve.dimension=$dimension) THEN 1 ELSE 0 END) completed_count FROM visual_sample vs JOIN media_file mf ON mf.media_file_id=vs.media_file_id JOIN library_root lr ON lr.library_root_id=mf.library_root_id WHERE vs.status='Completed' AND vs.frame_relative_path IS NOT NULL AND mf.media_content_id=vs.media_content_id AND mf.presence_state='Present' AND mf.cleanup_committed_at IS NULL AND lr.registration_state='Active' AND ($root IS NULL OR mf.library_root_id=$root);";
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, sql,
            ("$model", model.ModelId),
            ("$revision", model.ModelRevision),
            ("$processor", model.ProcessorVersion),
            ("$dtype", execution.Dtype),
            ("$dimension", model.Dimension),
            ("$root", libraryRootId?.ToString("D") ?? (object)DBNull.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new(0, 0, 0);
        var eligible = checked((int)reader.GetInt64(reader.GetOrdinal("eligible_count")));
        var completedOrdinal = reader.GetOrdinal("completed_count");
        var completed = reader.IsDBNull(completedOrdinal) ? 0 : checked((int)reader.GetInt64(completedOrdinal));
        return new(eligible, completed, Math.Max(0, eligible - completed));
    }

    public async Task<IReadOnlyList<RootScanCurrentness>> GetCurrentRootScanCurrentnessAsync(CancellationToken cancellationToken)
    {
        const string sql = """
SELECT r.library_root_id,r.registration_revision,s.state,s.error_count
FROM library_root r
JOIN library_scan s ON s.scan_id=(
    SELECT candidate.scan_id
    FROM library_scan candidate
    WHERE candidate.library_root_id=r.library_root_id
      AND candidate.registration_revision=r.registration_revision
      AND candidate.completed_at IS NOT NULL
    ORDER BY candidate.completed_at DESC,candidate.started_at DESC,candidate.rowid DESC
    LIMIT 1)
WHERE r.registration_state='Active';
""";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, reader => new RootScanCurrentness(
            Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3)), cancellationToken);
    }

    internal async Task<IReadOnlyList<(VisualSample Sample, MediaFile Media)>> GetMissingVisualEmbeddingCandidatesAsync(
        SemanticModelProfile model,
        SemanticExecutionProfile execution,
        Guid? libraryRootId,
        Guid? mediaFileId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(execution);
        var where = new List<string>
        {
            "vs.status='Completed'",
            "vs.frame_relative_path IS NOT NULL",
            "mf.media_content_id=vs.media_content_id",
            "mf.presence_state='Present'",
            "mf.cleanup_committed_at IS NULL",
            "lr.registration_state='Active'",
            "NOT EXISTS(SELECT 1 FROM visual_embedding ve WHERE ve.visual_sample_id=vs.visual_sample_id AND ve.status='Completed' AND ve.vector_float32 IS NOT NULL AND ve.normalized=1 AND ve.model_name=$model AND ve.model_revision=$revision AND ve.processor_version=$processor AND ve.dtype=$dtype AND ve.dimension=$dimension)"
        };
        var parameters = new List<(string Name, object? Value)>
        {
            ("$model", model.ModelId),
            ("$revision", model.ModelRevision),
            ("$processor", model.ProcessorVersion),
            ("$dtype", execution.Dtype),
            ("$dimension", model.Dimension)
        };
        if (libraryRootId is { } rootId)
        {
            where.Add("mf.library_root_id=$root");
            parameters.Add(("$root", rootId.ToString("D")));
        }
        if (mediaFileId is { } fileId)
        {
            where.Add("mf.media_file_id=$media");
            parameters.Add(("$media", fileId.ToString("D")));
        }
        var sql = "SELECT vs.*,mf.* FROM visual_sample vs JOIN media_file mf ON mf.media_file_id=vs.media_file_id JOIN library_root lr ON lr.library_root_id=mf.library_root_id WHERE " + string.Join(" AND ", where) + " ORDER BY mf.media_file_id,vs.sample_index;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, reader => (ReadVisualSample(reader), ReadMediaFile(reader)), cancellationToken, parameters.ToArray());
    }

    public async Task<SemanticIndexStatus> GetSemanticIndexStatusAsync(Guid mediaFileId, SemanticModelProfile model, SemanticExecutionProfile execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(execution);
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = "SELECT EXISTS(SELECT 1 FROM visual_embedding ve JOIN visual_sample vs ON vs.visual_sample_id=ve.visual_sample_id WHERE vs.media_file_id=$media AND ve.model_name=$model AND ve.model_revision=$revision AND ve.processor_version=$processor AND ve.dtype=$dtype AND ve.dimension=$dimension AND ve.status='Completed') completed_embedding,EXISTS(SELECT 1 FROM visual_sample vs WHERE vs.media_file_id=$media AND vs.status='Failed') failed_sample,EXISTS(SELECT 1 FROM visual_embedding ve JOIN visual_sample vs ON vs.visual_sample_id=ve.visual_sample_id WHERE vs.media_file_id=$media AND ve.model_name=$model AND ve.model_revision=$revision AND ve.processor_version=$processor AND ve.dtype=$dtype AND ve.dimension=$dimension AND ve.status='Failed') failed_embedding,(SELECT j.state FROM job j WHERE (j.job_type='VisualSampling' AND j.payload_json LIKE '%' || $media || '%') OR (j.job_type='VisualEmbedding' AND j.payload_json LIKE '%' || (SELECT vs.visual_sample_id FROM visual_sample vs WHERE vs.media_file_id=$media LIMIT 1) || '%') ORDER BY j.updated_at DESC LIMIT 1) latest_job_state;";
        await using var command = CreateCommand(connection, null, sql, ("$media", mediaFileId.ToString("D")), ("$model", model.ModelId), ("$revision", model.ModelRevision), ("$processor", model.ProcessorVersion), ("$dtype", execution.Dtype), ("$dimension", model.Dimension));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return SemanticIndexStatus.NotIndexed;
        var completed = reader.GetInt64(reader.GetOrdinal("completed_embedding")) != 0;
        var failed = reader.GetInt64(reader.GetOrdinal("failed_sample")) != 0 || reader.GetInt64(reader.GetOrdinal("failed_embedding")) != 0;
        var latestJob = GetNullableString(reader, "latest_job_state");
        if (string.Equals(latestJob, JobState.Running.ToString(), StringComparison.Ordinal)) return SemanticIndexStatus.Indexing;
        if (string.Equals(latestJob, JobState.Pending.ToString(), StringComparison.Ordinal) ||
            string.Equals(latestJob, JobState.RetryWaiting.ToString(), StringComparison.Ordinal) ||
            string.Equals(latestJob, JobState.Waiting.ToString(), StringComparison.Ordinal)) return SemanticIndexStatus.Queued;
        if (completed && failed) return SemanticIndexStatus.Partial;
        if (completed) return SemanticIndexStatus.Indexed;
        if (failed || string.Equals(latestJob, JobState.Failed.ToString(), StringComparison.Ordinal)) return SemanticIndexStatus.Failed;
        return SemanticIndexStatus.NotIndexed;
    }

    public async Task<PagedResult<MediaListItem>> QueryMediaAsync(Guid? rootId, string? search, int offset, int limit, CancellationToken cancellationToken)
    {
        offset = Math.Max(0, offset); limit = Math.Clamp(limit, 1, 500);
        var (filter, parameters) = BuildLibraryTextFilter(rootId, search);
        await using var connection = await OpenAsync(cancellationToken);
        var count = (int)await ScalarLongAsync(connection, null, "SELECT COUNT(1) FROM media_file mf LEFT JOIN media_metadata mm ON mm.media_file_id=mf.media_file_id LEFT JOIN media_recycle_lifecycle mrl ON mrl.media_file_id=mf.media_file_id LEFT JOIN media_permanent_delete_lifecycle mpdl ON mpdl.media_file_id=mf.media_file_id" + filter + ";", cancellationToken, parameters.ToArray());
        parameters.Add(("$limit", limit)); parameters.Add(("$offset", offset));
        var sql = "SELECT mf.media_file_id,mf.file_name,CASE WHEN mf.presence_state='Present' THEN mf.path ELSE COALESCE(mpdl.last_known_source_path,mrl.last_known_source_path,mf.path) END path,mf.size_bytes,mf.last_write_time_utc,mf.presence_state,mm.container_format,mm.duration_seconds,mm.width,mm.height,mm.video_codec,mm.fps_text,mm.bitrate,mm.audio_stream_count,mm.subtitle_stream_count,mm.raw_summary_json,mm.updated_at,(SELECT a.relative_path FROM artifact a JOIN analysis an ON an.analysis_id=a.analysis_id WHERE an.media_content_id=mf.media_content_id AND an.status='Completed' AND a.artifact_kind='Thumbnail' ORDER BY a.created_at DESC LIMIT 1) thumbnail_path,(SELECT j.state FROM job j WHERE j.payload_json LIKE '%' || mf.media_file_id || '%' ORDER BY j.updated_at DESC LIMIT 1) job_state,(SELECT an.error_code FROM analysis an WHERE an.media_content_id=mf.media_content_id AND an.status='Failed' ORDER BY an.completed_at DESC LIMIT 1) error_code FROM media_file mf LEFT JOIN media_metadata mm ON mm.media_file_id=mf.media_file_id LEFT JOIN media_recycle_lifecycle mrl ON mrl.media_file_id=mf.media_file_id LEFT JOIN media_permanent_delete_lifecycle mpdl ON mpdl.media_file_id=mf.media_file_id" + filter + " ORDER BY mf.file_name COLLATE NOCASE,mf.media_file_id LIMIT $limit OFFSET $offset;";
        var items = await QueryAsync(connection, null, sql, ReadMediaListItem, cancellationToken, parameters.ToArray());
        return new PagedResult<MediaListItem>(items, count, offset, limit);
    }

    public async Task<MediaListItem?> GetMediaListItemAsync(Guid mediaFileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = "SELECT mf.media_file_id,mf.file_name,CASE WHEN mf.presence_state='Present' THEN mf.path ELSE COALESCE(mpdl.last_known_source_path,mrl.last_known_source_path,mf.path) END path,mf.size_bytes,mf.last_write_time_utc,mf.presence_state,mm.container_format,mm.duration_seconds,mm.width,mm.height,mm.video_codec,mm.fps_text,mm.bitrate,mm.audio_stream_count,mm.subtitle_stream_count,mm.raw_summary_json,mm.updated_at,(SELECT a.relative_path FROM artifact a JOIN analysis an ON an.analysis_id=a.analysis_id WHERE an.media_content_id=mf.media_content_id AND a.artifact_kind='Thumbnail' ORDER BY a.created_at DESC LIMIT 1) thumbnail_path,NULL job_state,NULL error_code FROM media_file mf LEFT JOIN media_metadata mm ON mm.media_file_id=mf.media_file_id LEFT JOIN media_recycle_lifecycle mrl ON mrl.media_file_id=mf.media_file_id LEFT JOIN media_permanent_delete_lifecycle mpdl ON mpdl.media_file_id=mf.media_file_id WHERE mf.media_file_id=$id;";
        return await QuerySingleAsync(connection, null, sql, ReadMediaListItem, cancellationToken, ("$id", mediaFileId.ToString("D")));
    }

    public async Task<IReadOnlyList<MediaFile>> GetExactDuplicateHashCandidatesAsync(Guid? rootId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT mf.* FROM media_file mf JOIN (SELECT size_bytes,quick_fingerprint FROM media_file WHERE presence_state='Present' AND quick_fingerprint IS NOT NULL GROUP BY size_bytes,quick_fingerprint HAVING COUNT(*) > 1) candidate ON candidate.size_bytes=mf.size_bytes AND candidate.quick_fingerprint=mf.quick_fingerprint WHERE mf.presence_state='Present' AND ($root IS NULL OR mf.library_root_id=$root) AND NOT EXISTS(SELECT 1 FROM file_hash_evidence fhe WHERE fhe.media_file_id=mf.media_file_id AND fhe.media_content_id=mf.media_content_id AND fhe.algorithm='SHA-256' AND fhe.processing_version='full-sha256-v1' AND fhe.status='Verified' AND fhe.source_size_bytes=mf.size_bytes AND fhe.source_last_write_time_utc=mf.last_write_time_utc) ORDER BY mf.size_bytes,mf.quick_fingerprint,mf.normalized_path;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, ReadMediaFile, cancellationToken, ("$root", rootId?.ToString("D") ?? (object)DBNull.Value));
    }

    public async Task<FileHashEvidence?> GetCurrentFullHashEvidenceAsync(Guid mediaFileId, Guid mediaContentId, string algorithm, string processingVersion, CancellationToken cancellationToken)
    {
        const string sql = "SELECT fhe.* FROM file_hash_evidence fhe JOIN media_file mf ON mf.media_file_id=fhe.media_file_id WHERE fhe.media_file_id=$file AND fhe.media_content_id=$content AND fhe.algorithm=$algorithm AND fhe.processing_version=$version AND fhe.status='Verified' AND mf.media_content_id=fhe.media_content_id AND mf.size_bytes=fhe.source_size_bytes AND mf.last_write_time_utc=fhe.source_last_write_time_utc LIMIT 1;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QuerySingleAsync(connection, null, sql, ReadFileHashEvidence, cancellationToken, ("$file", mediaFileId.ToString("D")), ("$content", mediaContentId.ToString("D")), ("$algorithm", algorithm), ("$version", processingVersion));
    }

    public async Task<bool> RecordFullHashEvidenceAsync(FileHashEvidence evidence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!string.Equals(evidence.Algorithm, "SHA-256", StringComparison.Ordinal) || !string.Equals(evidence.Status, "Verified", StringComparison.Ordinal))
            throw new ArgumentException("Phase 5.1 only records verified SHA-256 evidence.", nameof(evidence));
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM media_file WHERE media_file_id=$id;", ReadMediaFile, cancellationToken, ("$id", evidence.MediaFileId.ToString("D")));
        if (current is null || current.PresenceState != PresenceState.Present || current.MediaContentId != evidence.MediaContentId || current.SizeBytes != evidence.SourceSizeBytes || current.LastWriteTimeUtc != evidence.SourceLastWriteTimeUtc)
        {
            await transaction.CommitAsync(cancellationToken);
            _log.Write("Information", "FullHashEvidenceRejectedAsStale", new { evidence.MediaFileId, evidence.MediaContentId, evidence.Algorithm });
            return false;
        }
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO file_hash_evidence(file_hash_evidence_id,media_file_id,media_content_id,algorithm,digest,source_size_bytes,source_last_write_time_utc,processing_version,status,error_code,error_message,computed_at) VALUES($id,$file,$content,$algorithm,$digest,$size,$mtime,$version,'Verified',NULL,NULL,$computed) ON CONFLICT(media_file_id,media_content_id,algorithm,processing_version) DO UPDATE SET digest=excluded.digest,source_size_bytes=excluded.source_size_bytes,source_last_write_time_utc=excluded.source_last_write_time_utc,status='Verified',error_code=NULL,error_message=NULL,computed_at=excluded.computed_at;", cancellationToken,
            ("$id", evidence.FileHashEvidenceId.ToString("D")), ("$file", evidence.MediaFileId.ToString("D")), ("$content", evidence.MediaContentId.ToString("D")), ("$algorithm", evidence.Algorithm), ("$digest", evidence.Digest), ("$size", evidence.SourceSizeBytes), ("$mtime", ToDb(evidence.SourceLastWriteTimeUtc)), ("$version", evidence.ProcessingVersion), ("$computed", ToDb(evidence.ComputedAt)));
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE media_file SET full_sha256=$digest WHERE media_file_id=$file AND media_content_id=$content AND size_bytes=$size AND last_write_time_utc=$mtime;", cancellationToken, ("$digest", evidence.Digest), ("$file", evidence.MediaFileId.ToString("D")), ("$content", evidence.MediaContentId.ToString("D")), ("$size", evidence.SourceSizeBytes), ("$mtime", ToDb(evidence.SourceLastWriteTimeUtc)));
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE media_content SET canonical_sha256=$digest,identity_state='FullHashVerified' WHERE media_content_id=$content AND byte_length=$size;", cancellationToken, ("$digest", evidence.Digest), ("$content", evidence.MediaContentId.ToString("D")), ("$size", evidence.SourceSizeBytes));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task RebuildExactDuplicateGroupsAsync(CancellationToken cancellationToken)
    {
        const string verifiedSql = "SELECT fhe.* FROM file_hash_evidence fhe JOIN media_file mf ON mf.media_file_id=fhe.media_file_id WHERE fhe.algorithm='SHA-256' AND fhe.processing_version='full-sha256-v1' AND fhe.status='Verified' AND mf.media_content_id=fhe.media_content_id AND mf.size_bytes=fhe.source_size_bytes AND mf.last_write_time_utc=fhe.source_last_write_time_utc;";
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var verified = await QueryAsync(connection, transaction, verifiedSql, ReadFileHashEvidence, cancellationToken);
        foreach (var digestGroup in verified.GroupBy(item => new { item.Algorithm, item.Digest, item.SourceSizeBytes, item.ProcessingVersion }))
        {
            var members = digestGroup.ToArray();
            if (members.Length < 2) continue;
            var now = DateTimeOffset.UtcNow;
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO duplicate_group(duplicate_group_id,relation_type,evidence_algorithm,evidence_digest,byte_length,processing_version,verified_at) VALUES($id,'Exact',$algorithm,$digest,$bytes,$version,$verified) ON CONFLICT(relation_type,evidence_algorithm,evidence_digest) DO UPDATE SET byte_length=excluded.byte_length,processing_version=excluded.processing_version,verified_at=excluded.verified_at;", cancellationToken,
                ("$id", Guid.NewGuid().ToString("D")), ("$algorithm", digestGroup.Key.Algorithm), ("$digest", digestGroup.Key.Digest), ("$bytes", digestGroup.Key.SourceSizeBytes), ("$version", digestGroup.Key.ProcessingVersion), ("$verified", ToDb(now)));
            var group = await QuerySingleAsync(connection, transaction, "SELECT duplicate_group_id,relation_type,evidence_algorithm,evidence_digest,byte_length,processing_version,verified_at,0 current_member_count,NULL review_decision,NULL review_note FROM duplicate_group WHERE relation_type='Exact' AND evidence_algorithm=$algorithm AND evidence_digest=$digest;", ReadDuplicateGroup, cancellationToken, ("$algorithm", digestGroup.Key.Algorithm), ("$digest", digestGroup.Key.Digest)) ?? throw new InvalidOperationException("Duplicate group was not persisted.");
            foreach (var evidence in members)
            {
                await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO duplicate_member(duplicate_group_id,media_file_id,media_content_id,file_hash_evidence_id,added_at) VALUES($group,$file,$content,$evidence,$added) ON CONFLICT(duplicate_group_id,media_file_id,media_content_id) DO UPDATE SET file_hash_evidence_id=excluded.file_hash_evidence_id;", cancellationToken,
                    ("$group", group.DuplicateGroupId.ToString("D")), ("$file", evidence.MediaFileId.ToString("D")), ("$content", evidence.MediaContentId.ToString("D")), ("$evidence", evidence.FileHashEvidenceId.ToString("D")), ("$added", ToDb(now)));
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public Task<PagedResult<DuplicateGroup>> QueryDuplicateGroupsAsync(Guid? rootId, bool includeIgnored, int offset, int limit, CancellationToken cancellationToken) =>
        QueryDuplicateGroupsByRelationAsync(rootId, DuplicateRelationType.Exact, includeIgnored, offset, limit, cancellationToken);

    public async Task<PagedResult<DuplicateGroup>> QueryDuplicateGroupsByRelationAsync(Guid? rootId, DuplicateRelationType? relationType, bool includeIgnored, int offset, int limit, CancellationToken cancellationToken)
    {
        offset = Math.Max(0, offset); limit = Math.Clamp(limit, 1, 500);
        const string groupsSql = "WITH scope_member AS (SELECT duplicate_group_id,media_file_id FROM duplicate_member UNION ALL SELECT duplicate_group_id,media_file_id FROM near_duplicate_member), active_member AS (SELECT dm.duplicate_group_id,dm.media_file_id FROM duplicate_member dm JOIN media_file mf ON mf.media_file_id=dm.media_file_id AND mf.media_content_id=dm.media_content_id AND mf.presence_state<>'Missing' UNION ALL SELECT ndm.duplicate_group_id,ndm.media_file_id FROM near_duplicate_member ndm JOIN media_file mf ON mf.media_file_id=ndm.media_file_id AND mf.media_content_id=ndm.media_content_id AND mf.presence_state<>'Missing'), grouped AS (SELECT dg.duplicate_group_id,dg.relation_type,dg.evidence_algorithm,dg.evidence_digest,dg.byte_length,dg.processing_version,dg.verified_at,COUNT(DISTINCT am.media_file_id) current_member_count,(SELECT dj.decision FROM duplicate_judgment dj WHERE dj.duplicate_group_id=dg.duplicate_group_id AND dj.target_scope='Group' ORDER BY dj.updated_at DESC LIMIT 1) review_decision,(SELECT dj.note FROM duplicate_judgment dj WHERE dj.duplicate_group_id=dg.duplicate_group_id AND dj.target_scope='Group' ORDER BY dj.updated_at DESC LIMIT 1) review_note FROM duplicate_group dg JOIN active_member am ON am.duplicate_group_id=dg.duplicate_group_id WHERE ($relation IS NULL OR dg.relation_type=$relation) AND NOT EXISTS(SELECT 1 FROM scope_member sm LEFT JOIN media_file rm ON rm.media_file_id=sm.media_file_id LEFT JOIN library_root sr ON sr.library_root_id=rm.library_root_id WHERE sm.duplicate_group_id=dg.duplicate_group_id AND (sr.registration_state IS NULL OR sr.registration_state<>'Active' OR ($root IS NOT NULL AND rm.library_root_id<>$root))) GROUP BY dg.duplicate_group_id) SELECT * FROM grouped WHERE current_member_count >= 2 AND ($includeIgnored=1 OR COALESCE(review_decision,'Unreviewed') NOT IN ('Ignored','NotDuplicate')) ORDER BY CASE COALESCE(review_decision,'Unreviewed') WHEN 'Unreviewed' THEN 0 WHEN 'ConfirmedDuplicate' THEN 1 WHEN 'KeepBoth' THEN 2 WHEN 'Preferred' THEN 3 ELSE 4 END,verified_at DESC";
        await using var connection = await OpenAsync(cancellationToken);
        var count = (int)await ScalarLongAsync(connection, null, "SELECT COUNT(1) FROM (" + groupsSql + ");", cancellationToken, ("$root", rootId?.ToString("D") ?? (object)DBNull.Value), ("$relation", relationType?.ToString() ?? (object)DBNull.Value), ("$includeIgnored", includeIgnored ? 1 : 0));
        var sql = groupsSql + " LIMIT $limit OFFSET $offset;";
        var items = await QueryAsync(connection, null, sql, ReadDuplicateGroup, cancellationToken, ("$root", rootId?.ToString("D") ?? (object)DBNull.Value), ("$relation", relationType?.ToString() ?? (object)DBNull.Value), ("$includeIgnored", includeIgnored ? 1 : 0), ("$limit", limit), ("$offset", offset));
        return new PagedResult<DuplicateGroup>(items, count, offset, limit);
    }

    public async Task<IReadOnlyList<DuplicateComparisonMember>> GetDuplicateComparisonMembersAsync(Guid duplicateGroupId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT dm.duplicate_group_id dm_group_id,dm.media_file_id dm_file_id,dm.media_content_id dm_content_id,dm.file_hash_evidence_id dm_evidence_id,dm.added_at dm_added_at,mf.*,mm.media_file_id md_media_file_id,mm.container_format md_container_format,mm.duration_seconds md_duration_seconds,mm.width md_width,mm.height md_height,mm.video_codec md_video_codec,mm.fps_text md_fps_text,mm.bitrate md_bitrate,mm.audio_stream_count md_audio_stream_count,mm.subtitle_stream_count md_subtitle_stream_count,mm.raw_summary_json md_raw_summary_json,mm.updated_at md_updated_at,fhe.*, (SELECT dj.decision FROM duplicate_judgment dj WHERE dj.duplicate_group_id=dm.duplicate_group_id AND dj.target_scope='Member' AND dj.media_file_id=dm.media_file_id ORDER BY dj.updated_at DESC LIMIT 1) judgment_decision,(SELECT dj.note FROM duplicate_judgment dj WHERE dj.duplicate_group_id=dm.duplicate_group_id AND dj.target_scope='Member' AND dj.media_file_id=dm.media_file_id ORDER BY dj.updated_at DESC LIMIT 1) judgment_note FROM duplicate_member dm JOIN media_file mf ON mf.media_file_id=dm.media_file_id AND mf.media_content_id=dm.media_content_id JOIN file_hash_evidence fhe ON fhe.file_hash_evidence_id=dm.file_hash_evidence_id LEFT JOIN media_metadata mm ON mm.media_file_id=mf.media_file_id WHERE dm.duplicate_group_id=$group ORDER BY mf.file_name COLLATE NOCASE,mf.media_file_id;";
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, sql, reader =>
        {
            var membership = new DuplicateMember(Guid.Parse(reader.GetString(reader.GetOrdinal("dm_group_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("dm_file_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("dm_content_id"))), Guid.Parse(reader.GetString(reader.GetOrdinal("dm_evidence_id"))), ParseTime(reader.GetString(reader.GetOrdinal("dm_added_at"))));
            var media = ReadMediaFile(reader);
            var evidence = ReadFileHashEvidence(reader);
            var decision = GetNullableString(reader, "judgment_decision") is { } text ? Enum.Parse<DuplicateJudgmentDecision>(text) : DuplicateJudgmentDecision.Unreviewed;
            return new DuplicateComparisonMember(membership, media, ReadJoinedMetadata(reader, media.MediaFileId), evidence, decision, GetNullableString(reader, "judgment_note"));
        }, cancellationToken, ("$group", duplicateGroupId.ToString("D")));
    }

    public async Task<DuplicateJudgment> UpsertDuplicateJudgmentAsync(Guid duplicateGroupId, Guid? mediaFileId, DuplicateJudgmentDecision decision, string? note, CancellationToken cancellationToken)
    {
        var scope = mediaFileId is null ? "Group" : "Member";
        var targetKey = mediaFileId?.ToString("D") ?? "group";
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (mediaFileId is not null)
        {
            var memberCount = await ScalarLongAsync(connection, transaction, "SELECT COUNT(1) FROM (SELECT media_file_id FROM duplicate_member WHERE duplicate_group_id=$group AND media_file_id=$file UNION ALL SELECT media_file_id FROM near_duplicate_member WHERE duplicate_group_id=$group AND media_file_id=$file);", cancellationToken, ("$group", duplicateGroupId.ToString("D")), ("$file", mediaFileId.Value.ToString("D")));
            if (memberCount != 1) throw new InvalidOperationException("The selected media is not an active duplicate group member.");
            if (decision == DuplicateJudgmentDecision.Preferred)
                await ExecuteNonQueryAsync(connection, transaction, "UPDATE duplicate_judgment SET decision='Unreviewed',updated_at=$at WHERE duplicate_group_id=$group AND target_scope='Member' AND decision='Preferred' AND media_file_id<>$file;", cancellationToken, ("$at", Now()), ("$group", duplicateGroupId.ToString("D")), ("$file", mediaFileId.Value.ToString("D")));
        }
        var now = DateTimeOffset.UtcNow;
        var existing = await QuerySingleAsync(connection, transaction, "SELECT * FROM duplicate_judgment WHERE duplicate_group_id=$group AND target_scope=$scope AND target_key=$key;", ReadDuplicateJudgment, cancellationToken, ("$group", duplicateGroupId.ToString("D")), ("$scope", scope), ("$key", targetKey));
        var judgment = new DuplicateJudgment(existing?.DuplicateJudgmentId ?? Guid.NewGuid(), duplicateGroupId, mediaFileId, decision, "Human", string.IsNullOrWhiteSpace(note) ? null : note.Trim(), existing?.CreatedAt ?? now, now);
        await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO duplicate_judgment(duplicate_judgment_id,duplicate_group_id,media_file_id,target_scope,target_key,decision,origin,note,created_at,updated_at) VALUES($id,$group,$file,$scope,$key,$decision,'Human',$note,$created,$updated) ON CONFLICT(duplicate_group_id,target_scope,target_key) DO UPDATE SET decision=excluded.decision,origin='Human',note=excluded.note,updated_at=excluded.updated_at;", cancellationToken,
            ("$id", judgment.DuplicateJudgmentId.ToString("D")), ("$group", duplicateGroupId.ToString("D")), ("$file", mediaFileId?.ToString("D") ?? (object)DBNull.Value), ("$scope", scope), ("$key", targetKey), ("$decision", decision.ToString()), ("$note", (object?)judgment.Note ?? DBNull.Value), ("$created", ToDb(judgment.CreatedAt)), ("$updated", ToDb(now)));
        await transaction.CommitAsync(cancellationToken);
        return judgment;
    }

    public async Task<IReadOnlyList<DuplicateJudgment>> GetDuplicateJudgmentsAsync(Guid duplicateGroupId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, "SELECT * FROM duplicate_judgment WHERE duplicate_group_id=$group ORDER BY target_scope,updated_at DESC;", ReadDuplicateJudgment, cancellationToken, ("$group", duplicateGroupId.ToString("D")));
    }

    public async Task<bool> IntegrityCheckAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var result = await ScalarStringAsync(connection, null, "PRAGMA quick_check;", cancellationToken);
        return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
    }

    public async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
    }

    private static async Task InsertContentAsync(SqliteConnection c, SqliteTransaction tx, MediaContent content, CancellationToken cancellationToken) => await ExecuteNonQueryAsync(c, tx, "INSERT INTO media_content(media_content_id,canonical_sha256,byte_length,identity_state,created_at) VALUES($id,$sha,$bytes,$state,$created);", cancellationToken, ("$id", content.MediaContentId.ToString("D")), ("$sha", (object?)content.CanonicalSha256 ?? DBNull.Value), ("$bytes", content.ByteLength), ("$state", content.IdentityState), ("$created", ToDb(content.CreatedAt)));
    private static async Task InsertMediaFileAsync(SqliteConnection c, SqliteTransaction tx, MediaFile media, Guid scanId, CancellationToken cancellationToken) => await ExecuteNonQueryAsync(c, tx, "INSERT INTO media_file(media_file_id,library_root_id,media_content_id,path,normalized_path,file_name,extension,size_bytes,creation_time_utc,last_write_time_utc,volume_identity,file_identity,quick_fingerprint,full_sha256,presence_state,first_seen_at,last_seen_at,last_scan_id) VALUES($id,$root,$content,$path,$normalized,$name,$extension,$size,$created,$modified,$volume,$fileId,$quick,NULL,'Present',$first,$last,$scan);", cancellationToken, ("$id", media.MediaFileId.ToString("D")), ("$root", media.LibraryRootId.ToString("D")), ("$content", media.MediaContentId.ToString("D")), ("$path", media.Path), ("$normalized", media.NormalizedPath), ("$name", media.FileName), ("$extension", media.Extension), ("$size", media.SizeBytes), ("$created", ToDb(media.CreationTimeUtc)), ("$modified", ToDb(media.LastWriteTimeUtc)), ("$volume", (object?)media.VolumeIdentity ?? DBNull.Value), ("$fileId", (object?)media.FileIdentity ?? DBNull.Value), ("$quick", (object?)media.QuickFingerprint ?? DBNull.Value), ("$first", ToDb(media.FirstSeenAt)), ("$last", ToDb(media.LastSeenAt)), ("$scan", scanId.ToString("D")));

    private static LibraryRoot ReadRoot(SqliteDataReader r) => new(Guid.Parse(r.GetString(r.GetOrdinal("library_root_id"))), r.GetString(r.GetOrdinal("configured_path")), r.GetString(r.GetOrdinal("normalized_path")), r.GetString(r.GetOrdinal("display_name")), GetNullableString(r, "volume_identity"), Enum.Parse<RootAvailability>(r.GetString(r.GetOrdinal("availability"))), ParseTime(r.GetString(r.GetOrdinal("created_at"))), GetNullableTime(r, "last_scan_at"), Enum.Parse<RootRegistrationState>(r.GetString(r.GetOrdinal("registration_state"))), Enum.Parse<LibraryRootKind>(r.GetString(r.GetOrdinal("root_kind"))), GetNullableString(r, "root_identity"), GetNullableTime(r, "unregistered_at"), r.GetInt64(r.GetOrdinal("registration_revision")));
    private static MediaContent ReadContent(SqliteDataReader r) => new(Guid.Parse(r.GetString(r.GetOrdinal("media_content_id"))), GetNullableString(r, "canonical_sha256"), r.GetInt64(r.GetOrdinal("byte_length")), r.GetString(r.GetOrdinal("identity_state")), ParseTime(r.GetString(r.GetOrdinal("created_at"))));
    private static MediaFile ReadMediaFile(SqliteDataReader r) => new(Guid.Parse(r.GetString(r.GetOrdinal("media_file_id"))), Guid.Parse(r.GetString(r.GetOrdinal("library_root_id"))), Guid.Parse(r.GetString(r.GetOrdinal("media_content_id"))), r.GetString(r.GetOrdinal("path")), r.GetString(r.GetOrdinal("normalized_path")), r.GetString(r.GetOrdinal("file_name")), r.GetString(r.GetOrdinal("extension")), r.GetInt64(r.GetOrdinal("size_bytes")), ParseTime(r.GetString(r.GetOrdinal("creation_time_utc"))), ParseTime(r.GetString(r.GetOrdinal("last_write_time_utc"))), GetNullableString(r, "volume_identity"), GetNullableString(r, "file_identity"), GetNullableString(r, "quick_fingerprint"), GetNullableString(r, "full_sha256"), Enum.Parse<PresenceState>(r.GetString(r.GetOrdinal("presence_state"))), ParseTime(r.GetString(r.GetOrdinal("first_seen_at"))), ParseTime(r.GetString(r.GetOrdinal("last_seen_at"))));
    private static FileHashEvidence ReadFileHashEvidence(SqliteDataReader r) => new(Guid.Parse(r.GetString(r.GetOrdinal("file_hash_evidence_id"))), Guid.Parse(r.GetString(r.GetOrdinal("media_file_id"))), Guid.Parse(r.GetString(r.GetOrdinal("media_content_id"))), r.GetString(r.GetOrdinal("algorithm")), r.GetString(r.GetOrdinal("digest")), r.GetInt64(r.GetOrdinal("source_size_bytes")), ParseTime(r.GetString(r.GetOrdinal("source_last_write_time_utc"))), r.GetString(r.GetOrdinal("processing_version")), r.GetString(r.GetOrdinal("status")), GetNullableString(r, "error_code"), GetNullableString(r, "error_message"), ParseTime(r.GetString(r.GetOrdinal("computed_at"))));
    private static DuplicateGroup ReadDuplicateGroup(SqliteDataReader r) => new(Guid.Parse(r.GetString(r.GetOrdinal("duplicate_group_id"))), Enum.Parse<DuplicateRelationType>(r.GetString(r.GetOrdinal("relation_type"))), r.GetString(r.GetOrdinal("evidence_algorithm")), r.GetString(r.GetOrdinal("evidence_digest")), r.GetInt64(r.GetOrdinal("byte_length")), r.GetString(r.GetOrdinal("processing_version")), ParseTime(r.GetString(r.GetOrdinal("verified_at"))), GetIntOrDefault(r, "current_member_count"), GetNullableString(r, "review_decision") is { } decision ? Enum.Parse<DuplicateJudgmentDecision>(decision) : DuplicateJudgmentDecision.Unreviewed, GetNullableString(r, "review_note"));
    private static DuplicateJudgment ReadDuplicateJudgment(SqliteDataReader r) => new(Guid.Parse(r.GetString(r.GetOrdinal("duplicate_judgment_id"))), Guid.Parse(r.GetString(r.GetOrdinal("duplicate_group_id"))), GetNullableString(r, "media_file_id") is { } mediaId ? Guid.Parse(mediaId) : null, Enum.Parse<DuplicateJudgmentDecision>(r.GetString(r.GetOrdinal("decision"))), r.GetString(r.GetOrdinal("origin")), GetNullableString(r, "note"), ParseTime(r.GetString(r.GetOrdinal("created_at"))), ParseTime(r.GetString(r.GetOrdinal("updated_at"))));

    private static Analysis ReadAnalysis(SqliteDataReader r) => new(Guid.Parse(r.GetString(r.GetOrdinal("analysis_id"))), Guid.Parse(r.GetString(r.GetOrdinal("media_content_id"))), r.GetString(r.GetOrdinal("analysis_type")), Enum.Parse<AnalysisStatus>(r.GetString(r.GetOrdinal("status"))), r.GetString(r.GetOrdinal("processor_name")), r.GetString(r.GetOrdinal("processor_version")), GetNullableString(r, "model_name"), GetNullableString(r, "model_version"), r.GetString(r.GetOrdinal("config_json")), r.GetString(r.GetOrdinal("config_hash")), r.GetString(r.GetOrdinal("input_identity_hash")), r.GetString(r.GetOrdinal("processing_version")), GetNullableTime(r, "started_at"), GetNullableTime(r, "completed_at"), GetNullableString(r, "error_code"), GetNullableString(r, "error_message"));
    private static VisualSample ReadVisualSample(SqliteDataReader r) => new(Guid.Parse(r.GetString(r.GetOrdinal("visual_sample_id"))), Guid.Parse(r.GetString(r.GetOrdinal("media_file_id"))), Guid.Parse(r.GetString(r.GetOrdinal("media_content_id"))), r.GetString(r.GetOrdinal("input_identity_hash")), r.GetInt32(r.GetOrdinal("sample_index")), r.GetDouble(r.GetOrdinal("requested_timestamp_seconds")), r.GetDouble(r.GetOrdinal("presentation_timestamp_seconds")), r.GetString(r.GetOrdinal("sampling_version")), r.GetString(r.GetOrdinal("cache_key")), GetNullableString(r, "frame_relative_path"), GetNullableString(r, "frame_sha256"), GetNullableLong(r, "frame_byte_size"), Enum.Parse<VisualSampleStatus>(r.GetString(r.GetOrdinal("status"))), GetNullableString(r, "error_code"), GetNullableString(r, "error_message"), ParseTime(r.GetString(r.GetOrdinal("created_at"))), ParseTime(r.GetString(r.GetOrdinal("updated_at"))));
    private static VisualEmbedding ReadVisualEmbedding(SqliteDataReader r, string prefix = "") => new(Guid.Parse(r.GetString(r.GetOrdinal(prefix + "visual_embedding_id"))), Guid.Parse(r.GetString(r.GetOrdinal(prefix + "visual_sample_id"))), r.GetString(r.GetOrdinal(prefix + "input_identity_hash")), r.GetString(r.GetOrdinal(prefix + "cache_key")), r.GetString(r.GetOrdinal(prefix + "model_name")), r.GetString(r.GetOrdinal(prefix + "model_revision")), r.GetString(r.GetOrdinal(prefix + "processor_version")), r.GetString(r.GetOrdinal(prefix + "sampling_version")), r.GetString(r.GetOrdinal(prefix + "dtype")), r.GetInt32(r.GetOrdinal(prefix + "dimension")), r.GetInt64(r.GetOrdinal(prefix + "normalized")) != 0, r.IsDBNull(r.GetOrdinal(prefix + "vector_float32")) ? null : r.GetFieldValue<byte[]>(r.GetOrdinal(prefix + "vector_float32")), Enum.Parse<VisualEmbeddingStatus>(r.GetString(r.GetOrdinal(prefix + "status"))), GetNullableString(r, prefix + "error_code"), GetNullableString(r, prefix + "error_message"), ParseTime(r.GetString(r.GetOrdinal(prefix + "created_at"))), ParseTime(r.GetString(r.GetOrdinal(prefix + "updated_at"))));

    private static MediaListItem ReadMediaListItem(SqliteDataReader r)
    {
        MediaMetadata? metadata = r.IsDBNull(r.GetOrdinal("container_format")) && r.IsDBNull(r.GetOrdinal("raw_summary_json")) ? null : new MediaMetadata(Guid.Parse(r.GetString(r.GetOrdinal("media_file_id"))), GetNullableString(r, "container_format"), GetNullableDouble(r, "duration_seconds"), GetNullableInt(r, "width"), GetNullableInt(r, "height"), GetNullableString(r, "video_codec"), GetNullableString(r, "fps_text"), GetNullableLong(r, "bitrate"), GetIntOrDefault(r, "audio_stream_count"), GetIntOrDefault(r, "subtitle_stream_count"), GetNullableString(r, "raw_summary_json") ?? "{}", GetNullableTime(r, "updated_at") ?? DateTimeOffset.MinValue);
        return new MediaListItem(Guid.Parse(r.GetString(r.GetOrdinal("media_file_id"))), r.GetString(r.GetOrdinal("file_name")), r.GetString(r.GetOrdinal("path")), r.GetInt64(r.GetOrdinal("size_bytes")), ParseTime(r.GetString(r.GetOrdinal("last_write_time_utc"))), Enum.Parse<PresenceState>(r.GetString(r.GetOrdinal("presence_state"))), GetNullableString(r, "thumbnail_path"), metadata, GetNullableString(r, "job_state"), GetNullableString(r, "error_code"));
    }

    private static MediaMetadata? ReadJoinedMetadata(SqliteDataReader r, Guid mediaFileId)
    {
        if (r.IsDBNull(r.GetOrdinal("md_media_file_id"))) return null;
        return new MediaMetadata(mediaFileId, GetNullableString(r, "md_container_format"), GetNullableDouble(r, "md_duration_seconds"), GetNullableInt(r, "md_width"), GetNullableInt(r, "md_height"), GetNullableString(r, "md_video_codec"), GetNullableString(r, "md_fps_text"), GetNullableLong(r, "md_bitrate"), GetIntOrDefault(r, "md_audio_stream_count"), GetIntOrDefault(r, "md_subtitle_stream_count"), GetNullableString(r, "md_raw_summary_json") ?? "{}", GetNullableTime(r, "md_updated_at") ?? DateTimeOffset.MinValue);
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters) { await using var command = CreateCommand(connection, transaction, sql, parameters); await command.ExecuteNonQueryAsync(cancellationToken); }
    private static async Task<long> ScalarLongAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters) { await using var command = CreateCommand(connection, transaction, sql, parameters); var value = await command.ExecuteScalarAsync(cancellationToken); return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
    private static async Task<string?> ScalarStringAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters) { await using var command = CreateCommand(connection, transaction, sql, parameters); var value = await command.ExecuteScalarAsync(cancellationToken); return value?.ToString(); }
    private static async Task<T?> QuerySingleAsync<T>(SqliteConnection connection, SqliteTransaction? transaction, string sql, Func<SqliteDataReader, T> map, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters) where T : class { await using var command = CreateCommand(connection, transaction, sql, parameters); await using var reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? map(reader) : null; }
    private static async Task<IReadOnlyList<T>> QueryAsync<T>(SqliteConnection connection, SqliteTransaction? transaction, string sql, Func<SqliteDataReader, T> map, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters) { await using var command = CreateCommand(connection, transaction, sql, parameters); await using var reader = await command.ExecuteReaderAsync(cancellationToken); var values = new List<T>(); while (await reader.ReadAsync(cancellationToken)) values.Add(map(reader)); return values; }
    private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters) { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value); return command; }
    private static string Now() => ToDb(DateTimeOffset.UtcNow);
    private static string ToDb(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    private static DateTimeOffset? GetNullableTime(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : ParseTime(reader.GetString(reader.GetOrdinal(name)));
    private static string? GetNullableString(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));
    private static int? GetNullableInt(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt32(reader.GetOrdinal(name));
    private static long? GetNullableLong(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetInt64(reader.GetOrdinal(name));
    private static double? GetNullableDouble(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetDouble(reader.GetOrdinal(name));
    private static int GetIntOrDefault(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? 0 : reader.GetInt32(reader.GetOrdinal(name));
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private const string SchemaV7 = """
CREATE TABLE review_decision_history(review_decision_history_id TEXT PRIMARY KEY,review_item_id TEXT NOT NULL,review_type TEXT NOT NULL CHECK(review_type IN ('ExactDuplicate','NearDuplicate','ContentSimilar','SuggestedTag')),source_stable_id TEXT NOT NULL,evidence_id TEXT NOT NULL,event_kind TEXT NOT NULL CHECK(event_kind IN ('CurrentStateImported','DecisionChanged')),previous_decision TEXT NULL,current_decision TEXT NOT NULL,note TEXT NULL,origin TEXT NOT NULL CHECK(origin='Human'),occurred_at TEXT NOT NULL);
CREATE INDEX ix_review_decision_history_item_time ON review_decision_history(review_item_id,occurred_at DESC);
CREATE INDEX ix_review_decision_history_evidence_time ON review_decision_history(evidence_id,occurred_at DESC);
INSERT INTO review_decision_history(review_decision_history_id,review_item_id,review_type,source_stable_id,evidence_id,event_kind,previous_decision,current_decision,note,origin,occurred_at)
SELECT lower(hex(randomblob(16))),CASE dg.relation_type WHEN 'Exact' THEN 'exact:' || dg.duplicate_group_id ELSE 'near:' || dg.duplicate_group_id END,CASE dg.relation_type WHEN 'Exact' THEN 'ExactDuplicate' ELSE 'NearDuplicate' END,dg.duplicate_group_id || CASE WHEN dj.media_file_id IS NULL THEN '' ELSE ':' || dj.media_file_id END,dg.duplicate_group_id,'CurrentStateImported',NULL,dj.decision,dj.note,'Human',dj.updated_at FROM duplicate_judgment dj JOIN duplicate_group dg ON dg.duplicate_group_id=dj.duplicate_group_id;
INSERT INTO review_decision_history(review_decision_history_id,review_item_id,review_type,source_stable_id,evidence_id,event_kind,previous_decision,current_decision,note,origin,occurred_at)
SELECT lower(hex(randomblob(16))),'content:' || content_similarity_evidence_id,'ContentSimilar',content_similarity_evidence_id,content_similarity_evidence_id,'CurrentStateImported',NULL,decision,note,'Human',updated_at FROM content_similarity_judgment;
INSERT INTO review_decision_history(review_decision_history_id,review_item_id,review_type,source_stable_id,evidence_id,event_kind,previous_decision,current_decision,note,origin,occurred_at)
SELECT lower(hex(randomblob(16))),'tag:' || media_tag_evidence_id,'SuggestedTag',media_tag_evidence_id,media_tag_evidence_id,'CurrentStateImported',NULL,decision,note,'Human',updated_at FROM media_tag_judgment;
CREATE TRIGGER review_history_duplicate_insert AFTER INSERT ON duplicate_judgment BEGIN INSERT INTO review_decision_history(review_decision_history_id,review_item_id,review_type,source_stable_id,evidence_id,event_kind,previous_decision,current_decision,note,origin,occurred_at) SELECT lower(hex(randomblob(16))),CASE dg.relation_type WHEN 'Exact' THEN 'exact:' || NEW.duplicate_group_id ELSE 'near:' || NEW.duplicate_group_id END,CASE dg.relation_type WHEN 'Exact' THEN 'ExactDuplicate' ELSE 'NearDuplicate' END,NEW.duplicate_group_id,NEW.duplicate_group_id,'DecisionChanged',NULL,NEW.decision,NEW.note,'Human',NEW.updated_at FROM duplicate_group dg WHERE dg.duplicate_group_id=NEW.duplicate_group_id; END;
CREATE TRIGGER review_history_duplicate_update AFTER UPDATE OF decision,note ON duplicate_judgment BEGIN INSERT INTO review_decision_history(review_decision_history_id,review_item_id,review_type,source_stable_id,evidence_id,event_kind,previous_decision,current_decision,note,origin,occurred_at) SELECT lower(hex(randomblob(16))),CASE dg.relation_type WHEN 'Exact' THEN 'exact:' || NEW.duplicate_group_id ELSE 'near:' || NEW.duplicate_group_id END,CASE dg.relation_type WHEN 'Exact' THEN 'ExactDuplicate' ELSE 'NearDuplicate' END,NEW.duplicate_group_id,NEW.duplicate_group_id,'DecisionChanged',OLD.decision,NEW.decision,NEW.note,'Human',NEW.updated_at FROM duplicate_group dg WHERE dg.duplicate_group_id=NEW.duplicate_group_id; END;
CREATE TRIGGER review_history_content_insert AFTER INSERT ON content_similarity_judgment BEGIN INSERT INTO review_decision_history(review_decision_history_id,review_item_id,review_type,source_stable_id,evidence_id,event_kind,previous_decision,current_decision,note,origin,occurred_at) VALUES(lower(hex(randomblob(16))),'content:' || NEW.content_similarity_evidence_id,'ContentSimilar',NEW.content_similarity_evidence_id,NEW.content_similarity_evidence_id,'DecisionChanged',NULL,NEW.decision,NEW.note,'Human',NEW.updated_at); END;
CREATE TRIGGER review_history_content_update AFTER UPDATE OF decision,note ON content_similarity_judgment BEGIN INSERT INTO review_decision_history(review_decision_history_id,review_item_id,review_type,source_stable_id,evidence_id,event_kind,previous_decision,current_decision,note,origin,occurred_at) VALUES(lower(hex(randomblob(16))),'content:' || NEW.content_similarity_evidence_id,'ContentSimilar',NEW.content_similarity_evidence_id,NEW.content_similarity_evidence_id,'DecisionChanged',OLD.decision,NEW.decision,NEW.note,'Human',NEW.updated_at); END;
CREATE TRIGGER review_history_tag_insert AFTER INSERT ON media_tag_judgment BEGIN INSERT INTO review_decision_history(review_decision_history_id,review_item_id,review_type,source_stable_id,evidence_id,event_kind,previous_decision,current_decision,note,origin,occurred_at) VALUES(lower(hex(randomblob(16))),'tag:' || NEW.media_tag_evidence_id,'SuggestedTag',NEW.media_tag_evidence_id,NEW.media_tag_evidence_id,'DecisionChanged',NULL,NEW.decision,NEW.note,'Human',NEW.updated_at); END;
CREATE TRIGGER review_history_tag_update AFTER UPDATE OF decision,note ON media_tag_judgment BEGIN INSERT INTO review_decision_history(review_decision_history_id,review_item_id,review_type,source_stable_id,evidence_id,event_kind,previous_decision,current_decision,note,origin,occurred_at) VALUES(lower(hex(randomblob(16))),'tag:' || NEW.media_tag_evidence_id,'SuggestedTag',NEW.media_tag_evidence_id,NEW.media_tag_evidence_id,'DecisionChanged',OLD.decision,NEW.decision,NEW.note,'Human',NEW.updated_at); END;
""";

    private const string SchemaV13 = """
CREATE TABLE batch_operation(batch_operation_id TEXT PRIMARY KEY,operation_type TEXT NOT NULL CHECK(operation_type IN ('Move','Rename','Recycle')),selected_item_count INTEGER NOT NULL CHECK(selected_item_count BETWEEN 1 AND 25),total_source_bytes INTEGER NOT NULL CHECK(total_source_bytes >= 0),state TEXT NOT NULL CHECK(state IN ('Planned','DryRunCompleted','FinalConfirmationRequired','Executing','Completed','PartiallyCompleted','Cancelled','FailedBeforeExecution','RecoveryRequired')),cancellation_requested INTEGER NOT NULL DEFAULT 0 CHECK(cancellation_requested IN (0,1)),created_at TEXT NOT NULL,dry_run_completed_at TEXT NULL,final_execution_confirmed_at TEXT NULL,started_at TEXT NULL,cancellation_requested_at TEXT NULL,completed_at TEXT NULL,summary_code TEXT NULL,summary_message TEXT NULL,updated_at TEXT NOT NULL);
CREATE INDEX ix_batch_operation_state_time ON batch_operation(state,cancellation_requested,updated_at DESC);
CREATE TABLE batch_item(batch_item_id TEXT PRIMARY KEY,batch_operation_id TEXT NOT NULL REFERENCES batch_operation(batch_operation_id),sequence INTEGER NOT NULL CHECK(sequence >= 0),operation_id TEXT NOT NULL UNIQUE REFERENCES file_operation_plan(operation_id),media_file_id TEXT NOT NULL REFERENCES media_file(media_file_id),media_content_id TEXT NOT NULL REFERENCES media_content(media_content_id),source_path TEXT NOT NULL,destination_path TEXT NULL,plan_state TEXT NOT NULL,batch_item_state TEXT NOT NULL CHECK(batch_item_state IN ('Planned','Ready','Blocked','FinalConfirmed','Executing','Completed','Failed','Cancelled','RecoveryRequired','NotStarted')),revalidation_state TEXT NULL,lifecycle_result TEXT NULL,recovery_state TEXT NULL,error_code TEXT NULL,error_message TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(batch_operation_id,sequence),UNIQUE(batch_operation_id,media_file_id));
CREATE INDEX ix_batch_item_batch_state ON batch_item(batch_operation_id,batch_item_state,sequence);
CREATE TABLE batch_operation_audit(batch_operation_audit_id TEXT PRIMARY KEY,batch_operation_id TEXT NOT NULL REFERENCES batch_operation(batch_operation_id),batch_item_id TEXT NULL REFERENCES batch_item(batch_item_id),occurred_at TEXT NOT NULL,event_name TEXT NOT NULL,detail_json TEXT NULL);
CREATE INDEX ix_batch_operation_audit_batch_time ON batch_operation_audit(batch_operation_id,occurred_at DESC);
""";

    private const string SchemaV14 = """
ALTER TABLE library_root ADD COLUMN registration_state TEXT NOT NULL DEFAULT 'Active' CHECK(registration_state IN ('Active','Unregistered'));
ALTER TABLE library_root ADD COLUMN root_kind TEXT NOT NULL DEFAULT 'Folder' CHECK(root_kind IN ('Folder','Drive'));
ALTER TABLE library_root ADD COLUMN root_identity TEXT NULL;
ALTER TABLE library_root ADD COLUMN unregistered_at TEXT NULL;
ALTER TABLE library_root ADD COLUMN registration_revision INTEGER NOT NULL DEFAULT 0;
ALTER TABLE media_file ADD COLUMN cleanup_committed_at TEXT NULL;
ALTER TABLE library_scan ADD COLUMN registration_revision INTEGER NOT NULL DEFAULT 0;
CREATE TABLE library_directory(directory_id TEXT PRIMARY KEY,library_root_id TEXT NOT NULL REFERENCES library_root(library_root_id),path TEXT NOT NULL,normalized_path TEXT NOT NULL,volume_identity TEXT NULL,directory_identity TEXT NULL,last_scan_id TEXT NULL REFERENCES library_scan(scan_id),last_seen_at TEXT NOT NULL,UNIQUE(library_root_id,normalized_path));
CREATE INDEX ix_library_directory_identity ON library_directory(volume_identity,directory_identity);
CREATE TABLE retention_state(target_type TEXT NOT NULL CHECK(target_type IN ('File','Folder','Drive','Root')),target_id TEXT NOT NULL,cause TEXT NOT NULL CHECK(cause IN ('FileMissing','FolderMissing','DriveOffline','ExplicitUnregister')),cause_scope_id TEXT NOT NULL,absence_started_at TEXT NOT NULL,last_authoritative_at TEXT NOT NULL,state TEXT NOT NULL CHECK(state IN ('Retained','CleanupCommitted','Completed')),cleanup_committed_at TEXT NULL,completed_at TEXT NULL,PRIMARY KEY(target_type,target_id));
CREATE INDEX ix_retention_state_candidate ON retention_state(state,cause,absence_started_at);
CREATE TABLE retention_cleanup_item(cleanup_id TEXT NOT NULL,target_type TEXT NOT NULL,target_id TEXT NOT NULL,media_file_id TEXT NOT NULL,artifact_relative_path TEXT NULL,PRIMARY KEY(cleanup_id,media_file_id,artifact_relative_path));
INSERT INTO app_setting(key,value,updated_at) VALUES('retention.file.days','30',strftime('%Y-%m-%dT%H:%M:%fZ','now')) ON CONFLICT(key) DO NOTHING;
INSERT INTO app_setting(key,value,updated_at) VALUES('retention.folder.days','60',strftime('%Y-%m-%dT%H:%M:%fZ','now')) ON CONFLICT(key) DO NOTHING;
INSERT INTO app_setting(key,value,updated_at) VALUES('retention.drive.days','365',strftime('%Y-%m-%dT%H:%M:%fZ','now')) ON CONFLICT(key) DO NOTHING;
""";

    private const string SchemaV12 = """
ALTER TABLE file_operation_plan ADD COLUMN production_permanent_delete_capability INTEGER NOT NULL DEFAULT 0 CHECK(production_permanent_delete_capability IN (0,1));
ALTER TABLE file_operation_plan ADD COLUMN selected_media_count INTEGER NOT NULL DEFAULT 1 CHECK(selected_media_count >= 1);
CREATE INDEX ix_file_operation_plan_permanent_delete_state ON file_operation_plan(production_permanent_delete_capability,state,updated_at DESC);
CREATE TABLE media_permanent_delete_lifecycle(media_file_id TEXT PRIMARY KEY REFERENCES media_file(media_file_id),media_content_id TEXT NOT NULL REFERENCES media_content(media_content_id),permanent_delete_operation_id TEXT NOT NULL UNIQUE REFERENCES file_operation_plan(operation_id),lifecycle_state TEXT NOT NULL CHECK(lifecycle_state='PermanentlyDeleted'),last_known_source_path TEXT NOT NULL,last_known_source_normalized_path TEXT NOT NULL,last_known_full_sha256 TEXT NOT NULL,last_known_size_bytes INTEGER NOT NULL,last_known_volume_identity TEXT NULL,last_known_file_identity TEXT NULL,source_available INTEGER NOT NULL CHECK(source_available IN (0,1)),permanently_deleted_at TEXT NOT NULL);
CREATE INDEX ix_media_permanent_delete_lifecycle_state_time ON media_permanent_delete_lifecycle(lifecycle_state,source_available,permanently_deleted_at DESC);
""";

    private const string SchemaV11 = """
ALTER TABLE file_operation_plan ADD COLUMN production_recycle_capability INTEGER NOT NULL DEFAULT 0 CHECK(production_recycle_capability IN (0,1));
CREATE INDEX ix_file_operation_plan_recycle_state ON file_operation_plan(production_recycle_capability,state,updated_at DESC);
CREATE TABLE media_recycle_lifecycle(media_file_id TEXT PRIMARY KEY REFERENCES media_file(media_file_id),media_content_id TEXT NOT NULL REFERENCES media_content(media_content_id),recycle_operation_id TEXT NOT NULL UNIQUE REFERENCES file_operation_plan(operation_id),lifecycle_state TEXT NOT NULL CHECK(lifecycle_state IN ('Recycled','RestoredExternally')),last_known_source_path TEXT NOT NULL,last_known_source_normalized_path TEXT NOT NULL,last_known_full_sha256 TEXT NOT NULL,last_known_size_bytes INTEGER NOT NULL,last_known_volume_identity TEXT NULL,last_known_file_identity TEXT NULL,source_available INTEGER NOT NULL CHECK(source_available IN (0,1)),recycled_at TEXT NOT NULL,restored_at TEXT NULL,restored_path TEXT NULL,restored_normalized_path TEXT NULL);
CREATE INDEX ix_media_recycle_lifecycle_state_time ON media_recycle_lifecycle(lifecycle_state,source_available,recycled_at DESC);
""";

    private const string SchemaV10 = """
ALTER TABLE file_operation_plan ADD COLUMN destination_library_root_id TEXT NULL;
ALTER TABLE file_operation_plan ADD COLUMN destination_root_path TEXT NULL;
ALTER TABLE file_operation_plan ADD COLUMN destination_volume_identity TEXT NULL;
ALTER TABLE file_operation_plan ADD COLUMN temporary_destination_path TEXT NULL;
ALTER TABLE file_operation_plan ADD COLUMN temporary_destination_normalized_path TEXT NULL;
ALTER TABLE file_operation_plan ADD COLUMN destination_free_space_snapshot_bytes INTEGER NULL;
ALTER TABLE file_operation_plan ADD COLUMN production_cross_volume_move_capability INTEGER NOT NULL DEFAULT 0 CHECK(production_cross_volume_move_capability IN (0,1));
ALTER TABLE file_operation_plan ADD COLUMN cross_volume_move_stage TEXT NOT NULL DEFAULT 'None' CHECK(cross_volume_move_stage IN ('None','PreflightReady','Copying','TempCopied','TempHashVerified','FinalPublished','SourceRemovalPending','SourceRemoved','DbPublished','RecoveryRequired'));
ALTER TABLE file_operation_preflight ADD COLUMN cross_volume_verified INTEGER NOT NULL DEFAULT 0 CHECK(cross_volume_verified IN (0,1));
ALTER TABLE file_operation_preflight ADD COLUMN temporary_destination_available INTEGER NULL CHECK(temporary_destination_available IN (0,1));
ALTER TABLE file_operation_preflight ADD COLUMN required_destination_free_bytes INTEGER NULL;
CREATE INDEX ix_file_operation_plan_cross_volume_stage ON file_operation_plan(production_cross_volume_move_capability,cross_volume_move_stage,updated_at DESC);
""";

    private const string SchemaV9 = """
ALTER TABLE file_operation_plan ADD COLUMN final_execution_confirmed_at TEXT NULL;
ALTER TABLE file_operation_plan ADD COLUMN production_same_volume_capability INTEGER NOT NULL DEFAULT 0 CHECK(production_same_volume_capability IN (0,1));
ALTER TABLE file_operation_preflight ADD COLUMN same_volume_verified INTEGER NOT NULL DEFAULT 0 CHECK(same_volume_verified IN (0,1));
""";

    private const string SchemaV8 = """
CREATE TABLE file_operation_proposal(file_operation_proposal_id TEXT PRIMARY KEY,media_file_id TEXT NOT NULL REFERENCES media_file(media_file_id),media_content_id TEXT NOT NULL REFERENCES media_content(media_content_id),review_item_id TEXT NULL,review_context TEXT NULL,suggested_operation_type TEXT NOT NULL CHECK(suggested_operation_type IN ('Move','Rename','Recycle','PermanentDelete')),created_at TEXT NOT NULL);
CREATE INDEX ix_file_operation_proposal_media_time ON file_operation_proposal(media_file_id,created_at DESC);
CREATE TABLE file_operation_intent(file_operation_intent_id TEXT PRIMARY KEY,file_operation_proposal_id TEXT NOT NULL REFERENCES file_operation_proposal(file_operation_proposal_id),operation_type TEXT NOT NULL CHECK(operation_type IN ('Move','Rename','Recycle','PermanentDelete')),requested_destination_path TEXT NULL,overwrite_policy TEXT NOT NULL CHECK(overwrite_policy='Prohibit'),conflict_policy TEXT NOT NULL CHECK(conflict_policy IN ('Abort','Skip','RenameCandidate')),created_at TEXT NOT NULL,acknowledged_at TEXT NULL);
CREATE INDEX ix_file_operation_intent_proposal_time ON file_operation_intent(file_operation_proposal_id,created_at DESC);
CREATE TABLE file_operation_plan(operation_id TEXT PRIMARY KEY,file_operation_intent_id TEXT NOT NULL UNIQUE REFERENCES file_operation_intent(file_operation_intent_id),library_root_id TEXT NOT NULL REFERENCES library_root(library_root_id),media_file_id TEXT NOT NULL REFERENCES media_file(media_file_id),media_content_id TEXT NOT NULL REFERENCES media_content(media_content_id),operation_type TEXT NOT NULL CHECK(operation_type IN ('Move','Rename','Recycle','PermanentDelete')),operation_mode TEXT NOT NULL CHECK(operation_mode IN ('ProductionDryRun','DisposableFixtureExecution')),source_path TEXT NOT NULL,source_normalized_path TEXT NOT NULL,source_volume_identity TEXT NULL,source_file_identity TEXT NULL,source_size_bytes INTEGER NOT NULL,source_last_write_time_utc TEXT NOT NULL,source_quick_fingerprint TEXT NULL,source_full_sha256 TEXT NULL,validation_strength TEXT NOT NULL CHECK(validation_strength IN ('StatAndStableIdentity','StrongFullHash')),destination_path TEXT NULL,destination_normalized_path TEXT NULL,expected_filesystem_effect TEXT NOT NULL,overwrite_policy TEXT NOT NULL CHECK(overwrite_policy='Prohibit'),conflict_policy TEXT NOT NULL CHECK(conflict_policy IN ('Abort','Skip','RenameCandidate')),recoverability TEXT NOT NULL CHECK(recoverability IN ('Reversible','ConditionallyReversible','Irreversible')),user_acknowledged INTEGER NOT NULL CHECK(user_acknowledged IN (0,1)),created_at TEXT NOT NULL,confirmed_at TEXT NULL,state TEXT NOT NULL,last_error_code TEXT NULL,last_error_message TEXT NULL,updated_at TEXT NOT NULL);
CREATE INDEX ix_file_operation_plan_state_updated ON file_operation_plan(state,updated_at DESC);
CREATE INDEX ix_file_operation_plan_media_created ON file_operation_plan(media_file_id,created_at DESC);
CREATE TABLE file_operation_preflight(file_operation_preflight_id TEXT PRIMARY KEY,operation_id TEXT NOT NULL REFERENCES file_operation_plan(operation_id),state TEXT NOT NULL,code TEXT NOT NULL,message TEXT NOT NULL,source_exists INTEGER NOT NULL CHECK(source_exists IN (0,1)),stable_identity_matches INTEGER NOT NULL CHECK(stable_identity_matches IN (0,1)),size_matches INTEGER NOT NULL CHECK(size_matches IN (0,1)),mtime_matches INTEGER NOT NULL CHECK(mtime_matches IN (0,1)),quick_fingerprint_matches INTEGER NOT NULL CHECK(quick_fingerprint_matches IN (0,1)),full_hash_matches INTEGER NULL CHECK(full_hash_matches IN (0,1)),destination_available INTEGER NOT NULL CHECK(destination_available IN (0,1)),destination_conflict INTEGER NOT NULL CHECK(destination_conflict IN (0,1)),destination_free_bytes INTEGER NULL,validated_at TEXT NOT NULL);
CREATE INDEX ix_file_operation_preflight_operation_time ON file_operation_preflight(operation_id,validated_at DESC);
CREATE TABLE file_operation_execution(file_operation_execution_id TEXT PRIMARY KEY,operation_id TEXT NOT NULL REFERENCES file_operation_plan(operation_id),media_file_id TEXT NOT NULL REFERENCES media_file(media_file_id),source_path TEXT NOT NULL,destination_path TEXT NULL,result TEXT NOT NULL CHECK(result IN ('Pending','DryRun','Completed','Skipped','Failed','RecoveryRequired','Cancelled')),started_at TEXT NULL,completed_at TEXT NULL,error_code TEXT NULL,error_message TEXT NULL,before_identity_json TEXT NULL,after_identity_json TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
CREATE INDEX ix_file_operation_execution_operation_time ON file_operation_execution(operation_id,updated_at DESC);
CREATE TABLE file_operation_audit(file_operation_audit_id TEXT PRIMARY KEY,operation_id TEXT NOT NULL REFERENCES file_operation_plan(operation_id),occurred_at TEXT NOT NULL,event_name TEXT NOT NULL,detail_json TEXT NULL);
CREATE INDEX ix_file_operation_audit_operation_time ON file_operation_audit(operation_id,occurred_at DESC);
""";

    private const string SchemaV6 = """
CREATE TABLE tag_text_embedding(tag_text_embedding_id TEXT PRIMARY KEY,taxonomy_version TEXT NOT NULL,tag_id TEXT NOT NULL,display_name TEXT NOT NULL,group_name TEXT NOT NULL,text_representation_version TEXT NOT NULL,text_value TEXT NOT NULL,model_name TEXT NOT NULL,model_revision TEXT NOT NULL,processor_version TEXT NOT NULL,dtype TEXT NOT NULL,dimension INTEGER NOT NULL,normalized INTEGER NOT NULL CHECK(normalized IN (0,1)),vector_float32 BLOB NULL,status TEXT NOT NULL CHECK(status IN ('Completed','Failed')),error_code TEXT NULL,error_message TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(taxonomy_version,tag_id,text_representation_version,model_name,model_revision,processor_version,dtype,dimension));
CREATE TABLE media_tag_evidence(media_tag_evidence_id TEXT PRIMARY KEY,media_file_id TEXT NOT NULL REFERENCES media_file(media_file_id),media_content_id TEXT NOT NULL REFERENCES media_content(media_content_id),representative_visual_sample_id TEXT NULL REFERENCES visual_sample(visual_sample_id),taxonomy_version TEXT NOT NULL,tag_id TEXT NOT NULL,display_name TEXT NOT NULL,group_name TEXT NOT NULL,source TEXT NOT NULL CHECK(source IN ('Human','Metadata','EmbeddingZeroShot','RuleBased','Imported')),model_name TEXT NOT NULL,model_revision TEXT NOT NULL,processor_version TEXT NOT NULL,dtype TEXT NOT NULL,dimension INTEGER NOT NULL,source_sampling_version TEXT NOT NULL,text_representation_version TEXT NOT NULL,aggregation_version TEXT NOT NULL,threshold_version TEXT NOT NULL,score_metric TEXT NOT NULL,maximum_score REAL NOT NULL,mean_score REAL NOT NULL,matched_sample_count INTEGER NOT NULL,completed_sample_count INTEGER NOT NULL,expected_sample_count INTEGER NOT NULL,coverage_ratio REAL NOT NULL,source_embedding_digest TEXT NOT NULL,status TEXT NOT NULL CHECK(status IN ('Suggested','NotSuggested','InsufficientEvidence','Failed')),error_code TEXT NULL,error_message TEXT NULL,created_at TEXT NOT NULL,UNIQUE(media_file_id,media_content_id,taxonomy_version,tag_id,source,model_name,model_revision,processor_version,dtype,dimension,source_sampling_version,text_representation_version,aggregation_version,threshold_version,source_embedding_digest));
CREATE TABLE media_tag_judgment(media_tag_judgment_id TEXT PRIMARY KEY,media_tag_evidence_id TEXT NOT NULL REFERENCES media_tag_evidence(media_tag_evidence_id),decision TEXT NOT NULL CHECK(decision IN ('Unreviewed','Confirmed','Rejected','Ignored')),origin TEXT NOT NULL CHECK(origin='Human'),note TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(media_tag_evidence_id));
CREATE TABLE manual_media_tag(manual_media_tag_id TEXT PRIMARY KEY,media_file_id TEXT NOT NULL REFERENCES media_file(media_file_id),canonical_tag TEXT NOT NULL,display_tag TEXT NOT NULL,source TEXT NOT NULL CHECK(source='Human'),note TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(media_file_id,canonical_tag));
CREATE INDEX ix_tag_text_embedding_lookup ON tag_text_embedding(taxonomy_version,tag_id,text_representation_version,model_name,model_revision,processor_version,dtype,dimension,status,updated_at DESC);
CREATE INDEX ix_media_tag_evidence_current ON media_tag_evidence(media_file_id,media_content_id,taxonomy_version,tag_id,status,created_at DESC);
CREATE INDEX ix_media_tag_evidence_filter ON media_tag_evidence(tag_id,status,media_file_id,media_content_id);
CREATE INDEX ix_media_tag_evidence_review ON media_tag_evidence(status,created_at DESC);
CREATE INDEX ix_media_tag_judgment_evidence ON media_tag_judgment(media_tag_evidence_id,decision,updated_at DESC);
CREATE INDEX ix_manual_media_tag_filter ON manual_media_tag(canonical_tag,media_file_id,updated_at DESC);
""";

    private const string SchemaV5 = """
CREATE TABLE content_similarity_representation(content_similarity_representation_id TEXT PRIMARY KEY,media_file_id TEXT NOT NULL REFERENCES media_file(media_file_id),media_content_id TEXT NOT NULL REFERENCES media_content(media_content_id),model_name TEXT NOT NULL,model_revision TEXT NOT NULL,processor_version TEXT NOT NULL,source_sampling_version TEXT NOT NULL,aggregation_version TEXT NOT NULL,source_embedding_digest TEXT NOT NULL,dtype TEXT NOT NULL,dimension INTEGER NOT NULL,normalized INTEGER NOT NULL CHECK(normalized IN (0,1)),available_sample_count INTEGER NOT NULL,expected_sample_count INTEGER NOT NULL,coverage_ratio REAL NOT NULL,vector_float32 BLOB NULL,status TEXT NOT NULL CHECK(status IN ('Completed','InsufficientEvidence','Partial','Failed','Cancelled')),error_code TEXT NULL,error_message TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(media_file_id,media_content_id,model_name,model_revision,processor_version,source_sampling_version,aggregation_version,source_embedding_digest,dtype,dimension));
CREATE TABLE content_similarity_lsh_bucket(content_similarity_representation_id TEXT NOT NULL REFERENCES content_similarity_representation(content_similarity_representation_id),candidate_index_version TEXT NOT NULL,band_index INTEGER NOT NULL CHECK(band_index BETWEEN 0 AND 3),band_value TEXT NOT NULL,PRIMARY KEY(content_similarity_representation_id,candidate_index_version,band_index));
CREATE TABLE content_similarity_evidence(content_similarity_evidence_id TEXT PRIMARY KEY,media_file_a_id TEXT NOT NULL REFERENCES media_file(media_file_id),media_content_a_id TEXT NOT NULL REFERENCES media_content(media_content_id),content_similarity_representation_a_id TEXT NOT NULL REFERENCES content_similarity_representation(content_similarity_representation_id),media_file_b_id TEXT NOT NULL REFERENCES media_file(media_file_id),media_content_b_id TEXT NOT NULL REFERENCES media_content(media_content_id),content_similarity_representation_b_id TEXT NOT NULL REFERENCES content_similarity_representation(content_similarity_representation_id),representative_visual_sample_a_id TEXT NOT NULL REFERENCES visual_sample(visual_sample_id),representative_visual_sample_b_id TEXT NOT NULL REFERENCES visual_sample(visual_sample_id),model_name TEXT NOT NULL,model_revision TEXT NOT NULL,processor_version TEXT NOT NULL,source_sampling_version TEXT NOT NULL,aggregation_version TEXT NOT NULL,threshold_version TEXT NOT NULL,similarity_metric TEXT NOT NULL,mean_cosine_similarity REAL NOT NULL,sample_aware_cosine_similarity REAL NOT NULL,representative_cosine_similarity REAL NOT NULL,coverage_sample_count_a INTEGER NOT NULL,coverage_expected_count_a INTEGER NOT NULL,coverage_sample_count_b INTEGER NOT NULL,coverage_expected_count_b INTEGER NOT NULL,evidence_digest TEXT NOT NULL,status TEXT NOT NULL CHECK(status IN ('Verified','InsufficientEvidence','Rejected','Partial','Failed','Cancelled')),error_code TEXT NULL,error_message TEXT NULL,verified_at TEXT NOT NULL,CHECK(media_file_a_id < media_file_b_id),UNIQUE(media_file_a_id,media_content_a_id,content_similarity_representation_a_id,media_file_b_id,media_content_b_id,content_similarity_representation_b_id,aggregation_version,threshold_version));
CREATE TABLE content_similarity_judgment(content_similarity_judgment_id TEXT PRIMARY KEY,content_similarity_evidence_id TEXT NOT NULL REFERENCES content_similarity_evidence(content_similarity_evidence_id),decision TEXT NOT NULL CHECK(decision IN ('Unreviewed','Related','NotRelated','KeepBoth','Ignored')),origin TEXT NOT NULL CHECK(origin='Human'),note TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(content_similarity_evidence_id));
CREATE INDEX ix_content_representation_current ON content_similarity_representation(media_file_id,media_content_id,model_name,model_revision,processor_version,source_sampling_version,aggregation_version,dtype,dimension,status,updated_at DESC);
CREATE INDEX ix_content_lsh_bucket_lookup ON content_similarity_lsh_bucket(candidate_index_version,band_index,band_value,content_similarity_representation_id);
CREATE INDEX ix_content_evidence_pair ON content_similarity_evidence(media_file_a_id,media_content_a_id,media_file_b_id,media_content_b_id,status);
CREATE INDEX ix_content_evidence_review ON content_similarity_evidence(status,verified_at DESC);
CREATE INDEX ix_content_judgment_edge ON content_similarity_judgment(content_similarity_evidence_id,updated_at DESC);
""";

    private const string SchemaV4 = """
CREATE TABLE near_duplicate_frame(near_duplicate_frame_id TEXT PRIMARY KEY,media_file_id TEXT NOT NULL REFERENCES media_file(media_file_id),media_content_id TEXT NOT NULL REFERENCES media_content(media_content_id),source_size_bytes INTEGER NOT NULL,source_last_write_time_utc TEXT NOT NULL,sample_index INTEGER NOT NULL,normalized_position REAL NOT NULL,requested_timestamp_seconds REAL NOT NULL,presentation_timestamp_seconds REAL NOT NULL,sampling_version TEXT NOT NULL,fingerprint_algorithm TEXT NOT NULL,fingerprint_hex TEXT NULL,frame_relative_path TEXT NULL,frame_sha256 TEXT NULL,frame_byte_size INTEGER NULL,status TEXT NOT NULL CHECK(status IN ('Completed','Partial','Failed','Cancelled')),error_code TEXT NULL,error_message TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(media_file_id,media_content_id,sampling_version,fingerprint_algorithm,sample_index));
CREATE TABLE near_duplicate_lsh_bucket(near_duplicate_frame_id TEXT NOT NULL REFERENCES near_duplicate_frame(near_duplicate_frame_id),sampling_version TEXT NOT NULL,fingerprint_algorithm TEXT NOT NULL,sample_index INTEGER NOT NULL,band_index INTEGER NOT NULL CHECK(band_index BETWEEN 0 AND 3),band_value TEXT NOT NULL,PRIMARY KEY(near_duplicate_frame_id,band_index));
CREATE TABLE near_duplicate_evidence(near_duplicate_evidence_id TEXT PRIMARY KEY,media_file_a_id TEXT NOT NULL REFERENCES media_file(media_file_id),media_content_a_id TEXT NOT NULL REFERENCES media_content(media_content_id),media_file_b_id TEXT NOT NULL REFERENCES media_file(media_file_id),media_content_b_id TEXT NOT NULL REFERENCES media_content(media_content_id),evidence_algorithm TEXT NOT NULL,sampling_version TEXT NOT NULL,threshold_version TEXT NOT NULL,evidence_digest TEXT NOT NULL,duration_a_seconds REAL NOT NULL,duration_b_seconds REAL NOT NULL,duration_difference_ratio REAL NOT NULL,compared_sample_count INTEGER NOT NULL,matched_sample_count INTEGER NOT NULL,longest_consecutive_match_count INTEGER NOT NULL,mean_hamming_distance REAL NOT NULL,max_hamming_distance INTEGER NOT NULL,visual_match_score REAL NOT NULL,status TEXT NOT NULL CHECK(status IN ('Verified','InsufficientEvidence','Rejected','Partial','Failed','Cancelled')),error_code TEXT NULL,error_message TEXT NULL,verified_at TEXT NOT NULL,CHECK(media_file_a_id < media_file_b_id),UNIQUE(media_file_a_id,media_content_a_id,media_file_b_id,media_content_b_id,evidence_algorithm,sampling_version,threshold_version));
CREATE TABLE near_duplicate_frame_match(near_duplicate_evidence_id TEXT NOT NULL REFERENCES near_duplicate_evidence(near_duplicate_evidence_id),sample_index INTEGER NOT NULL,near_duplicate_frame_a_id TEXT NOT NULL REFERENCES near_duplicate_frame(near_duplicate_frame_id),near_duplicate_frame_b_id TEXT NOT NULL REFERENCES near_duplicate_frame(near_duplicate_frame_id),hamming_distance INTEGER NOT NULL CHECK(hamming_distance BETWEEN 0 AND 64),is_match INTEGER NOT NULL CHECK(is_match IN (0,1)),PRIMARY KEY(near_duplicate_evidence_id,sample_index));
CREATE TABLE near_duplicate_member(duplicate_group_id TEXT NOT NULL REFERENCES duplicate_group(duplicate_group_id),media_file_id TEXT NOT NULL REFERENCES media_file(media_file_id),media_content_id TEXT NOT NULL REFERENCES media_content(media_content_id),near_duplicate_evidence_id TEXT NOT NULL REFERENCES near_duplicate_evidence(near_duplicate_evidence_id),added_at TEXT NOT NULL,PRIMARY KEY(duplicate_group_id,media_file_id,media_content_id));
CREATE INDEX ix_near_frame_current ON near_duplicate_frame(media_file_id,media_content_id,sampling_version,fingerprint_algorithm,status,sample_index);
CREATE INDEX ix_near_bucket_lookup ON near_duplicate_lsh_bucket(sampling_version,fingerprint_algorithm,sample_index,band_value);
CREATE INDEX ix_near_evidence_pair ON near_duplicate_evidence(media_file_a_id,media_content_a_id,media_file_b_id,media_content_b_id,status);
CREATE INDEX ix_near_member_group ON near_duplicate_member(duplicate_group_id,media_file_id,media_content_id);
CREATE TABLE duplicate_judgment_v4(duplicate_judgment_id TEXT PRIMARY KEY,duplicate_group_id TEXT NOT NULL REFERENCES duplicate_group(duplicate_group_id),media_file_id TEXT NULL REFERENCES media_file(media_file_id),target_scope TEXT NOT NULL CHECK(target_scope IN ('Group','Member')),target_key TEXT NOT NULL,decision TEXT NOT NULL CHECK(decision IN ('Unreviewed','ConfirmedDuplicate','KeepBoth','Preferred','Ignored','NotDuplicate')),origin TEXT NOT NULL CHECK(origin='Human'),note TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(duplicate_group_id,target_scope,target_key));
INSERT INTO duplicate_judgment_v4(duplicate_judgment_id,duplicate_group_id,media_file_id,target_scope,target_key,decision,origin,note,created_at,updated_at) SELECT duplicate_judgment_id,duplicate_group_id,media_file_id,target_scope,target_key,decision,origin,note,created_at,updated_at FROM duplicate_judgment;
DROP TABLE duplicate_judgment;
ALTER TABLE duplicate_judgment_v4 RENAME TO duplicate_judgment;
CREATE INDEX ix_duplicate_judgment_group_scope ON duplicate_judgment(duplicate_group_id,target_scope,updated_at DESC);
""";

    private const string SchemaV3 = """
CREATE TABLE file_hash_evidence(file_hash_evidence_id TEXT PRIMARY KEY,media_file_id TEXT NOT NULL REFERENCES media_file(media_file_id),media_content_id TEXT NOT NULL REFERENCES media_content(media_content_id),algorithm TEXT NOT NULL,digest TEXT NOT NULL,source_size_bytes INTEGER NOT NULL,source_last_write_time_utc TEXT NOT NULL,processing_version TEXT NOT NULL,status TEXT NOT NULL CHECK(status IN ('Verified','Stale','Failed','Cancelled')),error_code TEXT NULL,error_message TEXT NULL,computed_at TEXT NOT NULL,UNIQUE(media_file_id,media_content_id,algorithm,processing_version));
CREATE TABLE duplicate_group(duplicate_group_id TEXT PRIMARY KEY,relation_type TEXT NOT NULL CHECK(relation_type IN ('Exact','NearDuplicate','ContentSimilar')),evidence_algorithm TEXT NOT NULL,evidence_digest TEXT NOT NULL,byte_length INTEGER NOT NULL,processing_version TEXT NOT NULL,verified_at TEXT NOT NULL,UNIQUE(relation_type,evidence_algorithm,evidence_digest));
CREATE TABLE duplicate_member(duplicate_group_id TEXT NOT NULL REFERENCES duplicate_group(duplicate_group_id),media_file_id TEXT NOT NULL REFERENCES media_file(media_file_id),media_content_id TEXT NOT NULL REFERENCES media_content(media_content_id),file_hash_evidence_id TEXT NOT NULL REFERENCES file_hash_evidence(file_hash_evidence_id),added_at TEXT NOT NULL,PRIMARY KEY(duplicate_group_id,media_file_id,media_content_id));
CREATE TABLE duplicate_judgment(duplicate_judgment_id TEXT PRIMARY KEY,duplicate_group_id TEXT NOT NULL REFERENCES duplicate_group(duplicate_group_id),media_file_id TEXT NULL REFERENCES media_file(media_file_id),target_scope TEXT NOT NULL CHECK(target_scope IN ('Group','Member')),target_key TEXT NOT NULL,decision TEXT NOT NULL CHECK(decision IN ('Unreviewed','ConfirmedDuplicate','KeepBoth','Preferred','Ignored')),origin TEXT NOT NULL CHECK(origin='Human'),note TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(duplicate_group_id,target_scope,target_key));
CREATE INDEX ix_hash_evidence_current ON file_hash_evidence(media_file_id,media_content_id,algorithm,processing_version,status);
CREATE INDEX ix_hash_evidence_digest ON file_hash_evidence(algorithm,digest,status);
CREATE INDEX ix_duplicate_member_group ON duplicate_member(duplicate_group_id,media_file_id,media_content_id);
CREATE INDEX ix_duplicate_member_current ON duplicate_member(media_file_id,media_content_id);
CREATE INDEX ix_duplicate_judgment_group_scope ON duplicate_judgment(duplicate_group_id,target_scope,updated_at DESC);
""";

    private const string SchemaV2 = """
CREATE TABLE model_provenance(model_name TEXT NOT NULL,model_revision TEXT NOT NULL,processor_version TEXT NOT NULL,artifact_manifest_sha256 TEXT NOT NULL,license TEXT NOT NULL,runtime_network_allowed INTEGER NOT NULL CHECK(runtime_network_allowed IN (0,1)),recorded_at TEXT NOT NULL,PRIMARY KEY(model_name,model_revision,processor_version));
CREATE TABLE visual_sample(visual_sample_id TEXT PRIMARY KEY,media_file_id TEXT NOT NULL REFERENCES media_file(media_file_id),media_content_id TEXT NOT NULL REFERENCES media_content(media_content_id),input_identity_hash TEXT NOT NULL,sample_index INTEGER NOT NULL,requested_timestamp_seconds REAL NOT NULL,presentation_timestamp_seconds REAL NOT NULL,sampling_version TEXT NOT NULL,cache_key TEXT NOT NULL UNIQUE,frame_relative_path TEXT NULL,frame_sha256 TEXT NULL,frame_byte_size INTEGER NULL,status TEXT NOT NULL CHECK(status IN ('Pending','Completed','Failed')),error_code TEXT NULL,error_message TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(media_file_id,input_identity_hash,sampling_version,sample_index));
CREATE TABLE visual_embedding(visual_embedding_id TEXT PRIMARY KEY,visual_sample_id TEXT NOT NULL REFERENCES visual_sample(visual_sample_id),input_identity_hash TEXT NOT NULL,cache_key TEXT NOT NULL UNIQUE,model_name TEXT NOT NULL,model_revision TEXT NOT NULL,processor_version TEXT NOT NULL,sampling_version TEXT NOT NULL,dtype TEXT NOT NULL,dimension INTEGER NOT NULL,normalized INTEGER NOT NULL CHECK(normalized IN (0,1)),vector_float32 BLOB NULL,status TEXT NOT NULL CHECK(status IN ('Pending','Completed','Failed')),error_code TEXT NULL,error_message TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,UNIQUE(visual_sample_id,model_revision,processor_version,dtype));
CREATE INDEX ix_visual_sample_file_input_sampling ON visual_sample(media_file_id,input_identity_hash,sampling_version,sample_index);
CREATE INDEX ix_visual_embedding_status_model ON visual_embedding(status,model_revision,processor_version,dtype);
""";

    private const string SchemaV1 = """
CREATE TABLE app_setting(key TEXT PRIMARY KEY,value TEXT NOT NULL,updated_at TEXT NOT NULL);
CREATE TABLE library_root(library_root_id TEXT PRIMARY KEY,configured_path TEXT NOT NULL,normalized_path TEXT NOT NULL UNIQUE,display_name TEXT NOT NULL,volume_identity TEXT NULL,availability TEXT NOT NULL CHECK(availability IN ('Unknown','Online','Offline')),created_at TEXT NOT NULL,last_scan_at TEXT NULL);
CREATE TABLE library_scan(scan_id TEXT PRIMARY KEY,library_root_id TEXT NOT NULL REFERENCES library_root(library_root_id),state TEXT NOT NULL,started_at TEXT NOT NULL,completed_at TEXT NULL,files_seen INTEGER NOT NULL,files_added INTEGER NOT NULL,files_updated INTEGER NOT NULL,files_unavailable INTEGER NOT NULL,error_count INTEGER NOT NULL);
CREATE TABLE media_content(media_content_id TEXT PRIMARY KEY,canonical_sha256 TEXT NULL,byte_length INTEGER NOT NULL,identity_state TEXT NOT NULL,created_at TEXT NOT NULL);
CREATE TABLE media_file(media_file_id TEXT PRIMARY KEY,library_root_id TEXT NOT NULL REFERENCES library_root(library_root_id),media_content_id TEXT NOT NULL REFERENCES media_content(media_content_id),path TEXT NOT NULL,normalized_path TEXT NOT NULL,file_name TEXT NOT NULL,extension TEXT NOT NULL,size_bytes INTEGER NOT NULL,creation_time_utc TEXT NOT NULL,last_write_time_utc TEXT NOT NULL,volume_identity TEXT NULL,file_identity TEXT NULL,quick_fingerprint TEXT NULL,full_sha256 TEXT NULL,presence_state TEXT NOT NULL,first_seen_at TEXT NOT NULL,last_seen_at TEXT NOT NULL,last_scan_id TEXT NULL REFERENCES library_scan(scan_id),UNIQUE(library_root_id,normalized_path));
CREATE TABLE analysis(analysis_id TEXT PRIMARY KEY,media_content_id TEXT NOT NULL REFERENCES media_content(media_content_id),analysis_type TEXT NOT NULL,status TEXT NOT NULL,processor_name TEXT NOT NULL,processor_version TEXT NOT NULL,model_name TEXT NULL,model_version TEXT NULL,config_json TEXT NOT NULL,config_hash TEXT NOT NULL,input_identity_hash TEXT NOT NULL,processing_version TEXT NOT NULL,started_at TEXT NULL,completed_at TEXT NULL,error_code TEXT NULL,error_message TEXT NULL);
CREATE TABLE artifact(artifact_id TEXT PRIMARY KEY,analysis_id TEXT NOT NULL REFERENCES analysis(analysis_id),artifact_kind TEXT NOT NULL,relative_path TEXT NOT NULL,sha256 TEXT NOT NULL,byte_size INTEGER NOT NULL,mime_type TEXT NOT NULL,durability_class TEXT NOT NULL,created_at TEXT NOT NULL,UNIQUE(analysis_id,artifact_kind,relative_path));
CREATE TABLE media_metadata(media_file_id TEXT PRIMARY KEY REFERENCES media_file(media_file_id),container_format TEXT NULL,duration_seconds REAL NULL,width INTEGER NULL,height INTEGER NULL,video_codec TEXT NULL,fps_text TEXT NULL,bitrate INTEGER NULL,audio_stream_count INTEGER NOT NULL,subtitle_stream_count INTEGER NOT NULL,raw_summary_json TEXT NOT NULL,updated_at TEXT NOT NULL);
CREATE TABLE job(job_id TEXT PRIMARY KEY,job_type TEXT NOT NULL,state TEXT NOT NULL,phase TEXT NOT NULL,reason TEXT NOT NULL,payload_json TEXT NOT NULL,attempt INTEGER NOT NULL,max_attempts INTEGER NOT NULL,not_before TEXT NULL,started_at TEXT NULL,completed_at TEXT NULL,heartbeat_at TEXT NULL,progress_current INTEGER NOT NULL,progress_total INTEGER NOT NULL,progress_unit TEXT NOT NULL,progress_indeterminate INTEGER NOT NULL,last_error_code TEXT NULL,last_error_message TEXT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
CREATE TABLE job_event(job_event_id TEXT PRIMARY KEY,job_id TEXT NOT NULL REFERENCES job(job_id),occurred_at TEXT NOT NULL,event_name TEXT NOT NULL,detail_json TEXT NULL);
CREATE INDEX ix_media_file_root_path ON media_file(library_root_id,normalized_path);
CREATE INDEX ix_media_file_volume_file ON media_file(volume_identity,file_identity);
CREATE INDEX ix_media_file_content ON media_file(media_content_id);
CREATE INDEX ix_analysis_content_type_status ON analysis(media_content_id,analysis_type,status);
CREATE INDEX ix_library_scan_root_started ON library_scan(library_root_id,started_at DESC);
CREATE INDEX ix_job_state_not_before ON job(state,not_before);
CREATE INDEX ix_job_event_job_time ON job_event(job_id,occurred_at DESC);
""";
}
