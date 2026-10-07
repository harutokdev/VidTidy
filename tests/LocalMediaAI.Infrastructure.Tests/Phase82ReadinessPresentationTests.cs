using LocalMediaAI.App.ViewModels;
using LocalMediaAI.Core;
using LocalMediaAI.WorkerProtocol;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class Phase82ReadinessPresentationTests
{
    [Fact]
    public void EvaluatorMapsExistingProductStateToUserFacingNextActions()
    {
        var ready = new ReadinessInput(true, 1, true, RootAvailability.Online, true, 23, 10, 0, 0, false, WorkerAvailability.NotStarted, 0);
        var cases = new (ReadinessInput Input, string State, ReadinessAction Action)[]
        {
            (ready with { RootCount = 0, HasSelectedRoot = false, HasCompletedScan = false, TotalMediaCount = 0, IndexedMediaCount = 0 }, "NoRoot", ReadinessAction.RegisterFolder),
            (ready with { HasSelectedRoot = false }, "RootNotSelected", ReadinessAction.OpenLibraryRoots),
            (ready with { SelectedRootAvailability = RootAvailability.Offline }, "RootOffline", ReadinessAction.Rescan),
            (ready with { ActiveJobCount = 2, ActiveScanJobCount = 1 }, "Scanning", ReadinessAction.Refresh),
            (ready with { ActiveJobCount = 2, ActiveScanJobCount = 0 }, "Processing", ReadinessAction.Refresh),
            (ready with { WorkerAvailability = WorkerAvailability.Unavailable }, "WorkerUnavailable", ReadinessAction.RecheckWorker),
            (ready with { ReviewTargetCount = 3 }, "ReviewReady", ReadinessAction.OpenReview),
            (ready, "AiReady", ReadinessAction.OpenAiSearch),
            (ready with { IndexedMediaCount = 0 }, "WorkerNotStarted", ReadinessAction.RecheckWorker)
        };

        foreach (var item in cases)
        {
            var actual = ReadinessPresentation.Evaluate(item.Input);
            Assert.Equal(item.State, actual.State);
            Assert.Equal(item.Action, actual.Action);
            Assert.True(actual.HasAction);
            Assert.DoesNotContain("Finalized", actual.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("FullHashCandidates", actual.Meaning, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(ReadinessAction.Refresh, ReadinessPresentation.Evaluate(ready with { ActiveJobCount = 1, ActiveScanJobCount = 1 }).Action);
        Assert.Equal(ReadinessAction.Refresh, ReadinessPresentation.Evaluate(ready with { WorkerAvailability = WorkerAvailability.Starting }).Action);
    }

    [Fact]
    public void MainWindowKeepsPhase81NavigationAndAddsACompactStableBanner()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "LocalMediaAI.App", "MainWindow.xaml"));

        Assert.Contains("AutomationProperties.AutomationId=\"TaskWorkspaceTabs\"", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"LibraryMediaDataGrid\"", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ReadinessBanner\"", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ReadinessTitle\"", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ReadinessMeaning\"", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"ReadinessPrimaryAction\"", source, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"68\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RootRefreshCapturesSelectionBeforeReplacingTheBoundCollection()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "LocalMediaAI.App", "ViewModels", "MainViewModel.cs"));
        var captureIndex = source.IndexOf("var selectedRootId = SelectedRoot?.LibraryRootId;", StringComparison.Ordinal);
        var clearIndex = source.IndexOf("Roots.Clear();", captureIndex, StringComparison.Ordinal);
        var restoreIndex = source.IndexOf("x.LibraryRootId == selectedRootId.Value", clearIndex, StringComparison.Ordinal);

        Assert.True(captureIndex >= 0, "The selected root ID must be captured before a bound collection refresh.");
        Assert.True(clearIndex > captureIndex, "The root collection must only be cleared after capturing its selection.");
        Assert.True(restoreIndex > clearIndex, "The refreshed root instance must be restored from the captured ID.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LocalMediaAI.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find LocalMediaAI.sln from test base directory.");
    }
}
