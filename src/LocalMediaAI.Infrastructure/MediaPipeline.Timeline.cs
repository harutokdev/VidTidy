using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure;

/// <summary>
/// Copies of different length (start or end cut, a scene pulled out, more added): each video gets a timeline of frames
/// along its whole length (<see cref="TimelineSamplingPolicy"/>); a pair whose timelines share fingerprints is lined up
/// (<see cref="TimelineAlignment"/>), reading short stretches of both videos under protection, and kept as near-duplicate
/// evidence with the parts the longer one has more.
/// </summary>
public sealed partial class MediaPipeline
{
    /// <summary>Reads and fingerprints the timeline inside the protected session. A video that cannot be read is logged and left without one, to be tried again later.</summary>
    private async Task<StoredTimeline?> ReadTimelineAsync(ProcessingSourceSession session, AttemptScopedStagePersistence stagePersistence, MediaFile media, double durationSeconds, CancellationToken cancellationToken)
    {
        await stagePersistence.PersistPhaseEntryAsync("ProtectedNearDuplicateTimeline", cancellationToken);
        try
        {
            var (frames, sequential) = await session.ExecuteSourceStageAsync("NearDuplicateTimeline", (source, ct) => _ffmpeg.ReadTimelineAsync(source, media, durationSeconds, ct), cancellationToken);
            _log.Write("Information", "NearDuplicateTimelineRead", new { media.MediaFileId, Frames = frames.Count, Expected = TimelineSamplingPolicy.Times(durationSeconds).Count, Sequential = sequential });
            return BuildTimeline(media, durationSeconds, frames, sequential);
        }
        catch (MediaToolException ex)
        {
            _log.Write("Warning", "NearDuplicateTimelineFailed", new { media.MediaFileId, ex.Error.Code });
            return null;
        }
    }

    internal static StoredTimeline BuildTimeline(MediaFile media, double durationSeconds, IReadOnlyList<TimelineGrayFrame> frames, bool sequential)
    {
        var bars = frames.Count == 0 ? FrameCrop.None : NearDuplicatePreparation.Bars(frames.Select(frame => frame.Gray).ToArray());
        var prepared = NearDuplicatePreparation.Prepare(frames.Select((frame, index) => (index, frame.Gray)).ToArray(), bars);
        return new StoredTimeline(media.MediaFileId, media.MediaContentId, media.SizeBytes, media.LastWriteTimeUtc, durationSeconds,
            TimelineSamplingPolicy.Interval(durationSeconds), sequential, bars, frames.Zip(prepared, (frame, value) => new TimelineFrame(frame.Seconds, value)).ToArray());
    }

    /// <summary>
    /// Compares the video with every other one whose timeline shares fingerprints with it and has no evidence for the pair
    /// yet. Pairs with no frame in common at all are settled from the stored timelines alone; the rest read stretches of
    /// both videos. Each pair is judged once, when the second of the two gets its timeline.
    /// </summary>
    private async Task VerifyTimelineCandidatesAsync(MediaFile media, CancellationToken cancellationToken)
    {
        var mine = await _library.GetCurrentNearDuplicateTimelineAsync(media.MediaFileId, media.MediaContentId, cancellationToken);
        if (mine is null || mine.Frames.Count < TimelineMatching.MinimumMatchedFrames) return;
        var verified = false;
        foreach (var candidate in await _library.GetNearDuplicateTimelineCandidatesAsync(media.MediaFileId, media.MediaContentId, cancellationToken))
        {
            if (await _library.GetNearDuplicateEvidenceAsync(media.MediaFileId, media.MediaContentId, candidate.MediaFileId, candidate.MediaContentId,
                    NearDuplicateSamplingPolicy.Version, PerceptualFrameFingerprint.Algorithm, TimelineAlignment.ThresholdVersion, cancellationToken) is not null) continue;
            var other = await _library.GetCurrentNearDuplicateTimelineAsync(candidate.MediaFileId, candidate.MediaContentId, cancellationToken);
            var otherMedia = await _library.GetMediaFileAsync(candidate.MediaFileId, cancellationToken);
            if (other is null || otherMedia is null || otherMedia.PresenceState != PresenceState.Present || otherMedia.MediaContentId != candidate.MediaContentId) continue;
            var rough = TimelineMatching.Compare(mine.Frames, mine.DurationSeconds, other.Frames, other.DurationSeconds);
            TimelineMatch? match = rough.Pairs.Count == 0 ? rough with { IsContained = false } : await AlignWithSourcesAsync(media, mine, otherMedia, other, cancellationToken);
            if (match is null) continue;
            verified |= await PersistTimelineEvidenceAsync(media, mine, otherMedia, other, match, cancellationToken) && match.IsContained;
        }
        if (verified) await _library.RebuildNearDuplicateGroupsAsync(cancellationToken);
    }

