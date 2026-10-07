namespace LocalMediaAI.Infrastructure;

internal enum LibraryWideRefresh { ExactGroups, NearDuplicate, ContentSimilarity }

/// <summary>
/// Decides when a library-wide check runs. Its cost grows with the library, so running it after every file makes a
/// large first analysis quadratic: while more work of the same kind is queued it is deferred (at most one run per
/// interval meanwhile) and the pass end flushes whatever is due. Every kind starts due, so a deferral lost by closing
/// the app is caught up on the first pass of the next session.
/// </summary>
internal sealed class LibraryWideRefreshGate(Func<DateTimeOffset> clock, TimeSpan interval)
{
    private readonly object _gate = new();
    private readonly HashSet<LibraryWideRefresh> _due = [.. Enum.GetValues<LibraryWideRefresh>()];
    private readonly Dictionary<LibraryWideRefresh, DateTimeOffset> _lastRun = [];

    /// <summary>True to run now; false when the check was only marked due.</summary>
    public bool ShouldRunNow(LibraryWideRefresh kind, bool moreQueued)
    {
        lock (_gate)
        {
            var recent = _lastRun.TryGetValue(kind, out var last) && clock() - last < interval;
            if (moreQueued && recent)
            {
                _due.Add(kind);
                return false;
            }
            return true;
        }
    }

    public void Started(LibraryWideRefresh kind)
    {
        lock (_gate)
        {
            _due.Remove(kind);
            _lastRun[kind] = clock();
        }
    }

    public void Failed(LibraryWideRefresh kind)
    {
        lock (_gate) _due.Add(kind);
    }

    public IReadOnlyList<LibraryWideRefresh> Due()
    {
        lock (_gate) return [.. _due.OrderBy(kind => kind)];
    }
}
