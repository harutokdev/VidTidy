using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using LocalMediaAI.Core;
using LocalMediaAI.WorkerProtocol;
using GrpcContract = LocalMediaAI.WorkerProtocol.Grpc;

namespace LocalMediaAI.Infrastructure;

public sealed record WorkerProcessOptions(
    string PythonExecutablePath,
    string WorkerScriptPath,
    TimeSpan ReadyTimeout,
    TimeSpan RpcTimeout,
    TimeSpan GracefulShutdownTimeout,
    IReadOnlyDictionary<string, string>? EnvironmentOverrides = null)
{
    public static WorkerProcessOptions Resolve(AppPaths paths, SemanticModelProfile? semanticModel = null)
    {
        var model = semanticModel ?? SemanticModelRegistry.ResolveDevelopmentCandidate();
        return new(
            Path.Combine(paths.RuntimeRoot, model.RuntimeDirectoryName, "python.exe"),
            Path.Combine(paths.RuntimeRoot, "worker", "local_media_ai_worker.py"),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromSeconds(5));
    }
}

public sealed record WorkerReadyHandshake(string Kind, int Port, int ProtocolVersion);

public sealed class WorkerExitedUnexpectedlyException : Exception
{
    public const string ErrorCode = "WORKER_EXITED_UNEXPECTEDLY";

    public WorkerExitedUnexpectedlyException(Exception innerException)
        : base("Worker process exited unexpectedly while the job was running.", innerException)
    {
    }
}

public sealed class GrpcLocalWorkerClient : ILocalWorkerClient, IDisposable
{
    private const string TokenHeader = "x-local-media-ai-token";
    private readonly GrpcChannel _channel;
    private readonly GrpcContract.LocalMediaWorker.LocalMediaWorkerClient _client;
    private readonly string _token;
    private readonly TimeSpan _timeout;

    public GrpcLocalWorkerClient(int port, string token, TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
        _client = new GrpcContract.LocalMediaWorker.LocalMediaWorkerClient(_channel);
        _token = token;
        _timeout = timeout;
    }

    public async Task<WorkerInfo> HealthAsync(CancellationToken cancellationToken)
    {
        var response = await _client.HealthAsync(new GrpcContract.HealthRequest(), CallOptions(cancellationToken)).ResponseAsync;
        return ToWorkerInfo(response.Worker);
    }

    public async Task<WorkerVersion> VersionAsync(CancellationToken cancellationToken)
    {
        var response = await _client.VersionAsync(new GrpcContract.VersionRequest(), CallOptions(cancellationToken)).ResponseAsync;
        return new WorkerVersion(ToWorkerInfo(response.Worker), response.Capabilities.ToArray());
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        var response = await _client.ShutdownAsync(new GrpcContract.ShutdownRequest(), CallOptions(cancellationToken)).ResponseAsync;
        if (!response.Accepted) throw new InvalidOperationException("Worker rejected graceful shutdown.");
    }

