using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure;

/// <summary>
/// Provides local semantic visual search. Implementations return only in-memory query results
/// and must not transfer SQLite ownership away from C#.
/// </summary>
public interface ISemanticSearchService
{
    SemanticModelProfile SemanticModel { get; }
    SemanticExecutionProfile SemanticExecution { get; }
    Task<IReadOnlyList<SemanticSearchCandidate>> SearchAsync(string query, int topK, CancellationToken cancellationToken, Guid? libraryRootId = null);
}
