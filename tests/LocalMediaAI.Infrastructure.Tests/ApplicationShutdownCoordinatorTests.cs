using LocalMediaAI.App;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class ApplicationShutdownCoordinatorTests
{
    [Fact]
    public async Task RepeatedCloseRequestsJoinOneNonBlockingTeardownAndAuthorizeOnce()
    {
        var teardownEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTeardown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardownCalls = 0;
        var authorizationCalls = 0;
        var reportedFailures = 0;
        var linearizationCalls = 0;
        var coordinator = new ApplicationShutdownCoordinator(async () =>
        {
            Interlocked.Increment(ref teardownCalls);
            teardownEntered.TrySetResult(true);
            await releaseTeardown.Task;
        }, () => Interlocked.Increment(ref authorizationCalls), _ => Interlocked.Increment(ref reportedFailures),
            () => Interlocked.Increment(ref linearizationCalls));

        var first = coordinator.RequestShutdownAsync();
        await teardownEntered.Task;
        var repeated = coordinator.RequestShutdownAsync();

        Assert.Same(first, repeated);
        Assert.True(coordinator.IsShutdownStarted);
        Assert.False(first.IsCompleted);
        Assert.False(coordinator.IsFinalCloseAuthorized);
        Assert.Equal(1, teardownCalls);
        Assert.Equal(1, linearizationCalls);

        releaseTeardown.TrySetResult(true);
        await first;

        Assert.True(coordinator.IsFinalCloseAuthorized);
        Assert.Equal(1, teardownCalls);
        Assert.Equal(1, authorizationCalls);
        Assert.Equal(0, reportedFailures);
    }

    [Fact]
    public async Task TeardownFailureRemainsSharedAndDoesNotAuthorizeFinalClose()
    {
        var expected = new InvalidOperationException("synthetic teardown failure");
        var teardownCalls = 0;
        var authorizationCalls = 0;
        var reportedFailures = 0;
        var coordinator = new ApplicationShutdownCoordinator(() =>
        {
            Interlocked.Increment(ref teardownCalls);
            return Task.FromException(expected);
        }, () => Interlocked.Increment(ref authorizationCalls), exception =>
        {
            Assert.Same(expected, exception);
            Interlocked.Increment(ref reportedFailures);
        });

        var first = coordinator.RequestShutdownAsync();
        var repeated = coordinator.RequestShutdownAsync();

        Assert.Same(first, repeated);
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => first));
        Assert.False(coordinator.IsFinalCloseAuthorized);
        Assert.Equal(1, teardownCalls);
        Assert.Equal(0, authorizationCalls);
        Assert.Equal(1, reportedFailures);
    }

    [Fact]
    public void WpfCloseBridgeDefersFirstCloseAndKeepsFinalCloseOneWay()
    {
        var repositoryRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "LocalMediaAI.App", "MainWindow.xaml"));
        var window = File.ReadAllText(Path.Combine(repositoryRoot, "src", "LocalMediaAI.App", "MainWindow.xaml.cs"));
        var app = File.ReadAllText(Path.Combine(repositoryRoot, "src", "LocalMediaAI.App", "App.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(repositoryRoot, "src", "LocalMediaAI.App", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("Closing=\"Window_OnClosing\"", xaml, StringComparison.Ordinal);
        Assert.Contains("eventArgs.Cancel = true", window, StringComparison.Ordinal);
        Assert.Contains("await request()", window, StringComparison.Ordinal);
        Assert.Contains("_finalCloseAuthorized", window, StringComparison.Ordinal);
        Assert.Contains("new ApplicationShutdownCoordinator", app, StringComparison.Ordinal);
        Assert.Contains("await viewModel.ShutdownAsync()", app, StringComparison.Ordinal);
        Assert.Contains("await _terminalPipelineDrain!", viewModel, StringComparison.Ordinal);
        Assert.Contains("await _worker.DisposeAsync()", viewModel, StringComparison.Ordinal);
        Assert.True(viewModel.IndexOf("await _terminalPipelineDrain!", StringComparison.Ordinal)
            < viewModel.IndexOf("_lifetime.Cancel();", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalApplicationShutdownEntriesCannotBypassAppOwnedCoordinator()
    {
        var repositoryRoot = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(repositoryRoot, "src", "LocalMediaAI.App", "App.xaml.cs"));
        var window = File.ReadAllText(Path.Combine(repositoryRoot, "src", "LocalMediaAI.App", "MainWindow.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(repositoryRoot, "src", "LocalMediaAI.App", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("private ApplicationShutdownCoordinator? _shutdownCoordinator", app, StringComparison.Ordinal);
        Assert.Contains("GracefulShutdownRequest = RequestGracefulShutdownAsync", app, StringComparison.Ordinal);
        Assert.Contains("await RequestGracefulShutdownAsync()", app, StringComparison.Ordinal);
        Assert.Equal(1, Count(app, "Shutdown(-1)"));
        Assert.Equal(1, Count(window, "Close();"));
        Assert.Contains("_mainWindow.AuthorizeFinalClose()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ExitCommand", viewModel, StringComparison.Ordinal);
        Assert.Equal(1, Count(viewModel, "_pipeline.Dispose();"));
        Assert.Equal(1, Count(viewModel, "_lifetime.Cancel();"));
    }

    [Fact]
    public void StartupRequestsCleanupOnlyAfterRepositoryDependenciesAreInitializedAndDoesNotAwaitCompletion()
    {
        var repositoryRoot = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(repositoryRoot, "src", "LocalMediaAI.App", "App.xaml.cs"));
        var viewModel = File.ReadAllText(Path.Combine(repositoryRoot, "src", "LocalMediaAI.App", "ViewModels", "MainViewModel.cs"));
        var initialize = viewModel[viewModel.IndexOf("public async Task InitializeAsync()", StringComparison.Ordinal)..viewModel.IndexOf("private async Task RegisterRootAsync()", StringComparison.Ordinal)];

        var repositoryInitialization = initialize.IndexOf("await _library.InitializeAsync", StringComparison.Ordinal);
        var jobRecovery = initialize.IndexOf("await _jobs.RecoverInterruptedJobsAsync", StringComparison.Ordinal);
        var request = initialize.IndexOf("_pipeline.RequestRetentionCleanup(\"Startup\")", StringComparison.Ordinal);
        var continuation = initialize.IndexOf("await LoadRootsAsync", StringComparison.Ordinal);
        Assert.True(repositoryInitialization >= 0 && repositoryInitialization < request);
        Assert.True(jobRecovery >= 0 && jobRecovery < request);
        Assert.True(request < continuation);
        Assert.DoesNotContain("await _library.RunRetentionCleanupAsync", initialize, StringComparison.Ordinal);
        Assert.Equal(1, Count(app, "new RetentionCleanupCoordinator("));
        Assert.Contains("retentionCleanup: retentionCleanup", app, StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "LocalMediaAI.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("LocalMediaAI solution root was not found.");
    }
}
