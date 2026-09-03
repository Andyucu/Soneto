using Soneto.Core.Audio;

namespace Soneto.Core.Tests.Audio;

/// <summary>
/// Direct unit coverage for <see cref="SpscFloatRingBuffer"/> in isolation (no PortAudio, no
/// hardware) — the test the item 4b work item explicitly asked for but the implementer didn't
/// write (see <c>Docs/PROJECT-MEMORY.md</c> item 4b's "Deviation from the work item's ask").
/// Covers basic write/read correctness, wraparound, genuine concurrent producer/consumer
/// correctness with a real background thread on each side, and the documented "consumer falls
/// behind -> drop newest chunk, count it" behaviour.
/// </summary>
public class SpscFloatRingBufferTests
{
    [Fact]
    public void Write_then_read_returns_exact_values_in_order()
    {
        var ring = new SpscFloatRingBuffer(capacity: 64);
        float[] written = [1f, 2f, 3f, 4f, 5f];

        bool ok = ring.TryWrite(written);

        Assert.True(ok);
        var read = new List<float>();
        int count = ring.DrainInto(span => read.AddRange(span.ToArray()));

        Assert.Equal(written.Length, count);
        Assert.Equal(written, read);
        Assert.True(ring.IsEmpty);
        Assert.Equal(0, ring.DroppedSamples);
    }

    [Fact]
    public void Drain_on_empty_buffer_returns_zero_and_invokes_nothing()
    {
        var ring = new SpscFloatRingBuffer(capacity: 16);

        bool invoked = false;
        int count = ring.DrainInto(_ => invoked = true);

        Assert.Equal(0, count);
        Assert.False(invoked);
        Assert.True(ring.IsEmpty);
    }

    [Fact]
    public void TryWrite_with_empty_span_is_a_no_op_and_still_returns_true()
    {
        var ring = new SpscFloatRingBuffer(capacity: 8);

        bool ok = ring.TryWrite(ReadOnlySpan<float>.Empty);

        Assert.True(ok);
        Assert.True(ring.IsEmpty);
        Assert.Equal(0, ring.DroppedSamples);
    }

    [Fact]
    public void Wraparound_across_capacity_boundary_preserves_order_and_values()
    {
        // Small capacity and repeated write/drain cycles of a size that doesn't evenly divide
        // the capacity, deliberately forcing the internal write/read index to cross the end of
        // the backing array (the "two-span" wraparound path in both TryWrite and DrainInto)
        // many times over.
        const int capacity = 10;
        var ring = new SpscFloatRingBuffer(capacity);

        var expected = new List<float>();
        var actual = new List<float>();
        float nextValue = 0f;

        for (int cycle = 0; cycle < 50; cycle++)
        {
            int chunkSize = 3 + (cycle % 4); // 3,4,5,6,3,4,5,6,...
            var chunk = new float[chunkSize];
            for (int i = 0; i < chunkSize; i++)
                chunk[i] = nextValue++;

            bool ok = ring.TryWrite(chunk);
            Assert.True(ok, $"cycle {cycle}: write of {chunkSize} samples unexpectedly dropped");
            expected.AddRange(chunk);

            int drained = ring.DrainInto(span => actual.AddRange(span.ToArray()));
            Assert.Equal(chunkSize, drained);
        }

        Assert.Equal(expected, actual);
        Assert.True(ring.IsEmpty);
        Assert.Equal(0, ring.DroppedSamples);
    }

    [Fact]
    public void Concurrent_producer_and_consumer_deliver_all_data_uncorrupted_when_consumer_keeps_up()
    {
        // Ring sized to comfortably exceed the full dataset so this scenario is deterministically
        // drop-free regardless of scheduler timing (a smaller ring plus a busy-loop producer with
        // no pacing was observed to be genuinely, legitimately drop-prone during development of
        // this test — that backpressure/drop behavior is covered on its own terms by the two
        // dedicated "producer outpaces consumer" tests below). What this test verifies instead is
        // the actual cross-thread Volatile publish/observe path under real concurrent load: two
        // real OS threads racing on TryWrite/DrainInto for over a million samples, with no
        // corruption, no reordering, and no lost data.
        const int capacity = 2_000_000;
        const int chunkSize = 64;
        const int chunkCount = 20_000; // 1,280,000 samples total

        var ring = new SpscFloatRingBuffer(capacity);
        var received = new List<float>(chunkCount * chunkSize);
        Exception? consumerError = null;

        var consumerDone = new ManualResetEventSlim(false);
        var consumer = new Thread(() =>
        {
            try
            {
                long expectedNext = 0;
                while (true)
                {
                    int drained = ring.DrainInto(span =>
                    {
                        foreach (var v in span)
                        {
                            if (v != expectedNext)
                                throw new InvalidOperationException(
                                    $"Out-of-order/corrupted sample: expected {expectedNext}, got {v}");
                            expectedNext++;
                            received.Add(v);
                        }
                    });

                    if (drained == 0)
                    {
                        if (received.Count >= chunkCount * chunkSize)
                            break;
                        Thread.Sleep(0);
                    }
                }
            }
            catch (Exception ex)
            {
                consumerError = ex;
            }
            finally
            {
                consumerDone.Set();
            }
        })
        { IsBackground = true };
        consumer.Start();

        var producer = new Thread(() =>
        {
            float next = 0;
            for (int i = 0; i < chunkCount; i++)
            {
                var chunk = new float[chunkSize];
                for (int j = 0; j < chunkSize; j++)
                    chunk[j] = next++;

                // Spin until it fits — the ring is sized generously and the consumer keeps up,
                // so this should never spin for long; a bounded retry avoids a true hang if the
                // assumption is ever wrong.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!ring.TryWrite(chunk))
                {
                    Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
                        "Producer could not write within 10s -- consumer appears stalled.");
                    Thread.Sleep(0);
                }
            }
        })
        { IsBackground = true };
        producer.Start();

