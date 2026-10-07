namespace LocalMediaAI.App.ViewModels;

public sealed partial class MainViewModel
{
    public string PinnedDuplicateMemberGeneralDisplay => PinnedDuplicateMember is { } member ? L.F("比較対象: {0} | {1}", member.Media.FileName, member.Media.Path) : L.T("比較対象は未指定です。");
    public string PinnedNearDuplicateMemberGeneralDisplay => PinnedNearDuplicateMember is { } member ? L.F("比較対象: {0} | {1}", member.Media.FileName, member.Media.Path) : L.T("比較対象は未指定です。");
    public string PinnedContentSimilarityMemberGeneralDisplay => PinnedContentSimilarityMember is { } member ? L.F("比較対象: {0} | {1}", member.Media.FileName, member.Media.Path) : L.T("比較対象は未指定です。");
}
