using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LocalMediaAI.Core;

/// <summary>
/// Content Similarityのversioned high-precision profileです。これらの値は候補生成ではなく、
/// pair-level evidenceをpublishする際の最終検証条件です。
/// </summary>
public sealed record ContentSimilarityVerificationProfile(
    string ThresholdVersion,
    int TopK,
    double MinimumMeanCosine,
    double MinimumSampleAwareCosine,
    double MinimumRepresentativeCosine,
    int TopSampleCount,
    double HighPriorityScore)
{
    public static readonly ContentSimilarityVerificationProfile HighPrecisionV1 = new(
        "content-similarity-v1-high-precision",
        TopK: 20,
        MinimumMeanCosine: 0.72d,
        MinimumSampleAwareCosine: 0.80d,
        MinimumRepresentativeCosine: 0.82d,
        TopSampleCount: 3,
        HighPriorityScore: 0.88d);

    public bool IsVerified(double meanCosine, ContentSimilarityPairScore sampleAware) =>
        meanCosine >= MinimumMeanCosine &&
        sampleAware.TopSampleCosine >= MinimumSampleAwareCosine &&
        sampleAware.RepresentativeCosine >= MinimumRepresentativeCosine;

    public string PriorityFor(double score) => score >= HighPriorityScore ? "High" : "Medium";
}

public sealed record ContentSimilarityPairScore(
    double TopSampleCosine,
    double RepresentativeCosine,
    Guid RepresentativeVisualSampleAId,
    Guid RepresentativeVisualSampleBId);

public sealed record ContentSimilarityAggregate(
    float[] Vector,
    string SourceEmbeddingDigest,
    int AvailableSampleCount,
    int ExpectedSampleCount,
    double CoverageRatio)
{
    public bool HasCompleteCoverage => ExpectedSampleCount > 0 && AvailableSampleCount == ExpectedSampleCount && CoverageRatio >= 1d;
}

/// <summary>
/// Phase 5.3のCPU-only vector operations。既存visual_embeddingがsource of truthであり、
/// このclassは新しいAI推論、model download、Original Media readを行いません。
/// </summary>
public static class ContentSimilarityVectors
{
    public const string AggregationVersion = "mean-unit-v1";
    public const string SampleAwareAggregationVersion = "top-3-sample-cosine-v1";
    public const string CandidateIndexVersion = "content-similarity-sign-lsh-v1";
    public const string SimilarityMetric = "normalized-embedding-exact-cosine-v1";

    public static ContentSimilarityAggregate BuildMeanUnit(
        IReadOnlyList<(VisualEmbedding Embedding, VisualSample Sample)> completedEmbeddings,
        int expectedSampleCount)
    {
        ArgumentNullException.ThrowIfNull(completedEmbeddings);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedSampleCount);
        if (completedEmbeddings.Count == 0)
            return new ContentSimilarityAggregate(Array.Empty<float>(), SourceDigest(completedEmbeddings), 0, expectedSampleCount, 0d);

        var dimension = completedEmbeddings[0].Embedding.Dimension;
        if (dimension <= 0) throw new InvalidOperationException("Visual embedding dimension must be positive.");
        var accumulator = new double[dimension];
        foreach (var entry in completedEmbeddings)
        {
            ValidateEmbedding(entry.Embedding, dimension);
            var vector = Decode(entry.Embedding.VectorFloat32!, dimension);
            for (var index = 0; index < dimension; index++) accumulator[index] += vector[index];
        }

