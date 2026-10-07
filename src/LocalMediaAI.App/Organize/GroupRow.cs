using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.App.Organize;

/// <summary>One duplicate group in the organize list, with the file chosen to keep.</summary>
public sealed class GroupRow : ViewModelBase
{
    private Guid _keepId;
    private bool _isManual;
    private GroupStatus _status = GroupStatus.Open;
    private bool _isReady = true;

    public GroupRow(OrganizeGroupData data, Guid keepId)
    {
        Data = data;
        _keepId = keepId;
    }

    public OrganizeGroupData Data { get; }
    public Guid GroupId => Data.GroupId;
    public GroupCategory Category => Data.Category;
    public CleanupKind QueueKind => Category == GroupCategory.Exact ? CleanupKind.Exact : CleanupKind.Similar;
    public IReadOnlyList<OrganizeFile> Files => Data.Files;
    public int MatchPercent => (int)Math.Round((Data.MatchRatio ?? 1) * 100);

    public Guid KeepId
    {
        get => _keepId;
        internal set { if (Set(ref _keepId, value)) RaiseDerived(); }
    }

    public bool IsManual { get => _isManual; internal set => Set(ref _isManual, value); }

    public GroupStatus Status
    {
        get => _status;
        internal set { if (Set(ref _status, value)) Raise(nameof(IsOpen)); }
    }

    public bool IsOpen => Status == GroupStatus.Open;

    /// <summary>False while any file sits on a drive that is not connected.</summary>
    public bool IsReady { get => _isReady; internal set => Set(ref _isReady, value); }

    public OrganizeFile Keep => Files.First(f => f.MediaFileId == KeepId);
    public IReadOnlyList<OrganizeFile> CleanupFiles => Files.Where(f => f.MediaFileId != KeepId).ToArray();
    public long CleanupBytes => CleanupFiles.Sum(f => f.SizeBytes);
    public string CleanupSizeText => MediaDisplayFormat.FileSize(CleanupBytes);

    /// <summary>"3つが完全に同じ・819 MB 片付けられる" or "一致率 92%・1.2 GB 片付けられる".</summary>
    public string HeadText => Category == GroupCategory.Exact
        ? L.F("{0}つが完全に同じ・{1} 片付けられる", Files.Count, CleanupSizeText)
        : L.F("一致率 {0}%・{1} 片付けられる", MatchPercent, CleanupSizeText);

    public GroupCleanupPlan ToPlan() => new(Keep.ToCandidate(), CleanupFiles.Select(f => f.ToCandidate()).ToArray());

    /// <summary>A similar group whose choice is not in doubt (see <see cref="ClearSimilarRule"/>); it can be approved with others.</summary>
    public bool IsClear => Category == GroupCategory.Similar
        && ClearSimilarRule.IsClear(Data.MatchRatio, Facts(Keep), CleanupFiles.Select(Facts).ToArray());

    /// <summary>True when the group went into the cleanup list through 「まとめて承認」, so its files need no check one by one.</summary>
    public bool IsBulkApproved { get; internal set; }

    private bool _isGoalLine;
    private long _listBytes;

    /// <summary>The cleanup list plus the groups down to this one reach the space goal; the list draws a line under it.</summary>
    public bool IsGoalLine { get => _isGoalLine; internal set => Set(ref _isGoalLine, value); }

    /// <summary>Space this group frees on the goal's drive (all drives without a goal), shown on its row.</summary>
    public long ListBytes { get => _listBytes; internal set { if (Set(ref _listBytes, value)) Raise(nameof(ListSizeText)); } }

    public string ListSizeText => MediaDisplayFormat.FileSize(ListBytes);

    /// <summary>"×3" for identical copies, "一致率 97%" for similar videos.</summary>
    public string CountText => Category == GroupCategory.Exact ? $"×{Files.Count}" : L.F("一致率 {0}%", MatchPercent);

    /// <summary>Resolution and length of the kept file; identical copies share them.</summary>
    public string KeepFactsText => string.Join(L.T("・"), new[] { Keep.Width is null ? null : Keep.ResolutionText, Keep.DurationSeconds is null ? null : Keep.DurationText }.Where(x => x is not null));

    /// <summary>Where each file is: the kept one first, then the ones cleaned up. Identical copies differ only in this.</summary>
    public IReadOnlyList<LocationChip> Locations =>
        [new LocationChip(true, FolderNameOf(Keep), false), .. CleanupFiles.Select(f => new LocationChip(false, FolderNameOf(f), f.RootId == Keep.RootId))];

    /// <summary>Full paths for the row's tooltip.</summary>
    public string PathsText => string.Join(Environment.NewLine, Files.Select(f => $"{(f.MediaFileId == KeepId ? L.T("残す") : L.T("片付ける"))}: {f.Path}"));

    private static VideoFacts Facts(OrganizeFile file) => new(file.PixelCount, file.Bitrate, file.DurationSeconds);

    /// <summary>The registered folder's name: the first part of "整理済み ＞ 旅行".</summary>
    private static string FolderNameOf(OrganizeFile file) => file.Location.Split(L.T(" ＞ "))[0];

    /// <summary>Each file with its letter (A, B, ...) and whether it is the one kept.</summary>
    public IReadOnlyList<FileChoice> Choices => Files.Select((f, i) => new FileChoice(this, f, ((char)('A' + i)).ToString(), f.MediaFileId == KeepId)).ToArray();

