using Grpc.Core;
using Grpc.Net.Client;
using LocalMediaAI.Infrastructure;
using LocalMediaAI.WorkerProtocol;
using GrpcContract = LocalMediaAI.WorkerProtocol.Grpc;
using Microsoft.Data.Sqlite;

namespace LocalMediaAI.Infrastructure.Tests;

// A portable Python Worker performs first health-call runtime initialization. It must not
// race unrelated Worker host tests for CUDA/runtime initialization and loopback RPC budget.
[CollectionDefinition(nameof(WorkerRuntimeSerial), DisableParallelization = true)]
public sealed class WorkerRuntimeSerial;

[Collection(nameof(WorkerRuntimeSerial))]
public sealed class WorkerRuntimeTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LocalMediaAIWorkerTests", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private JsonFileLogger _log = null!;
    private bool _fixtureDisposed;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "portable.flag"), string.Empty);
        _paths = AppPaths.Create(_root);
        _paths.EnsureDirectories();
        _log = new JsonFileLogger(_paths);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Dispose();
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; Directory.Exists(_root) && attempt < 8; attempt++)
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
    }

    public void Dispose()
    {
        if (_fixtureDisposed) return;
        _fixtureDisposed = true;
        _log?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task WorkerHealthVersionAndGracefulStopUsePortableRuntime()
    {
        using var manager = new WorkerProcessManager(_paths, _log, PortableOptions());
        var started = await manager.EnsureStartedAsync(CancellationToken.None);

        Assert.True(started.Availability == WorkerAvailability.Available, FailureDiagnostics(manager));
        Assert.Equal(ProtocolVersion.Current, started.Worker!.ProtocolVersion);
        Assert.NotNull(manager.ProcessId);
        Assert.True(File.Exists(PortablePythonPath()));

        await manager.StopAsync(CancellationToken.None);
        Assert.Equal(WorkerAvailability.Stopped, manager.Status.Availability);
        Assert.Null(manager.ProcessId);
    }

    [Fact]
    public async Task WorkerRejectsInvalidMetadataToken()
    {
        using var manager = new WorkerProcessManager(_paths, _log, PortableOptions());
        var started = await manager.EnsureStartedAsync(CancellationToken.None);
        Assert.True(started.Availability == WorkerAvailability.Available, FailureDiagnostics(manager));
        using var invalidClient = new GrpcLocalWorkerClient(Assert.IsType<int>(started.Port), "invalid-token", TimeSpan.FromSeconds(2));

        var error = await Assert.ThrowsAsync<RpcException>(() => invalidClient.HealthAsync(CancellationToken.None));

        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
        Assert.Equal(WorkerAvailability.Available, manager.Status.Availability);
    }

    [Fact]
    public async Task WorkerRejectsMissingMetadataToken()
    {
        using var manager = new WorkerProcessManager(_paths, _log, PortableOptions());
        var started = await manager.EnsureStartedAsync(CancellationToken.None);
        Assert.True(started.Availability == WorkerAvailability.Available, FailureDiagnostics(manager));
        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{Assert.IsType<int>(started.Port)}");
        var missingClient = new GrpcContract.LocalMediaWorker.LocalMediaWorkerClient(channel);

        var error = await Assert.ThrowsAsync<RpcException>(async () => await missingClient.VersionAsync(
            new GrpcContract.VersionRequest(),
            deadline: DateTime.UtcNow.AddSeconds(2)).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
        Assert.Equal(WorkerAvailability.Available, manager.Status.Availability);
    }

    [Fact]
    public void WorkerSourceAuditRejectsSqliteDbPathAndExternalNetworkOwnership()
    {
        var sourceRoot = Path.Combine(ProjectRoot(), "worker", "python");
        var source = File.ReadAllText(Path.Combine(sourceRoot, "worker.py"));
        var protocol = File.ReadAllText(Path.Combine(sourceRoot, "local_media_ai_worker.proto"));
        var runtimeSource = File.ReadAllText(Path.Combine(ProjectRoot(), "src", "LocalMediaAI.Infrastructure", "WorkerRuntime.cs"));

        Assert.DoesNotContain("sqlite3", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("library.db", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("database_path", protocol, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("db_path", protocol, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0.0.0.0", source, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ArgumentList.Add(_sessionToken)", runtimeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryDatabase", runtimeSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerProtocolMismatchIsReportedWithoutLeavingChild()
    {
        var overrides = new Dictionary<string, string> { ["LOCAL_MEDIA_AI_WORKER_PROTOCOL_VERSION_OVERRIDE"] = "999" };
        using var manager = new WorkerProcessManager(_paths, _log, PortableOptions(overrides));

        var status = await manager.EnsureStartedAsync(CancellationToken.None);

        Assert.True(status.Availability == WorkerAvailability.Unavailable, FailureDiagnostics(manager));
        Assert.True(status.ErrorCode == "WORKER_PROTOCOL_MISMATCH", FailureDiagnostics(manager));
        Assert.Null(manager.ProcessId);
    }

    [Fact]
    public async Task WorkerReadyTimeoutIsReportedWithoutLeavingChild()
    {
        var delayed = Path.Combine(_root, "never-ready.py");
        await File.WriteAllTextAsync(delayed, "import time\ntime.sleep(10)\n");
        var options = new WorkerProcessOptions(PortablePythonPath(), delayed, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(300));
        using var manager = new WorkerProcessManager(_paths, _log, options);

        var status = await manager.EnsureStartedAsync(CancellationToken.None);

        Assert.True(status.Availability == WorkerAvailability.Unavailable, FailureDiagnostics(manager));
        Assert.True(status.ErrorCode == "WORKER_READY_TIMEOUT", FailureDiagnostics(manager));
        Assert.Null(manager.ProcessId);
    }

    [Fact]
    public async Task RepeatedAsyncDisposalStopsAndReapsWorkerExactlyOnce()
    {
        var manager = new WorkerProcessManager(_paths, _log, PortableOptions());
        var started = await manager.EnsureStartedAsync(CancellationToken.None);
        Assert.Equal(WorkerAvailability.Available, started.Availability);
        var processId = Assert.IsType<int>(manager.ProcessId);

        await Task.WhenAll(manager.DisposeAsync().AsTask(), manager.DisposeAsync().AsTask());

        Assert.Equal(WorkerAvailability.Stopped, manager.Status.Availability);
        Assert.Null(manager.ProcessId);
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(processId));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.EnsureStartedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WorkerUnexpectedExitIsVisibleAndDoesNotThrowFromHost()
    {
        using var manager = new WorkerProcessManager(_paths, _log, PortableOptions());
        var started = await manager.EnsureStartedAsync(CancellationToken.None);
        Assert.True(started.Availability == WorkerAvailability.Available, FailureDiagnostics(manager));
        var processId = Assert.IsType<int>(manager.ProcessId);

        using var process = System.Diagnostics.Process.GetProcessById(processId);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        await WaitForAsync(() => manager.Status.Availability == WorkerAvailability.Unavailable, TimeSpan.FromSeconds(3));

        Assert.Equal("WORKER_EXITED_UNEXPECTEDLY", manager.Status.ErrorCode);
        Assert.Null(manager.ProcessId);
    }

    private static string FailureDiagnostics(WorkerProcessManager manager) => $"status={manager.Status.Availability}; code={manager.Status.ErrorCode}; stdout={string.Join(" | ", manager.CapturedStandardOutput)}; stderr={string.Join(" | ", manager.CapturedStandardError)}";

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var started = DateTimeOffset.UtcNow;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow - started > timeout) throw new TimeoutException("Expected worker state was not observed.");
            await Task.Delay(25);
        }
    }

    private static WorkerProcessOptions PortableOptions(IReadOnlyDictionary<string, string>? overrides = null) => new(
        PortablePythonPath(),
        PortableWorkerPath(),
        TimeSpan.FromSeconds(20),
        // Health and Version lazily import the portable AI runtime. Use the production
        // startup diagnostic budget; token tests must not measure cold import latency.
        TimeSpan.FromSeconds(20),
        // Production-equivalent shutdown grace prevents a healthy portable Python child
        // from being classified as a leaked host when another test assembly is ending concurrently.
        TimeSpan.FromSeconds(5),
        overrides);

    private static string PortablePythonPath() => Path.Combine(ProjectRoot(), "runtime", "python", "python.exe");
    private static string PortableWorkerPath() => Path.Combine(ProjectRoot(), "runtime", "worker", "local_media_ai_worker.py");

    private static string ProjectRoot()
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
