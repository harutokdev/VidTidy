using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalMediaAI.Core;
using LocalMediaAI.Core.Organize;
using LocalMediaAI.Core.Processing;

namespace LocalMediaAI.App.Organize;

/// <summary>設定 — keep rule order, folder priority, startup check, destination, retention, technical details.</summary>
public partial class OrganizeView
{


    private void OpenSettings_OnClick(object sender, RoutedEventArgs e) => ShowSettings();

    private async void ShowSettings()
    {
        if (Vm is null) return;
        RetentionSettings retention;
        try { retention = await Vm.LoadRetentionAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { retention = new RetentionSettings(30, 90, null); }
        ProcessingSettings processing;
        try { processing = await Vm.LoadProcessingAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { processing = ProcessingSettings.Defaults; }

        var settings = Vm.Settings;
        var criteria = settings.Criteria.ToList();
        var folders = Vm.Folders.Select(f => f.Folder).ToList();
        var dateDir = new ComboBox { Width = 190, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, SelectedIndex = settings.DateDirection == KeepDateDirection.Oldest ? 0 : 1 };
        dateDir.Items.Add(L.T("更新日時が古い方"));
        dateDir.Items.Add(L.T("更新日時が新しい方"));
        AutomationPropertiesHelper.SetName(dateDir, L.T("更新日時の向き"));

        OpenModal(L.T("設定"));
        var (japanese, english) = LanguageFieldset();
        var keep = Fieldset(L.T("完全に同じファイルで残す方"));
        keep.Children.Add(FieldLabel(L.T("判定の順番（つかんで並べ替えます。上から順に判定します）")));
        keep.Children.Add(ReorderList(criteria, (c, i) => OrderItem(i == 0 ? "①" : "②", null,
            c == KeepCriterion.FolderPriority ? new TextBlock { Text = L.T("フォルダーの優先順位が高い方"), VerticalAlignment = VerticalAlignment.Center } : dateDir, null), c => c == KeepCriterion.FolderPriority ? L.T("フォルダーの優先順位") : L.T("更新日時")));
        keep.Children.Add(FieldLabel(L.T("フォルダーの優先順位（つかんで並べ替えます。上ほど優先して残します）")));
        keep.Children.Add(ReorderList(folders, (f, i) => OrderItem((i + 1).ToString(CultureInfo.InvariantCulture), FolderGlyph(), new TextBlock { Text = f.Name }, f.Path), f => f.Name));
        keep.Children.Add(Hint(L.T("キーボードでは、項目を選んで Alt + ↑↓ で動かせます。一覧で残す方を自分で選び直した組は、ルールを変えてもその選択がそのまま残ります。")));

        var check = Fieldset(L.T("起動時の確認"));
        var auto = SwitchOption("autocheck", L.T("起動時に自動で確認する（おすすめ）"), L.T("登録したフォルダーで増えた・消えた・変わったファイルを確認し、新しいファイルだけを解析します。"), settings.CheckChangesOnStartup);
        var manual = SwitchOption("autocheck", L.T("手動で確認する"), L.T("起動時は確認しません。左の欄の「変更を確認」を押すと、すべてのフォルダーをまとめて確認します。大きなHDDで起動を軽くしたいときに。"), !settings.CheckChangesOnStartup);
        check.Children.Add(auto);
        check.Children.Add(manual);

        var pace = Fieldset(L.T("解析の進め方"));
        string Cycle(ProcessingPace value)
        {
            var cycle = new ProcessingSettings(value, false).Cycle;
            return L.F("約{0:0}分ごとに{1:0}分、元の動画があるドライブを休ませます", cycle.Work.TotalMinutes, cycle.Rest.TotalMinutes);
        }
        var standard = SwitchOption("pace", L.T("標準（おすすめ）"), Cycle(ProcessingPace.Standard) + L.T("。"), processing.Pace == ProcessingPace.Standard);
        var quiet = SwitchOption("pace", L.T("静か"), Cycle(ProcessingPace.Quiet) + L.T("。発熱や音を抑えたいときに。解析には時間がかかります。"), processing.Pace == ProcessingPace.Quiet);
        var fastest = SwitchOption("pace", L.T("最速"), L.T("休まずに解析します。"), processing.Pace == ProcessingPace.Fastest);
        pace.Children.Add(standard);
        pace.Children.Add(quiet);
        pace.Children.Add(fastest);
        var idleOnly = new CheckBox { Content = L.T("パソコンを使っていない間だけ解析する（3分以上操作がないとき）"), IsChecked = processing.OnlyWhenIdle, Foreground = Res("Text"), Margin = new Thickness(0, 6, 0, 0) };
        pace.Children.Add(idleOnly);
        pace.Children.Add(Hint(L.T("どの設定でも、元の動画を読むのは1台のドライブにつき同時に1つだけです。ほかの作業でドライブを使っているときは、そちらを優先します。")));

        var dest = Fieldset(L.T("片付けの送り先（既定）"));
        dest.Children.Add(new RadioButton { Content = L.T("Windowsのごみ箱"), GroupName = "sdest", IsChecked = true, Foreground = Res("Text") });
        dest.Children.Add(new RadioButton { Content = L.T("保管フォルダーへ移動（準備中）"), GroupName = "sdest", IsEnabled = false, Foreground = Res("Text"), Margin = new Thickness(0, 6, 0, 0) });
        dest.Children.Add(Hint(L.T("保管フォルダー（空欄なら使いません。整理するフォルダーの外に指定します）は、片付けパネルから作れるようにします。")));

        var keepInfo = Fieldset(L.T("情報を残す期間"));
        keepInfo.Children.Add(Hint(L.T("見つからなくなったものの情報（解析結果や判断の記録）を、このアプリの中にどれだけ残すかです。元のファイルを削除する設定ではありません。")));
        var fileDays = RetentionRow(keepInfo, L.T("ファイル"), L.T("移動・削除されて見つからなくなったファイル"), retention.FileDays);
        var folderDays = RetentionRow(keepInfo, L.T("フォルダー"), L.T("整理対象から外した・見つからなくなったフォルダー"), retention.FolderDays);
        var driveDays = RetentionRow(keepInfo, L.T("ドライブ"), L.T("接続されていないドライブ（外付けHDDなど）"), retention.DriveDays);

        var other = Fieldset(L.T("そのほか"));
        var tech = new CheckBox { Content = L.T("詳しい技術情報を表示する"), IsChecked = settings.ShowTechnicalDetails, Foreground = Res("Text") };
        other.Children.Add(tech);
        foreach (var item in new[] { L.T("ファイルの識別番号とハッシュ値（完全に同じと判定した根拠）"), L.T("よく似ていると判定した数値の根拠（一致した場面の数など）"), L.T("解析の処理状態と、失敗したときのエラーの種類"), L.T("使っているAIモデルの名前と動作状態") })
            other.Children.Add(new TextBlock { Text = L.T("・") + item, FontSize = 12, Foreground = Res("Faint"), Margin = new Thickness(26, 2, 0, 0) });
        other.Children.Add(Hint(L.T("通常の整理には必要ありません。問題を調べるときに使います。")));

        SetButtons((L.T("キャンセル"), "BtnBase", HideOverlay), (L.T("保存"), "Primary", async () =>
        {
            var updated = settings with
            {
                Criteria = criteria.ToArray(),
                DateDirection = dateDir.SelectedIndex == 1 ? KeepDateDirection.Newest : KeepDateDirection.Oldest,
                FolderOrder = folders.Select(f => f.Id).ToArray(),
                CheckChangesOnStartup = auto.IsChecked == true,
                ShowTechnicalDetails = tech.IsChecked == true
            };
            try
            {
                var days = new RetentionSettings(fileDays(), folderDays(), driveDays()).Validate();
                await Vm.SaveSettingsAsync(updated, CancellationToken.None);
                await Vm.SaveRetentionAsync(days, CancellationToken.None);
                await Vm.SaveProcessingAsync(new ProcessingSettings(
                    quiet.IsChecked == true ? ProcessingPace.Quiet : fastest.IsChecked == true ? ProcessingPace.Fastest : ProcessingPace.Standard,
                    idleOnly.IsChecked == true), CancellationToken.None);
                var languageSaved = SaveLanguageChoice(japanese, english);
                HideOverlay();
                RefreshChrome();
                RenderDetail();
                Toast(languageSaved switch
                {
                    false => L.T("表示言語を保存できませんでした"),
                    true when (english?.IsChecked == true ? UiLanguage.English : UiLanguage.Japanese) != L.Language => L.T("設定を保存しました。表示言語は次に起動したときに切り替わります"),
                    _ => L.T("設定を保存しました")
                });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
            {
                Toast(L.T("保存できませんでした：日数は 1 以上の数字で入力してください"));
            }
        }));
    }

    /// <summary>The language for the next start: the saved choice, else the one shown now.</summary>
    private UiLanguage StoredLanguage => Vm?.ReadLanguage?.Invoke() ?? L.Language;

    /// <summary>
    /// Keeps the chosen language when it differs from the saved one. Returns null when nothing needed saving, false when
    /// it could not be saved (the other settings are already saved by then).
    /// </summary>
    private bool? SaveLanguageChoice(RadioButton? japanese, RadioButton? english)
    {
        if (japanese is null || Vm?.SaveLanguage is null) return null;
        var language = english?.IsChecked == true ? UiLanguage.English : UiLanguage.Japanese;
        if (language == StoredLanguage) return null;
        try
        {
            Vm.SaveLanguage(language);
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Vm.ReportLanguageSaveFailure?.Invoke(ex);
            return false;
        }
    }

    /// <summary>Display language. The language names stay in their own language, so either one can be found.</summary>
    private (RadioButton? Japanese, RadioButton? English) LanguageFieldset()
    {
        if (Vm?.SaveLanguage is null) return (null, null);
        var field = Fieldset(L.T("表示言語"));
        var stored = StoredLanguage;
        var japanese = SwitchOption("language", "日本語", "Japanese", stored == UiLanguage.Japanese);  // i18n: keep (shown in both languages)
        var english = SwitchOption("language", "English", "英語", stored == UiLanguage.English);  // i18n: keep (shown in both languages)
        AutomationPropertiesHelper.SetName(japanese, "日本語 (Japanese)");  // i18n: keep (shown in both languages)
        AutomationPropertiesHelper.SetName(english, "English (英語)");  // i18n: keep (shown in both languages)
        field.Children.Add(japanese);
        field.Children.Add(english);
        field.Children.Add(Hint(L.T("次に起動したときに切り替わります。")));
        return (japanese, english);
    }

    private StackPanel Fieldset(string legend)
    {
        var content = new StackPanel();
        var box = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 16, 14, 14), Margin = new Thickness(0, 10, 0, 4), Child = content };
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.Children.Add(box);
        grid.Children.Add(new Border
        {
            Background = Res("Surface"), Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(10, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = legend, FontWeight = FontWeights.SemiBold }
        });
        OverlayBody.Children.Add(grid);
        return content;
    }

    private TextBlock FieldLabel(string text) => new() { Text = text, FontSize = 13, Foreground = Res("Muted"), Margin = new Thickness(0, 4, 0, 6), TextWrapping = TextWrapping.Wrap };

    private TextBlock Hint(string text) => new() { Text = text, FontSize = 12, Foreground = Res("Faint"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

    private RadioButton SwitchOption(string group, string title, string detail, bool isChecked)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, Foreground = Res("Text") });
        body.Children.Add(new TextBlock { Text = detail, FontSize = 12, Foreground = Res("Faint"), TextWrapping = TextWrapping.Wrap });
        return new RadioButton { Content = body, GroupName = group, IsChecked = isChecked, Margin = new Thickness(0, 4, 0, 6), VerticalContentAlignment = VerticalAlignment.Top };
    }