        var mean = new float[dimension];
        for (var index = 0; index < dimension; index++) mean[index] = (float)(accumulator[index] / completedEmbeddings.Count);
        NormalizeInPlace(mean);
        var coverage = expectedSampleCount == 0 ? 0d : Math.Clamp(completedEmbeddings.Count / (double)expectedSampleCount, 0d, 1d);
        return new ContentSimilarityAggregate(mean, SourceDigest(completedEmbeddings), completedEmbeddings.Count, expectedSampleCount, coverage);
    }

    public static ContentSimilarityPairScore VerifyTopSamples(
        IReadOnlyList<(VisualEmbedding Embedding, VisualSample Sample)> left,
        IReadOnlyList<(VisualEmbedding Embedding, VisualSample Sample)> right,
        int topSampleCount)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (topSampleCount is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(topSampleCount));
        if (left.Count == 0 || right.Count == 0) throw new InvalidOperationException("Completed samples are required for pair verification.");

        var scores = new List<(double Score, Guid LeftId, Guid RightId)>();
        foreach (var leftEntry in left)
        {
            ValidateEmbedding(leftEntry.Embedding, leftEntry.Embedding.Dimension);
            var leftVector = Decode(leftEntry.Embedding.VectorFloat32!, leftEntry.Embedding.Dimension);
            foreach (var rightEntry in right)
            {
                if (rightEntry.Embedding.Dimension != leftVector.Length)
                    throw new InvalidOperationException("Content similarity vectors have incompatible dimensions.");
                ValidateEmbedding(rightEntry.Embedding, rightEntry.Embedding.Dimension);
                var rightVector = Decode(rightEntry.Embedding.VectorFloat32!, rightEntry.Embedding.Dimension);
                scores.Add((Cosine(leftVector, rightVector), leftEntry.Sample.VisualSampleId, rightEntry.Sample.VisualSampleId));
            }
        }

        var ordered = scores.OrderByDescending(item => item.Score)
            .ThenBy(item => item.LeftId)
            .ThenBy(item => item.RightId)
            .Take(topSampleCount)
            .ToArray();
        var representative = ordered[0];
        return new ContentSimilarityPairScore(ordered.Average(item => item.Score), representative.Score, representative.LeftId, representative.RightId);
    }

    public static double Cosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length == 0 || left.Length != right.Length) throw new InvalidOperationException("Content similarity vectors have incompatible dimensions.");
        double total = 0d;
        for (var index = 0; index < left.Length; index++)
        {
            if (!float.IsFinite(left[index]) || !float.IsFinite(right[index]))
                throw new InvalidOperationException("Content similarity vector has a non-finite value.");
            total += left[index] * right[index];
        }
        return Math.Clamp(total, -1d, 1d);
    }

    public static byte[] Encode(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] Decode(byte[] bytes, int dimension)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (dimension <= 0 || bytes.Length != dimension * sizeof(float))
            throw new InvalidOperationException("Content similarity vector format is invalid.");
        var vector = new float[dimension];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        foreach (var value in vector)
            if (!float.IsFinite(value)) throw new InvalidOperationException("Content similarity vector has a non-finite value.");
        return vector;
    }

    public static void NormalizeInPlace(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        double normSquared = 0d;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value)) throw new InvalidOperationException("Content similarity vector has a non-finite value.");
            normSquared += value * value;
        }
        var norm = Math.Sqrt(normSquared);
        if (norm < 1e-12d) throw new InvalidOperationException("Content similarity vector cannot be normalized because its norm is zero.");
        for (var index = 0; index < vector.Length; index++) vector[index] = (float)(vector[index] / norm);
    }

    private static string SourceDigest(IReadOnlyList<(VisualEmbedding Embedding, VisualSample Sample)> entries)
    {
        var canonical = string.Join("\n", entries.OrderBy(entry => entry.Sample.SampleIndex)
            .ThenBy(entry => entry.Sample.VisualSampleId)
            .Select(entry => string.Join("|", entry.Sample.SampleIndex.ToString(CultureInfo.InvariantCulture), entry.Sample.VisualSampleId.ToString("D"), entry.Sample.CacheKey, entry.Embedding.VisualEmbeddingId.ToString("D"), entry.Embedding.CacheKey, Convert.ToHexString(SHA256.HashData(entry.Embedding.VectorFloat32!)))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void ValidateEmbedding(VisualEmbedding embedding, int dimension)
    {
        if (embedding.Status != VisualEmbeddingStatus.Completed || !embedding.Normalized || embedding.VectorFloat32 is null || embedding.Dimension != dimension)
            throw new InvalidOperationException("Visual embedding is not a completed normalized source vector.");
        var vector = Decode(embedding.VectorFloat32, dimension);
        double normSquared = 0d;
        foreach (var value in vector) normSquared += value * value;
        var norm = Math.Sqrt(normSquared);
        if (norm is < 0.995d or > 1.005d) throw new InvalidOperationException("Visual embedding source vector is not normalized.");
    }
}

/// <summary>
/// Deterministic, dependency-free sign-LSH candidate blocking. It is not final similarity evidence.
/// Four 16-bit bands are persisted, and every projection uses only eight deterministic dimensions
/// to make a 100k rebuild bounded without a native ANN dependency.
/// </summary>
public static class ContentSimilaritySignLsh
{
    public const int BitCount = 64;
    public const int BandCount = 4;
    public const int BitsPerBand = BitCount / BandCount;
    public const int ProjectionTermsPerBit = 8;

    public static string Signature(ReadOnlySpan<float> vector)
    {
        if (vector.Length == 0) throw new ArgumentException("Vector must not be empty.", nameof(vector));
        ulong bits = 0;
        for (var bit = 0; bit < BitCount; bit++)
        {
            double projection = 0d;
            for (var term = 0; term < ProjectionTermsPerBit; term++)
            {
                var state = Mix(((ulong)(bit + 1) << 32) | (uint)(term + 1));
                var index = (int)(state % (ulong)vector.Length);
                projection += ((state & 1UL) == 0UL ? 1d : -1d) * vector[index];
            }
            if (projection >= 0d) bits |= 1UL << bit;
        }
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, bits);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static IReadOnlyList<string> Bands(string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        if (signature.Length != 16 || !ulong.TryParse(signature, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bits))
            throw new InvalidOperationException("Content similarity LSH signature is invalid.");
        var bands = new string[BandCount];
        for (var band = 0; band < BandCount; band++)
        {
            var value = (ushort)((bits >> (band * BitsPerBand)) & 0xffffUL);
            bands[band] = value.ToString("x4", CultureInfo.InvariantCulture);
        }
        return bands;
    }

    private static ulong Mix(ulong value)
    {
        value += 0x9e3779b97f4a7c15UL;
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }
}
