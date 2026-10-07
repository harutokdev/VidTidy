using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalMediaAI.Core.Organize;

namespace LocalMediaAI.App.Organize;

public partial class OrganizeView : UserControl
{
    private Point _dragStart;
    private OrganizeViewModel? _subscribed;

    public OrganizeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Raised when the user asks to register a new folder. The host owns scanning.</summary>
    public event EventHandler? AddFolderRequested;

    /// <summary>Raised when the user opens the previous (legacy) screens.</summary>
    public event EventHandler? OpenLegacyRequested;

    private OrganizeViewModel? Vm => DataContext as OrganizeViewModel;

    private void Attach()
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnVmChanged;
        _subscribed = Vm;
        if (_subscribed is not null) _subscribed.PropertyChanged += OnVmChanged;
        RefreshChrome();
        RenderDetail();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OrganizeViewModel.Selected)) RenderDetail();
        if (e.PropertyName is nameof(OrganizeViewModel.NextAction) or nameof(OrganizeViewModel.QueueBytes) or nameof(OrganizeViewModel.AnalysisText) or nameof(OrganizeViewModel.ListedGroups)) RefreshChrome();
    }

    // ---------- toolbar ----------
    private void More_OnClick(object sender, RoutedEventArgs e)
    {
        if (MoreButton.ContextMenu is { } menu) { menu.PlacementTarget = MoreButton; menu.IsOpen = true; }
    }

    private void OpenLegacy_OnClick(object sender, RoutedEventArgs e) => OpenLegacyRequested?.Invoke(this, EventArgs.Empty);

    private void AddFolder_OnClick(object sender, RoutedEventArgs e) => AddFolderRequested?.Invoke(this, EventArgs.Empty);

    private async void Reload_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        try { await Vm.LoadAsync(CancellationToken.None); RenderDetail(); }
        catch (Exception ex) { ShowMessage(L.T("最新の状態にできませんでした"), ex.Message); }
    }

    private void ClearQueue_OnClick(object sender, RoutedEventArgs e) { Vm?.ClearQueue(); RenderDetail(); }

    // ---------- next action ----------
    private void NextAction_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        switch (Vm.NextAction.Kind)
        {
            case NextActionKind.ApproveExact: ShowApproval(); break;
            case NextActionKind.ReviewSimilar: Vm.Selected = Vm.FirstOpenForReview(); break;
            case NextActionKind.ApproveClear: ShowClearApproval(); break;
            case NextActionKind.RunQueue: ShowWizard(); break;
            case NextActionKind.ReviewNew: Vm.AcknowledgeNewGroups(); Vm.Selected = Vm.ExactGroups.Concat(Vm.SimilarGroups).Concat(Vm.NeedsCheckGroups).FirstOrDefault(g => g.IsOpen); break;
        }
    }

    // ---------- group list ----------
    private void Group_OnClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not null && sender is FrameworkElement { DataContext: GroupRow group }) { Vm.Selected = group; ((UIElement)sender).Focus(); }
    }

    private void Group_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && Vm is not null && sender is FrameworkElement { DataContext: GroupRow group }) { Vm.Selected = group; e.Handled = true; }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandlePlayerKey(e)) { e.Handled = true; return; }
        if (e.Key == Key.Escape && Overlay.Visibility == Visibility.Visible && _dismissable) { HideOverlay(); e.Handled = true; return; }
        if (Vm is null || Overlay.Visibility == Visibility.Visible || Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.J) { Vm.SelectRelative(1); e.Handled = true; return; }
        if (e.Key == Key.K) { Vm.SelectRelative(-1); e.Handled = true; return; }
        if (Vm.Selected is not { IsOpen: true } group) return;
        var digit = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;
        if (digit >= 0 && digit < group.Files.Count) { Vm.ChooseKeep(group, group.Files[digit].MediaFileId); RenderDetail(); e.Handled = true; return; }
        if (e.Key == Key.N) { _ = KeepAsIsAsync(group); e.Handled = true; }
    }

    // ---------- folders ----------
    private void Folder_OnMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    /// <summary>Drags a folder row with a floating copy and an insertion line, then saves the new priority.</summary>
    private async void Folder_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (Vm is null || e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: FolderRow folder } element) return;
        if (e.OriginalSource is DependencyObject origin && FindParent<Button>(origin) is not null) return; // the ⋯ button is not a handle
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var rows = Enumerable.Range(0, Vm.Folders.Count)
            .Select(i => FolderList.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement)
            .OfType<FrameworkElement>().ToArray();
        var from = Vm.Folders.IndexOf(folder);
        var insertAt = DragReorder.Run(element, rows, FolderList);
        if (insertAt is { } at && DragReorder.TargetIndex(from, at) != from) await MoveFolderAsync(folder.Id, DragReorder.TargetIndex(from, at));
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var node = child; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is T match) return match;
        return null;
    }

    private async void FolderUp_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && sender is MenuItem { Tag: FolderRow folder }) await MoveFolderAsync(folder.Id, Vm.Folders.IndexOf(folder) - 1);
    }

    private async void FolderDown_OnClick(object sender, RoutedEventArgs e)
    {
        if (Vm is not null && sender is MenuItem { Tag: FolderRow folder }) await MoveFolderAsync(folder.Id, Vm.Folders.IndexOf(folder) + 1);
    }

    private void FolderOpen_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: FolderRow folder }) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { ShowMessage(L.T("エクスプローラーで開けませんでした"), ex.Message); }
    }

    private async Task MoveFolderAsync(Guid id, int index)
    {
        if (Vm is null) return;
        try { await Vm.MoveFolderAsync(id, index, CancellationToken.None); RenderDetail(); }
        catch (Exception ex) { ShowMessage(L.T("優先順位を保存できませんでした"), ex.Message); }
    }

}

internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);
}
