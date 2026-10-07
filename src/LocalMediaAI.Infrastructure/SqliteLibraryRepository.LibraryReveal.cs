using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure;

public sealed partial class SqliteLibraryRepository
{
    // Shares membership, filtering and ordering with the paged Library queries.
    // Only an ordinal is returned: no media/index materialization or path-based navigation.
    public async Task<int?> FindLibraryMediaOffsetAsync(Guid mediaFileId, Guid? rootId, string? search, string? tag, bool includeSuggested, CancellationToken cancellationToken)
    {
        string from, filter;
        List<(string, object?)> parameters;
        if (string.IsNullOrWhiteSpace(tag))
        {
            (filter, parameters) = BuildLibraryTextFilter(rootId, search);
            from = " FROM media_file mf LEFT JOIN media_metadata mm ON mm.media_file_id=mf.media_file_id LEFT JOIN media_recycle_lifecycle mrl ON mrl.media_file_id=mf.media_file_id LEFT JOIN media_permanent_delete_lifecycle mpdl ON mpdl.media_file_id=mf.media_file_id";
        }
        else
        {
            var (predicate, args) = BuildLibraryTagFilter(rootId, tag, includeSuggested);
            filter = " WHERE " + predicate;
            parameters = args.ToList();
            from = " FROM media_file mf LEFT JOIN media_metadata mm ON mm.media_file_id=mf.media_file_id";
        }
        parameters.Add(("$target", mediaFileId.ToString("D")));
        var sql = "WITH visible AS (SELECT mf.media_file_id,ROW_NUMBER() OVER (ORDER BY mf.file_name COLLATE NOCASE,mf.media_file_id)-1 ordinal" + from + filter + ") SELECT ordinal FROM visible WHERE media_file_id=$target;";
        await using var connection = await OpenAsync(cancellationToken);
        var value = await ScalarStringAsync(connection, null, sql, cancellationToken, parameters.ToArray());
        return int.TryParse(value, out var offset) ? offset : null;
    }

    private static (string Filter, List<(string, object?)> Parameters) BuildLibraryTextFilter(Guid? rootId, string? search)
    {
        var where = new List<string> { "mf.presence_state NOT IN ('Missing','Recycled','PermanentlyDeleted')", "mf.cleanup_committed_at IS NULL", "EXISTS(SELECT 1 FROM library_root scope_root WHERE scope_root.library_root_id=mf.library_root_id AND scope_root.registration_state='Active')" };
        var parameters = new List<(string, object?)>();
        if (rootId is not null) { where.Add("mf.library_root_id=$root"); parameters.Add(("$root", rootId.Value.ToString("D"))); }
        if (!string.IsNullOrWhiteSpace(search)) { where.Add("(mf.file_name LIKE $search ESCAPE '\\' OR mf.path LIKE $search ESCAPE '\\' OR mrl.last_known_source_path LIKE $search ESCAPE '\\' OR mpdl.last_known_source_path LIKE $search ESCAPE '\\' OR mm.video_codec LIKE $search ESCAPE '\\')"); parameters.Add(("$search", $"%{EscapeLike(search.Trim())}%")); }
        return (" WHERE " + string.Join(" AND ", where), parameters);
    }

    private static (string Predicate, (string Name, object? Value)[] Args) BuildLibraryTagFilter(Guid? rootId, string canonicalOrTaxonomyTag, bool includeSuggested)
    {
        var tag = canonicalOrTaxonomyTag.Trim();
        var display = TaggingTaxonomy.All.FirstOrDefault(item => string.Equals(item.TagId, tag, StringComparison.Ordinal))?.DisplayName ?? TagNormalization.Display(tag);
        var canonical = TagNormalization.CanonicalizeManualTag(display);
        var predicate = BuildTagFilterPredicate(includeSuggested) + " AND mf.presence_state NOT IN ('Missing','Recycled','PermanentlyDeleted') AND mf.cleanup_committed_at IS NULL AND EXISTS(SELECT 1 FROM library_root sr WHERE sr.library_root_id=mf.library_root_id AND sr.registration_state='Active') AND ($root IS NULL OR mf.library_root_id=$root)";
        var args = new (string Name, object? Value)[] { ("$tag", tag), ("$display", display), ("$canonical", canonical), ("$root", rootId?.ToString("D") ?? (object)DBNull.Value) };
        return (predicate, args);
    }
}
