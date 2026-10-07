using System.Globalization;
using System.Windows.Data;
using LocalMediaAI.Core;

namespace LocalMediaAI.App;
public sealed class LibraryDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DuplicateRelationType.Exact => L.T("完全一致"), DuplicateRelationType.NearDuplicate => L.T("類似重複"), DuplicateRelationType.ContentSimilar => L.T("内容の類似"),
        DuplicateJudgmentDecision.Unreviewed => L.T("未確認"), DuplicateJudgmentDecision.ConfirmedDuplicate => L.T("重複を確認済み"), DuplicateJudgmentDecision.KeepBoth => L.T("両方を保持"), DuplicateJudgmentDecision.Preferred => L.T("優先ファイルを指定済み"), DuplicateJudgmentDecision.Ignored => L.T("対象外"), DuplicateJudgmentDecision.NotDuplicate => L.T("重複ではない"),
        ContentSimilarityJudgmentDecision.Unreviewed => L.T("未確認"), ContentSimilarityJudgmentDecision.Related => L.T("関連あり"), ContentSimilarityJudgmentDecision.NotRelated => L.T("関連なし"), ContentSimilarityJudgmentDecision.KeepBoth => L.T("両方を保持"), ContentSimilarityJudgmentDecision.Ignored => L.T("対象外"),
        RootAvailability.Online => L.T("利用可能"), RootAvailability.Offline => L.T("オフライン"), RootAvailability.Unknown => L.T("状態を確認できません"),
        PresenceState.Unknown => L.T("状態を確認できません"), PresenceState.Present => L.T("利用可能"), PresenceState.Missing => L.T("見つかりません"), PresenceState.OfflineRoot => L.T("登録フォルダーがオフライン"), PresenceState.AccessDenied => L.T("アクセスできません"), PresenceState.Recycled => L.T("ごみ箱へ移動済み"), PresenceState.PermanentlyDeleted => L.T("完全削除済み"),
        string path => System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), _ => L.T("状態を確認できません")
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
