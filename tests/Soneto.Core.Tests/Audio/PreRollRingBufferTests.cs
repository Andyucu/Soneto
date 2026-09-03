using Soneto.Core.Audio;

namespace Soneto.Core.Tests.Audio;

/// <summary>
/// Tests for <see cref="PreRollRingBuffer"/> — pure in-memory circular buffer logic, no audio
/// device needed. Covers plan §1.5's "Full [pre-roll], from the 2nd utterance of a burst
/// onward" requirement: old samples must fall off once capacity is exceeded, a snapshot must
/// return exactly the requested duration (the classic off-by-one boundary bug), and
/// under-filled behaviour (requesting more than has accumulated yet) must degrade gracefully
/// rather than return garbage/incorrect data. Explicitly left undone by item 4c's implementer.
/// </summary>
public class PreRollRingBufferTests
{
    private static float[] Sequence(int start, int count)
    {
        var arr = new float[count];
        for (int i = 0; i < count; i++)
            arr[i] = start + i;
        return arr;
    }

    [Fact]
    public void Append_UnderCapacity_CountReflectsSamplesAdded()
    {
        var buffer = new PreRollRingBuffer(capacitySamples: 100);
        buffer.Append(Sequence(0, 40));

        Assert.Equal(40, buffer.Count);
        Assert.Equal(100, buffer.Capacity);
    }

    [Fact]
    public void Append_OlderThanCapacity_AreDiscarded_OnlyMostRecentSurvive()
    {
        // Capacity for 10 samples ("preRollMs" worth); feed 25 -- only the newest 10 should
        // survive, i.e. samples 15..24.
        var buffer = new PreRollRingBuffer(capacitySamples: 10);
        buffer.Append(Sequence(0, 25));

        Assert.Equal(10, buffer.Count);
        var snapshot = buffer.Snapshot(10);
        Assert.Equal(Sequence(15, 10), snapshot);
    }

    [Fact]
    public void Append_InMultipleChunksOlderThanCapacity_AreDiscarded_OnlyMostRecentSurvive()
    {
        // Same as above but fed in several smaller Append() calls, exercising the wrap-around
        // path rather than the single-call "samples.Length >= capacity" fast path.
        var buffer = new PreRollRingBuffer(capacitySamples: 10);
        buffer.Append(Sequence(0, 7));
        buffer.Append(Sequence(7, 7));  // total fed so far: 0..13, capacity 10 -> keep 4..13
        buffer.Append(Sequence(14, 5)); // total fed: 0..18 -> keep newest 10: 9..18

        Assert.Equal(10, buffer.Count);
        Assert.Equal(Sequence(9, 10), buffer.Snapshot(10));
    }

    [Fact]
    public void Append_SingleChunkLongerThanCapacity_KeepsOnlyNewestCapacitySamples()
    {
        var buffer = new PreRollRingBuffer(capacitySamples: 5);
        buffer.Append(Sequence(0, 50)); // one call, far longer than capacity

        Assert.Equal(5, buffer.Count);
        Assert.Equal(Sequence(45, 5), buffer.Snapshot(5));
    }

    [Fact]
    public void Snapshot_ReturnsExactlyRequestedDuration_NotMoreNotLess()
    {
        var buffer = new PreRollRingBuffer(capacitySamples: 100);
        buffer.Append(Sequence(0, 100));

        // Exact boundary: requesting exactly the capacity/count must return exactly that many.
        var full = buffer.Snapshot(100);
        Assert.Equal(100, full.Length);
        Assert.Equal(Sequence(0, 100), full);

        // Requesting fewer must return exactly that many -- the newest N, not the oldest N.
        var partial = buffer.Snapshot(30);
        Assert.Equal(30, partial.Length);
        Assert.Equal(Sequence(70, 30), partial);

        // Off-by-one just under and just over the exact count.
        Assert.Equal(99, buffer.Snapshot(99).Length);
        Assert.Equal(100, buffer.Snapshot(101).Length); // clamped to Count, not 101
    }

    [Fact]
    public void Snapshot_ZeroOrNegative_ReturnsEmpty()
    {
        var buffer = new PreRollRingBuffer(capacitySamples: 10);
        buffer.Append(Sequence(0, 10));

        Assert.Empty(buffer.Snapshot(0));
        Assert.Empty(buffer.Snapshot(-5));
    }

    [Fact]
    public void Snapshot_BeforeAnythingAppended_ReturnsEmpty()
    {
        var buffer = new PreRollRingBuffer(capacitySamples: 10);
        Assert.Empty(buffer.Snapshot(10));
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Snapshot_RequestingMoreThanAccumulatedSoFar_ReturnsOnlyWhatsAccumulated()
    {
        // Right after the buffer starts (before it's had time to fill to capacity), a request
        // for more pre-roll than exists yet must return only what's actually there -- not pad
        // with zeros/garbage, not throw.
        var buffer = new PreRollRingBuffer(capacitySamples: 1000);
        buffer.Append(Sequence(0, 12));

        var snapshot = buffer.Snapshot(500);
        Assert.Equal(12, snapshot.Length);
        Assert.Equal(Sequence(0, 12), snapshot);
    }

    [Fact]
    public void Snapshot_OrderIsOldestFirst()
    {
        var buffer = new PreRollRingBuffer(capacitySamples: 5);
        buffer.Append([10f, 20f, 30f]);
        var snapshot = buffer.Snapshot(3);
        Assert.Equal(new[] { 10f, 20f, 30f }, snapshot);
    }

    [Fact]
    public void Clear_DiscardsAllBufferedSamples()
    {
        var buffer = new PreRollRingBuffer(capacitySamples: 10);
        buffer.Append(Sequence(0, 10));
        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.Snapshot(10));

        // Buffer must still be usable after Clear (not left in a broken internal state).
        buffer.Append(Sequence(100, 4));
        Assert.Equal(4, buffer.Count);
        Assert.Equal(Sequence(100, 4), buffer.Snapshot(4));
    }

    [Fact]
    public void Append_EmptySpan_IsNoOp()
    {
        var buffer = new PreRollRingBuffer(capacitySamples: 10);
        buffer.Append(Sequence(0, 5));
        buffer.Append(ReadOnlySpan<float>.Empty);

        Assert.Equal(5, buffer.Count);
        Assert.Equal(Sequence(0, 5), buffer.Snapshot(5));
    }

    [Fact]
    public void Constructor_NonPositiveCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreRollRingBuffer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreRollRingBuffer(-1));
    }
}