    public async Task<WorkerJobResult> RunAsync(WorkerJobRequest request, IProgress<WorkerJobProgress>? progress, CancellationToken cancellationToken)
    {
        var grpcRequest = new GrpcContract.JobRequest
        {
            JobId = request.JobId.ToString("D"),
            JobType = request.JobType,
            ConfigJson = request.ConfigJson,
            ExpectedProcessorVersion = request.ExpectedProcessorVersion
        };
        foreach (var descriptor in request.InputDescriptors)
            grpcRequest.InputDescriptors[descriptor.Key] = descriptor.Value;
        using var call = _client.RunJob(grpcRequest, CallOptions(cancellationToken));
        WorkerJobResult? result = null;
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            var current = call.ResponseStream.Current;
            switch (current.PayloadCase)
            {
                case GrpcContract.JobProgressOrResult.PayloadOneofCase.Progress:
                    var update = current.Progress;
                    progress?.Report(new WorkerJobProgress(Guid.Parse(update.JobId), update.Phase, update.Current, update.Total, update.Unit, DateTimeOffset.TryParse(update.HeartbeatUtc, out var heartbeat) ? heartbeat : DateTimeOffset.UtcNow, update.Metrics.ToDictionary(metric => metric.Key, metric => metric.Value)));
                    break;
                case GrpcContract.JobProgressOrResult.PayloadOneofCase.Result:
                    var completed = current.Result;
                    result = new WorkerJobResult(Guid.Parse(completed.JobId), completed.Outcome, completed.Artifacts.Select(x => new ProducedArtifactDescriptor(x.Kind, x.RelativePath, x.Sha256, x.ByteSize, x.MimeType)).ToArray(), EmptyToNull(completed.StructuredResultJson), EmptyToNull(completed.ErrorCode), EmptyToNull(completed.ErrorMessage));
                    break;
            }
        }
        return result ?? throw new InvalidOperationException("Worker stream completed without a result.");
    }

    public async Task CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await _client.CancelJobAsync(new GrpcContract.CancelJobRequest { JobId = jobId.ToString("D") }, CallOptions(cancellationToken)).ResponseAsync;
    }

    public void Dispose() => _channel.Dispose();

    private CallOptions CallOptions(CancellationToken cancellationToken) => new(
        headers: new Metadata { { TokenHeader, _token } },
        deadline: DateTime.UtcNow.Add(_timeout),
        cancellationToken: cancellationToken);

    private static WorkerInfo ToWorkerInfo(GrpcContract.WorkerInfo value) => new(value.ProtocolVersion, value.WorkerVersion, value.PythonVersion, value.BackendAvailable, EmptyToNull(value.BackendDetail));
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

public sealed class WorkerProcessManager : IAsyncDisposable, IDisposable
{
    private const int MaximumCapturedLineLength = 2_048;
    private const int MaximumCapturedLines = 64;
    private readonly AppPaths _paths;
    private readonly IStructuredLogger _log;
    private readonly WorkerProcessOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _requestAdmission = new(1, 1);
    private readonly object _disposeGate = new();
    private readonly BoundedProcessOutput _stdout = new(MaximumCapturedLines, MaximumCapturedLineLength);
    private readonly BoundedProcessOutput _stderr = new(MaximumCapturedLines, MaximumCapturedLineLength);
    private Process? _process;
    private GrpcLocalWorkerClient? _client;
    private string? _sessionToken;
    private bool _stopRequested;
    private bool _disposed;
    private bool _terminalDisposalRequested;
    private TaskCompletionSource<bool>? _disposeCompletion;
    private WorkerRuntimeStatus _status = new(WorkerAvailability.NotStarted, null, null, null, null, null, DateTimeOffset.UtcNow);

    public WorkerProcessManager(AppPaths paths, IStructuredLogger log, WorkerProcessOptions? options = null)
    {
        _paths = paths;
        _log = log;
        _options = options ?? WorkerProcessOptions.Resolve(paths);
    }

    public event EventHandler<WorkerRuntimeStatus>? StatusChanged;
    public WorkerRuntimeStatus Status => _status;
    public int? ProcessId => _process is { HasExited: false } process ? process.Id : null;
    public IReadOnlyList<string> CapturedStandardOutput => _stdout.Snapshot();
    public IReadOnlyList<string> CapturedStandardError => _stderr.Snapshot();

    public Task<WorkerRuntimeStatus> EnsureStartedAsync(CancellationToken cancellationToken) =>
        EnsureStartedCoreAsync(allowAdmittedRequestDuringTerminalDisposal: false, cancellationToken);

