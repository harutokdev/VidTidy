using System.Globalization;
using LocalMediaAI.Core;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure;

public sealed partial class SqliteLibraryRepository
{
    private const string FileRetentionKey = "retention.file.days";
    private const string FolderRetentionKey = "retention.folder.days";
    private const string DriveRetentionKey = "retention.drive.days";
    internal Func<RetentionRecord, CancellationToken, Task>? BeforeFinalCleanupEligibilityForTests { get; set; }

    public async Task<RetentionSettings> GetRetentionSettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var values = await QueryAsync(connection, null, "SELECT key,value FROM app_setting WHERE key IN ($file,$folder,$drive);",
            r => (Key: r.GetString(0), Value: r.GetString(1)), cancellationToken,
            ("$file", FileRetentionKey), ("$folder", FolderRetentionKey), ("$drive", DriveRetentionKey));
        long? Read(string key, long fallback)
        {
            var value = values.FirstOrDefault(item => item.Key == key).Value;
            if (string.Equals(value, "unlimited", StringComparison.OrdinalIgnoreCase)) return null;
            return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days > 0 && days <= RetentionSettings.MaximumSafeDays ? days : fallback;
        }
        return new RetentionSettings(Read(FileRetentionKey, 30), Read(FolderRetentionKey, 60), Read(DriveRetentionKey, 365));
    }

    public async Task SaveRetentionSettingsAsync(RetentionSettings settings, CancellationToken cancellationToken)
    {
        settings = settings.Validate();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var pair in new[] { (FileRetentionKey, settings.FileDays), (FolderRetentionKey, settings.FolderDays), (DriveRetentionKey, settings.DriveDays) })
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO app_setting(key,value,updated_at) VALUES($key,$value,$at) ON CONFLICT(key) DO UPDATE SET value=excluded.value,updated_at=excluded.updated_at;", cancellationToken,
                ("$key", pair.Item1), ("$value", pair.Item2?.ToString(CultureInfo.InvariantCulture) ?? "unlimited"), ("$at", Now()));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RetentionRecord>> GetRetentionRecordsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await QueryAsync(connection, null, "SELECT * FROM retention_state ORDER BY absence_started_at,target_type,target_id;", ReadRetention, cancellationToken);
    }

    public async Task UpdateRootIdentityAsync(Guid rootId, string configuredPath, string? volumeIdentity, string? rootIdentity, CancellationToken cancellationToken)
    {
        var normalized = PathPolicy.NormalizeForIdentity(configuredPath);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, null, "UPDATE library_root SET configured_path=$path,normalized_path=$normalized,volume_identity=COALESCE($volume,volume_identity),root_identity=COALESCE($identity,root_identity),availability='Online' WHERE library_root_id=$id AND registration_state='Active';", cancellationToken,
            ("$path", configuredPath), ("$normalized", normalized), ("$volume", (object?)volumeIdentity ?? DBNull.Value), ("$identity", (object?)rootIdentity ?? DBNull.Value), ("$id", rootId.ToString("D")));
    }

    public async Task RecordRootObservationAsync(Guid rootId, RootObservationKind observation, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var root = await QuerySingleAsync(connection, transaction, "SELECT * FROM library_root WHERE library_root_id=$id;", ReadRoot, cancellationToken, ("$id", rootId.ToString("D"))) ?? throw new InvalidOperationException("Library root does not exist.");
        if (root.RegistrationState != RootRegistrationState.Active) { await transaction.CommitAsync(cancellationToken); return; }
        if (observation == RootObservationKind.Unknown)
        {
            await ExecuteNonQueryAsync(connection, transaction, "UPDATE library_root SET availability='Unknown' WHERE library_root_id=$id; UPDATE media_file SET presence_state='AccessDenied' WHERE library_root_id=$id AND cleanup_committed_at IS NULL;", cancellationToken, ("$id", rootId.ToString("D")));
            await ClearRootRetentionAsync(connection, transaction, rootId, cancellationToken);
        }
        else if (observation is RootObservationKind.DriveOffline or RootObservationKind.RootFolderMissing)
        {
            var cause = observation == RootObservationKind.DriveOffline ? RetentionCause.DriveOffline : RetentionCause.FolderMissing;
            var type = observation == RootObservationKind.DriveOffline ? RetentionTargetType.Drive : RetentionTargetType.Folder;
            await ExecuteNonQueryAsync(connection, transaction, "UPDATE library_root SET availability=$availability WHERE library_root_id=$id; UPDATE media_file SET presence_state=$presence WHERE library_root_id=$id AND cleanup_committed_at IS NULL; DELETE FROM retention_state WHERE state='Retained' AND ((target_type='File' AND target_id IN (SELECT media_file_id FROM media_file WHERE library_root_id=$id)) OR (target_type='Folder' AND target_id IN (SELECT directory_id FROM library_directory WHERE library_root_id=$id)));", cancellationToken,
                ("$availability", observation == RootObservationKind.DriveOffline ? "Offline" : "Online"), ("$presence", observation == RootObservationKind.DriveOffline ? "OfflineRoot" : "Missing"), ("$id", rootId.ToString("D")));
            await SetRetentionAsync(connection, transaction, type, rootId.ToString("D"), cause, rootId.ToString("D"), observedAt, cancellationToken);
        }
        else
        {
            await ExecuteNonQueryAsync(connection, transaction, "UPDATE library_root SET availability='Online' WHERE library_root_id=$id; DELETE FROM retention_state WHERE target_id=$id AND state='Retained' AND target_type IN ('Drive','Folder','Root');", cancellationToken, ("$id", rootId.ToString("D")));
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UnregisterRootAsync(Guid rootId, DateTimeOffset unregisteredAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var root = await QuerySingleAsync(connection, transaction, "SELECT * FROM library_root WHERE library_root_id=$id;", ReadRoot, cancellationToken, ("$id", rootId.ToString("D"))) ?? throw new InvalidOperationException("Library root does not exist.");
        if (root.RegistrationState == RootRegistrationState.Unregistered) { await transaction.CommitAsync(cancellationToken); return; }
        var tombstonePath = root.NormalizedPath + "#unregistered-" + root.LibraryRootId.ToString("N");
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE library_root SET registration_state='Unregistered',unregistered_at=$at,normalized_path=$tombstone,registration_revision=registration_revision+1 WHERE library_root_id=$id;", cancellationToken,
            ("$at", ToDb(unregisteredAt)), ("$tombstone", tombstonePath), ("$id", rootId.ToString("D")));

        // A media row has one current owner. Move it to the most-specific remaining active root
        // when its current path is still covered; only truly out-of-scope rows join root retention.
        var otherRoots = await QueryAsync(connection, transaction, "SELECT * FROM library_root WHERE registration_state='Active' ORDER BY length(normalized_path) DESC;", ReadRoot, cancellationToken);
        var media = await QueryAsync(connection, transaction, "SELECT * FROM media_file WHERE library_root_id=$id AND cleanup_committed_at IS NULL;", ReadMediaFile, cancellationToken, ("$id", rootId.ToString("D")));
        foreach (var item in media)
        {
            var owner = otherRoots.FirstOrDefault(candidate => IsPathInScope(candidate.NormalizedPath, item.NormalizedPath));
            if (owner is not null)
                await ExecuteNonQueryAsync(connection, transaction, "UPDATE media_file SET library_root_id=$owner WHERE media_file_id=$file;", cancellationToken, ("$owner", owner.LibraryRootId.ToString("D")), ("$file", item.MediaFileId.ToString("D")));
            else
                await ExecuteNonQueryAsync(connection, transaction, "UPDATE media_file SET presence_state='Missing' WHERE media_file_id=$file;", cancellationToken, ("$file", item.MediaFileId.ToString("D")));
        }
        await ClearRootRetentionAsync(connection, transaction, rootId, cancellationToken);
        await SetRetentionAsync(connection, transaction, root.RootKind == LibraryRootKind.Drive ? RetentionTargetType.Drive : RetentionTargetType.Root, rootId.ToString("D"), RetentionCause.ExplicitUnregister, rootId.ToString("D"), unregisteredAt, cancellationToken);
        await CancelQueuedRootJobsAsync(connection, transaction, rootId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<LibraryRoot> ReregisterRootAsync(string configuredPath, CancellationToken cancellationToken)
    {
        _paths.TestLab?.EnsureMediaPathAllowed(configuredPath);
        var identity = WindowsFileIdentityReader.TryGetDirectory(configuredPath);
        if (identity is null) return await AddOrGetLibraryRootAsync(configuredPath, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var root = await QuerySingleAsync(connection, transaction, "SELECT * FROM library_root WHERE registration_state='Unregistered' AND volume_identity=$volume AND root_identity=$identity ORDER BY unregistered_at DESC LIMIT 1;", ReadRoot, cancellationToken, ("$volume", identity.VolumeIdentity), ("$identity", identity.FileIdentity));
        if (root is null) { await transaction.CommitAsync(cancellationToken); return await AddOrGetLibraryRootAsync(configuredPath, cancellationToken); }
        var normalized = PathPolicy.NormalizeForIdentity(configuredPath);
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE library_root SET registration_state='Active',unregistered_at=NULL,configured_path=$path,normalized_path=$normalized,availability='Unknown',registration_revision=registration_revision+1 WHERE library_root_id=$id; DELETE FROM retention_state WHERE target_id=$id AND state='Retained' AND cause='ExplicitUnregister';", cancellationToken,
            ("$path", configuredPath), ("$normalized", normalized), ("$id", root.LibraryRootId.ToString("D")));
        await transaction.CommitAsync(cancellationToken);
        return (await GetLibraryRootAsync(root.LibraryRootId, cancellationToken))!;
    }

    public async Task ReconcileCompletedScanAbsenceAsync(Guid rootId, Guid scanId, ScanAuthoritySnapshot snapshot, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        if (!snapshot.CompletedAuthoritatively)
        {
            await RecordRootObservationAsync(rootId, RootObservationKind.Unknown, observedAt, cancellationToken);
            return;
        }
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var root = await QuerySingleAsync(connection, transaction, "SELECT * FROM library_root WHERE library_root_id=$id;", ReadRoot, cancellationToken, ("$id", rootId.ToString("D"))) ?? throw new InvalidOperationException("Library root does not exist.");
        if (root.RegistrationState != RootRegistrationState.Active || await ScalarLongAsync(connection, transaction, "SELECT COUNT(1) FROM library_scan WHERE scan_id=$scan AND library_root_id=$root AND registration_revision=$revision AND state='Completed';", cancellationToken, ("$scan", scanId.ToString("D")), ("$root", rootId.ToString("D")), ("$revision", root.RegistrationRevision)) != 1) { await transaction.CommitAsync(cancellationToken); return; }

        var observedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in snapshot.AccessibleDirectories)
        {
            var existing = !string.IsNullOrWhiteSpace(directory.VolumeIdentity) && !string.IsNullOrWhiteSpace(directory.DirectoryIdentity)
                ? await QuerySingleAsync(connection, transaction, "SELECT directory_id Id FROM library_directory WHERE volume_identity=$volume AND directory_identity=$identity LIMIT 1;", r => new Box(r.GetString(0)), cancellationToken, ("$volume", directory.VolumeIdentity), ("$identity", directory.DirectoryIdentity))
                : null;
            var id = existing?.Value ?? Guid.NewGuid().ToString("D");
            observedIds.Add(id);
            await ExecuteNonQueryAsync(connection, transaction, "INSERT INTO library_directory(directory_id,library_root_id,path,normalized_path,volume_identity,directory_identity,last_scan_id,last_seen_at) VALUES($id,$root,$path,$normalized,$volume,$identity,$scan,$at) ON CONFLICT(directory_id) DO UPDATE SET library_root_id=excluded.library_root_id,path=excluded.path,normalized_path=excluded.normalized_path,volume_identity=excluded.volume_identity,directory_identity=excluded.directory_identity,last_scan_id=excluded.last_scan_id,last_seen_at=excluded.last_seen_at; DELETE FROM retention_state WHERE target_type='Folder' AND target_id=$id AND state='Retained';", cancellationToken,
                ("$id", id), ("$root", rootId.ToString("D")), ("$path", directory.FullPath), ("$normalized", directory.NormalizedPath), ("$volume", (object?)directory.VolumeIdentity ?? DBNull.Value), ("$identity", (object?)directory.DirectoryIdentity ?? DBNull.Value), ("$scan", scanId.ToString("D")), ("$at", ToDb(observedAt)));
        }

        var oldDirectories = await QueryAsync(connection, transaction, "SELECT directory_id,path,normalized_path FROM library_directory WHERE library_root_id=$root AND (last_scan_id IS NULL OR last_scan_id<>$scan) ORDER BY length(normalized_path);", r => new DirectoryBox(r.GetString(0), r.GetString(1), r.GetString(2)), cancellationToken, ("$root", rootId.ToString("D")), ("$scan", scanId.ToString("D")));
        var unknown = snapshot.UnknownDirectoryPrefixes.Select(PathPolicy.NormalizeForIdentity).ToArray();
        var accessible = snapshot.AccessibleDirectories.Select(item => item.NormalizedPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingFolders = oldDirectories.Where(d => !unknown.Any(prefix => IsPathInScope(prefix, d.NormalizedPath)) && accessible.Contains(PathPolicy.NormalizeForIdentity(Path.GetDirectoryName(d.Path) ?? root.ConfiguredPath))).ToArray();
        foreach (var folder in missingFolders.Where(candidate => !missingFolders.Any(parent => parent != candidate && IsPathInScope(parent.NormalizedPath, candidate.NormalizedPath))))
            await SetRetentionAsync(connection, transaction, RetentionTargetType.Folder, folder.Id, RetentionCause.FolderMissing, folder.Id, observedAt, cancellationToken);

        var unseen = await QueryAsync(connection, transaction, "SELECT * FROM media_file WHERE library_root_id=$root AND cleanup_committed_at IS NULL AND (last_scan_id IS NULL OR last_scan_id<>$scan) AND presence_state NOT IN ('Recycled','PermanentlyDeleted');", ReadMediaFile, cancellationToken, ("$root", rootId.ToString("D")), ("$scan", scanId.ToString("D")));
        foreach (var file in unseen)
        {
            if (unknown.Any(prefix => IsPathInScope(prefix, file.NormalizedPath)))
            {
                await ExecuteNonQueryAsync(connection, transaction, "UPDATE media_file SET presence_state='AccessDenied' WHERE media_file_id=$id; DELETE FROM retention_state WHERE target_type='File' AND target_id=$id AND state='Retained';", cancellationToken, ("$id", file.MediaFileId.ToString("D")));
                continue;
            }
            var folder = missingFolders.FirstOrDefault(candidate => IsPathInScope(candidate.NormalizedPath, file.NormalizedPath));
            await ExecuteNonQueryAsync(connection, transaction, "UPDATE media_file SET presence_state='Missing' WHERE media_file_id=$id;", cancellationToken, ("$id", file.MediaFileId.ToString("D")));
            if (folder is not null)
                await ExecuteNonQueryAsync(connection, transaction, "DELETE FROM retention_state WHERE target_type='File' AND target_id=$id AND state='Retained';", cancellationToken, ("$id", file.MediaFileId.ToString("D")));
            else
                await SetRetentionAsync(connection, transaction, RetentionTargetType.File, file.MediaFileId.ToString("D"), RetentionCause.FileMissing, file.MediaFileId.ToString("D"), observedAt, cancellationToken);
        }
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE library_root SET availability='Online',volume_identity=COALESCE($volume,volume_identity),root_identity=COALESCE($identity,root_identity) WHERE library_root_id=$root; DELETE FROM retention_state WHERE target_id=$root AND state='Retained' AND cause IN ('DriveOffline','FolderMissing');", cancellationToken,
            ("$volume", (object?)snapshot.VolumeIdentity ?? DBNull.Value), ("$identity", (object?)snapshot.RootDirectoryIdentity ?? DBNull.Value), ("$root", rootId.ToString("D")));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<RetentionCleanupResult> RunRetentionCleanupAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var candidates = 0; var committed = 0; var completed = 0; var rescued = 0; var unknown = 0;
        var settings = await GetRetentionSettingsAsync(cancellationToken);
        var records = await GetRetentionRecordsAsync(cancellationToken);
        foreach (var record in records.Where(value => value.State == RetentionLifecycleState.Retained))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var days = record.Cause switch { RetentionCause.FileMissing => settings.FileDays, RetentionCause.FolderMissing => settings.FolderDays, RetentionCause.ExplicitUnregister when record.TargetType == RetentionTargetType.Root => settings.FolderDays, _ => settings.DriveDays };
            if (days is null || now < record.AbsenceStartedAt || (now - record.AbsenceStartedAt).TotalDays < days.Value) continue;
            candidates++;
            if (BeforeFinalCleanupEligibilityForTests is not null) await BeforeFinalCleanupEligibilityForTests(record, cancellationToken);
            var eligibility = await ConfirmCleanupEligibilityAsync(record, cancellationToken);
            if (eligibility == CleanupEligibility.Rescued) { await ResolveRetentionAfterFinalCheckAsync(record, rescued: true, cancellationToken); rescued++; continue; }
            if (eligibility == CleanupEligibility.Unknown) { await ResolveRetentionAfterFinalCheckAsync(record, rescued: false, cancellationToken); unknown++; continue; }
            if (!await CommitCleanupAsync(record, now, cancellationToken)) continue;
            committed++;
        }
        var committedRecords = (await GetRetentionRecordsAsync(cancellationToken)).Where(value => value.State == RetentionLifecycleState.CleanupCommitted).ToArray();
        foreach (var record in committedRecords)
        {
            await CompleteCommittedCleanupAsync(record, now, cancellationToken);
            completed++;
        }
        return new(candidates, committed, completed, rescued, unknown);
    }

    private async Task ResolveRetentionAfterFinalCheckAsync(RetentionRecord record, bool rescued, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, transaction, "DELETE FROM retention_state WHERE target_type=$type AND target_id=$id AND state='Retained';", cancellationToken, ("$type", record.TargetType.ToString()), ("$id", record.TargetId));
        if (record.TargetType == RetentionTargetType.File)
            await ExecuteNonQueryAsync(connection, transaction, "UPDATE media_file SET presence_state=$presence,last_seen_at=CASE WHEN $rescued=1 THEN $at ELSE last_seen_at END WHERE media_file_id=$id AND cleanup_committed_at IS NULL;", cancellationToken,
                ("$presence", rescued ? "Present" : "AccessDenied"), ("$rescued", rescued ? 1 : 0), ("$at", Now()), ("$id", record.TargetId));
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<CleanupEligibility> ConfirmCleanupEligibilityAsync(RetentionRecord record, CancellationToken cancellationToken)
    {
        if (record.Cause == RetentionCause.ExplicitUnregister)
        {
            var root = await GetLibraryRootAsync(Guid.Parse(record.TargetId), cancellationToken);
            return root?.RegistrationState == RootRegistrationState.Unregistered ? CleanupEligibility.Absent : CleanupEligibility.Rescued;
        }
        if (record.TargetType == RetentionTargetType.File)
        {
            var media = await GetMediaFileAsync(Guid.Parse(record.TargetId), cancellationToken);
            if (media is null || media.PresenceState == PresenceState.Present) return CleanupEligibility.Rescued;
            try
            {
                if (!File.Exists(media.Path))
                {
                    var parent = Path.GetDirectoryName(media.Path);
                    if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) return CleanupEligibility.Unknown;
                    return Directory.EnumerateFileSystemEntries(parent).Any(entry => string.Equals(Path.GetFullPath(entry), Path.GetFullPath(media.Path), StringComparison.OrdinalIgnoreCase))
                        ? CleanupEligibility.Unknown
                        : CleanupEligibility.Absent;
                }
                var identity = WindowsFileIdentityReader.TryGet(media.Path);
                return identity is not null && identity.VolumeIdentity == media.VolumeIdentity && identity.FileIdentity == media.FileIdentity ? CleanupEligibility.Rescued : CleanupEligibility.Absent;
            }
            catch (UnauthorizedAccessException) { return CleanupEligibility.Unknown; }
            catch (IOException) { return CleanupEligibility.Unknown; }
        }
        if (record.TargetType == RetentionTargetType.Drive && record.Cause == RetentionCause.DriveOffline)
        {
            var root = await GetLibraryRootAsync(Guid.Parse(record.TargetId), cancellationToken);
            if (root is null || string.IsNullOrWhiteSpace(root.VolumeIdentity)) return CleanupEligibility.Unknown;
            foreach (var drive in DriveInfo.GetDrives())
            {
                try { if (drive.IsReady && WindowsVolumeIdentityReader.TryGetForPath(drive.RootDirectory.FullName) == root.VolumeIdentity) return CleanupEligibility.Rescued; }
                catch (UnauthorizedAccessException) { return CleanupEligibility.Unknown; }
                catch (IOException) { return CleanupEligibility.Unknown; }
            }
            return CleanupEligibility.Absent;
        }
        var location = await GetRetentionLocationAsync(record, cancellationToken);
        var path = location?.Path;
        if (string.IsNullOrWhiteSpace(path)) return CleanupEligibility.Unknown;
        try
        {
            if (Directory.Exists(path))
            {
                var identity = WindowsFileIdentityReader.TryGetDirectory(path);
                if (location is { VolumeIdentity: not null, StableIdentity: not null } && identity is not null)
                    return identity.VolumeIdentity == location.VolumeIdentity && identity.FileIdentity == location.StableIdentity ? CleanupEligibility.Rescued : CleanupEligibility.Absent;
                return CleanupEligibility.Unknown;
            }
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) return CleanupEligibility.Unknown;
            return Directory.EnumerateFileSystemEntries(parent).Any(entry => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase))
                ? CleanupEligibility.Unknown
                : CleanupEligibility.Absent;
        }
        catch (UnauthorizedAccessException) { return CleanupEligibility.Unknown; }
        catch (IOException) { return CleanupEligibility.Unknown; }
    }

    private async Task<bool> CommitCleanupAsync(RetentionRecord expected, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // This transaction is the sole durable logical point-of-no-return. It re-reads
        // registration, cause, start time and latest settings while holding the writer.
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await QuerySingleAsync(connection, transaction, "SELECT * FROM retention_state WHERE target_type=$type AND target_id=$id;", ReadRetention, cancellationToken, ("$type", expected.TargetType.ToString()), ("$id", expected.TargetId));
        if (current is null || current.State != RetentionLifecycleState.Retained || current.Cause != expected.Cause || current.AbsenceStartedAt != expected.AbsenceStartedAt) { await transaction.CommitAsync(cancellationToken); return false; }
        var key = current.Cause switch { RetentionCause.FileMissing => FileRetentionKey, RetentionCause.FolderMissing => FolderRetentionKey, RetentionCause.ExplicitUnregister when current.TargetType == RetentionTargetType.Root => FolderRetentionKey, _ => DriveRetentionKey };
        var value = await ScalarStringAsync(connection, transaction, "SELECT value FROM app_setting WHERE key=$key;", cancellationToken, ("$key", key));
        if (string.Equals(value, "unlimited", StringComparison.OrdinalIgnoreCase) || !long.TryParse(value, out var days) || now < current.AbsenceStartedAt || (now - current.AbsenceStartedAt).TotalDays < days) { await transaction.CommitAsync(cancellationToken); return false; }
        if (current.Cause == RetentionCause.ExplicitUnregister && await ScalarLongAsync(connection, transaction, "SELECT COUNT(1) FROM library_root WHERE library_root_id=$id AND registration_state='Unregistered';", cancellationToken, ("$id", current.TargetId)) != 1) { await transaction.CommitAsync(cancellationToken); return false; }
        var cleanupId = Guid.NewGuid().ToString("D");
        var files = await SelectCurrentlyOwnedCleanupFilesAsync(connection, transaction, current, cancellationToken);
        foreach (var fileId in files)
        {
            var paths = await QueryAsync(connection, transaction, "SELECT DISTINCT a.relative_path FROM artifact a JOIN analysis an ON an.analysis_id=a.analysis_id JOIN media_file mf ON mf.media_content_id=an.media_content_id WHERE mf.media_file_id=$file AND NOT EXISTS(SELECT 1 FROM media_file other WHERE other.media_content_id=mf.media_content_id AND other.media_file_id<>mf.media_file_id AND other.cleanup_committed_at IS NULL);", r => r.GetString(0), cancellationToken, ("$file", fileId));
            if (paths.Count == 0) paths = [string.Empty];
            foreach (var path in paths)
                await ExecuteNonQueryAsync(connection, transaction, "INSERT OR IGNORE INTO retention_cleanup_item(cleanup_id,target_type,target_id,media_file_id,artifact_relative_path) VALUES($cleanup,$type,$target,$file,$path);", cancellationToken, ("$cleanup", cleanupId), ("$type", current.TargetType.ToString()), ("$target", current.TargetId), ("$file", fileId), ("$path", path));
        }
        await ExecuteNonQueryAsync(connection, transaction, "UPDATE retention_state SET state='CleanupCommitted',cleanup_committed_at=$at WHERE target_type=$type AND target_id=$id AND state='Retained';", cancellationToken, ("$at", ToDb(now)), ("$type", current.TargetType.ToString()), ("$id", current.TargetId));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task CompleteCommittedCleanupAsync(RetentionRecord record, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var items = await QueryAsync(connection, null, "SELECT media_file_id,artifact_relative_path FROM retention_cleanup_item WHERE target_type=$type AND target_id=$id;", r => (FileId: r.GetString(0), Path: r.GetString(1)), cancellationToken, ("$type", record.TargetType.ToString()), ("$id", record.TargetId));
        foreach (var path in items.Select(item => item.Path).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var full = Path.GetFullPath(Path.Combine(_paths.CacheRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathPolicy.IsWithinRoot(_paths.CacheRoot, full)) continue;
            try { if (File.Exists(full)) File.Delete(full); }
            catch (IOException ex) { _log.Write("Warning", "RetentionArtifactCleanupRetry", new { record.TargetType, record.TargetId, Path = path }, ex); return; }
            catch (UnauthorizedAccessException ex) { _log.Write("Warning", "RetentionArtifactCleanupRetry", new { record.TargetType, record.TargetId, Path = path }, ex); return; }
        }
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var fileId in items.Select(item => item.FileId).Distinct()) await DeleteMediaInformationAsync(connection, transaction, fileId, now, cancellationToken);
        await ExecuteNonQueryAsync(connection, transaction, "DELETE FROM retention_cleanup_item WHERE target_type=$type AND target_id=$id; UPDATE retention_state SET state='Completed',completed_at=$at WHERE target_type=$type AND target_id=$id AND state='CleanupCommitted';", cancellationToken, ("$type", record.TargetType.ToString()), ("$id", record.TargetId), ("$at", ToDb(now)));
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task DeleteMediaInformationAsync(SqliteConnection c, SqliteTransaction tx, string fileId, DateTimeOffset now, CancellationToken ct)
    {
        // File-operation proposal/plan/execution/audit and review_decision_history are
        // intentionally untouched. The media row becomes their minimal FK anchor.
        const string sql = """
DELETE FROM content_similarity_judgment WHERE content_similarity_evidence_id IN (SELECT content_similarity_evidence_id FROM content_similarity_evidence WHERE media_file_a_id=$file OR media_file_b_id=$file);
DELETE FROM content_similarity_evidence WHERE media_file_a_id=$file OR media_file_b_id=$file;
DELETE FROM content_similarity_lsh_bucket WHERE content_similarity_representation_id IN (SELECT content_similarity_representation_id FROM content_similarity_representation WHERE media_file_id=$file);
DELETE FROM content_similarity_representation WHERE media_file_id=$file;
DELETE FROM near_duplicate_timeline_evidence WHERE near_duplicate_evidence_id IN (SELECT near_duplicate_evidence_id FROM near_duplicate_evidence WHERE media_file_a_id=$file OR media_file_b_id=$file);
DELETE FROM near_duplicate_timeline_bucket WHERE media_file_id=$file;
DELETE FROM near_duplicate_timeline_frame WHERE media_file_id=$file;
DELETE FROM near_duplicate_timeline WHERE media_file_id=$file;
DELETE FROM near_duplicate_frame_match WHERE near_duplicate_evidence_id IN (SELECT near_duplicate_evidence_id FROM near_duplicate_evidence WHERE media_file_a_id=$file OR media_file_b_id=$file);
DELETE FROM near_duplicate_member WHERE media_file_id=$file;
DELETE FROM near_duplicate_evidence WHERE media_file_a_id=$file OR media_file_b_id=$file;
DELETE FROM near_duplicate_prepared_lsh_bucket WHERE media_file_id=$file;
DELETE FROM near_duplicate_prepared_frame WHERE media_file_id=$file;
DELETE FROM near_duplicate_preparation WHERE media_file_id=$file;
DELETE FROM near_duplicate_lsh_bucket WHERE near_duplicate_frame_id IN (SELECT near_duplicate_frame_id FROM near_duplicate_frame WHERE media_file_id=$file);
DELETE FROM near_duplicate_frame WHERE media_file_id=$file;
DELETE FROM duplicate_member WHERE media_file_id=$file;
DELETE FROM duplicate_judgment WHERE media_file_id=$file;
DELETE FROM duplicate_judgment WHERE duplicate_group_id IN (SELECT dg.duplicate_group_id FROM duplicate_group dg WHERE NOT EXISTS(SELECT 1 FROM duplicate_member dm WHERE dm.duplicate_group_id=dg.duplicate_group_id) AND NOT EXISTS(SELECT 1 FROM near_duplicate_member nm WHERE nm.duplicate_group_id=dg.duplicate_group_id));
DELETE FROM duplicate_group WHERE NOT EXISTS(SELECT 1 FROM duplicate_member dm WHERE dm.duplicate_group_id=duplicate_group.duplicate_group_id) AND NOT EXISTS(SELECT 1 FROM near_duplicate_member nm WHERE nm.duplicate_group_id=duplicate_group.duplicate_group_id);
DELETE FROM media_tag_judgment WHERE media_tag_evidence_id IN (SELECT media_tag_evidence_id FROM media_tag_evidence WHERE media_file_id=$file);
DELETE FROM media_tag_evidence WHERE media_file_id=$file;
DELETE FROM manual_media_tag WHERE media_file_id=$file;
DELETE FROM visual_embedding WHERE visual_sample_id IN (SELECT visual_sample_id FROM visual_sample WHERE media_file_id=$file);
DELETE FROM visual_sample WHERE media_file_id=$file;
DELETE FROM file_hash_evidence WHERE media_file_id=$file;
DELETE FROM media_metadata WHERE media_file_id=$file;
UPDATE media_file SET path='',normalized_path='#cleaned-' || media_file_id,file_name='',extension='',volume_identity=NULL,file_identity=NULL,quick_fingerprint=NULL,full_sha256=NULL,presence_state='Missing',cleanup_committed_at=$at WHERE media_file_id=$file;
DELETE FROM artifact WHERE analysis_id IN (SELECT analysis_id FROM analysis WHERE media_content_id IN (SELECT media_content_id FROM media_file WHERE media_file_id=$file) AND NOT EXISTS(SELECT 1 FROM media_file other WHERE other.media_content_id=analysis.media_content_id AND other.media_file_id<>$file AND other.cleanup_committed_at IS NULL));
DELETE FROM analysis WHERE media_content_id IN (SELECT media_content_id FROM media_file WHERE media_file_id=$file) AND NOT EXISTS(SELECT 1 FROM media_file other WHERE other.media_content_id=analysis.media_content_id AND other.media_file_id<>$file AND other.cleanup_committed_at IS NULL);
""";
        await ExecuteNonQueryAsync(c, tx, sql, ct, ("$file", fileId), ("$at", ToDb(now)));
    }

    private static async Task<IReadOnlyList<string>> SelectCurrentlyOwnedCleanupFilesAsync(SqliteConnection c, SqliteTransaction tx, RetentionRecord record, CancellationToken ct)
    {
        if (record.TargetType == RetentionTargetType.File)
            return await QueryAsync(c, tx, "SELECT media_file_id FROM media_file WHERE media_file_id=$id AND presence_state<>'Present' AND cleanup_committed_at IS NULL;", r => r.GetString(0), ct, ("$id", record.TargetId));
        string? prefix = null;
        Guid? rootId = null;
        if (record.TargetType == RetentionTargetType.Folder && record.Cause == RetentionCause.FolderMissing)
            prefix = await ScalarStringAsync(c, tx, "SELECT normalized_path FROM library_directory WHERE directory_id=$id;", ct, ("$id", record.TargetId));
        else rootId = Guid.Parse(record.TargetId);
        if (prefix is not null)
            return await QueryAsync(c, tx, "SELECT media_file_id FROM media_file WHERE cleanup_committed_at IS NULL AND presence_state<>'Present' AND (normalized_path=$prefix OR normalized_path LIKE $child);", r => r.GetString(0), ct, ("$prefix", prefix), ("$child", prefix.TrimEnd('\\') + "\\%"));
        return await QueryAsync(c, tx, "SELECT media_file_id FROM media_file WHERE library_root_id=$root AND cleanup_committed_at IS NULL AND presence_state<>'Present';", r => r.GetString(0), ct, ("$root", rootId!.Value.ToString("D")));
    }

    private async Task<RetentionLocation?> GetRetentionLocationAsync(RetentionRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        if (record.TargetType == RetentionTargetType.Folder && record.Cause == RetentionCause.FolderMissing)
            return await QuerySingleAsync(connection, null, "SELECT path,volume_identity,directory_identity FROM library_directory WHERE directory_id=$id;", r => new RetentionLocation(r.GetString(0), GetNullableString(r, "volume_identity"), GetNullableString(r, "directory_identity")), cancellationToken, ("$id", record.TargetId));
        return await QuerySingleAsync(connection, null, "SELECT configured_path,volume_identity,root_identity FROM library_root WHERE library_root_id=$id;", r => new RetentionLocation(r.GetString(0), GetNullableString(r, "volume_identity"), GetNullableString(r, "root_identity")), cancellationToken, ("$id", record.TargetId));
    }

    private static async Task SetRetentionAsync(SqliteConnection c, SqliteTransaction tx, RetentionTargetType type, string id, RetentionCause cause, string scope, DateTimeOffset at, CancellationToken ct)
    {
        await ExecuteNonQueryAsync(c, tx, "INSERT INTO retention_state(target_type,target_id,cause,cause_scope_id,absence_started_at,last_authoritative_at,state,cleanup_committed_at,completed_at) VALUES($type,$id,$cause,$scope,$at,$at,'Retained',NULL,NULL) ON CONFLICT(target_type,target_id) DO UPDATE SET cause=excluded.cause,cause_scope_id=excluded.cause_scope_id,absence_started_at=CASE WHEN retention_state.state='Retained' AND retention_state.cause=excluded.cause AND retention_state.cause_scope_id=excluded.cause_scope_id THEN retention_state.absence_started_at ELSE excluded.absence_started_at END,last_authoritative_at=excluded.last_authoritative_at,state=CASE WHEN retention_state.state='Completed' THEN retention_state.state ELSE 'Retained' END,cleanup_committed_at=CASE WHEN retention_state.state IN ('CleanupCommitted','Completed') THEN retention_state.cleanup_committed_at ELSE NULL END,completed_at=CASE WHEN retention_state.state='Completed' THEN retention_state.completed_at ELSE NULL END;", ct,
            ("$type", type.ToString()), ("$id", id), ("$cause", cause.ToString()), ("$scope", scope), ("$at", ToDb(at)));
    }

    private static async Task ClearRootRetentionAsync(SqliteConnection c, SqliteTransaction tx, Guid rootId, CancellationToken ct) =>
        await ExecuteNonQueryAsync(c, tx, "DELETE FROM retention_state WHERE state='Retained' AND (target_id=$root OR (target_type='File' AND target_id IN (SELECT media_file_id FROM media_file WHERE library_root_id=$root)) OR (target_type='Folder' AND target_id IN (SELECT directory_id FROM library_directory WHERE library_root_id=$root)));", ct, ("$root", rootId.ToString("D")));

    private static async Task CancelQueuedRootJobsAsync(SqliteConnection c, SqliteTransaction tx, Guid rootId, CancellationToken ct)
    {
        var root = rootId.ToString("D");
        await ExecuteNonQueryAsync(c, tx, "UPDATE job SET state='Cancelled',phase='RootUnregistered',reason='CancelRequested',completed_at=$at,updated_at=$at,last_error_code='ROOT_UNREGISTERED',last_error_message='The library root was explicitly unregistered.' WHERE state IN ('Pending','Waiting','Paused','RetryWaiting') AND (json_extract(payload_json,'$.LibraryRootId')=$root OR json_extract(payload_json,'$.MediaFileId') IN (SELECT media_file_id FROM media_file WHERE library_root_id=$root));", ct, ("$at", Now()), ("$root", root));
    }

    private static RetentionRecord ReadRetention(SqliteDataReader r) => new(Enum.Parse<RetentionTargetType>(r.GetString(r.GetOrdinal("target_type"))), r.GetString(r.GetOrdinal("target_id")), Enum.Parse<RetentionCause>(r.GetString(r.GetOrdinal("cause"))), r.GetString(r.GetOrdinal("cause_scope_id")), ParseTime(r.GetString(r.GetOrdinal("absence_started_at"))), ParseTime(r.GetString(r.GetOrdinal("last_authoritative_at"))), Enum.Parse<RetentionLifecycleState>(r.GetString(r.GetOrdinal("state"))), GetNullableTime(r, "cleanup_committed_at"), GetNullableTime(r, "completed_at"));
    private static bool IsPathInScope(string root, string path) => string.Equals(root.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    private enum CleanupEligibility { Absent, Rescued, Unknown }
    private sealed record Box(string Value);
    private sealed record DirectoryBox(string Id, string Path, string NormalizedPath);
    private sealed record RetentionLocation(string Path, string? VolumeIdentity, string? StableIdentity);
}
