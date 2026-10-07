using LocalMediaAI.Core;
using LocalMediaAI.Infrastructure;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class CachingResourceSnapshotProviderTests
{
    [Fact]
    public async Task AReadingIsReusedUntilItIsOlderThanTheMaximumAge()
    {
        var now = DateTimeOffset.UtcNow;
        var inner = new CountingProvider();
        var cached = new CachingResourceSnapshotProvider(inner, TimeSpan.FromSeconds(5), () => now);

        var first = await cached.CaptureAsync(CancellationToken.None);
        now += TimeSpan.FromSeconds(4);
        var second = await cached.CaptureAsync(CancellationToken.None);
        now += TimeSpan.FromSeconds(2);
        var third = await cached.CaptureAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.NotSame(first, third);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task AFailedReadingIsNotCached()
    {
        var inner = new CountingProvider { FailNext = true };
        var cached = new CachingResourceSnapshotProvider(inner, TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => cached.CaptureAsync(CancellationToken.None));
        await cached.CaptureAsync(CancellationToken.None);

        Assert.Equal(2, inner.Calls);
    }

    private sealed class CountingProvider : IResourceSnapshotProvider
    {
        public int Calls;
        public bool FailNext;

        public Task<ResourceSnapshot> CaptureAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("reading failed");
            }
            return Task.FromResult(new ResourceSnapshot(50, 8L << 30, true, 6L << 30, null, null, null));
        }
    }
}