        producer.Join(TimeSpan.FromSeconds(30));
        bool consumerFinished = consumerDone.Wait(TimeSpan.FromSeconds(30));

        Assert.True(consumerFinished, "Consumer thread did not finish within 30s.");
        Assert.Null(consumerError);
        Assert.Equal(0, ring.DroppedSamples);
        Assert.Equal(chunkCount * chunkSize, received.Count);

        for (int i = 0; i < received.Count; i++)
            Assert.Equal((float)i, received[i]);
    }

    [Fact]
    public void Producer_outpacing_consumer_drops_newest_chunk_cleanly_and_increments_counter()
    {
        // Tiny ring, and the consumer is never run at all until after every write attempt --
        // this deterministically forces the buffer-full path without relying on scheduling
        // timing, so the test can't be flaky.
        const int capacity = 100;
        var ring = new SpscFloatRingBuffer(capacity);

        // Fill it exactly to capacity: should succeed and report full (no room for anything else).
        var fill = new float[capacity];
        for (int i = 0; i < capacity; i++) fill[i] = i;
        Assert.True(ring.TryWrite(fill));
        Assert.Equal(0, ring.DroppedSamples);

        // Now the ring is completely full (0 free samples) -- the next write, of any size,
        // must be dropped in its entirety, not partially applied, and must not corrupt what's
        // already buffered.
        var overflow1 = new float[] { 999f, 998f, 997f };
        bool ok1 = ring.TryWrite(overflow1);
        Assert.False(ok1);
        Assert.Equal(3, ring.DroppedSamples);

        var overflow2 = new float[10];
        for (int i = 0; i < overflow2.Length; i++) overflow2[i] = -1f;
        bool ok2 = ring.TryWrite(overflow2);
        Assert.False(ok2);
        Assert.Equal(13, ring.DroppedSamples); // 3 + 10, cumulative

        // The original data must still be intact and readable, uncorrupted by the dropped
        // write attempts -- proving "drop cleanly" rather than "partially/torn write."
        var drained = new List<float>();
        int count = ring.DrainInto(span => drained.AddRange(span.ToArray()));
        Assert.Equal(capacity, count);
        Assert.Equal(fill, drained);

        // Buffer is empty again now; a subsequent write of a size that would have exceeded the
        // previous free space now fits and is not dropped.
        var next = new float[] { 42f };
        Assert.True(ring.TryWrite(next));
        Assert.Equal(13, ring.DroppedSamples); // unchanged
    }

    [Fact]
    public void Sustained_producer_faster_than_consumer_drops_without_deadlock_or_corruption()
    {
        // A slow/throttled consumer against a fast producer over many iterations: the real
        // "consumer falls behind mid-stream" scenario (as opposed to the deterministic
        // full-then-drain case above), confirming the drop path doesn't deadlock or corrupt
        // data under genuine concurrent contention either.
        const int capacity = 256;
        const int chunkSize = 64;
        const int chunkCount = 500;

        var ring = new SpscFloatRingBuffer(capacity);
        var received = new List<float>();
        Exception? consumerError = null;
        var consumerDone = new ManualResetEventSlim(false);
        bool producerFinished = false;

        var consumer = new Thread(() =>
        {
            try
            {
                int drainedRounds = 0;
                while (drainedRounds < chunkCount * 2) // generous bound; exits via producer signal below
                {
                    int drained = ring.DrainInto(span => received.AddRange(span.ToArray()));
                    drainedRounds++;
                    Thread.Sleep(1); // deliberately slower than the producer below
                    if (Volatile.Read(ref producerFinished) && ring.IsEmpty)
                        break;
                }
            }
            catch (Exception ex)
            {
                consumerError = ex;
            }
            finally
            {
                consumerDone.Set();
            }
        })
        { IsBackground = true };

        consumer.Start();

        for (int i = 0; i < chunkCount; i++)
        {
            var chunk = new float[chunkSize];
            for (int j = 0; j < chunkSize; j++)
                chunk[j] = i * 1000f + j; // encode chunk index so we can validate no torn writes

            ring.TryWrite(chunk); // intentionally ignore return -- drops are expected here
        }
        Volatile.Write(ref producerFinished, true);

        bool consumerFinished = consumerDone.Wait(TimeSpan.FromSeconds(15));
        Assert.True(consumerFinished, "Consumer thread did not finish within 15s (possible deadlock).");
        Assert.Null(consumerError);

        // Drops must have happened (producer is much faster than the throttled consumer) and
        // must be a whole multiple of chunkSize -- proving whole chunks were dropped, never a
        // partial/torn chunk.
        Assert.True(ring.DroppedSamples > 0, "Expected the fast producer to overrun the slow consumer at least once.");
        Assert.Equal(0, ring.DroppedSamples % chunkSize);

        // Every sample actually received must belong to some fully-intact chunk: reconstruct
        // chunk index/offset from the encoding above and check internal consistency in groups
        // of chunkSize.
        Assert.Equal(0, received.Count % chunkSize);
        for (int c = 0; c < received.Count / chunkSize; c++)
        {
            float baseValue = received[c * chunkSize];
            float chunkIndex = MathF.Floor(baseValue / 1000f);
            for (int j = 0; j < chunkSize; j++)
            {
                float expected = chunkIndex * 1000f + j;
                Assert.Equal(expected, received[c * chunkSize + j]);
            }
        }
    }
}
