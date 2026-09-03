using Soneto.Platform.Windows.Interop;

namespace Soneto.Platform.Windows.Tests;

/// <summary>
/// Pure-logic coverage for <c>WindowsTextInjector.BuildUnicodeSynthBatches</c> (item 10's
/// <c>UnicodeSynth</c> injection method, plan §1.12's clipboard-set-failure fallback) -- no
/// hardware, no real <c>SendInput</c> call, mirrors <c>ModifierSanitizerTests</c>'/
/// <c>InjectionOutcomeMapperTests</c>' established "test the pulled-out pure decision
/// directly via <c>InternalsVisibleTo</c>" pattern for this test project. Building the
/// <c>INPUT[]</c> batches has no OS side effect at all -- only actually calling
/// <c>SendInput</c> with them would touch the live desktop, which
/// <c>WindowsTextInjectorTests.InjectAsync_UnicodeSynthMethod_ThrowsOnCancellation_BeforeAnySendInputCall</c>
/// deliberately avoids exercising in the default run.
/// </summary>
public sealed class WindowsTextInjectorUnicodeSynthBatchingTests
{
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [Fact]
    public void BuildUnicodeSynthBatches_EmptyText_ReturnsNoBatches()
    {
        var batches = WindowsTextInjector.BuildUnicodeSynthBatches("", batchSize: 50);
        Assert.Empty(batches);
    }

    [Fact]
    public void BuildUnicodeSynthBatches_TextShorterThanBatchSize_ReturnsOneBatch()
    {
        var batches = WindowsTextInjector.BuildUnicodeSynthBatches("hello", batchSize: 50);

        Assert.Single(batches);
        Assert.Equal(10, batches[0].Length); // 5 chars * (down + up)
    }

    [Fact]
    public void BuildUnicodeSynthBatches_TextExactlyOneBatchSize_ReturnsOneBatch()
    {
        string text = new string('a', 50);
        var batches = WindowsTextInjector.BuildUnicodeSynthBatches(text, batchSize: 50);

        Assert.Single(batches);
        Assert.Equal(100, batches[0].Length);
    }

    [Fact]
    public void BuildUnicodeSynthBatches_TextOneCodeUnitOverBatchSize_SplitsIntoTwoBatches()
    {
        string text = new string('a', 51);
        var batches = WindowsTextInjector.BuildUnicodeSynthBatches(text, batchSize: 50);

        Assert.Equal(2, batches.Count);
        Assert.Equal(100, batches[0].Length); // 50 code units
        Assert.Equal(2, batches[1].Length);   // 1 code unit
    }

    [Fact]
    public void BuildUnicodeSynthBatches_LongTranscript_BatchesInGroupsOf50CodeUnits()
    {
        // Plan §1.8 "Other notes" literal number: batched ~50.
        string text = new string('x', 205);
        var batches = WindowsTextInjector.BuildUnicodeSynthBatches(text, batchSize: 50);

        Assert.Equal(5, batches.Count); // 50+50+50+50+5
        Assert.Equal(100, batches[0].Length);
        Assert.Equal(100, batches[1].Length);
        Assert.Equal(100, batches[2].Length);
        Assert.Equal(100, batches[3].Length);
        Assert.Equal(10, batches[4].Length);
    }

    [Fact]
    public void BuildUnicodeSynthBatches_EachCodeUnit_ProducesADownThenUpPair_WithCorrectVkScanAndFlags()
    {
        // Per plan §1.8: wVk=0, wScan=<the code unit>, KEYEVENTF_UNICODE set on both events,
        // KEYEVENTF_KEYUP additionally set on the up event only.
        var batches = WindowsTextInjector.BuildUnicodeSynthBatches("Ab", batchSize: 50);
        var batch = Assert.Single(batches);
        Assert.Equal(4, batch.Length);

        AssertUnicodeDown(batch[0], 'A');
        AssertUnicodeUp(batch[1], 'A');
        AssertUnicodeDown(batch[2], 'b');
        AssertUnicodeUp(batch[3], 'b');
    }

    [Fact]
    public void BuildUnicodeSynthBatches_PreservesTextOrderAcrossBatchBoundaries()
    {
        string text = new string('a', 49) + "XY"; // 'X' is the last code unit of batch 1, 'Y' the first of batch 2
        var batches = WindowsTextInjector.BuildUnicodeSynthBatches(text, batchSize: 50);

        Assert.Equal(2, batches.Count);
        AssertUnicodeDown(batches[0][^2], 'X'); // last down/up pair of batch 1
        AssertUnicodeUp(batches[0][^1], 'X');
        AssertUnicodeDown(batches[1][0], 'Y'); // first pair of batch 2
        AssertUnicodeUp(batches[1][1], 'Y');
    }

    [Fact]
    public void BuildUnicodeSynthBatches_HandlesDiacritics_ByRawUtf16CodeUnitValue()
    {
        // ș/ț are single UTF-16 code units (not surrogate pairs) -- confirms no special-casing
        // corrupts non-ASCII text, matching this project's standing diacritic-correctness bar.
        var batches = WindowsTextInjector.BuildUnicodeSynthBatches("ș", batchSize: 50);
        var batch = Assert.Single(batches);
        AssertUnicodeDown(batch[0], 'ș');
        AssertUnicodeUp(batch[1], 'ș');
    }

    [Fact]
    public void BuildUnicodeSynthBatches_ThrowsForNullText()
    {
        Assert.Throws<ArgumentNullException>(() => WindowsTextInjector.BuildUnicodeSynthBatches(null!, 50));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BuildUnicodeSynthBatches_ThrowsForNonPositiveBatchSize(int batchSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsTextInjector.BuildUnicodeSynthBatches("x", batchSize));
    }

    private static void AssertUnicodeDown(InjectionNativeMethods.INPUT input, char expected)
    {
        Assert.Equal(InjectionNativeMethods.INPUT_KEYBOARD, input.type);
        Assert.Equal(0, input.U.ki.wVk);
        Assert.Equal(expected, (char)input.U.ki.wScan);
        Assert.Equal(KEYEVENTF_UNICODE, input.U.ki.dwFlags);
    }

    private static void AssertUnicodeUp(InjectionNativeMethods.INPUT input, char expected)
    {
        Assert.Equal(InjectionNativeMethods.INPUT_KEYBOARD, input.type);
        Assert.Equal(0, input.U.ki.wVk);
        Assert.Equal(expected, (char)input.U.ki.wScan);
        Assert.Equal(KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, input.U.ki.dwFlags);
    }
}
