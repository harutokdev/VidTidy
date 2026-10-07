using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using LocalMediaAI.App.ViewModels;

namespace LocalMediaAI.App;
public partial class LibraryWorkspace : UserControl
{
    private bool? _narrow;
    private bool _wideSidebarChecked = true;
    private bool _wideDetailChecked = true;
    private bool _applyingResponsiveState;
    public LibraryWorkspace() { InitializeComponent(); }
    public void FocusSearch() { MediaSearch.Focus(); MediaSearch.SelectAll(); }
    public void FocusTable() => MediaTable.Focus();
    public void FocusRegisteredFolders()
    {
        DetailToggle.IsChecked = false;
        SidebarToggle.IsChecked = true;
        UpdateLayout();
        ManagementFolders.Focus();
    }
    public void RevealSelectedMedia()
    {
        if (DataContext is not MainViewModel { SelectedMedia: { } selected } vm || !vm.MediaItems.Contains(selected)) { FocusTable(); return; }
        DetailToggle.IsChecked = false;
        MediaTable.UpdateLayout();
        MediaTable.ScrollIntoView(selected);
        MediaTable.UpdateLayout();
        var column = MediaTable.Columns.OfType<DataGridTextColumn>().FirstOrDefault(column => column.Visibility == Visibility.Visible)
            ?? MediaTable.Columns.FirstOrDefault(column => column.Visibility == Visibility.Visible);
        if (column is not null) MediaTable.CurrentCell = new DataGridCellInfo(selected, column);
        FocusTable();
        if (column is not null && MediaTable.ItemContainerGenerator.ContainerFromItem(selected) is DataGridRow { IsVisible: true } row
            && FindCell(row, column) is { } cell) cell.Focus();
    }
    private static DataGridCell? FindCell(DependencyObject parent, DataGridColumn column)
    {
        if (parent is DataGridCell cell && ReferenceEquals(cell.Column, column)) return cell;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            if (FindCell(VisualTreeHelper.GetChild(parent, index), column) is { } found) return found;
        return null;
    }
    public bool DismissSettings()
    {
        if (!LibrarySettings.IsOpen) return false;
        LibrarySettings.IsOpen = false;
        return true;
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = ActualWidth < 1300;
        if (_narrow != narrow)
        {
            if (narrow)
            {
                _wideSidebarChecked = SidebarToggle.IsChecked == true;
                _wideDetailChecked = DetailToggle.IsChecked == true;
            }
            _narrow = narrow;
            _applyingResponsiveState = true;
            try
            {
                SidebarToggle.IsChecked = narrow ? false : _wideSidebarChecked;
                DetailToggle.IsChecked = narrow ? false : _wideDetailChecked;
            }
            finally { _applyingResponsiveState = false; }
        }
        UpdatePanels();
    }
    private void OnPanelToggle(object sender, RoutedEventArgs e)
    {
        if (ContentGrid is null || _applyingResponsiveState) return;
        if (_narrow == false)
        {
            _wideSidebarChecked = SidebarToggle.IsChecked == true;
            _wideDetailChecked = DetailToggle.IsChecked == true;
        }
        UpdatePanels();
    }
    private void UpdatePanels()
    {
        var focusHidden = FolderPanel.IsKeyboardFocusWithin && SidebarToggle.IsChecked != true || DetailPanel.IsKeyboardFocusWithin && DetailToggle.IsChecked != true;
        var narrow = _narrow != false;
        var temporaryDetail = narrow && DetailToggle.IsChecked == true;
        BackToTable.Visibility = temporaryDetail ? Visibility.Visible : Visibility.Collapsed;
        FolderPanel.Visibility = SidebarToggle.IsChecked == true && !temporaryDetail ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = new GridLength(SidebarToggle.IsChecked == true && !temporaryDetail ? 200 : 0);
        DetailPanel.Visibility = DetailToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MediaArea.Visibility = temporaryDetail ? Visibility.Collapsed : Visibility.Visible;
        DetailColumn.Width = new GridLength(!narrow && DetailToggle.IsChecked == true ? 300 : 0);
        Grid.SetRow(DetailPanel, 0); Grid.SetColumn(DetailPanel, narrow ? 0 : 2); Grid.SetColumnSpan(DetailPanel, narrow ? 3 : 1);
        DetailPanel.MaxHeight = double.PositiveInfinity;
        if (focusHidden) { if (temporaryDetail) BackToTable.Focus(); else FocusTable(); }
    }
    private void ReturnToTable(object sender, RoutedEventArgs e) { DetailToggle.IsChecked = false; FocusTable(); }
    private void ShowAll(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) vm.CurrentLibraryScope = LibraryScopePresentation.All; }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && MediaTable.IsKeyboardFocusWithin && DataContext is MainViewModel vm && vm.LibraryPreviewCommand.CanExecute(null)) { vm.LibraryPreviewCommand.Execute(null); e.Handled = true; }
        if (e.Key == Key.Escape && LibrarySettings.IsOpen) { LibrarySettings.IsOpen = false; e.Handled = true; }
    }
    private void CloseSettings(object sender, RoutedEventArgs e) => LibrarySettings.IsOpen = false;
    private void OnSettingsClosed(object? sender, EventArgs e) { SettingsToggle.IsChecked = false; SettingsToggle.Focus(); }
}
