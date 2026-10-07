using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalMediaAI.App.ViewModels;

namespace LocalMediaAI.App;

public partial class AiSearchWorkspace : UserControl
{
    private bool? _narrow;
    public AiSearchWorkspace() => InitializeComponent();
    public void FocusQuery() { QueryInput.Focus(); QueryInput.SelectAll(); }
    public void FocusResultsOrQuery()
    {
        if (DataContext is MainViewModel { AiSearchHasEvidence: true }) { EvidenceToggle.IsChecked = false; ResultsTable.Focus(); }
        else FocusQuery();
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = ActualWidth < 1300;
        if (_narrow != narrow) { _narrow = narrow; EvidenceToggle.IsChecked = false; }
        UpdateEvidencePanel();
    }
    private void OnEvidenceToggle(object sender, RoutedEventArgs e) { if (ResultsTable is not null) UpdateEvidencePanel(); }
    private void UpdateEvidencePanel()
    {
        var narrow = _narrow != false; var temporary = narrow && EvidenceToggle.IsChecked == true;
        EvidenceToggle.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        EvidenceColumn.Width = new GridLength(narrow ? 0 : 360); EvidenceRow.Height = new GridLength(0);
        ResultsTable.Visibility = temporary ? Visibility.Collapsed : Visibility.Visible;
        EvidencePanel.Visibility = !narrow || temporary ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(EvidencePanel, 0); Grid.SetColumn(EvidencePanel, narrow ? 0 : 1); Grid.SetColumnSpan(EvidencePanel, narrow ? 2 : 1);
        MatchedFrame.Height = narrow ? 100 : 160;
        EvidencePanel.Margin = narrow ? new Thickness(0) : new Thickness(8, 0, 0, 0);
        BackToResults.Visibility = temporary ? Visibility.Visible : Visibility.Collapsed;
        if (temporary && ResultsTable.IsKeyboardFocusWithin) BackToResults.Focus();
    }
    private void ReturnToResults(object sender, RoutedEventArgs e) { EvidenceToggle.IsChecked = false; ResultsTable.Focus(); }
    private void OnLoadingRow(object sender, DataGridRowEventArgs e) => e.Row.Header = e.Row.GetIndex() + 1;
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainViewModel vm) return;
        if (QueryInput.IsKeyboardFocusWithin && vm.AiSearchCanSubmit) { vm.SemanticSearchCommand.Execute(null); e.Handled = true; }
        else if (ResultsTable.IsKeyboardFocusWithin && vm.AiSearchPreviewCommand.CanExecute(null)) { vm.AiSearchPreviewCommand.Execute(null); e.Handled = true; }
    }
}
