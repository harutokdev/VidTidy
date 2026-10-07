namespace LocalMediaAI.Core;

/// <summary>
/// A video's timeline as stored (<see cref="TimelineSamplingPolicy"/>): its bar-free frames along the whole length.
/// Current while the file keeps <paramref name="SourceSizeBytes"/> and <paramref name="SourceLastWriteTimeUtc"/>.
/// No frames means the video was read but gave none (it is not tried again until it changes).
/// </summary>
public sealed record StoredTimeline(
    Guid MediaFileId,
    Guid MediaContentId,
    long SourceSizeBytes,
    DateTimeOffset SourceLastWriteTimeUtc,
    double DurationSeconds,
    double IntervalSeconds,
    bool ReadSequentially,
    FrameCrop Bars,
    IReadOnlyList<TimelineFrame> Frames);

/// <summary>Another video whose timeline shares fingerprints with the one in hand, and a length that differs by more than the five-sample rule allows.</summary>
public sealed record TimelineCandidate(Guid MediaFileId, Guid MediaContentId, double DurationSeconds);

/// <summary>What a verified timeline comparison found: which video is the shorter one and the parts the longer one has more.</summary>
public sealed record TimelineEvidenceDetail(
    Guid NearDuplicateEvidenceId,
    Guid ShorterMediaFileId,
    double ShorterCoverage,
    int MatchedFrames,
    IReadOnlyList<TimelineSpan> ExtraInLonger,
    IReadOnlyList<TimelineSpan> ExtraInShorter);
