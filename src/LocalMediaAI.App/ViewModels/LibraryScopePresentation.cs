namespace LocalMediaAI.App.ViewModels;

/// <summary>Session-only view scope; null identity means all active registered roots.</summary>
public sealed record LibraryScopePresentation(Guid? LibraryRootId, string DisplayLabel)
{
    // Made on each use so the label follows the display language (records compare by value).
    public static LibraryScopePresentation All => new(null, L.T("すべての登録フォルダー"));
}
