namespace LocalMediaAI.Core.Organize;

/// <summary>
/// Estimates time left for background analysis from how fast the remaining job count falls.
/// The window restarts whenever new work is added, so a fresh folder does not inherit an old rate.
/// </summary>
public sealed class AnalysisEta
{
    private DateTimeOffset _start;
    private int _startRemaining;
    private int _remaining;

    public bool IsRunning => _remaining > 0;

    /// <summary>Null while there is no measurable progress yet.</summary>
    public TimeSpan? Remaining { get; private set; }

    public void Observe(DateTimeOffset now, int remaining)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(remaining);
        if (remaining == 0)
        {
            _remaining = 0;
            Remaining = TimeSpan.Zero;
            return;
        }
        if (_remaining == 0 || remaining > _remaining)
        {
            _start = now;
            _startRemaining = remaining;
            _remaining = remaining;
            Remaining = null;
            return;
        }
        _remaining = remaining;
        var done = _startRemaining - remaining;
        var elapsed = now - _start;
        Remaining = done <= 0 || elapsed <= TimeSpan.Zero ? null : TimeSpan.FromSeconds(Math.Round(elapsed.TotalSeconds / done * remaining));
    }

    public static string Format(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromMinutes(1)) return L.T("1分未満");
        var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
        return minutes < 60 ? L.F("約{0}分", minutes) : L.F("約{0}時間{1}分", minutes / 60, minutes % 60);
    }
}
