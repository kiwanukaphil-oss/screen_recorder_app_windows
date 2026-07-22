using Recorder.Common.Buffers;

namespace Recorder.Tests;

public class BoundedFrameQueueTests
{
    [Fact]
    public void Dequeues_in_fifo_order()
    {
        var queue = new BoundedFrameQueue<int>(capacity: 3);
        queue.EnqueueDroppingOldest(1, out _);
        queue.EnqueueDroppingOldest(2, out _);

        Assert.True(queue.TryDequeue(out int first));
        Assert.True(queue.TryDequeue(out int second));
        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void Enqueue_at_capacity_drops_oldest_and_reports_it()
    {
        var queue = new BoundedFrameQueue<string>(capacity: 2);
        queue.EnqueueDroppingOldest("a", out _);
        queue.EnqueueDroppingOldest("b", out _);

        bool didDrop = queue.EnqueueDroppingOldest("c", out string? dropped);

        Assert.True(didDrop);
        Assert.Equal("a", dropped);
        Assert.Equal(1, queue.DroppedCount);
        Assert.True(queue.TryDequeue(out string? next));
        Assert.Equal("b", next);
    }

    [Fact]
    public void Rejects_capacity_below_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedFrameQueue<int>(0));
    }
}