    /// <summary>Lines up the pair reading both videos under protection; null when either cannot be leased now (it is tried again later).</summary>
    private async Task<TimelineMatch?> AlignWithSourcesAsync(MediaFile a, StoredTimeline timelineA, MediaFile b, StoredTimeline timelineB, CancellationToken cancellationToken)
    {
        if (!IsAllowedByTestLab(a.Path) || !IsAllowedByTestLab(b.Path)) return null;
        try
        {
            await using var sessionA = await ProcessingSourceSession.AcquireAsync(_paths, a.MediaFileId, a.MediaContentId, a.Path, _log, cancellationToken);
            await using var sessionB = await ProcessingSourceSession.AcquireAsync(_paths, b.MediaFileId, b.MediaContentId, b.Path, _log, cancellationToken);
            var interval = Math.Max(timelineA.IntervalSeconds, timelineB.IntervalSeconds);
            var match = await TimelineAlignment.AlignAsync(Video(sessionA, a, timelineA), Video(sessionB, b, timelineB), interval, cancellationToken);
            // Nothing is published from these reads.
            sessionA.CompletePublicationBoundary(ProcessingPublicationResolution.Rejected);
            sessionB.CompletePublicationBoundary(ProcessingPublicationResolution.Rejected);
            return match;
        }
        catch (ProcessingSourceLeaseUnavailableException ex)
        {
            _log.Write("Information", "TimelineAlignmentDeferred", new { A = a.MediaFileId, B = b.MediaFileId, ex.Code });
            return null;
        }
        catch (MediaToolException ex)
        {
            _log.Write("Warning", "TimelineAlignmentFailed", new { A = a.MediaFileId, B = b.MediaFileId, ex.Error.Code });
            return null;
        }
    }

    private TimelineVideo Video(ProcessingSourceSession session, MediaFile media, StoredTimeline timeline)
    {
        IReadOnlyList<PreparedNearDuplicateFrame> Prepare(IReadOnlyList<TimelineGrayFrame> frames) =>
            NearDuplicatePreparation.Prepare(frames.Select((frame, index) => (index, frame.Gray)).ToArray(), timeline.Bars);
        TimelineWindowReader window = async (from, to, fps, ct) =>
        {
            var frames = await session.ExecuteSourceStageAsync("TimelineAlignment", (source, c) => _ffmpeg.ReadStretchAsync(source, media, from, to, fps, c), ct);
            return frames.Zip(Prepare(frames), (frame, value) => new TimelineFrame(frame.Seconds, value)).ToArray();
        };
        TimelineFrameReader at = async (times, ct) =>
        {
            var frames = await session.ExecuteSourceStageAsync("TimelineAlignment", (source, c) => _ffmpeg.ReadFramesAtAsync(source, media, times, c), ct);
            var bySeconds = frames.Zip(Prepare(frames), (frame, value) => (frame.Seconds, value)).GroupBy(f => f.Seconds).ToDictionary(g => g.Key, g => g.First().value);
            return times.Select(t => bySeconds.GetValueOrDefault(t)).ToArray();
        };
        return new TimelineVideo(timeline.Frames, timeline.DurationSeconds, window, at);
    }

    /// <summary>Keeps the judgment as near-duplicate evidence (and, when verified, which parts the longer video has more).</summary>
    private async Task<bool> PersistTimelineEvidenceAsync(MediaFile media, StoredTimeline mine, MediaFile otherMedia, StoredTimeline other, TimelineMatch match, CancellationToken cancellationToken)
    {
        var (a, ta, b, tb) = string.CompareOrdinal(media.MediaFileId.ToString("D"), otherMedia.MediaFileId.ToString("D")) < 0
            ? (media, mine, otherMedia, other) : (otherMedia, other, media, mine);
        // TimelineMatching compared this video (A) with the other (B), so AIsShorter refers to them in that order.
        var shorterId = match.AIsShorter ? media.MediaFileId : otherMedia.MediaFileId;
        var longest = Math.Max(ta.DurationSeconds, tb.DurationSeconds);
        var compared = match.ShorterCoverage > 0 ? (int)Math.Round(match.MatchedFrames / match.ShorterCoverage) : match.MatchedFrames;
        var status = match.IsContained ? "Verified" : "Rejected";
        var digest = AnalysisKey.Sha256(AnalysisKey.CanonicalJson(new { relation = "NearDuplicateTimeline", FileA = a.MediaFileId, ContentA = a.MediaContentId, FileB = b.MediaFileId, ContentB = b.MediaContentId, TimelineAlignment.ThresholdVersion }));
        var evidence = new NearDuplicateEvidence(Guid.NewGuid(), a.MediaFileId, a.MediaContentId, b.MediaFileId, b.MediaContentId, PerceptualFrameFingerprint.Algorithm,
            NearDuplicateSamplingPolicy.Version, TimelineAlignment.ThresholdVersion, digest, ta.DurationSeconds, tb.DurationSeconds,
            longest <= 0 ? 0 : Math.Abs(ta.DurationSeconds - tb.DurationSeconds) / longest, compared, match.MatchedFrames, match.MatchedFrames, 0, 0,
            match.ShorterCoverage, status, null, null, DateTimeOffset.UtcNow);
        if (!await _library.UpsertNearDuplicateEvidenceAsync(evidence, [], cancellationToken)) return false;
        if (match.IsContained)
            await _library.SaveTimelineEvidenceDetailAsync(new TimelineEvidenceDetail(evidence.NearDuplicateEvidenceId, shorterId, match.ShorterCoverage, match.MatchedFrames, match.ExtraInLonger, match.ExtraInShorter), cancellationToken);
        _log.Write("Information", "TimelineEvidence", new { A = a.MediaFileId, B = b.MediaFileId, Status = status, match.MatchedFrames, match.ShorterCoverage });
        return true;
    }
}
