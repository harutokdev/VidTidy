using System.Windows;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.App;

public partial class App : Application, IDisposable
{
    private JsonFileLogger? _logger;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;
    private ApplicationShutdownCoordinator? _shutdownCoordinator;
    private Task? _initializationTask;
    private bool _disposed;
    /// <summary>A first-start language choice that could not be saved; logged once the logger exists.</summary>
    private Exception? _languageSaveFailure;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            // A malformed testlab.flag throws here and fails closed through the startup-failure path below.
            var paths = AppPaths.Create(AppContext.BaseDirectory);
            paths.EnsureDataLocationPresent();
            paths.EnsureDirectories();
            // The language is fixed before any window or text is made; a change takes effect after a restart.
            L.Use(UiLanguageSetting.Read(paths.DataRoot) ?? AskLanguage(paths.DataRoot));
            _logger = new JsonFileLogger(paths);
            if (_languageSaveFailure is { } pickerFailure) _logger.Write("Warning", "UiLanguageSaveFailed", new { }, pickerFailure);
            var library = new SqliteLibraryRepository(paths, _logger);
            var jobs = new SqliteJobStore(paths, _logger);
            var ffmpeg = new FfmpegAdapter(FfmpegPaths.Resolve(paths), paths, _logger);
            ffmpeg.CleanupIncompleteThumbnailArtifacts();
            var concurrency = PipelineConcurrencyOptions.FromEnvironment();
            var semanticModel = SemanticModelRegistry.ResolveDevelopmentCandidate();
            var semanticExecution = SemanticExecutionProfile.Resolve(semanticModel);
            var aiResourceResolution = await AiResourceCache.ResolveModelsRootAsync(paths, semanticModel, _logger, CancellationToken.None);
            var effectivePaths = paths with { ModelsRoot = aiResourceResolution.ModelsRoot };
            var worker = new WorkerProcessManager(effectivePaths, _logger, WorkerProcessOptions.Resolve(effectivePaths, semanticModel));
            var retentionCleanup = new RetentionCleanupCoordinator(
                token => library.RunRetentionCleanupAsync(DateTimeOffset.UtcNow, token), _logger);
            var pipeline = new MediaPipeline(library, jobs, new FileSystemMediaScanner(), ffmpeg, _logger, concurrency, worker, effectivePaths, semanticModel: semanticModel, semanticExecution: semanticExecution, retentionCleanup: retentionCleanup, userIdle: UserInputIdle.Current);
            var semanticSearch = new SemanticSearchService(library, worker, effectivePaths, _logger, semanticModel, semanticExecution);
            var fileOperations = new SafeFileOperationService(library, _logger);
            _viewModel = new MainViewModel(library, jobs, pipeline, worker, semanticSearch, _logger, fileOperations: fileOperations);
            _mainWindow = new MainWindow { DataContext = _viewModel };
            var organize = new Organize.OrganizeViewModel(new Organize.RepositoryOrganizeDataSource(library, MediaPipeline.CurrentThumbnailConfigHash()))
            {
                Executor = new Organize.SafeCleanupExecutor(library, fileOperations, _logger),
                Background = new Organize.PipelineOrganizeBackground(pipeline, jobs, _logger, library),
                ThumbnailCacheRoot = pipeline.ThumbnailCacheRoot,
                SaveLanguage = language => UiLanguageSetting.Write(paths.DataRoot, language),
                ReadLanguage = () => UiLanguageSetting.Read(paths.DataRoot),
                ReportLanguageSaveFailure = ex => _logger.Write("Warning", "UiLanguageSaveFailed", new { }, ex)
            };
            _mainWindow.AttachOrganize(organize);
            _shutdownCoordinator = new ApplicationShutdownCoordinator(
                TeardownAsync,
                () =>
                {
                    _logger.Write("Information", "GracefulShutdownFinalCloseAuthorized", new { });
                    _mainWindow.AuthorizeFinalClose();
                },
                ex => _viewModel.ReportGracefulShutdownFailure(ex),
                () => _logger.Write("Information", "GracefulShutdownLinearized", new { }));
            _mainWindow.GracefulShutdownRequest = RequestGracefulShutdownAsync;
            MainWindow = _mainWindow;
            _mainWindow.Show();
            // Apply the saved pace before initialization can start the first run (migration is idempotent).
            try
            {
                await library.InitializeAsync(CancellationToken.None);
                pipeline.Processing = await library.GetProcessingSettingsAsync(CancellationToken.None);
            }
            catch (Exception ex) { _logger.Write("Warning", "ProcessingSettingsLoadFailed", new { }, ex); }
            _initializationTask = _viewModel.InitializeAsync();
            await _initializationTask;
            try { await organize.StartAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.Write("Warning", "OrganizeLoadFailed", new { }, ex); }
        }
        catch (Exception ex)
        {
            _logger?.Write("Fatal", "ApplicationStartupFailed", new { Operation = "Startup" }, ex);
            MessageBox.Show(ex switch
                {
                    TestLabViolationException => L.T("テスト用モードの設定（testlab.flag）が正しくないため起動できません。") + ex.Message,
                    DataLocationException => ex.Message,
                    _ => L.T("アプリケーションを初期化できませんでした。data/logs のローカルログを確認してください。")
                }, "VidTidy", MessageBoxButton.OK, MessageBoxImage.Error);
            if (_shutdownCoordinator is not null)
            {
                try
                {
                    await RequestGracefulShutdownAsync();
                    return;
                }
                catch { return; }
            }
            Shutdown(-1);
        }
    }

    /// <summary>
    /// First start: asks which language to show and keeps the answer. Closing the window without choosing uses the
    /// Windows language for this run only, so the question comes back next time.
    /// </summary>
    private UiLanguage AskLanguage(string dataRoot)
    {
        var previous = ShutdownMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var picker = new Localization.LanguagePickerWindow();
            picker.ShowDialog();
            if (picker.Choice is not { } choice) return Localization.LanguagePickerWindow.SuggestedLanguage(System.Globalization.CultureInfo.CurrentUICulture);
            try { UiLanguageSetting.Write(dataRoot, choice); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { _languageSaveFailure = ex; }
            return choice;
        }
        finally { ShutdownMode = previous; }
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        Dispose();
    }

    private async Task TeardownAsync()
    {
        var viewModel = _viewModel;
        if (viewModel is null) return;

        // Close production admission immediately even if startup initialization is
        // still settling. Its eventual run request then observes terminal shutdown.
        viewModel.BeginTerminalShutdown();
        if (_initializationTask is not null)
        {
            try { await _initializationTask; }
            catch { /* Startup failure was already reported; teardown must still run. */ }
        }
        await viewModel.ShutdownAsync();
    }

    private Task RequestGracefulShutdownAsync()
    {
        var coordinator = _shutdownCoordinator ?? throw new InvalidOperationException("Shutdown coordinator is not initialized.");
        return coordinator.RequestShutdownAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Normal Exit follows completed async teardown. This remains an idempotent
        // startup-failure/test fallback rather than the primary WPF close path.
        _viewModel?.Dispose();
        _logger?.Dispose();
        GC.SuppressFinalize(this);
    }
}
