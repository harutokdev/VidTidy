using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.App.Organize;

/// <summary>Right-side drawers (片付け, すべてのファイル, 解析できなかったファイル) and folder actions.</summary>
public partial class OrganizeView
{
    private bool _priorityShown;

    // ---------- 片付け ----------
    private void OpenCleanup_OnClick(object sender, RoutedEventArgs e) => ShowCleanupDrawer();

    private void ShowCleanupDrawer()
    {
        if (Vm is null) return;
        OpenDrawer(L.T("片付け"));
        AddQueueSection();
        AddDestinationSection();
        AddDriveSection();
        AddJudgmentSection();
        AddHistorySection();
    }

    private void AddQueueSection()
    {
        var pill = PillOf(L.F("完全に同じ {0}/{1}件・ほか {2}/{3}件・{4}", Vm!.QueueExactFileCount, Vm.ExactLimit, Vm.QueueOtherFileCount, Vm.OtherLimit, MediaDisplayFormat.FileSize(Vm.QueueBytes)));
        var clear = Vm.QueueFileCount > 0 ? SmallGhost(L.T("すべて外す"), () => { Vm.ClearQueue(); RenderDetail(); ShowCleanupDrawer(); }) : null;
        var section = DrawerSection(L.T("今回の整理リスト"), pill, clear);
        if (Vm.QueueFileCount == 0)
        {
            section.Children.Add(Muted(L.T("まだ何も入っていません。重複の一覧で残す方を選ぶと、ここに入ります。"), 13));
            return;
        }
        foreach (var group in Vm.QueuedGroups)
            foreach (var file in group.CleanupFiles)
                section.Children.Add(QueueRow(file, Mark(group.Category), () => { Vm.Dequeue(group); RenderDetail(); ShowCleanupDrawer(); }));
        foreach (var file in Vm.QueuedSingles.ToArray())
            section.Children.Add(QueueRow(file, PillOf(L.T("個別")), () => { Vm.DequeueSingle(file.MediaFileId); ShowCleanupDrawer(); }));
    }

    private DockPanel QueueRow(OrganizeFile file, UIElement tag, Action remove)
    {
        var row = new DockPanel();
        var button = SmallGhost(L.T("外す"), remove);
        DockPanel.SetDock(button, Dock.Right);
        row.Children.Add(button);
        row.Children.Add(FileLine(file, tag));
        return row;
    }

    private void AddDestinationSection()
    {
        var section = DrawerSection(L.T("送り先"));
        var recycle = new StackPanel();
        var title = new TextBlock();
        title.Inlines.Add(new Run(L.T("Windowsのごみ箱へ移動 ")));
        var open = new Hyperlink(new Run(L.T("ごみ箱を開く"))) { Foreground = Res("AccentHi"), TextDecorations = null };
        open.Click += (_, _) => OpenRecycleBin();
        title.Inlines.Add(open);
        recycle.Children.Add(title);
        recycle.Children.Add(new TextBlock { Text = L.T("ごみ箱から元に戻せます。ドライブの容量は、ごみ箱を空にしたときに空きます。"), FontSize = 12, Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap });
        section.Children.Add(DestOption(recycle, true, true));
        var vault = new StackPanel();
        var vaultTitle = new StackPanel { Orientation = Orientation.Horizontal };
        vaultTitle.Children.Add(new TextBlock { Text = L.T("保管フォルダーへ移動") });
        vaultTitle.Children.Add(PillOf(L.T("準備中")));
        vault.Children.Add(vaultTitle);
        vault.Children.Add(new TextBlock { Text = L.T("すぐには消さず、指定したフォルダーにまとめて置く方法です。この機能はこれから作ります。"), FontSize = 12, Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap });
        section.Children.Add(DestOption(vault, false, false));
    }

