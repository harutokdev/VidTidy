using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed partial class UnifiedReviewFoundationTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIUnifiedReviewTests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;
    private bool _disposed;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_root);
        _log = new JsonFileLogger(_paths);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Dispose();
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; Directory.Exists(_root) && attempt < 8; attempt++)
        {
            try { Directory.Delete(_root, true); }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _log?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void StableIdsAndActionCompatibilityExcludePersonAndCrossTypeActions()
    {
        var source = Guid.NewGuid();
        var id = UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.ExactDuplicate, source);
        Assert.Equal("exact:" + source.ToString("D"), id);
        Assert.True(UnifiedReviewPolicy.TryParseReviewItemId(id, out var type, out var parsed));
        Assert.Equal(UnifiedReviewType.ExactDuplicate, type);
        Assert.Equal(source, parsed);
        Assert.False(UnifiedReviewPolicy.IsAllowed(UnifiedReviewType.SuggestedTag, UnifiedReviewAction.Related));
        Assert.False(UnifiedReviewPolicy.IsAllowed(UnifiedReviewType.ContentSimilar, UnifiedReviewAction.ConfirmDuplicate));
        Assert.DoesNotContain("Person", Enum.GetNames<UnifiedReviewType>());
    }

    [Fact]
    public async Task UnifiedProjectionKeepsExactAndTagEvidenceSeparatedAndPrioritized()
    {
        var fixture = await CreateFixtureAsync();
        var exactGroup = await SeedExactGroupAsync(fixture.Repository, fixture.Media[0], fixture.Media[1]);
        var tag = await SeedSuggestedTagAsync(fixture.Repository, fixture.Media[2]);

        var page = await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(fixture.Root.LibraryRootId, UnifiedReviewFilter.All, UnifiedReviewSort.Priority, 0, 100), CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        var exact = Assert.Single(page.Items, item => item.ReviewType == UnifiedReviewType.ExactDuplicate);
        var suggested = Assert.Single(page.Items, item => item.ReviewType == UnifiedReviewType.SuggestedTag);
        Assert.Equal(UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.ExactDuplicate, exactGroup.DuplicateGroupId), exact.ReviewItemId);
        Assert.Equal(UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.SuggestedTag, tag.MediaTagEvidenceId), suggested.ReviewItemId);
        Assert.Contains("SHA-256", exact.ScoreText, StringComparison.Ordinal);
        Assert.Contains("Tag Match", suggested.ScoreText, StringComparison.Ordinal);
        Assert.Equal(ReviewEvidenceState.Current, exact.EvidenceState);
        Assert.Equal(ReviewEvidenceState.Current, suggested.EvidenceState);
        Assert.Equal(UnifiedReviewType.ExactDuplicate, page.Items[0].ReviewType);
        Assert.DoesNotContain("confidence", exact.ScoreText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnifiedDecisionWritesExistingJudgmentAndAppendOnlyHistory()
    {
        var fixture = await CreateFixtureAsync();
        var group = await SeedExactGroupAsync(fixture.Repository, fixture.Media[0], fixture.Media[1]);
        var itemId = UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.ExactDuplicate, group.DuplicateGroupId);

        var first = await fixture.Repository.ApplyUnifiedReviewDecisionAsync(new UnifiedReviewDecisionRequest(itemId, UnifiedReviewAction.KeepBoth, "human reviewed"), CancellationToken.None);
        var second = await fixture.Repository.ApplyUnifiedReviewDecisionAsync(new UnifiedReviewDecisionRequest(itemId, UnifiedReviewAction.ResetToUnreviewed, "reconsider"), CancellationToken.None);

        Assert.Equal("Unreviewed", first.PreviousDecision);
        Assert.Equal("KeepBoth", first.CurrentDecision);
        Assert.Equal("KeepBoth", second.PreviousDecision);
        Assert.Equal("Unreviewed", second.CurrentDecision);
        var groupPage = await fixture.Repository.QueryDuplicateGroupsByRelationAsync(fixture.Root.LibraryRootId, DuplicateRelationType.Exact, true, 0, 10, CancellationToken.None);
        Assert.Equal(DuplicateJudgmentDecision.Unreviewed, Assert.Single(groupPage.Items).ReviewDecision);
        var history = await fixture.Repository.GetUnifiedReviewDecisionHistoryAsync(itemId, CancellationToken.None);
        Assert.Contains(history, entry => entry.EventKind == "DecisionChanged" && entry.CurrentDecision == "KeepBoth");
        Assert.Contains(history, entry => entry.EventKind == "DecisionChanged" && entry.PreviousDecision == "KeepBoth" && entry.CurrentDecision == "Unreviewed");
    }

    [Fact]
    public async Task LibraryScopeRequiresWholeExactRelationAndFiltersSingleMediaWithoutChangingHistory()
    {
        var a = await CreateFixtureAsync();
        var b = await CreateFixtureAsync();
        var aa = await SeedExactGroupAsync(a.Repository, a.Media[0], a.Media[1]);
        var ab = await SeedExactGroupAsync(a.Repository, a.Media[2], b.Media[2], "cross-root fixture");
        var tagA = await SeedSuggestedTagAsync(a.Repository, a.Media[2]);
        var tagB = await SeedSuggestedTagAsync(a.Repository, b.Media[2]);
        var abId = UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.ExactDuplicate, ab.DuplicateGroupId);
        await a.Repository.ApplyUnifiedReviewDecisionAsync(new UnifiedReviewDecisionRequest(abId, UnifiedReviewAction.KeepBoth), CancellationToken.None);
        var historyBefore = await a.Repository.GetUnifiedReviewDecisionHistoryAsync(abId, CancellationToken.None);

        var specific = await a.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(a.Root.LibraryRootId, UnifiedReviewFilter.All, UnifiedReviewSort.Priority, 0, 100), CancellationToken.None);
        Assert.Equal(2, specific.TotalCount);
        Assert.Contains(specific.Items, item => item.EvidenceId == aa.DuplicateGroupId && item.InvolvedMediaCount == 2);
        Assert.Contains(specific.Items, item => item.EvidenceId == tagA.MediaTagEvidenceId);
        Assert.DoesNotContain(specific.Items, item => item.EvidenceId == ab.DuplicateGroupId || item.EvidenceId == tagB.MediaTagEvidenceId);
        var legacy = await a.Repository.QueryDuplicateGroupsByRelationAsync(a.Root.LibraryRootId, null, true, 0, 10, CancellationToken.None);
        Assert.Equal(aa.DuplicateGroupId, Assert.Single(legacy.Items).DuplicateGroupId);
        Assert.Equal(tagA.MediaTagEvidenceId, Assert.Single((await a.Repository.QueryMediaTagReviewAsync(a.Root.LibraryRootId, false, 0, 10, CancellationToken.None)).Items).Evidence.MediaTagEvidenceId);

        var all = await a.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(null, UnifiedReviewFilter.All, UnifiedReviewSort.Priority, 0, 100), CancellationToken.None);
        Assert.Equal(4, all.TotalCount);
        Assert.Contains(all.Items, item => item.EvidenceId == ab.DuplicateGroupId && item.InvolvedMediaCount == 2);
        Assert.Equal(historyBefore, await a.Repository.GetUnifiedReviewDecisionHistoryAsync(abId, CancellationToken.None));
        Assert.Equal(6, (await a.Repository.QueryMediaAsync(null, null, 0, 100, CancellationToken.None)).TotalCount);
        Assert.Equal(2, (await a.Repository.QueryMediaByTagAsync(null, "ocean", true, 0, 100, CancellationToken.None)).TotalCount);
        Assert.Equal(3, (await a.Repository.QueryMediaAsync(a.Root.LibraryRootId, null, 0, 100, CancellationToken.None)).TotalCount);

        await a.Repository.UnregisterRootAsync(b.Root.LibraryRootId, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(3, (await a.Repository.QueryMediaAsync(null, null, 0, 100, CancellationToken.None)).TotalCount);
        Assert.Equal(1, (await a.Repository.QueryMediaByTagAsync(null, "ocean", true, 0, 100, CancellationToken.None)).TotalCount);
        var activeOnly = await a.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(null, UnifiedReviewFilter.All, UnifiedReviewSort.Priority, 0, 100), CancellationToken.None);
        Assert.DoesNotContain(activeOnly.Items, item => item.EvidenceId == ab.DuplicateGroupId || item.EvidenceId == tagB.MediaTagEvidenceId);
    }

    [Fact]
    public async Task LegacyJudgmentIsCapturedByHistoryTriggerAndResumeUsesStableAnchor()
    {
        var fixture = await CreateFixtureAsync();
        var tag = await SeedSuggestedTagAsync(fixture.Repository, fixture.Media[2]);
        await fixture.Repository.UpsertMediaTagJudgmentAsync(tag.MediaTagEvidenceId, MediaTagJudgmentDecision.Confirmed, "legacy workspace", CancellationToken.None);
        var itemId = UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.SuggestedTag, tag.MediaTagEvidenceId);
        var history = await fixture.Repository.GetUnifiedReviewDecisionHistoryAsync(itemId, CancellationToken.None);
        Assert.Contains(history, entry => entry.CurrentDecision == "Confirmed" && entry.EventKind == "DecisionChanged");

        await fixture.Repository.SaveUnifiedReviewResumeStateAsync(new UnifiedReviewResumeState(UnifiedReviewFilter.SuggestedTag, UnifiedReviewSort.CreatedNewest, 100, itemId, DateTimeOffset.MinValue), CancellationToken.None);
        var restored = await fixture.Repository.GetUnifiedReviewResumeStateAsync(CancellationToken.None);
        Assert.Equal(UnifiedReviewFilter.SuggestedTag, restored.Filter);
        Assert.Equal(UnifiedReviewSort.CreatedNewest, restored.Sort);
        Assert.Equal(100, restored.Offset);
        Assert.Equal(itemId, restored.AnchorReviewItemId);
    }

    [Fact]
    public async Task SchemaV7MigrationBackfillsV6HumanDecisionHistoryWithoutChangingCurrentJudgment()
    {
        var fixture = await CreateFixtureAsync();
        var group = await SeedExactGroupAsync(fixture.Repository, fixture.Media[0], fixture.Media[1]);
        await fixture.Repository.UpsertDuplicateJudgmentAsync(group.DuplicateGroupId, null, DuplicateJudgmentDecision.KeepBoth, "v6 judgment", CancellationToken.None);
        SqliteConnection.ClearAllPools();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE IF EXISTS retention_cleanup_item; DROP TABLE IF EXISTS retention_state; DROP TABLE IF EXISTS library_directory; ALTER TABLE library_scan DROP COLUMN registration_revision; ALTER TABLE media_file DROP COLUMN cleanup_committed_at; ALTER TABLE library_root DROP COLUMN registration_revision; ALTER TABLE library_root DROP COLUMN unregistered_at; ALTER TABLE library_root DROP COLUMN root_identity; ALTER TABLE library_root DROP COLUMN root_kind; ALTER TABLE library_root DROP COLUMN registration_state; DELETE FROM schema_migration WHERE version=14;";
            await command.ExecuteNonQueryAsync();
            command.CommandText = "DROP INDEX IF EXISTS ix_batch_operation_audit_batch_time; DROP TABLE IF EXISTS batch_operation_audit; DROP INDEX IF EXISTS ix_batch_item_batch_state; DROP TABLE IF EXISTS batch_item; DROP INDEX IF EXISTS ix_batch_operation_state_time; DROP TABLE IF EXISTS batch_operation; DROP TRIGGER IF EXISTS review_history_duplicate_insert; DROP TRIGGER IF EXISTS review_history_duplicate_update; DROP TRIGGER IF EXISTS review_history_content_insert; DROP TRIGGER IF EXISTS review_history_content_update; DROP TRIGGER IF EXISTS review_history_tag_insert; DROP TRIGGER IF EXISTS review_history_tag_update; DROP INDEX IF EXISTS ix_media_permanent_delete_lifecycle_state_time; DROP TABLE IF EXISTS media_permanent_delete_lifecycle; DROP INDEX IF EXISTS ix_media_recycle_lifecycle_state_time; DROP TABLE IF EXISTS media_recycle_lifecycle; DROP INDEX IF EXISTS ix_file_operation_plan_recycle_state; DROP TABLE IF EXISTS file_operation_audit; DROP TABLE IF EXISTS file_operation_execution; DROP TABLE IF EXISTS file_operation_preflight; DROP TABLE IF EXISTS file_operation_plan; DROP TABLE IF EXISTS file_operation_intent; DROP TABLE IF EXISTS file_operation_proposal; DROP TABLE review_decision_history; DELETE FROM schema_migration WHERE version IN (7,8,9,10,11,12,13);";
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        var migrated = new SqliteLibraryRepository(_paths, _log);
        await migrated.InitializeAsync(CancellationToken.None);
        var itemId = UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.ExactDuplicate, group.DuplicateGroupId);
        var history = await migrated.GetUnifiedReviewDecisionHistoryAsync(itemId, CancellationToken.None);
        Assert.Contains(history, entry => entry.EventKind == "CurrentStateImported" && entry.CurrentDecision == "KeepBoth" && entry.Note == "v6 judgment");
        var groups = await migrated.QueryDuplicateGroupsByRelationAsync(fixture.Root.LibraryRootId, DuplicateRelationType.Exact, true, 0, 10, CancellationToken.None);
        Assert.Equal(DuplicateJudgmentDecision.KeepBoth, Assert.Single(groups.Items).ReviewDecision);
    }

    [Fact]
    public async Task IncompatibleAndUnavailableUnifiedDecisionsAreRejectedWithoutEvidenceDeletion()
    {
        var fixture = await CreateFixtureAsync();
        var tag = await SeedSuggestedTagAsync(fixture.Repository, fixture.Media[2]);
        var itemId = UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.SuggestedTag, tag.MediaTagEvidenceId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.ApplyUnifiedReviewDecisionAsync(new UnifiedReviewDecisionRequest(itemId, UnifiedReviewAction.Related), CancellationToken.None));

        File.Delete(fixture.Media[2].Path);
        await RescanAsync(fixture.Repository, fixture.Root);
        var unavailable = await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(fixture.Root.LibraryRootId, UnifiedReviewFilter.MissingOrOfflineSource, UnifiedReviewSort.Priority, 0, 100), CancellationToken.None);
        var row = Assert.Single(unavailable.Items);
        Assert.Equal(itemId, row.ReviewItemId);
        Assert.Equal(ReviewEvidenceState.SourceUnavailable, row.EvidenceState);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.ApplyUnifiedReviewDecisionAsync(new UnifiedReviewDecisionRequest(itemId, UnifiedReviewAction.ConfirmTag), CancellationToken.None));
        var retained = await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(fixture.Root.LibraryRootId, UnifiedReviewFilter.All, UnifiedReviewSort.Priority, 0, 100), CancellationToken.None);
        Assert.Contains(retained.Items, item => item.ReviewItemId == itemId);
    }

    [Fact]
    public async Task ManualTagSurvivesUnifiedSuggestedTagDecision()
    {
        var fixture = await CreateFixtureAsync();
        var tag = await SeedSuggestedTagAsync(fixture.Repository, fixture.Media[2]);
        var manual = await fixture.Repository.AddManualMediaTagAsync(fixture.Media[2].MediaFileId, "家族旅行", "human metadata", CancellationToken.None);
        var itemId = UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.SuggestedTag, tag.MediaTagEvidenceId);
        await fixture.Repository.ApplyUnifiedReviewDecisionAsync(new UnifiedReviewDecisionRequest(itemId, UnifiedReviewAction.RejectTag, "AI suggestion rejected"), CancellationToken.None);
        var preserved = await fixture.Repository.GetManualMediaTagsAsync(fixture.Media[2].MediaFileId, CancellationToken.None);
        Assert.Contains(preserved, value => value.ManualMediaTagId == manual.ManualMediaTagId && value.DisplayTag == "家族旅行");
        var history = await fixture.Repository.GetUnifiedReviewDecisionHistoryAsync(itemId, CancellationToken.None);
        Assert.Contains(history, entry => entry.CurrentDecision == "Rejected");
    }

    [Fact]
    public async Task OfflineRootStateRetainsHumanDecisionAndHistoryWithoutMassDeletion()
    {
        var fixture = await CreateFixtureAsync();
        var group = await SeedExactGroupAsync(fixture.Repository, fixture.Media[0], fixture.Media[1]);
        var itemId = UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.ExactDuplicate, group.DuplicateGroupId);
        await fixture.Repository.ApplyUnifiedReviewDecisionAsync(new UnifiedReviewDecisionRequest(itemId, UnifiedReviewAction.KeepBoth, "offline retention"), CancellationToken.None);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE media_file SET presence_state='OfflineRoot' WHERE library_root_id=$root;";
            command.Parameters.AddWithValue("$root", fixture.Root.LibraryRootId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }
        var offline = await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(fixture.Root.LibraryRootId, UnifiedReviewFilter.MissingOrOfflineSource, UnifiedReviewSort.Priority, 0, 100), CancellationToken.None);
        var row = Assert.Single(offline.Items);
        Assert.Equal(itemId, row.ReviewItemId);
        Assert.Equal(ReviewEvidenceState.SourceUnavailable, row.EvidenceState);
        Assert.Equal("KeepBoth", row.HumanDecision);
        var history = await fixture.Repository.GetUnifiedReviewDecisionHistoryAsync(itemId, CancellationToken.None);
        Assert.Contains(history, entry => entry.CurrentDecision == "KeepBoth");
    }

    [Fact]
    public async Task QueryIsPagedAndFilterNeverChangesStableIdentity()
    {
        var fixture = await CreateFixtureAsync();
        await SeedExactGroupAsync(fixture.Repository, fixture.Media[0], fixture.Media[1]);
        var tag = await SeedSuggestedTagAsync(fixture.Repository, fixture.Media[2]);
        var all = await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(fixture.Root.LibraryRootId, UnifiedReviewFilter.All, UnifiedReviewSort.Priority, 0, 1), CancellationToken.None);
        var tags = await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(fixture.Root.LibraryRootId, UnifiedReviewFilter.SuggestedTag, UnifiedReviewSort.Priority, 0, 10), CancellationToken.None);
        Assert.Equal(2, all.TotalCount);
        Assert.Single(all.Items);
        Assert.Equal(1, tags.TotalCount);
        Assert.Equal(UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.SuggestedTag, tag.MediaTagEvidenceId), Assert.Single(tags.Items).ReviewItemId);
    }

    [Fact]
    public async Task WindowCountPreservesNormalFilteredAndOutOfRangePageTotals()
    {
        var fixture = await CreateFixtureAsync();
        await SeedExactGroupAsync(fixture.Repository, fixture.Media[0], fixture.Media[1]);
        var firstTag = await SeedSuggestedTagAsync(fixture.Repository, fixture.Media[2], "ocean", "海");
        var secondTag = await SeedSuggestedTagAsync(fixture.Repository, fixture.Media[2], "mountain", "山");

        var normal = await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(fixture.Root.LibraryRootId, UnifiedReviewFilter.All, UnifiedReviewSort.Priority, 0, 2), CancellationToken.None);
        var filtered = await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(fixture.Root.LibraryRootId, UnifiedReviewFilter.SuggestedTag, UnifiedReviewSort.Priority, 0, 1), CancellationToken.None);
        var outOfRange = await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(fixture.Root.LibraryRootId, UnifiedReviewFilter.All, UnifiedReviewSort.Priority, 99, 2), CancellationToken.None);

        Assert.Equal(3, normal.TotalCount);
        Assert.Equal(2, normal.Items.Count);
        Assert.Equal(2, filtered.TotalCount);
        Assert.Equal(UnifiedReviewType.SuggestedTag, filtered.Items[0].ReviewType);
        Assert.Contains(filtered.Items[0].ReviewItemId, new[]
        {
            UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.SuggestedTag, firstTag.MediaTagEvidenceId),
            UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.SuggestedTag, secondTag.MediaTagEvidenceId)
        });
        Assert.Equal(3, outOfRange.TotalCount);
        Assert.Empty(outOfRange.Items);
        Assert.Contains(normal.Items, item => item.ReviewType == UnifiedReviewType.ExactDuplicate);
    }

    [Fact]
    public async Task UnifiedReviewOperationLatencySanity()
    {
        var fixture = await CreateFixtureAsync();
        var group = await SeedExactGroupAsync(fixture.Repository, fixture.Media[0], fixture.Media[1]);
        var firstTag = await SeedSuggestedTagAsync(fixture.Repository, fixture.Media[2], "ocean", "海");
        var secondTag = await SeedSuggestedTagAsync(fixture.Repository, fixture.Media[2], "mountain", "山");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var initial = await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(fixture.Root.LibraryRootId, UnifiedReviewFilter.All, UnifiedReviewSort.Priority, 0, 100), CancellationToken.None);
        var initialMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var filtered = await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(fixture.Root.LibraryRootId, UnifiedReviewFilter.SuggestedTag, UnifiedReviewSort.Priority, 0, 1), CancellationToken.None);
        var filterAndPageMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        await fixture.Repository.ApplyUnifiedReviewDecisionAsync(new UnifiedReviewDecisionRequest(UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.ExactDuplicate, group.DuplicateGroupId), UnifiedReviewAction.KeepBoth, "latency"), CancellationToken.None);
        var decisionMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        foreach (var evidence in new[] { firstTag, secondTag })
            await fixture.Repository.ApplyUnifiedReviewDecisionAsync(new UnifiedReviewDecisionRequest(UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.SuggestedTag, evidence.MediaTagEvidenceId), UnifiedReviewAction.ConfirmTag, "same-type bulk"), CancellationToken.None);
        var bulkMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        await fixture.Repository.SaveUnifiedReviewResumeStateAsync(new UnifiedReviewResumeState(UnifiedReviewFilter.SuggestedTag, UnifiedReviewSort.Priority, 0, UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.SuggestedTag, firstTag.MediaTagEvidenceId), DateTimeOffset.UtcNow), CancellationToken.None);
        var resume = await fixture.Repository.GetUnifiedReviewResumeStateAsync(CancellationToken.None);
        var resumeMs = timer.Elapsed.TotalMilliseconds;
        Assert.Equal(3, initial.TotalCount);
        Assert.Single(filtered.Items);
        Assert.Equal(UnifiedReviewFilter.SuggestedTag, resume.Filter);
        Assert.Equal(UnifiedReviewPolicy.CreateReviewItemId(UnifiedReviewType.SuggestedTag, firstTag.MediaTagEvidenceId), resume.AnchorReviewItemId);
        Console.WriteLine($"PHASE56_UNIFIED_REVIEW_OPERATIONS: initial={initialMs:F1}ms; filter-plus-page={filterAndPageMs:F1}ms; decision-save={decisionMs:F1}ms; same-type-2-item-bulk={bulkMs:F1}ms; resume-save-load={resumeMs:F1}ms");
    }

    [Fact]
    public async Task SyntheticTenThousandAndOneHundredThousandReviewQueryPagingSanity()
    {
        var fixture = await CreateFixtureAsync();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await SeedSyntheticSuggestedTagEvidenceAsync(fixture.Media[2], 0, 10_000);
        var tenThousand = await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(fixture.Root.LibraryRootId, UnifiedReviewFilter.Unreviewed, UnifiedReviewSort.Priority, 0, 100), CancellationToken.None);
        var tenThousandElapsed = stopwatch.Elapsed;
        Assert.Equal(10_000, tenThousand.TotalCount);
        Assert.Equal(100, tenThousand.Items.Count);

        stopwatch.Restart();
        await SeedSyntheticSuggestedTagEvidenceAsync(fixture.Media[2], 10_000, 90_000);
        var oneHundredThousand = await fixture.Repository.QueryUnifiedReviewAsync(new UnifiedReviewQuery(fixture.Root.LibraryRootId, UnifiedReviewFilter.SuggestedTag, UnifiedReviewSort.Priority, 99_900, 100), CancellationToken.None);
        var oneHundredThousandElapsed = stopwatch.Elapsed;
        Assert.Equal(100_000, oneHundredThousand.TotalCount);
        Assert.Equal(100, oneHundredThousand.Items.Count);
        Assert.All(oneHundredThousand.Items, item => Assert.Equal(UnifiedReviewType.SuggestedTag, item.ReviewType));
        Console.WriteLine($"PHASE56_UNIFIED_REVIEW_SCALE: 10k-query={tenThousandElapsed.TotalMilliseconds:F1}ms; 90k-insert-plus-100k-query={oneHundredThousandElapsed.TotalMilliseconds:F1}ms; page={oneHundredThousand.Items.Count}; total={oneHundredThousand.TotalCount}");
    }

    private async Task SeedSyntheticSuggestedTagEvidenceAsync(MediaFile media, int startInclusive, int count)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString());
        await connection.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO media_tag_evidence(media_tag_evidence_id,media_file_id,media_content_id,representative_visual_sample_id,taxonomy_version,tag_id,display_name,group_name,source,model_name,model_revision,processor_version,dtype,dimension,source_sampling_version,text_representation_version,aggregation_version,threshold_version,score_metric,maximum_score,mean_score,matched_sample_count,completed_sample_count,expected_sample_count,coverage_ratio,source_embedding_digest,status,error_code,error_message,created_at) VALUES($id,$file,$content,NULL,'synthetic-v1',$tag,$display,'Synthetic','EmbeddingZeroShot','synthetic-model','synthetic-revision','synthetic-processor','float32',4,'fixed-interval-v1','tag-descriptive-v1','media-sample-max-mean-v1','synthetic-threshold','Cosine similarity',0.90,0.80,1,1,1,1.0,$digest,'Suggested',NULL,NULL,$created);";
        command.Parameters.Add("$id", SqliteType.Text);
        command.Parameters.Add("$file", SqliteType.Text);
        command.Parameters.Add("$content", SqliteType.Text);
        command.Parameters.Add("$tag", SqliteType.Text);
        command.Parameters.Add("$display", SqliteType.Text);
        command.Parameters.Add("$digest", SqliteType.Text);
        command.Parameters.Add("$created", SqliteType.Text);
        var now = DateTimeOffset.UtcNow.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        for (var index = startInclusive; index < startInclusive + count; index++)
        {
            command.Parameters["$id"].Value = Guid.NewGuid().ToString("D");
            command.Parameters["$file"].Value = media.MediaFileId.ToString("D");
            command.Parameters["$content"].Value = media.MediaContentId.ToString("D");
            command.Parameters["$tag"].Value = "synthetic-" + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters["$display"].Value = "Synthetic " + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters["$digest"].Value = "synthetic-digest-" + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters["$created"].Value = now;
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    private async Task<(SqliteLibraryRepository Repository, LibraryRoot Root, MediaFile[] Media)> CreateFixtureAsync()
    {
        var mediaDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mediaDirectory);
        await File.WriteAllBytesAsync(Path.Combine(mediaDirectory, "a.mp4"), Pattern(4096, 1));
        await File.WriteAllBytesAsync(Path.Combine(mediaDirectory, "b.mp4"), Pattern(4096, 1));
        await File.WriteAllBytesAsync(Path.Combine(mediaDirectory, "c.mp4"), Pattern(4096, 3));
        var repository = new SqliteLibraryRepository(_paths, _log);
        await repository.InitializeAsync(CancellationToken.None);
        var root = await repository.AddOrGetLibraryRootAsync(mediaDirectory, CancellationToken.None);
        await RescanAsync(repository, root);
        var page = await repository.QueryMediaAsync(root.LibraryRootId, null, 0, 100, CancellationToken.None);
        var media = new List<MediaFile>();
        foreach (var item in page.Items) media.Add((await repository.GetMediaFileAsync(item.MediaFileId, CancellationToken.None))!);
        return (repository, root, media.OrderBy(value => value.FileName, StringComparer.Ordinal).ToArray());
    }

    private static async Task RescanAsync(SqliteLibraryRepository repository, LibraryRoot root)
    {
        var scanner = new FileSystemMediaScanner();
        var scan = await repository.StartScanAsync(root.LibraryRootId, CancellationToken.None);
        var count = 0;
        await foreach (var file in scanner.EnumerateAsync(root, CancellationToken.None)) { await repository.UpsertScannedFileAsync(file, scan, CancellationToken.None); count++; }
        await repository.CompleteScanAsync(scan, new ScanCompletion("Completed", count, count, 0, 0, 0, DateTimeOffset.UtcNow), CancellationToken.None);
        await repository.MarkAbsentFilesAfterCompletedScanAsync(root.LibraryRootId, scan, CancellationToken.None);
    }

    private static async Task<DuplicateGroup> SeedExactGroupAsync(SqliteLibraryRepository repository, MediaFile first, MediaFile second, string digestMarker = "shared exact fixture")
    {
        var digest = AnalysisKey.Sha256(digestMarker);
        foreach (var media in new[] { first, second })
        {
            Assert.True(await repository.RecordFullHashEvidenceAsync(new FileHashEvidence(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, "SHA-256", digest, media.SizeBytes, media.LastWriteTimeUtc, "full-sha256-v1", "Verified", null, null, DateTimeOffset.UtcNow), CancellationToken.None));
        }
        await repository.RebuildExactDuplicateGroupsAsync(CancellationToken.None);
        var page = await repository.QueryDuplicateGroupsByRelationAsync(null, DuplicateRelationType.Exact, true, 0, 10, CancellationToken.None);
        return Assert.Single(page.Items, group => group.EvidenceDigest == digest);
    }

    private static async Task<MediaTagEvidence> SeedSuggestedTagAsync(SqliteLibraryRepository repository, MediaFile media, string tagId = "ocean", string displayName = "海")
    {
        var evidence = new MediaTagEvidence(Guid.NewGuid(), media.MediaFileId, media.MediaContentId, null, TaggingTaxonomy.TaxonomyVersion, tagId, displayName, "Environment", MediaTagSource.EmbeddingZeroShot, "fixture-model", "fixture-revision", "fixture-processor", "float32", 4, "fixed-interval-v1", TaggingTaxonomy.DescriptiveTextRepresentationVersion, "media-sample-max-mean-v1", "tagging-embedding-zero-shot-v1-high-precision", "Cosine similarity", 0.9d, 0.8d, 2, 2, 2, 1d, AnalysisKey.Sha256(media.MediaFileId.ToString("N") + tagId), MediaTagEvidenceStatus.Suggested, null, null, DateTimeOffset.UtcNow);
        Assert.True(await repository.UpsertMediaTagEvidenceAsync(evidence, CancellationToken.None));
        return evidence;
    }

    private static byte[] Pattern(int length, int salt) => Enumerable.Range(0, length).Select(index => (byte)((index * 31 + salt) % 251)).ToArray();
}
