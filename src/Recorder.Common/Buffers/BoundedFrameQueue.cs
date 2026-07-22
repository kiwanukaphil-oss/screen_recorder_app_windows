namespace Recorder.Common.Buffers;

/// <summary>
/// Fixed-capacity FIFO used as the hand-off point between pipeline stages
/// (e.g. capture thread → encode thread). When full, enqueueing drops the OLDEST
/// entry: for video, stale frames are worthless and a slow consumer must never
/// back-pressure the capture side (see PLAN.md, principle 2).
/// Lock-based for now; profiled and, if needed, replaced with a lock-free ring in M2.
/// </summary>
public sealed class BoundedFrameQueue<T>
{
    private readonly Queue<T> _items;
    private readonly object _gate = new();
    private long _droppedCount;

    public BoundedFrameQueue(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Queue capacity must be at least 1.");
        }
        Capacity = capacity;
        _items = new Queue<T>(capacity);
    }

    public int Capacity { get; }

    /// <summary>Total entries discarded because the queue was full. Surfaced in the diagnostics panel.</summary>
    public long DroppedCount
    {
        get { lock (_gate) { return _droppedCount; } }
    }

    public int Count
    {
        get { lock (_gate) { return _items.Count; } }
    }

    /// <summary>
    /// Adds an item, evicting the oldest entry when the queue is at capacity.
    /// Returns the evicted item (so the caller can recycle pooled resources such as
    /// GPU textures) or default when nothing was dropped.
    /// </summary>
    public bool EnqueueDroppingOldest(T item, out T? dropped)
    {
        lock (_gate)
        {
            bool didDrop = _items.Count == Capacity;
            dropped = didDrop ? _items.Dequeue() : default;
            if (didDrop)
            {
                _droppedCount++;
            }
            _items.Enqueue(item);
            return didDrop;
        }
    }

    public bool TryDequeue(out T? item)
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                item = default;
                return false;
            }
            item = _items.Dequeue();
            return true;
        }
    }
}