    private Border DestOption(UIElement content, bool selected, bool enabled)
    {
        var radio = new RadioButton { IsChecked = selected, IsEnabled = enabled, GroupName = "dest", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 10, 0) };
        var panel = new DockPanel();
        DockPanel.SetDock(radio, Dock.Left);
        panel.Children.Add(radio);
        panel.Children.Add(content);
        return new Border
        {
            Child = panel, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = selected ? Res("Accent") : Res("Line2"), Background = selected ? Hex("#1A2A3C") : System.Windows.Media.Brushes.Transparent, Opacity = enabled ? 1 : 0.85
        };
    }

    private void AddDriveSection()
    {
        var section = DrawerSection(L.T("ドライブの空き（目安）"));
        section.Children.Add(new ItemsControl { ItemsSource = Vm!.Drives, ItemTemplate = (DataTemplate)FindResource("DriveItem"), Focusable = false });
        section.Children.Add(Note(L.T("ごみ箱の中の量は、Windowsのごみ箱から読み取った実際の値です。「空にすると」の数字は、ごみ箱を空にしたときの目安です。"), false));
    }

    private void AddJudgmentSection()
    {
        var section = DrawerSection(L.T("判断の記録"), PillOf(L.F("{0}件", Vm!.Judgments.Count)));
        if (Vm.Judgments.Count == 0)
        {
            section.Children.Add(Muted(L.T("「重複ではない」などの判断をすると、ここに記録されます。あとから取り消せます。"), 13));
            return;
        }
        foreach (var record in Vm.Judgments.ToArray())
        {
            var undo = SmallGhost(L.T("取り消す"), async () =>
            {
                try { await Vm.UndoJudgmentAsync(record, CancellationToken.None); RefreshChrome(); ShowCleanupDrawer(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { Toast(L.T("取り消せませんでした")); }
            });
            var row = new DockPanel();
            DockPanel.SetDock(undo, Dock.Right);
            row.Children.Add(undo);
            row.Children.Add(FileLine(record.Group.Files[0], Mark(record.Group.Category), PillOf(record.Label)));
            section.Children.Add(row);
        }
        section.Children.Add(Note(L.T("取り消すと、その組は重複の一覧に戻ります。"), false));
    }

    private void AddHistorySection()
    {
        var section = DrawerSection(L.T("これまでの片付け"), PillOf(L.F("合計 {0}", MediaDisplayFormat.FileSize(Vm!.CleanedTotalBytes))));
        if (Vm.History.Count == 0)
        {
            section.Children.Add(Muted(L.T("まだ片付けた記録はありません。"), 13));
            return;
        }
        var table = new Grid();
        foreach (var width in new[] { 1.4, 0.7, 0.9, 1.4, 1.2 }) table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        string[] heads = [L.T("日時"), L.T("件数"), L.T("容量"), L.T("送り先"), ""];
        AddHistoryRow(table, 0, heads.Select(h => (UIElement)new TextBlock { Text = h, FontSize = 12, Foreground = Res("Muted") }).ToArray());
        var index = 1;
        foreach (var run in Vm.History)
        {
            var dest = new TextBlock();
            dest.Inlines.Add(new Run(run.Operation == "Recycle" ? L.T("ごみ箱 ") : L.T("保管フォルダー ")));
            if (run.Operation == "Recycle")
            {
                var open = new Hyperlink(new Run(L.T("開く"))) { Foreground = Res("AccentHi"), TextDecorations = null };
                open.Click += (_, _) => OpenRecycleBin();
                dest.Inlines.Add(open);
            }
            var files = Link(L.T("移動したファイル"), () => _ = ShowHistoryFilesAsync(run));
            files.Margin = new Thickness(0);
            AddHistoryRow(table, index++,
                new TextBlock { Text = run.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) },
                new TextBlock { Text = L.F("{0}件", run.FileCount) },
                new TextBlock { Text = MediaDisplayFormat.FileSize(run.Bytes) },
                dest, files);
        }
        section.Children.Add(table);
        section.Children.Add(Note(L.T("元に戻したいときは、ごみ箱（または保管フォルダー）から戻してください。このアプリでは元に戻せません。"), false));
    }

    private void AddHistoryRow(Grid table, int row, params UIElement[] cells)
    {
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < cells.Length; c++)
        {
            if (cells[c] is TextBlock text) text.FontSize = row == 0 ? 12 : 13;
            var border = new Border { Child = cells[c], Padding = new Thickness(6, 8, 6, 8), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1) };
            Grid.SetRow(border, row);
            Grid.SetColumn(border, c);
            table.Children.Add(border);
        }
    }

    private async Task ShowHistoryFilesAsync(CleanupRun run)
    {
        if (Vm is null) return;
        IReadOnlyList<string> paths;
        try { paths = await Vm.LoadHistoryFilesAsync(run, CancellationToken.None); }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { Toast(L.T("読み込めませんでした")); return; }
        OpenModal(L.T("移動したファイル"), L.F("{0}・{1}件・{2}", run.CompletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), run.FileCount, MediaDisplayFormat.FileSize(run.Bytes)));
        foreach (var path in paths)
            OverlayBody.Children.Add(new TextBlock { Text = path, FontSize = 12, Foreground = Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, ToolTip = path, Margin = new Thickness(0, 3, 0, 3) });
        SetButtons((L.T("片付けに戻る"), "BtnBase", ShowCleanupDrawer), (L.T("閉じる"), "Primary", HideOverlay));
    }

    // ---------- すべてのファイル ----------
    private void OpenFiles_OnClick(object sender, RoutedEventArgs e) => _ = ShowFilesDrawerAsync(null);

    private async Task ShowFilesDrawerAsync(Guid? folderId)
    {
        if (Vm is null) return;
        OpenDrawer(L.T("すべてのファイル"));
        var chips = new WrapPanel { Margin = new Thickness(16, 8, 16, 8) };
        chips.Children.Add(new TextBlock { Text = L.T("フォルダー:"), FontSize = 12, Foreground = Res("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        chips.Children.Add(Chip(L.T("すべて"), folderId is null, () => _ = ShowFilesDrawerAsync(null)));
        foreach (var folder in Vm.Folders) { var id = folder.Id; chips.Children.Add(Chip(folder.Name, folderId == id, () => _ = ShowFilesDrawerAsync(id))); }
        DrawerBody.Children.Add(new Border { Child = chips, Background = Hex("#18202A"), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1) });
        IReadOnlyList<OrganizeFile> files;
        try { files = await Vm.LoadFilesAsync(folderId, 200, CancellationToken.None); }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { Toast(L.T("ファイルを読み込めませんでした")); return; }
        foreach (var file in files) DrawerBody.Children.Add(FilesRow(file));
        if (files.Count == 0) DrawerBody.Children.Add(Muted(L.T("ファイルはありません。"), 13, new Thickness(16)));
        else if (files.Count >= 200) DrawerBody.Children.Add(Muted(L.T("最初の200本を表示しています。"), 12, new Thickness(16, 8, 16, 16)));
    }

    private Border FilesRow(OrganizeFile file)
    {
        var tags = new List<UIElement>();
        var group = Vm!.GroupOf(file.MediaFileId);
        if (group is not null)
        {
            tags.Add(Mark(group.Category));
            tags.Add(Link(L.T("重複の組を見る"), () => { HideOverlay(); Vm.Selected = group; }));
        }
        if (Vm.QueuedGroups.Any(g => g.CleanupFiles.Any(f => f.MediaFileId == file.MediaFileId)) || Vm.QueuedSingles.Any(f => f.MediaFileId == file.MediaFileId))
            tags.Add(PillOf(L.T("整理リスト"), "trash"));
        var menu = new Button { Style = (Style)FindResource("Kebab"), Margin = new Thickness(6, 8, 0, 0) };
        AutomationPropertiesHelper.SetName(menu, L.F("{0}の操作（再生・エクスプローラーで表示・整理リストに入れる）", file.FileName));
        menu.Click += (_, _) => OpenFileMenu(menu, file, allowQueue: true);
        var row = new DockPanel { Margin = new Thickness(16, 0, 10, 0) };
        DockPanel.SetDock(menu, Dock.Right);
        row.Children.Add(menu);
        row.Children.Add(FileLine(file, tags.ToArray()));
        return new Border { Child = row, BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private Button Chip(string label, bool on, Action onClick)
    {
        var button = new Button { Content = label, Style = (Style)FindResource("Small"), Margin = new Thickness(0, 2, 6, 2), Padding = new Thickness(10, 2, 10, 2), BorderBrush = on ? Res("Accent") : Res("Line2") };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void QueueSingle(OrganizeFile file)
    {
        if (Vm is null) return;
        var message = Vm.EnqueueSingle(file) switch
        {
            SingleQueueResult.Added => L.F("「{0}」を整理リストに入れました（実行前に1件ずつ確認します）", file.FileName),
            SingleQueueResult.AlreadyQueued => L.T("すでに整理リストに入っています"),
            SingleQueueResult.KeptInGroup => L.T("重複の組で「残す」にしているファイルです"),
            SingleQueueResult.WaitingForDrive => L.T("ドライブが接続されていません"),
            _ => L.F("個別のファイルとよく似ている動画は、1回{0}件までです。先に実行してください", Vm.OtherLimit)
        };
        Toast(message);
    }

    // ---------- 解析できなかったファイル ----------
    private void OpenFailed_OnClick(object sender, RoutedEventArgs e) => ShowFailedDrawer();

    private void ShowFailedDrawer()
    {
        if (Vm is null) return;
        OpenDrawer(L.T("解析できなかったファイル"), PillOf(L.F("{0}本", Vm.Unanalyzed.Count)));
        var section = DrawerSection(L.T("これらのファイルは、重複かどうか調べられていません"));
        section.Children.Add(Note(L.T("原因を直してから「もう一度解析」を押してください。"), true));
        if (Vm.Unanalyzed.Count == 0) section.Children.Add(Muted(L.T("解析できなかったファイルはありません。"), 13));
        foreach (var file in Vm.Unanalyzed)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(SmallGhost(L.T("エクスプローラーで表示"), () => RevealInExplorer(file.Path)));
            buttons.Children.Add(SmallGhost(L.T("もう一度解析"), async () =>
            {
                try { Toast(await Vm.RecheckFolderAsync(file.LibraryRootId, CancellationToken.None) ? L.T("フォルダーをもう一度確認して解析します") : L.T("いま確認・解析しています。終わってから、もう一度押してください")); }
                catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { Toast(L.T("解析を始められませんでした")); }
            }));
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = file.FileName, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            text.Children.Add(new TextBlock { Text = System.IO.Path.GetDirectoryName(file.Path) ?? file.Path, FontSize = 12, Foreground = Res("Faint"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            text.Children.Add(new TextBlock { Text = FriendlyAnalysisError(file), FontSize = 12, Foreground = Res("WarnText"), TextWrapping = TextWrapping.Wrap });
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(text);
            section.Children.Add(new Border { Child = row, BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1) });
        }
    }

    private static string FriendlyAnalysisError(UnanalyzedFile file)
    {
        var code = file.ErrorCode ?? string.Empty;
        if (code.Contains("ACCESS", StringComparison.OrdinalIgnoreCase)) return L.T("アクセスが許可されていません");
        if (code.Contains("MISSING", StringComparison.OrdinalIgnoreCase) || code.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase)) return L.T("ファイルを読み込めませんでした（ドライブやファイルを確認してください）");
        if (code.Contains("TRUNC", StringComparison.OrdinalIgnoreCase) || code.Contains("EOF", StringComparison.OrdinalIgnoreCase)) return L.T("ファイルの最後まで読めませんでした（コピーの途中の可能性があります）");
        return L.T("動画を読み込めませんでした（ファイルが壊れている可能性があります）");
    }

    // ---------- folder actions ----------
    private void FolderFiles_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FolderRow folder }) _ = ShowFilesDrawerAsync(folder.Id);
    }

    private async void FolderRescan_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not FrameworkElement { Tag: FolderRow folder }) return;
        try { Toast(await Vm.RecheckFolderAsync(folder.Id, CancellationToken.None) ? L.F("「{0}」をもう一度確認して解析します", folder.Name) : L.T("いま確認・解析しています。終わってから、もう一度押してください")); }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { Toast(L.T("解析を始められませんでした")); }
    }

    private void FolderRemove_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null || sender is not FrameworkElement { Tag: FolderRow folder }) return;
        OpenModal(L.F("「{0}」を整理対象から外しますか？", folder.Name), folder.Path);
        var removed = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Res("Muted") };
        removed.Inlines.Add(new Run(L.T("フォルダーとその中のファイルは")));
        removed.Inlines.Add(new Bold(new Run(L.T("削除されません"))) { Foreground = Res("Text") });
        removed.Inlines.Add(new Run(L.T("。このアプリの一覧に表示されなくなるだけです。")));
        OverlayBody.Children.Add(Note(removed, false));
        OverlayBody.Children.Add(Note(L.T("このフォルダーに関係する重複の組は一覧から消えます。整理リストは空になります。フォルダーの情報は、設定の「情報を残す期間」の日数だけ残ります。"), false));
        SetButtons((L.T("キャンセル"), "BtnBase", HideOverlay), (L.T("整理対象から外す"), "DangerBtn", async () =>
        {
            try { await Vm.RemoveFolderAsync(folder.Id, CancellationToken.None); HideOverlay(); RenderDetail(); RefreshChrome(); Toast(L.F("「{0}」を整理対象から外しました", folder.Name)); }
            catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or NotSupportedException) { Toast(L.T("外せませんでした")); }
        }));
    }

    // ---------- first analysis: folder priority ----------
    /// <summary>After the first analysis, ask once which folder's copy to keep (prototype prioModal).</summary>
    private void MaybeAskPriority()
    {
        if (Vm is null || _priorityShown || Overlay.Visibility == Visibility.Visible) return;
        if (Vm.Settings.PriorityConfirmed || Vm.Folders.Count < 2 || Vm.IsAnalyzing || Vm.ExactOpenCount == 0) return;
        _priorityShown = true;
        var folders = Vm.Folders.Select(f => f.Folder).ToList();
        OpenModal(L.T("完全に同じ動画が見つかりました"), L.T("同じ動画が複数のフォルダーにあるとき、どのフォルダーにある方を残しますか？ 上ほど優先して残します。つかんで並べ替えてください。"), Mark(GroupCategory.Exact), dismissable: false);
        OverlayBody.Children.Add(ReorderList(folders, (f, i) => OrderItem((i + 1).ToString(CultureInfo.InvariantCulture), FolderGlyph(), new TextBlock { Text = f.Name }, f.Path), f => f.Name));
        OverlayBody.Children.Add(Hint(L.T("同じ順位のときは、更新日時が古い方を残します。あとから設定や左の欄でいつでも変えられます。")));
        SetButtons((L.T("この順番で決める"), "Primary", async () =>
        {
            try
            {
                await Vm.SaveSettingsAsync(Vm.Settings with { FolderOrder = folders.Select(f => f.Id).ToArray(), PriorityConfirmed = true }, CancellationToken.None);
                HideOverlay();
                RefreshChrome();
                RenderDetail();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { Toast(L.T("保存できませんでした")); }
        }));
    }

    private Button SmallGhost(string label, Action onClick)
    {
        var button = new Button { Content = label, Style = (Style)FindResource("Small"), Background = System.Windows.Media.Brushes.Transparent, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => onClick();
        return button;
    }
}
