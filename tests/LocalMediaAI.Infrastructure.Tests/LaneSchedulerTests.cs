using System.Collections.Concurrent;
using LocalMediaAI.Core.Processing;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class LaneSchedulerTests
{
    private static readonly ProcessingSettings Fastest = new(ProcessingPace.Fastest, false);

    [Fact]
    public async Task ComputeWorkRunsWhileTheSourceDriveIsStillBusy()
    {
        var world = new FakeWorld();
        world.AddSource("disk-1", 4, producesCompute: 1, sourceDelay: TimeSpan.FromMilliseconds(60));
        var scheduler = world.Scheduler(() => Fastest);

        Assert.True(await scheduler.RunAsync([world.Lane("disk-1")], CancellationToken.None));

        Assert.Equal(4, world.ComputeDone);
        Assert.True(world.FirstComputeStart < world.LastSourceEnd, "compute should overlap source work");
    }

    [Fact]
    public async Task EachDriveServesOneJobAtATimeButDrivesRunInParallel()
    {
        var world = new FakeWorld();
        world.AddSource("disk-1", 5, producesCompute: 0, sourceDelay: TimeSpan.FromMilliseconds(30));
        world.AddSource("disk-2", 5, producesCompute: 0, sourceDelay: TimeSpan.FromMilliseconds(30));
        var scheduler = world.Scheduler(() => Fastest);

        await scheduler.RunAsync([world.Lane("disk-1"), world.Lane("disk-2")], CancellationToken.None);

        Assert.Equal(1, world.MaxConcurrent("disk-1"));
        Assert.Equal(1, world.MaxConcurrent("disk-2"));
        Assert.Equal(2, world.MaxConcurrentSourceOverall);
    }

    [Fact]
    public async Task ARestingDriveDoesNotHoldBackComputeWorkAndStopEndsTheRest()
    {
        var world = new FakeWorld();
        world.AddSource("disk-1", 3, producesCompute: 0, sourceDelay: TimeSpan.Zero);
        world.AddCompute(5);
        var clock = DateTimeOffset.UtcNow;
        var pacer = new SourcePacer(() => clock, () => null);
        pacer.JobStarted();
        clock += new ProcessingSettings(ProcessingPace.Standard, false).Cycle.Work + TimeSpan.FromSeconds(1);
        pacer.JobFinished();
        var scheduler = world.Scheduler(() => new ProcessingSettings(ProcessingPace.Standard, false));

        var run = scheduler.RunAsync([new LaneScheduler.SourceLane("disk-1", world.Filter("disk-1"), pacer)], CancellationToken.None);
        await world.WaitUntilAsync(() => world.ComputeDone == 5);
        Assert.Equal(0, world.SourceDone);
        Assert.False(run.IsCompleted);

        world.Stop();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, world.SourceDone);
    }

    [Fact]
    public async Task ARestWithNothingLeftToDoEndsThePassAtOnce()
    {
        var world = new FakeWorld();
        var clock = DateTimeOffset.UtcNow;
        var pacer = new SourcePacer(() => clock, () => null);
        pacer.JobStarted();
        clock += new ProcessingSettings(ProcessingPace.Standard, false).Cycle.Work + TimeSpan.FromSeconds(1);
        pacer.JobFinished();
        var scheduler = world.Scheduler(() => new ProcessingSettings(ProcessingPace.Standard, false));

        var processed = await scheduler.RunAsync([new LaneScheduler.SourceLane("disk-1", world.Filter("disk-1"), pacer)], CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(processed);
    }

    [Fact]
    public async Task SwitchingToFastestDuringARestResumesTheDrive()
    {
        var world = new FakeWorld();
        world.AddSource("disk-1", 2, producesCompute: 0, sourceDelay: TimeSpan.Zero);
        var clock = DateTimeOffset.UtcNow;
        var pacer = new SourcePacer(() => clock, () => null);
        pacer.JobStarted();
        clock += new ProcessingSettings(ProcessingPace.Standard, false).Cycle.Work + TimeSpan.FromSeconds(1);
        pacer.JobFinished();
        var settings = new ProcessingSettings(ProcessingPace.Standard, false);
        var scheduler = world.Scheduler(() => settings);

        var run = scheduler.RunAsync([new LaneScheduler.SourceLane("disk-1", world.Filter("disk-1"), pacer)], CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(0, world.SourceDone);
        settings = Fastest;
        world.WakeAll();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, world.SourceDone);
    }

    [Fact]
    public async Task StopArrivingWhileALaneChecksForWorkIsNotLost()
    {
        var world = new FakeWorld();
        world.AddSource("disk-1", 2, producesCompute: 0, sourceDelay: TimeSpan.Zero);
        var clock = DateTimeOffset.UtcNow;
        var pacer = new SourcePacer(() => clock, () => null);
        pacer.JobStarted();
        clock += new ProcessingSettings(ProcessingPace.Standard, false).Cycle.Work + TimeSpan.FromSeconds(1);
        pacer.JobFinished();
        var inHasWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.HasWorkHook = async () => { inHasWork.TrySetResult(); await release.Task; };
        var scheduler = world.Scheduler(() => new ProcessingSettings(ProcessingPace.Standard, false));

        var run = scheduler.RunAsync([new LaneScheduler.SourceLane("disk-1", world.Filter("disk-1"), pacer)], CancellationToken.None);
        await inHasWork.Task;
        world.Stop();           // fires the wake signal while the lane is inside the work check
        release.TrySetResult();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ADrainedDriveStillPicksUpWorkThatArrivesWhileAnotherDriveIsBusy()
    {
        var world = new FakeWorld();
        world.AddSource("disk-1", 6, producesCompute: 0, sourceDelay: TimeSpan.FromMilliseconds(80));
        world.AddSource("disk-2", 0, producesCompute: 0, sourceDelay: TimeSpan.Zero);
        var scheduler = world.Scheduler(() => Fastest);

        var run = scheduler.RunAsync([world.Lane("disk-1"), world.Lane("disk-2")], CancellationToken.None);
        await Task.Delay(120);
        world.AddSource("disk-2", 1, producesCompute: 0, sourceDelay: TimeSpan.Zero);
        await world.WaitUntilAsync(() => world.SourceDoneOn("disk-2") == 1);
        Assert.True(world.SourceDoneOn("disk-1") < 6, "disk-2's new job should not wait for disk-1 to drain");

        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(7, world.SourceDone);
    }

    [Fact]
    public async Task ARestartRequestEndsThePassAfterTheCurrentJobs()
    {
        var world = new FakeWorld();
        world.AddSource("disk-1", 50, producesCompute: 0, sourceDelay: TimeSpan.FromMilliseconds(20));
        var scheduler = world.Scheduler(() => Fastest);

        var run = scheduler.RunAsync([world.Lane("disk-1")], CancellationToken.None);
        await world.WaitUntilAsync(() => world.SourceDone >= 2);
        world.RequestRestart();

        Assert.True(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(world.SourceDone < 50);
    }

    [Fact]
    public async Task OnlyWhileIdleStillRunsAChangeCheckTheUserAskedFor()
    {
        var world = new FakeWorld();
        world.AddSource("disk-1", 1, producesCompute: 0, sourceDelay: TimeSpan.Zero, isScan: true);
        world.AddSource("disk-1", 2, producesCompute: 0, sourceDelay: TimeSpan.Zero);
        var pacer = new SourcePacer(() => DateTimeOffset.UtcNow, () => TimeSpan.Zero);
        var scheduler = world.Scheduler(() => new ProcessingSettings(ProcessingPace.Fastest, OnlyWhenIdle: true));

        var run = scheduler.RunAsync([new LaneScheduler.SourceLane("disk-1", world.Filter("disk-1"), pacer)], CancellationToken.None);
        await world.WaitUntilAsync(() => world.SourceDone == 1);
        await Task.Delay(100);
        Assert.Equal(1, world.SourceDone);
        Assert.True(pacer.IsWaitingForUser);

        world.Stop();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AFailingLaneIsReportedAtOnceAndDoesNotHangTheOthers()
    {
        var world = new FakeWorld();
        world.AddSource("disk-1", 3, producesCompute: 0, sourceDelay: TimeSpan.FromMilliseconds(10));
        world.AddSource("disk-2", 1, producesCompute: 0, sourceDelay: TimeSpan.Zero);
        world.FailOn = "disk-2";
        var failed = new List<string>();
        var scheduler = world.Scheduler(() => Fastest, (lane, _) => { lock (failed) failed.Add(lane); });

        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.RunAsync([world.Lane("disk-1"), world.Lane("disk-2")], CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["disk-2"], failed);
    }

    [Fact]
    public async Task WithNoSourceLanesComputeWorkStillRunsAndThePassEnds()
    {
        var world = new FakeWorld();
        world.AddCompute(3);
        var scheduler = world.Scheduler(() => Fastest);

        Assert.True(await scheduler.RunAsync([], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, world.ComputeDone);
    }

    /// <summary>In-memory queues standing in for the job store and the job executors.</summary>
    private sealed class FakeWorld
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<(int Compute, TimeSpan Delay, bool IsScan)>> _source = new();
        private readonly ConcurrentDictionary<string, int> _doneOn = new();
        private volatile bool _restart;
        private readonly ConcurrentQueue<int> _compute = new();
        private readonly ConcurrentDictionary<string, int> _running = new();
        private readonly ConcurrentDictionary<string, int> _maxRunning = new();
        private readonly object _gate = new();
        private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sourceRunning;
        private volatile bool _enabled = true;

        public int SourceDone;
        public int ComputeDone;
        public int MaxConcurrentSourceOverall;
        public DateTimeOffset FirstComputeStart = DateTimeOffset.MaxValue;
        public DateTimeOffset LastSourceEnd = DateTimeOffset.MinValue;

        public Func<Task>? HasWorkHook { get; set; }
        public string? FailOn { get; set; }

        public void AddSource(string disk, int count, int producesCompute, TimeSpan sourceDelay, bool isScan = false)
        {
            var queue = _source.GetOrAdd(disk, _ => new());
            for (var i = 0; i < count; i++) queue.Enqueue((producesCompute, sourceDelay, isScan));
        }

        public int SourceDoneOn(string disk) => _doneOn.GetValueOrDefault(disk);

        public void RequestRestart() { _restart = true; WakeAll(); }

        public void AddCompute(int count) { for (var i = 0; i < count; i++) _compute.Enqueue(0); }

        public JobLaneFilter Filter(string disk) => new(JobLaneKind.Source, [DiskId(disk)]);

        public LaneScheduler.SourceLane Lane(string disk) => new(disk, Filter(disk), new SourcePacer(() => DateTimeOffset.UtcNow, () => null));

        public int MaxConcurrent(string disk) => _maxRunning.GetValueOrDefault(disk);

        public void Stop() { _enabled = false; WakeAll(); }

        public void WakeAll()
        {
            TaskCompletionSource fired;
            lock (_gate) { fired = _wake; _wake = new(TaskCreationOptions.RunContinuationsAsynchronously); }
            fired.TrySetResult();
        }

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
            Assert.True(condition());
        }

        public LaneScheduler Scheduler(Func<ProcessingSettings> settings, Action<string, Exception>? laneFailed = null) => new(ProcessAsync, HasWorkAsync, () => _enabled, settings,
            () => { lock (_gate) return _wake.Task; }, () => _restart, TimeSpan.FromMilliseconds(50), laneFailed);

        private async Task<bool> HasWorkAsync(JobLaneFilter lane, CancellationToken cancellationToken)
        {
            if (HasWorkHook is { } hook) await hook();
            return lane.Kind == JobLaneKind.Compute ? !_compute.IsEmpty : _source.TryGetValue(DiskName(lane), out var queue) && !queue.IsEmpty;
        }

        private async Task<bool> ProcessAsync(JobLaneFilter lane, Action onStarted, Action onFinished, CancellationToken cancellationToken)
        {
            if (lane.Kind == JobLaneKind.Compute)
            {
                if (!_compute.TryDequeue(out _)) return false;
                lock (_gate) if (DateTimeOffset.UtcNow < FirstComputeStart) FirstComputeStart = DateTimeOffset.UtcNow;
                await Task.Delay(5, cancellationToken);
                Interlocked.Increment(ref ComputeDone);
                return true;
            }
            var disk = DiskName(lane);
            if (disk == FailOn) throw new InvalidOperationException("simulated lane failure");
            if (!_source.TryGetValue(disk, out var queue) || !queue.TryPeek(out var next)) return false;
            if (lane.ScansOnly && !next.IsScan) return false;
            if (!queue.TryDequeue(out var job)) return false;
            onStarted();
            var running = _running.AddOrUpdate(disk, 1, (_, value) => value + 1);
            _maxRunning.AddOrUpdate(disk, running, (_, value) => Math.Max(value, running));
            var overall = Interlocked.Increment(ref _sourceRunning);
            lock (_gate) MaxConcurrentSourceOverall = Math.Max(MaxConcurrentSourceOverall, overall);
            await Task.Delay(job.Delay, cancellationToken);
            for (var i = 0; i < job.Compute; i++) _compute.Enqueue(0);
            Interlocked.Decrement(ref _sourceRunning);
            _running.AddOrUpdate(disk, 0, (_, value) => value - 1);
            lock (_gate) LastSourceEnd = DateTimeOffset.UtcNow;
            Interlocked.Increment(ref SourceDone);
            _doneOn.AddOrUpdate(disk, 1, (_, value) => value + 1);
            onFinished();
            return true;
        }

        private static Guid DiskId(string disk) => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(disk)));

        private string DiskName(JobLaneFilter lane) => _source.Keys.FirstOrDefault(key => lane.RootIds?.Contains(DiskId(key)) == true) ?? "?";
    }
}
