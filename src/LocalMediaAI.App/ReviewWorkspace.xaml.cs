using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;

namespace LocalMediaAI.App;
public partial class ReviewWorkspace : UserControl
{
    public ReviewWorkspace() => InitializeComponent();
    public void FocusQueue() => Queue.Focus();
    public void FocusFilter() => CandidateFilter.Focus();
    private void ClearFilter(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel vm) vm.UnifiedReviewFilter = UnifiedReviewFilter.All; }
    private void OnEvidenceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.FocusedElement is TextBox or Button) return;
        var card = (Keyboard.FocusedElement as FrameworkElement)?.DataContext as ReviewMediaEvidence;
        if (card is not null) { card.Preview.Execute(null); e.Handled = true; }
        else if (DataContext is MainViewModel vm && vm.ReviewEvidence.FirstOrDefault() is { } first) { first.Preview.Execute(null); e.Handled = true; }
    }
}
