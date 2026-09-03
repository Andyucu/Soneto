using Microsoft.Extensions.Logging;
using Soneto.Core.Dictionary;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Tests for <see cref="DictionaryCollisionWarnings"/> (Phase 2 work item 8, §2.8): proves the
/// plan's own named risky example (a "cloud" <see cref="CorrectionPair"/>) triggers a warning
/// naming the entry's id and the colliding word, an uncommon single-word pattern does not, a
/// multi-word pattern is never a candidate regardless of its tokens, disabled entries are
/// skipped, and non-CorrectionPair/VocabularyTerm entry types are ignored entirely.
/// </summary>
public class DictionaryCollisionWarningsTests
{
    [Fact]
    public void SingleWordCorrectionPair_CollidingWithCommonWord_LogsWarning()
    {
        var logger = new TestLogger<DictionaryCollisionWarningsTests>();
        DictionaryEntry[] entries =
        [
            new CorrectionPair { Id = "risky-cloud", From = "cloud", To = "Cloudflare" },
        ];

        DictionaryCollisionWarnings.Check(entries, logger);

        Assert.True(logger.HasEntry(LogLevel.Warning, "risky-cloud"));
        Assert.True(logger.HasEntry(LogLevel.Warning, "cloud"));

        var message = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning).Message;
        Assert.Contains("risky-cloud", message);
        Assert.Contains("cloud", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingleWordVocabularyTerm_CollidingWithCommonWord_LogsWarning()
    {
        var logger = new TestLogger<DictionaryCollisionWarningsTests>();
        DictionaryEntry[] entries =
        [
            new VocabularyTerm { Id = "vocab-cloud", Term = "Cloud" },
        ];

        DictionaryCollisionWarnings.Check(entries, logger);

        Assert.True(logger.HasEntry(LogLevel.Warning, "vocab-cloud"));
    }

    [Fact]
    public void UncommonSingleWordPattern_DoesNotLogWarning()
    {
        var logger = new TestLogger<DictionaryCollisionWarningsTests>();
        DictionaryEntry[] entries =
        [
            new CorrectionPair { Id = "webmethods-fix", From = "webMethods", To = "webMethods" },
            new VocabularyTerm { Id = "webmethods-vocab", Term = "webMethods" },
        ];

        DictionaryCollisionWarnings.Check(entries, logger);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void MultiWordPattern_NeverLogsWarning_EvenIfATokenIsCommon()
    {
        var logger = new TestLogger<DictionaryCollisionWarningsTests>();
        DictionaryEntry[] entries =
        [
            new CorrectionPair { Id = "web-methods-pair", From = "web methods", To = "webMethods" },
        ];

        DictionaryCollisionWarnings.Check(entries, logger);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void DisabledEntry_NeverLogsWarning()
    {
        var logger = new TestLogger<DictionaryCollisionWarningsTests>();
        DictionaryEntry[] entries =
        [
            new CorrectionPair { Id = "disabled-cloud", From = "cloud", To = "Cloudflare", Enabled = false },
        ];

        DictionaryCollisionWarnings.Check(entries, logger);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void NonCorrectionPairOrVocabularyTerm_EntryTypesAreIgnored()
    {
        var logger = new TestLogger<DictionaryCollisionWarningsTests>();
        DictionaryEntry[] entries =
        [
            new RegexRule { Id = "regex-cloud", Pattern = "cloud", Replacement = "Cloudflare" },
            new SpokenCommand { Id = "command-cloud", Phrase = "cloud", Emits = "\n\n" },
            new PerAppOverride { Id = "override-cloud", ProcessName = "cloud" },
        ];

        DictionaryCollisionWarnings.Check(entries, logger);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }
}
