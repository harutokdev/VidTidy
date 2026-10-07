using LocalMediaAI.App.Organize;

namespace LocalMediaAI.Infrastructure.Tests;

public sealed class DragReorderTests
{
    [Theory]
    [InlineData(0, 0, 0)] // dropped above itself: stays
    [InlineData(0, 1, 0)] // dropped just below itself: stays
    [InlineData(0, 3, 2)] // first item dropped after the third: becomes last of three
    [InlineData(2, 0, 0)] // last item dropped above the first
    [InlineData(2, 1, 1)]
    public void InsertionLineMapsToTheNewIndex(int from, int insertAt, int expected) =>
        Assert.Equal(expected, DragReorder.TargetIndex(from, insertAt));
}
