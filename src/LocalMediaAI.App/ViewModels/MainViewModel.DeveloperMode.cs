namespace LocalMediaAI.App.ViewModels;
public sealed partial class MainViewModel
{
    private bool _isDeveloperMode;
    private readonly SemaphoreSlim _developerModeSaveGate = new(1, 1);
    private string _developerModeFeedback = string.Empty;
    public string DeveloperModeFeedback { get => _developerModeFeedback; private set => Set(ref _developerModeFeedback, value); }
    public Task DeveloperModeSaveTask { get; private set; } = Task.CompletedTask;
    public bool IsDeveloperMode
    {
        get => _isDeveloperMode;
        set
        {
            if (!Set(ref _isDeveloperMode, value)) return;
            if (!value) IsJobDiagnosticsExpanded = false;
            DeveloperModeSaveTask = SaveDeveloperModeAsync(value);
        }
    }
    public async Task LoadPresentationSettingsAsync()
    {
        try
        {
            var enabled = await _library.GetDeveloperModeAsync(_lifetime.Token);
            Set(ref _isDeveloperMode, enabled, nameof(IsDeveloperMode));
            if (!enabled) IsJobDiagnosticsExpanded = false;
        }
        catch (Exception ex) { DeveloperModeFeedback = L.T("開発者モードの設定を確認できませんでした。"); _log.Write("Warning", "DeveloperModeLoadFailed", new { }, ex); }
    }
    private async Task SaveDeveloperModeAsync(bool enabled)
    {
        await _developerModeSaveGate.WaitAsync();
        try
        {
            await _library.SaveDeveloperModeAsync(enabled, _lifetime.Token);
            if (enabled == IsDeveloperMode) DeveloperModeFeedback = L.T("✓ 表示設定を保存しました。");
        }
        catch (Exception ex)
        {
            DeveloperModeFeedback = L.T("表示は変更しましたが設定を保存できませんでした。再起動後は以前の設定になります。");
            _log.Write("Warning", "DeveloperModeSaveFailed", new { Enabled = enabled }, ex);
        }
        finally { _developerModeSaveGate.Release(); }
    }
    private bool _isJobDiagnosticsExpanded;
    public bool IsJobDiagnosticsExpanded
    {
        get => _isJobDiagnosticsExpanded;
        set
        {
            if (!Set(ref _isJobDiagnosticsExpanded, value && IsDeveloperMode)) return;
            _ = LoadSelectedJobEventsAsync(_isJobDiagnosticsExpanded ? SelectedJobIssue?.Job.JobId : null);
        }
    }
}
