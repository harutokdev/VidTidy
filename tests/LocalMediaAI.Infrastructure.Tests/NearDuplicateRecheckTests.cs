using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed partial class NearDuplicateFoundationTests
{
    /// <summary>A plain clip and the same clip pillarboxed: similar only once the black bars are cut off.</summary>
    private async Task<(SqliteLibraryRepository Repository, MediaPipeline Pipeline, LibraryRoot Root)> PillarboxPairAsync(string name)
    {
        var tools = ResolveBundledFfmpeg();
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var original = Path.Combine(directory, "original.mp4");
        await RunFfmpegAsync(tools, "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=15", "-t", "8", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", original);
        await RunFfmpegAsync(tools, "-i", original, "-vf", "scale=240:180,pad=320:180:40:0", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", Path.Combine(directory, "pillarbox.mp4"));
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var root = await repository.AddOrGetLibraryRootAsync(directory, CancellationToken.None);
        var pipeline = new MediaPipeline(repository, new SqliteJobStore(_paths, _log), new FileSystemMediaScanner(), new FfmpegAdapter(tools, _paths, _log, TimeSpan.FromSeconds(60)), _log,
            resourceAdmission: new AdmitEverything(), paths: _paths);
        await RescanAsync(pipeline, root);
        return (repository, pipeline, root);
    }

    private static async Task RescanAsync(MediaPipeline pipeline, LibraryRoot root)
    {
        await pipeline.EnqueueScanAsync(root.LibraryRootId, CancellationToken.None);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);
    }

    private static async Task<IReadOnlyList<DuplicateGroup>> NearGroupsAsync(SqliteLibraryRepository repository, LibraryRoot root) =>
        (await repository.QueryDuplicateGroupsByRelationAsync(root.LibraryRootId, DuplicateRelationType.NearDuplicate, true, 0, 10, CancellationToken.None)).Items;

    private async Task<string> ScalarTextAsync(string sql, string parameter)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$p", parameter);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>As a catalog judged by the build before black bars were cut off: the pair was rejected and has no group.</summary>
    private const string RejectedBeforeBarsSql = "DELETE FROM near_duplicate_member; DELETE FROM duplicate_group WHERE relation_type='NearDuplicate'; DELETE FROM near_duplicate_recheck; UPDATE near_duplicate_evidence SET status='Rejected'; SELECT 0;";

    [Fact]
    public async Task APairJudgedNotSimilarBeforeBarsWereCutIsJudgedAgainWhenTheVideosArePrepared()
    {
        var (repository, pipeline, root) = await PillarboxPairAsync("recheck");
        using var _ = pipeline;
        var evidenceId = await ScalarTextAsync("SELECT near_duplicate_evidence_id FROM near_duplicate_evidence WHERE threshold_version=$p;", NearDuplicateVerificationProfile.HighPrecisionV1.ThresholdVersion);
        await ScalarAsync(RejectedBeforeBarsSql + " DELETE FROM near_duplicate_prepared_lsh_bucket; DELETE FROM near_duplicate_prepared_frame; DELETE FROM near_duplicate_preparation; SELECT 0;");
        Assert.Empty(await NearGroupsAsync(repository, root));

        await RescanAsync(pipeline, root);

        Assert.Equal(2, Assert.Single(await NearGroupsAsync(repository, root)).CurrentMemberCount);
        Assert.Equal("Verified", await ScalarTextAsync("SELECT status FROM near_duplicate_evidence WHERE near_duplicate_evidence_id=$p;", evidenceId));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_recheck;"));
    }

    [Fact]
    public async Task ALibraryAlreadyPreparedJudgesItsRejectedPairsAgainOnTheNextScan()
    {
        var (repository, pipeline, root) = await PillarboxPairAsync("recheck-prepared");
        using var _ = pipeline;
        await ScalarAsync(RejectedBeforeBarsSql);
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_preparation WHERE status<>'Completed';"));

        await RescanAsync(pipeline, root);

        Assert.Single(await NearGroupsAsync(repository, root));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_recheck;"));
    }

    [Fact]
    public async Task APairStillNotSimilarIsRecordedAndNotJudgedAgain()
    {
        var (repository, pipeline, root) = await PillarboxPairAsync("recheck-unreadable");
        using var _ = pipeline;
        // One video's stored frames could not be read, so the bars cannot be cut: the pair stays as it was.
        await ScalarAsync(RejectedBeforeBarsSql + " DELETE FROM near_duplicate_prepared_frame WHERE media_file_id=(SELECT MIN(media_file_id) FROM near_duplicate_preparation); " +
            "UPDATE near_duplicate_preparation SET status='Unavailable' WHERE media_file_id=(SELECT MIN(media_file_id) FROM near_duplicate_preparation); SELECT 0;");

        await RescanAsync(pipeline, root);
        var checkedAt = await ScalarTextAsync("SELECT checked_at FROM near_duplicate_recheck WHERE preparation_version=$p;", PreparationVersion);
        await RescanAsync(pipeline, root);

        Assert.Empty(await NearGroupsAsync(repository, root));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_evidence WHERE status='Verified';"));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_recheck;"));
        Assert.NotNull(checkedAt);
        Assert.Equal(checkedAt, await ScalarTextAsync("SELECT checked_at FROM near_duplicate_recheck WHERE preparation_version=$p;", PreparationVersion));
    }

    [Fact]
    public async Task ARejectedPairIsJudgedAgainOnlyOncePerPreparationVersion()
    {
        var (_, pipeline, root) = await PillarboxPairAsync("recheck-once");
        using var _ = pipeline;
        // Rejected, and already judged again with the current preparation: the pair stays as it is.
        await ScalarAsync(RejectedBeforeBarsSql.Replace(" SELECT 0;", string.Empty, StringComparison.Ordinal) +
            $" INSERT INTO near_duplicate_recheck(near_duplicate_evidence_id,preparation_version,checked_at) SELECT near_duplicate_evidence_id,'{PreparationVersion}','2026-10-07T00:00:00.0000000+00:00' FROM near_duplicate_evidence; SELECT 0;");

        await RescanAsync(pipeline, root);

        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_evidence WHERE status='Verified';"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(1) FROM duplicate_group WHERE relation_type='NearDuplicate';"));
    }

    [Fact]
    public async Task ASimilarPairIsNotJudgedAgainAndKeepsItsGroupAndDecision()
    {
        var (repository, pipeline, root) = await PillarboxPairAsync("recheck-keep");
        using var _ = pipeline;
        var group = Assert.Single(await NearGroupsAsync(repository, root));
        await repository.UpsertDuplicateJudgmentAsync(group.DuplicateGroupId, null, DuplicateJudgmentDecision.KeepBoth, null, CancellationToken.None);
        var verifiedAt = await ScalarTextAsync("SELECT verified_at FROM near_duplicate_evidence WHERE threshold_version=$p;", NearDuplicateVerificationProfile.HighPrecisionV1.ThresholdVersion);

        await RescanAsync(pipeline, root);

        Assert.Equal(group.DuplicateGroupId, Assert.Single(await NearGroupsAsync(repository, root)).DuplicateGroupId);
        Assert.Contains(await repository.GetDuplicateJudgmentsAsync(group.DuplicateGroupId, CancellationToken.None), judgment => judgment.Decision == DuplicateJudgmentDecision.KeepBoth);
        Assert.Equal(verifiedAt, await ScalarTextAsync("SELECT verified_at FROM near_duplicate_evidence WHERE threshold_version=$p;", NearDuplicateVerificationProfile.HighPrecisionV1.ThresholdVersion));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(1) FROM near_duplicate_recheck;"));
    }
}