    private async Task<WorkerRuntimeStatus> EnsureStartedCoreAsync(bool allowAdmittedRequestDuringTerminalDisposal, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!allowAdmittedRequestDuringTerminalDisposal) ThrowIfTerminalDisposalRequested();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!allowAdmittedRequestDuringTerminalDisposal) ThrowIfTerminalDisposalRequested();
            if (_status.Availability == WorkerAvailability.Available) return _status;
            SetStatus(WorkerAvailability.Starting, null, null, null, null, null);
            if (!File.Exists(_options.PythonExecutablePath) || !File.Exists(_options.WorkerScriptPath))
                return FailStart("WORKER_RUNTIME_NOT_FOUND", L.T("Worker runtime が RuntimeRoot に配置されていません。"), null);

            _stdout.Clear();
            _stderr.Clear();
            _stopRequested = false;
            _sessionToken = CreateToken();
            var process = new Process { StartInfo = CreateStartInfo(_sessionToken), EnableRaisingEvents = true };
            process.ErrorDataReceived += OnErrorLine;
            process.Exited += OnExited;
            if (!process.Start()) return FailStart("WORKER_START_FAILED", L.T("Worker processを開始できませんでした。"), null);
            _process = process;
            process.BeginErrorReadLine();

            WorkerReadyHandshake ready;
            try { ready = await ReadReadyHandshakeAsync(process, cancellationToken); }
            catch (Exception ex)
            {
                await StopProcessLockedAsync(CancellationToken.None, suppressExitEvent: true);
                return FailStart(ex is TimeoutException ? "WORKER_READY_TIMEOUT" : "WORKER_READY_INVALID", L.T("Workerの起動確認に失敗しました。"), ex);
            }

            if (ready.ProtocolVersion != ProtocolVersion.Current)
            {
                await StopProcessLockedAsync(CancellationToken.None, suppressExitEvent: true);
                return FailStart("WORKER_PROTOCOL_MISMATCH", L.T("Worker protocol versionがこのアプリと一致しません。"), null);
            }

            _client = new GrpcLocalWorkerClient(ready.Port, _sessionToken, _options.RpcTimeout);
            try
            {
                var health = await _client.HealthAsync(cancellationToken);
                var version = await _client.VersionAsync(cancellationToken);
                if (health.ProtocolVersion != ProtocolVersion.Current || version.Worker.ProtocolVersion != ProtocolVersion.Current)
                    throw new WorkerProtocolMismatchException();
                if (!version.Capabilities.Contains("health", StringComparer.Ordinal) || !version.Capabilities.Contains("version", StringComparer.Ordinal))
                    throw new InvalidOperationException("Worker did not advertise mandatory diagnostic capabilities.");
                SetStatus(WorkerAvailability.Available, null, null, process.Id, ready.Port, health);
                WriteLog("Information", "WorkerAvailable", new { ProcessId = process.Id, ready.Port, health.ProtocolVersion, health.WorkerVersion, health.PythonVersion, Capabilities = version.Capabilities });
                return _status;
            }
            catch (Exception ex)
            {
                await StopProcessLockedAsync(CancellationToken.None, suppressExitEvent: true);
                return FailStart(ex is WorkerProtocolMismatchException ? "WORKER_PROTOCOL_MISMATCH" : "WORKER_HEALTH_FAILED", L.T("WorkerのHealth/Version確認に失敗しました。"), ex);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<WorkerJobResult> RunAsync(WorkerJobRequest request, IProgress<WorkerJobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        await _requestAdmission.WaitAsync(cancellationToken);
        try
        {
            lock (_disposeGate) ThrowIfTerminalDisposalRequested();
            try
            {
                var status = await EnsureStartedCoreAsync(allowAdmittedRequestDuringTerminalDisposal: true, cancellationToken);
                if (status.Availability != WorkerAvailability.Available || _client is null)
                {
                    if (string.Equals(status.ErrorCode, WorkerExitedUnexpectedlyException.ErrorCode, StringComparison.Ordinal))
                        throw new WorkerExitedUnexpectedlyException(new InvalidOperationException(status.UserMessage));
                    throw new InvalidOperationException(status.UserMessage ?? L.T("Workerを利用できません。"));
                }
                var version = await _client.VersionAsync(cancellationToken);
                if (!version.Capabilities.Contains(request.JobType, StringComparer.Ordinal))
                    throw new InvalidOperationException($"Worker does not advertise capability '{request.JobType}'.");
                try
                {
                    return await _client.RunAsync(request, progress, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && !_stopRequested)
                {
                    throw new WorkerExitedUnexpectedlyException(ex);
                }
            }
            catch (WorkerExitedUnexpectedlyException)
            {
                throw;
            }
            catch (RpcException ex) when (!cancellationToken.IsCancellationRequested && !_stopRequested)
            {
                throw new WorkerExitedUnexpectedlyException(ex);
            }
            catch (Exception ex) when (HasUnexpectedWorkerExit())
            {
                throw new WorkerExitedUnexpectedlyException(ex);
            }
        }
        finally
        {
            _requestAdmission.Release();
        }
    }

    private bool HasUnexpectedWorkerExit()
    {
        if (string.Equals(_status.ErrorCode, WorkerExitedUnexpectedlyException.ErrorCode, StringComparison.Ordinal)) return true;
        return !_stopRequested && _process is { HasExited: true };
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return;
        await _requestAdmission.WaitAsync(cancellationToken);
        try { await StopCoreAsync(cancellationToken); }
        finally { _requestAdmission.Release(); }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is null) { SetStatus(WorkerAvailability.Stopped, null, null, null, null, null); return; }
            SetStatus(WorkerAvailability.Stopping, null, null, ProcessId, _status.Port, _status.Worker);
            _stopRequested = true;
            if (_client is not null)
            {
                using var gracefulCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                gracefulCts.CancelAfter(_options.GracefulShutdownTimeout);
                try { await _client.ShutdownAsync(gracefulCts.Token); } catch (Exception ex) { WriteLog("Warning", "WorkerGracefulShutdownFailed", new { ErrorCode = "WORKER_GRACEFUL_SHUTDOWN_FAILED" }, ex); }
            }
            await StopProcessLockedAsync(cancellationToken);
            SetStatus(WorkerAvailability.Stopped, null, null, null, null, null);
            WriteLog("Information", "WorkerStopped", new { });
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await RequestDisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private Task<bool> RequestDisposeAsync()
    {
        TaskCompletionSource<bool> completion;
        lock (_disposeGate)
        {
            if (_disposeCompletion is not null) return _disposeCompletion.Task;
            _terminalDisposalRequested = true;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeCompletion = completion;
        }
        _ = DisposeCoreAsync(completion);
        return completion.Task;
    }

    private async Task DisposeCoreAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            await StopAsync(CancellationToken.None);
            _disposed = true;
            _gate.Dispose();
            _requestAdmission.Dispose();
            completion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    // MainViewModel.Dispose is reached from WPF Application.Exit on the UI thread.
    // StopAsync awaits loopback gRPC/process shutdown, so synchronously blocking that
    // same synchronization context would deadlock a successful Worker shutdown.
    public void Dispose() => Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();

    private ProcessStartInfo CreateStartInfo(string token)
    {
        var start = new ProcessStartInfo(_options.PythonExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_options.WorkerScriptPath)!
        };
        start.ArgumentList.Add(_options.WorkerScriptPath);
        start.ArgumentList.Add("--serve");
        start.Environment["LOCAL_MEDIA_AI_WORKER_TOKEN"] = token;
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["HF_DATASETS_OFFLINE"] = "1";
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        if (_options.EnvironmentOverrides is not null)
            foreach (var overrideValue in _options.EnvironmentOverrides)
                start.Environment[overrideValue.Key] = overrideValue.Value;
        return start;
    }

    private async Task<WorkerReadyHandshake> ReadReadyHandshakeAsync(Process process, CancellationToken cancellationToken)
    {
        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCts.CancelAfter(_options.ReadyTimeout);
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(startupCts.Token);
            if (line is null) throw new InvalidOperationException("Worker exited before ready handshake.");
            _stdout.Add(line);
            if (TryParseReady(line, out var ready)) return ready;
            throw new InvalidOperationException("Worker ready handshake is invalid.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Worker ready handshake timed out.");
        }
    }

    private async Task StopProcessLockedAsync(CancellationToken cancellationToken, bool suppressExitEvent = false)
    {
        if (suppressExitEvent) _stopRequested = true;
        var process = _process;
        var client = _client;
        _client = null;
        _process = null;
        _sessionToken = null;
        client?.Dispose();
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(_options.GracefulShutdownTimeout, CancellationToken.None); }
                catch (TimeoutException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(_options.GracefulShutdownTimeout, CancellationToken.None);
                }
            }
        }
        finally { process.Dispose(); }
    }

    private void OnErrorLine(object sender, DataReceivedEventArgs eventArgs)
    {
        if (eventArgs.Data is { } line) _stderr.Add(line);
    }

    private void OnExited(object? sender, EventArgs eventArgs)
    {
        if (_stopRequested || _disposed) return;
        var exited = sender as Process;
        var exitCode = exited?.ExitCode;
        _client?.Dispose();
        _client = null;
        _sessionToken = null;
        _process = null;
        SetStatus(WorkerAvailability.Unavailable, "WORKER_EXITED_UNEXPECTEDLY", L.T("Worker processが予期せず終了しました。"), null, null, null);
        WriteLog("Warning", "WorkerExitedUnexpectedly", new { ErrorCode = "WORKER_EXITED_UNEXPECTEDLY", ExitCode = exitCode, StdoutLines = _stdout.Count, StderrLines = _stderr.Count });
    }

    private WorkerRuntimeStatus FailStart(string code, string userMessage, Exception? exception)
    {
        SetStatus(WorkerAvailability.Unavailable, code, userMessage, null, null, null);
        WriteLog("Warning", "WorkerUnavailable", new { ErrorCode = code }, exception);
        return _status;
    }

    private void SetStatus(WorkerAvailability availability, string? errorCode, string? userMessage, int? processId, int? port, WorkerInfo? worker)
    {
        _status = new WorkerRuntimeStatus(availability, errorCode, userMessage, processId, port, worker, DateTimeOffset.UtcNow);
        StatusChanged?.Invoke(this, _status);
    }

    private void WriteLog(string level, string eventName, object fields, Exception? exception = null)
    {
        try { _log.Write(level, eventName, fields, exception); }
        catch (ObjectDisposedException) { }
    }

    private static bool TryParseReady(string line, out WorkerReadyHandshake ready)
    {
        ready = null!;
        if (line.Length > MaximumCapturedLineLength) return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("kind", out var kind) || kind.GetString() != "ready" ||
                !root.TryGetProperty("port", out var port) || !port.TryGetInt32(out var portNumber) || portNumber is < 1 or > 65535 ||
                !root.TryGetProperty("protocol_version", out var version) || !version.TryGetInt32(out var protocolVersion))
                return false;
            ready = new WorkerReadyHandshake("ready", portNumber, protocolVersion);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void ThrowIfTerminalDisposalRequested()
    {
        if (_terminalDisposalRequested)
            throw new ObjectDisposedException(nameof(WorkerProcessManager), "Worker terminal disposal is in progress.");
    }

    private sealed class WorkerProtocolMismatchException : Exception { }

    private sealed class BoundedProcessOutput(int maximumLines, int maximumLineLength)
    {
        private readonly Queue<string> _lines = new();
        private readonly object _sync = new();
        public int Count { get { lock (_sync) return _lines.Count; } }
        public void Add(string line)
        {
            var bounded = line.Length <= maximumLineLength ? line : line[..maximumLineLength];
            lock (_sync)
            {
                if (_lines.Count == maximumLines) _lines.Dequeue();
                _lines.Enqueue(bounded);
            }
        }
        public string[] Snapshot() { lock (_sync) return _lines.ToArray(); }
        public void Clear() { lock (_sync) _lines.Clear(); }
    }
}
