namespace LocalMediaAI.Core.Processing;

/// <summary>Which worker lane a job runs in. Source jobs read original media; compute jobs only touch the cache, catalog and GPU.</summary>
public enum JobLaneKind { Source, Compute }

public static class JobLanes
{
    public static readonly IReadOnlySet<JobType> ComputeTypes = new HashSet<JobType>
    {
        JobType.VisualEmbedding, JobType.ContentSimilarityRepresentation, JobType.TagSuggestion
    };

    public static readonly IReadOnlySet<JobType> SourceTypes = Enum.GetValues<JobType>().Where(type => !ComputeTypes.Contains(type)).ToHashSet();

    /// <summary>Stages cheap enough to run for every file before any deep analysis starts.</summary>
    public const ProtectedSourceStageEligibility QuickStages = ProtectedSourceStageEligibility.Metadata | ProtectedSourceStageEligibility.Thumbnail;

    public static JobLaneKind KindOf(JobType type) => ComputeTypes.Contains(type) ? JobLaneKind.Compute : JobLaneKind.Source;

    public static bool IsQuickPass(ProtectedSourceStageEligibility stages) =>
        stages != ProtectedSourceStageEligibility.None && (stages & ~QuickStages) == ProtectedSourceStageEligibility.None;

    /// <summary>
    /// Splits one file's source work into a quick pass (metadata + thumbnail) and a deep pass (sampling, fingerprints, hash).
    /// The deep pass keeps Metadata when it was requested so it can still find the duration on its own; the stage is
    /// skipped cheaply when the quick pass already stored it.
    /// </summary>
    public static (ProtectedSourceStageEligibility Quick, ProtectedSourceStageEligibility Deep) SplitPasses(ProtectedSourceStageEligibility stages)
    {
        var quick = stages & QuickStages;
        var heavy = stages & ~QuickStages;
        if (heavy == ProtectedSourceStageEligibility.None) return (quick, ProtectedSourceStageEligibility.None);
        if (quick == ProtectedSourceStageEligibility.None) return (ProtectedSourceStageEligibility.None, heavy);
        return (quick, heavy | (stages & ProtectedSourceStageEligibility.Metadata));
    }
}

/// <summary>How hard the app may work the drives holding the originals.</summary>
public enum ProcessingPace { Fastest, Standard, Quiet }

/// <summary>Work for <see cref="Work"/>, then let the drive rest for <see cref="Rest"/>. A zero work window means no rests.</summary>
public sealed record DutyCycle(TimeSpan Work, TimeSpan Rest)
{
    public static DutyCycle Continuous { get; } = new(TimeSpan.Zero, TimeSpan.Zero);

    public bool IsContinuous => Work <= TimeSpan.Zero || Rest <= TimeSpan.Zero;

    public double RestShare => IsContinuous ? 0 : Rest / (Work + Rest);
}

/// <param name="Pace">Rest pattern for the drives holding originals.</param>
/// <param name="OnlyWhenIdle">Reads originals only while nobody has used the keyboard or mouse for <see cref="IdleThreshold"/>.</param>
public sealed record ProcessingSettings(ProcessingPace Pace, bool OnlyWhenIdle)
{
    public static ProcessingSettings Defaults { get; } = new(ProcessingPace.Standard, false);

    public static TimeSpan IdleThreshold { get; } = TimeSpan.FromMinutes(3);

    // Calibrated from the Performance E drive-temperature soak (docs/perf-e-analysis-throughput-plan-2026-09-29.md):
    // the drive heats and cools with a time constant of about 40 minutes, so a rest shorter than about 10 minutes
    // barely cools it. Under the harshest load measured the drive would level off near 45 C running continuously;
    // these cycles keep it about 2 C (Standard) and 5 C (Quiet) below that.
    public static TimeSpan MinimumUsefulRest { get; } = TimeSpan.FromMinutes(10);

    public DutyCycle Cycle => Pace switch
    {
        ProcessingPace.Fastest => DutyCycle.Continuous,
        ProcessingPace.Quiet => new DutyCycle(TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(10)),
        _ => new DutyCycle(TimeSpan.FromMinutes(50), TimeSpan.FromMinutes(10))
    };
}

/// <summary>Whether the next source job may start now, and if not, how long to wait and why.</summary>
/// <param name="WaitingForUser">True when the wait is only the "only while idle" rule (the drive itself is not resting).</param>
public sealed record PacingDecision(TimeSpan? Wait, bool WaitingForUser)
{
    public static PacingDecision Go { get; } = new(null, false);
}

/// <summary>
/// Paces the jobs of one source drive: after a full work window the drive rests, and an idle gap at least as long as
/// the rest counts as a rest. Thread-safe; the clock and the user-idle probe are injected so the rules are testable.
/// </summary>
public sealed class SourcePacer(Func<DateTimeOffset> clock, Func<TimeSpan?> userIdleFor)
{
    private readonly object _gate = new();
    private DateTimeOffset? _windowStart;
    private DateTimeOffset? _restUntil;
    private DateTimeOffset _lastActivity = DateTimeOffset.MinValue;
    private int _running;

    public bool IsResting { get { lock (_gate) return _restUntil is { } until && clock() < until; } }

    public DateTimeOffset? RestingUntil { get { lock (_gate) return _restUntil is { } until && clock() < until ? until : null; } }

    /// <summary>True while the last decision held work back only because the user is active.</summary>
    public bool IsWaitingForUser { get; private set; }

    /// <summary>How long to hold off before starting the next source job, or null to start now.</summary>
    public TimeSpan? WaitBeforeNextJob(ProcessingSettings settings) => Decide(settings).Wait;

    public PacingDecision Decide(ProcessingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var now = clock();
        var cycle = settings.Cycle;
        lock (_gate)
        {
            if (cycle.IsContinuous)
            {
                _restUntil = null;
                _windowStart = null;
            }
            else
            {
                if (_restUntil is { } until)
                {
                    if (now < until) return Hold(until - now, waitingForUser: false);
                    _restUntil = null;
                    _windowStart = null;
                }
                if (_windowStart is not null && _running == 0 && now - _lastActivity >= cycle.Rest) _windowStart = null;
                if (_windowStart is { } start && now - start >= cycle.Work)
                {
                    _restUntil = now + cycle.Rest;
                    _windowStart = null;
                    return Hold(cycle.Rest, waitingForUser: false);
                }
            }
        }
        if (settings.OnlyWhenIdle && userIdleFor() is { } idle && idle < ProcessingSettings.IdleThreshold)
            return Hold(ProcessingSettings.IdleThreshold - idle, waitingForUser: true);
        IsWaitingForUser = false;
        return PacingDecision.Go;
    }

    private PacingDecision Hold(TimeSpan wait, bool waitingForUser)
    {
        IsWaitingForUser = waitingForUser;
        return new PacingDecision(wait, waitingForUser);
    }

    public void JobStarted()
    {
        var now = clock();
        lock (_gate)
        {
            _windowStart ??= now;
            _lastActivity = now;
            _running++;
        }
    }

    public void JobFinished()
    {
        var now = clock();
        lock (_gate)
        {
            _lastActivity = now;
            if (_running > 0) _running--;
        }
    }
}
