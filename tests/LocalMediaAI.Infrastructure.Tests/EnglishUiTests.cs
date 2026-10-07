using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalMediaAI.App;
using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

/// <summary>The language is app-wide, so these run alone, after the parallel tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnglishUiGroup
{
    public const string Name = "English UI";
}

/// <summary>
/// The app in English: every screen loads, and no Japanese is left in what is shown (file and folder names of the
/// fixture are plain ASCII, so any Japanese on screen is untranslated app text). Screens are saved to
/// artifacts/ui-english for a look.
/// </summary>
[Collection(EnglishUiGroup.Name)]
public sealed partial class EnglishUiTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LocalMediaAIEnglishUi", Guid.NewGuid().ToString("N"));
    private JsonFileLogger _log = null!;
    private SqliteLibraryRepository _library = null!;
    private MainViewModel _vm = null!;
    private AppPaths _paths = null!;

    public async Task InitializeAsync()
    {
        L.Use(UiLanguage.English);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_directory);
        _paths.EnsureDirectories();
        _log = new JsonFileLogger(_paths);
        _library = new SqliteLibraryRepository(_paths, _log);
        await _library.InitializeAsync(CancellationToken.None);
        var media = Directory.CreateDirectory(Path.Combine(_directory, "media")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(media, "clip-a.mp4"), new byte[120]);
        await File.WriteAllBytesAsync(Path.Combine(media, "clip-b.mp4"), new byte[130]);
        var root = await _library.AddOrGetLibraryRootAsync(media, CancellationToken.None);
        var jobs = new SqliteJobStore(_paths, _log);
        var worker = new WorkerProcessManager(_paths, _log);
        var pipeline = new MediaPipeline(_library, jobs, new FileSystemMediaScanner(), new FfmpegAdapter(new FfmpegPaths(null, null), _paths, _log), _log, worker: worker, paths: _paths);
        await pipeline.EnqueueScanAsync(root.LibraryRootId, CancellationToken.None);
        await pipeline.RunUntilIdleAsync(CancellationToken.None);
        _vm = new MainViewModel(_library, jobs, pipeline, worker, new NoSearch(), _log, fileOperations: new SafeFileOperationService(_library, _log));
    }

    public Task DisposeAsync()
    {
        L.Use(UiLanguage.Japanese);
        _vm.Dispose();
        _log.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task EveryScreenShowsEnglishOnly()
    {
        _vm.RefreshCommand.Execute(null);
        for (var i = 0; i < 200 && !_vm.RefreshCommand.CanExecute(null); i++) await Task.Delay(25);
        var organize = new LocalMediaAI.App.Organize.OrganizeViewModel(new LocalMediaAI.App.Organize.RepositoryOrganizeDataSource(_library)) { SaveLanguage = _ => { }, ReadLanguage = () => UiLanguage.Japanese };
        await organize.StartAsync(CancellationToken.None);
        var leftovers = new List<string>();
        await OnUiThreadAsync(() =>
        {
            var window = new MainWindow { DataContext = _vm, Width = 1600, Height = 1000, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.AttachOrganize(organize);
                window.Show();
                Settle(window);
                leftovers.AddRange(Japanese(window, "organize"));
                Save(window, "organize");
                var screen = (LocalMediaAI.App.Organize.OrganizeView)window.FindName("OrganizeScreen");
                typeof(LocalMediaAI.App.Organize.OrganizeView).GetMethod("ShowSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(screen, null);
                for (var i = 0; i < 40; i++) Settle(window);
                var settings = Japanese(window, "settings").ToArray();
                // The language names stay in their own language so either can be found: 日本語 is expected here.
                leftovers.AddRange(settings.Where(text => !text.EndsWith(": 日本語", StringComparison.Ordinal) && !text.EndsWith(": 英語", StringComparison.Ordinal)));
                Assert.Contains(settings, text => text.EndsWith(": 日本語", StringComparison.Ordinal));
                // Showing English but Japanese is saved for the next start: Settings shows the saved choice, so it can be undone.
                var japaneseChoice = Descendants(window).OfType<RadioButton>().Single(r => System.Windows.Automation.AutomationProperties.GetName(r) == "日本語 (Japanese)");
                Assert.True(japaneseChoice.IsChecked);
                Save(window, "settings");
                typeof(MainWindow).GetMethod("ShowLegacyScreen", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                var tabs = (TabControl)window.FindName("TaskWorkspaceTabs");
                foreach (var (index, name) in new[] { (0, "library"), (1, "ai-search"), (2, "review") })
                {
                    tabs.SelectedIndex = index;
                    Settle(window);
                    leftovers.AddRange(Japanese(window, name));
                    Save(window, name);
                }
                window.ShowOperationWorkspaceForRecovery();
                Settle(window);
                leftovers.AddRange(Japanese(window, "file-operations"));
                Save(window, "file-operations");
            }
            finally
            {
                window.DataContext = null;
                window.AuthorizeFinalClose();
                window.Close();
            }
        });
        Assert.True(leftovers.Count == 0, "Japanese left on screen:\n" + string.Join("\n", leftovers.Distinct().Take(60)));
    }

    private sealed class NoSearch : ISemanticSearchService
    {
        public SemanticModelProfile SemanticModel => SemanticModelRegistry.ResolveDevelopmentCandidate();
        public SemanticExecutionProfile SemanticExecution => new("cuda", "float32");
        public Task<IReadOnlyList<SemanticSearchCandidate>> SearchAsync(string query, int topK, CancellationToken cancellationToken, Guid? libraryRootId = null) => Task.FromResult<IReadOnlyList<SemanticSearchCandidate>>([]);
    }

    private static IEnumerable<string> Japanese(DependencyObject root, string screen)
    {
        foreach (var element in Descendants(root).OfType<FrameworkElement>().Where(e => e.IsVisible))
        {
            var text = element switch
            {
                TextBlock block => block.Text,
                ContentControl { Content: string content } => content,
                _ => null
            };
            if (text is not null && JapaneseText().IsMatch(text)) yield return $"[{screen}] {element.GetType().Name}: {text}";
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static void Save(Window window, string name)
    {
        var directory = Path.Combine(RepositoryRoot(), "artifacts", "ui-english");
        Directory.CreateDirectory(directory);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }

    private static Task OnUiThreadAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "LocalMediaAI.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository root was not found.");
    }

    [GeneratedRegex(@"[\u3000-\u303f\u3040-\u30ff\u4e00-\u9fff\uff01-\uff60]")]
    private static partial Regex JapaneseText();
}
