using LocalMediaAI.Core;

namespace LocalMediaAI.Infrastructure;

/// <summary>
/// Pairs judged not similar before black bars were cut off (<see cref="NearDuplicatePreparation"/>) are judged once
/// more from the stored fingerprints, never reading a video. Only a pair that is now similar is updated: its evidence
/// row and group id are fixed by the pair and versions, and similar pairs are never touched, so groups and the user's
/// decisions on them stay. Each pair is judged again once per preparation version.
/// </summary>
public sealed partial class MediaPipeline
{
    private async Task RecheckRejectedNearDuplicatesAsync(CancellationToken cancellationToken)
    {
        var due = await _library.GetNearDuplicateRechecksDueAsync(NearDuplicateVerificationProfile.HighPrecisionV1.ThresholdVersion, NearDuplicatePreparation.Version, cancellationToken);
        if (due.Count == 0) return;
        var upgraded = 0;
        foreach (var previous in due)
        {
            var candidate = new NearDuplicateCandidate(previous.MediaFileAId, previous.MediaContentAId, previous.MediaFileBId, previous.MediaContentBId, previous.DurationASeconds, previous.DurationBSeconds);
            var (evidence, matches) = await JudgeNearDuplicatePairAsync(candidate, previous.SamplingVersion, previous.EvidenceAlgorithm, cancellationToken);
            var nowSimilar = evidence.Status == "Verified";
            // Marked only once the outcome is kept, so a failed write is tried again on the next scan.
            if (nowSimilar && !await _library.UpsertNearDuplicateEvidenceAsync(evidence, matches, cancellationToken)) continue;
            if (nowSimilar) upgraded++;
            await _library.MarkNearDuplicateRecheckedAsync(previous.NearDuplicateEvidenceId, NearDuplicatePreparation.Version, cancellationToken);
        }
        _log.Write("Information", "NearDuplicateRechecked", new { Pairs = due.Count, NowSimilar = upgraded });
        if (upgraded > 0) await _library.RebuildNearDuplicateGroupsAsync(cancellationToken);
    }
}