    /// <summary>Side-by-side facts for similar groups. The better value is flagged only when the files differ.</summary>
    public IReadOnlyList<CompareRow> CompareRows =>
    [
        Compare(L.T("解像度"), f => f.PixelCount, f => f.ResolutionText),
        Compare(L.T("ビットレート"), f => f.Bitrate ?? 0, f => f.BitrateText),
        Compare(L.T("長さ"), f => (long)Math.Round(f.DurationSeconds ?? 0), f => f.DurationText),
        new(L.T("サイズ"), Files.Select(f => new CompareCell(f.SizeText, false)).ToArray()),
        new(L.T("形式"), Files.Select(f => new CompareCell(f.FormatText, false)).ToArray()),
        new(L.T("更新日"), Files.Select(f => new CompareCell(f.DateText, false)).ToArray())
    ];

    public IReadOnlyList<string> Warnings
    {
        get
        {
            var list = new List<string>();
            var durations = Files.Select(f => f.DurationSeconds ?? 0).ToArray();
            var spread = durations.Max() - durations.Min();
            if (LengthText is { } length) list.Add(length);
            else if (spread > 5) list.Add(L.F("長さが最大 {0} 違います。一部にしか無い場面があるかもしれません。", MediaDisplayFormat.Duration(spread)));
            if (Files.Select(f => f.ResolutionText).Distinct().Count() > 1) list.Add(L.T("解像度が違います。"));
            return list;
        }
    }

    /// <summary>"B の方が長く、最初の 0:03 と 2:00〜3:00 が A にはありません。" for a copy of different length.</summary>
    public string? LengthText
    {
        get
        {
            if (Data.Length is not { } length || Files.Count != 2) return null;
            var longer = Files.ToList().FindIndex(f => f.MediaFileId == length.LongerFileId);
            if (longer < 0) return null;
            var (longerLetter, shorterLetter) = (((char)('A' + longer)).ToString(), ((char)('A' + (1 - longer))).ToString());
            var total = Files[longer].DurationSeconds ?? double.MaxValue;
            var parts = length.ExtraInLonger.Select(span => span.StartSeconds < 0.5 ? L.F("最初の {0}", MediaDisplayFormat.Duration(span.EndSeconds))
                : span.EndSeconds >= total - 0.5 ? L.F("最後の {0}", MediaDisplayFormat.Duration(span.EndSeconds - span.StartSeconds))
                : L.F("{0}〜{1}", MediaDisplayFormat.Duration(span.StartSeconds), MediaDisplayFormat.Duration(span.EndSeconds))).ToArray();
            return parts.Length == 0
                ? L.F("{0} の方が長いですが、{1} にない場面は見つかりませんでした。", longerLetter, shorterLetter)
                : L.F("{0} の方が長く、{1} が {2} にはありません。", longerLetter, string.Join(L.T(" と "), parts), shorterLetter);
        }
    }

    private CompareRow Compare(string label, Func<OrganizeFile, long> value, Func<OrganizeFile, string> text)
    {
        var values = Files.Select(value).ToArray();
        var best = values.Max();
        var differs = Files.Select(text).Distinct().Count() > 1; // only mark a winner the user can actually see
        return new(label, Files.Select((f, i) => new CompareCell(text(f), differs && values[i] == best)).ToArray());
    }

    private void RaiseDerived()
    {
        Raise(nameof(Choices));
        Raise(nameof(Keep));
        Raise(nameof(CleanupFiles));
        Raise(nameof(CleanupBytes));
        Raise(nameof(CleanupSizeText));
        Raise(nameof(HeadText));
        Raise(nameof(IsClear));
        Raise(nameof(KeepFactsText));
        Raise(nameof(Locations));
        Raise(nameof(PathsText));
    }
}

/// <summary>One place on a one-line group row: 「残す 整理済み」 or 「片付ける ダウンロード（同じフォルダー）」.</summary>
public sealed record LocationChip(bool IsKeep, string Folder, bool SameFolder)
{
    public string Role => IsKeep ? L.T("残す") : L.T("片付ける");
    public string Text => SameFolder ? Folder + L.T("（同じフォルダー）") : Folder;
}

public sealed record FileChoice(GroupRow Group, OrganizeFile File, string Letter, bool IsKeep);

public sealed record CompareCell(string Text, bool IsBest);

public sealed record CompareRow(string Label, IReadOnlyList<CompareCell> Cells);

/// <summary>A registered folder in the side panel. Rank 1 is kept first among exact duplicates.</summary>
public sealed class FolderRow(OrganizeFolder folder, int rank) : ViewModelBase
{
    private int _rank = rank;
    public OrganizeFolder Folder { get; } = folder;
    public Guid Id => Folder.Id;
    public string Name => Folder.Name;
    public string Path => Folder.Path;
    public bool IsOnline => Folder.IsOnline;
    public int Rank { get => _rank; internal set => Set(ref _rank, value); }

    private string? _analysisText;
    private double _analysisPercent;

    /// <summary>"解析中 42%・残り約3分" while this folder is being analysed; null otherwise.</summary>
    public string? AnalysisText { get => _analysisText; internal set { if (Set(ref _analysisText, value)) Raise(nameof(IsAnalyzing)); } }
    public double AnalysisPercent { get => _analysisPercent; internal set => Set(ref _analysisPercent, value); }
    public bool IsAnalyzing => AnalysisText is not null;

    /// <summary>"D:" for D:\動画.</summary>
    public string Drive => (System.IO.Path.GetPathRoot(Path) ?? string.Empty).TrimEnd('\\');

    public string StatusText => IsOnline
        ? L.F("{0}本・解析済み", Folder.FileCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture))
        : L.F("接続されていません（{0}）", Drive);
}
