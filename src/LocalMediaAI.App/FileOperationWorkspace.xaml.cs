using System.Windows.Controls;
using System.Windows.Input;
using System.ComponentModel;
using System.Windows;
using LocalMediaAI.App.ViewModels;

namespace LocalMediaAI.App;
public partial class FileOperationWorkspace : UserControl
{
    private MainViewModel? _vm;
    private (int Step, bool Batch, Guid? Target)? _focusContext;
    private IInputElement? _selectorReturnFocus;
    public void FocusOperationSelector()
    {
        UpdateLayout();
        if (SingleOperationSelector.IsVisible && SingleOperationSelector.IsEnabled) SingleOperationSelector.Focus();
    }
    public void FocusTargetSearch()
    {
        // Hidden/locked preparation and Batch have no editable single-target search.
        if (!TargetSelector.IsVisible || !TargetSelector.IsEnabled) return;
        TargetSelector.IsExpanded = true;
        UpdateLayout();
        if (TargetSearch.IsVisible && TargetSearch.IsEnabled) { TargetSearch.Focus(); TargetSearch.SelectAll(); }
    }
    private void OpenTargetSelector(object sender, RoutedEventArgs e)
    {
        if (!TargetSelector.IsVisible || !TargetSelector.IsEnabled) return;
        TargetSelector.IsExpanded = !TargetSelector.IsExpanded;
        if (TargetSelector.IsExpanded) FocusTargetSearch();
    }
    private void FocusCurrentStep()
    {
        UpdateLayout();
        if (_vm?.OperationShowConfirmation == true && FinalConfirmationFocusTarget.IsVisible) FinalConfirmationFocusTarget.Focus();
        else if (RetryPreflight.IsVisible && RetryPreflight.IsEnabled) RetryPreflight.Focus();
        else if (AcknowledgementAction.IsVisible && AcknowledgementAction.IsEnabled) AcknowledgementAction.Focus();
        else if (SingleOperationSelector.IsVisible && SingleOperationSelector.IsEnabled) SingleOperationSelector.Focus();
        else CurrentStepFocusTarget.Focus();
    }
    public bool DismissTargetSelector()
    {
        if (!TargetSelector.IsExpanded) return false;
        TargetSelector.IsExpanded = false;
        return true;
    }
    private void OnTargetSelectorExpanded(object sender, RoutedEventArgs e) { if (ReferenceEquals(e.OriginalSource, TargetSelector)) _selectorReturnFocus = Keyboard.FocusedElement ?? FocusManager.GetFocusedElement(Window.GetWindow(this)); }
    private void OnTargetSelectorClosed(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, TargetSelector)) return;
        Dispatcher.BeginInvoke(() => { if (_selectorReturnFocus is UIElement { IsVisible: true, IsEnabled: true } control) control.Focus(); else FocusOperationSelector(); });
    }
    private void ReturnToLibrary(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) window.ShowLibraryForTargetSelection();
    }
    public FileOperationWorkspace()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnPresentationChanged;
            _vm = e.NewValue as MainViewModel; _focusContext = null;
            if (_vm is not null) _vm.PropertyChanged += OnPresentationChanged;
            UpdateConfirmationFocus();
        };
        Loaded += (_, _) => { _focusContext = null; UpdateConfirmationFocus(); };
    }
    private void OnPresentationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.OperationCurrentStep)) UpdateConfirmationFocus();
    }
    private void UpdateConfirmationFocus()
    {
        if (_vm is null) return;
        var context = (_vm.OperationCurrentStep, _vm.OperationBatchMode, _vm.OperationTarget?.MediaFileId);
        if (_focusContext == context) return;
        var leavingConfirmation = _focusContext?.Step == 3 && context.OperationCurrentStep != 3;
        _focusContext = context;
        if (IsVisible && leavingConfirmation)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                var focused = Keyboard.FocusedElement ?? FocusManager.GetFocusedElement(Window.GetWindow(this));
                if (IsVisible && _vm?.OperationShowConfirmation == false && focused is not UIElement { IsVisible: true, IsEnabled: true }) FocusCurrentStep();
            });
        if (_vm.OperationShowConfirmation && _vm.OperationIsPermanentDelete)
            Dispatcher.BeginInvoke(() => { if (_vm?.OperationShowConfirmation == true && _vm.OperationIsPermanentDelete && IsVisible) FinalConfirmationFocusTarget.Focus(); });
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // WPF Button invokes its command on Enter even when IsDefault=false.
        // Deliberate click/Space remains available; Enter/Delete never authorize irreversible execution.
        if (e.Key is Key.Enter or Key.Return or Key.Delete && (e.OriginalSource == PermanentExecute || PermanentExecute.IsKeyboardFocusWithin)) e.Handled = true;
    }
}