    /// <summary>Row of the retention grid: label, 日数を指定／無制限, a number box with 日. Returns a reader for the chosen value.</summary>
    private Func<long?> RetentionRow(StackPanel parent, string label, string detail, long? days)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var mode = new ComboBox { Width = 120, SelectedIndex = days is null ? 1 : 0, VerticalAlignment = VerticalAlignment.Center };
        mode.Items.Add(L.T("日数を指定"));
        mode.Items.Add(L.T("無制限"));
        AutomationPropertiesHelper.SetName(mode, L.F("{0}の情報を残す期間", label));
        var box = new TextBox { Width = 64, Text = days?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, TextAlignment = TextAlignment.Right, IsEnabled = days is not null, VerticalContentAlignment = VerticalAlignment.Center };
        AutomationPropertiesHelper.SetName(box, L.F("{0}の情報を残す日数", label));
        var unit = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), Opacity = days is null ? 0.4 : 1 };
        unit.Children.Add(box);
        unit.Children.Add(new TextBlock { Text = L.T(" 日"), VerticalAlignment = VerticalAlignment.Center, Foreground = Res("Muted") });
        mode.SelectionChanged += (_, _) => { var limited = mode.SelectedIndex == 0; box.IsEnabled = limited; unit.Opacity = limited ? 1 : 0.4; if (limited && box.Text.Length == 0) box.Text = "30"; };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(mode);
        controls.Children.Add(unit);
        DockPanel.SetDock(controls, Dock.Right);
        row.Children.Add(controls);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = label });
        text.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = Res("Faint"), TextWrapping = TextWrapping.Wrap });
        row.Children.Add(text);
        parent.Children.Add(row);
        return () => mode.SelectedIndex == 1 ? null : long.Parse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture);
    }

    /// <summary>One draggable row (.order .it): grip, number, optional icon, content with optional path.</summary>
    private Border OrderItem(string number, UIElement? icon, UIElement content, string? sub)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock { Text = "⠿", Foreground = Res("Faint"), VerticalAlignment = VerticalAlignment.Center });
        var num = new TextBlock { Text = number, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(num, 1);
        grid.Children.Add(num);
        if (icon is not null) { Grid.SetColumn(icon, 2); grid.Children.Add(icon); }
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (content is FrameworkElement { Parent: Panel owner }) owner.Children.Remove(content); // the date picker is reused when the list re-renders
        body.Children.Add(content);
        if (sub is not null) body.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Res("Faint"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        Grid.SetColumn(body, 3);
        grid.Children.Add(body);
        return new Border { Child = grid, Background = Res("Raised"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.SizeNS, Focusable = true, AllowDrop = true };
    }

    /// <summary>A list the user reorders by dragging or with Alt+↑↓. The list is mutated in place and re-rendered; <paramref name="changed"/> runs after each move.</summary>
    private static StackPanel ReorderList<T>(List<T> items, Func<T, int, Border> render, Func<T, string> name, Action? changed = null)
    {
        var panel = new StackPanel();
        void Rebuild(int focus = -1)
        {
            panel.Children.Clear();
            for (var i = 0; i < items.Count; i++)
            {
                var index = i;
                var item = render(items[i], i);
                AutomationPropertiesHelper.SetName(item, L.F("{0}、{1}番目", name(items[i]), i + 1));
                var start = default(Point);
                item.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(panel);
                item.MouseMove += (_, e) =>
                {
                    if (e.LeftButton != MouseButtonState.Pressed || e.OriginalSource is ComboBox or TextBox) return;
                    if ((e.GetPosition(panel) - start).Length < SystemParameters.MinimumVerticalDragDistance) return;
                    var insertAt = DragReorder.Run(item, panel.Children.OfType<FrameworkElement>().ToArray(), panel);
                    if (insertAt is { } at) Move(index, DragReorder.TargetIndex(index, at));
                };
                item.KeyDown += (_, e) =>
                {
                    if (Keyboard.Modifiers != ModifierKeys.Alt) return;
                    var key = e.Key == Key.System ? e.SystemKey : e.Key;
                    if (key == Key.Up && index > 0) { Move(index, index - 1); e.Handled = true; }
                    else if (key == Key.Down && index < items.Count - 1) { Move(index, index + 1); e.Handled = true; }
                };
                panel.Children.Add(item);
                if (i == focus) item.Loaded += (_, _) => item.Focus();
            }
        }
        void Move(int from, int to)
        {
            if (from == to) return;
            var moved = items[from];
            items.RemoveAt(from);
            items.Insert(to, moved);
            Rebuild(to);
            changed?.Invoke();
        }
        Rebuild();
        return panel;
    }
}
