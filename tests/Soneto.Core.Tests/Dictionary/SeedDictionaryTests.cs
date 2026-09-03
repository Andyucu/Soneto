using Soneto.Core.Dictionary;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Phase 2 work item 10 (§2.10): validates the embedded <c>seed-dictionary.json</c> resource
/// itself -- separate from <c>DictionaryServiceTests</c>' "missing file writes and loads the
/// seed dictionary" test, which exercises the same content through the first-run code path.
/// Round-trips the embedded resource through the REAL <see cref="DictionaryService"/> load
/// pipeline (not just <see cref="DictionaryDocument"/>'s raw deserialization), since that is
/// what actually validates regex compilation / duplicate-Id / empty-field rules against it.
/// </summary>
public sealed class SeedDictionaryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dictionaryPath;

    public SeedDictionaryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "soneto-seed-dictionary-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dictionaryPath = Path.Combine(_tempDir, "dictionary.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void SeedDictionary_Json_IsNonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(SeedDictionary.Json));
    }

    [Fact]
    public async Task SeedDictionary_LoadsThroughRealDictionaryService_WithNoRejectedEntries()
    {
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);

        var ok = await sut.LoadAsync();

        Assert.True(ok);
        Assert.Empty(sut.Current.RejectedEntries);
        // 24 VocabularyTerm entries + 4 SpokenCommand entries, per §2.10.
        Assert.Equal(28, sut.Current.Entries.Count);
        Assert.Equal(24, sut.Current.Entries.OfType<VocabularyTerm>().Count());
        Assert.Equal(4, sut.Current.Entries.OfType<SpokenCommand>().Count());
    }

    [Fact]
    public async Task SeedDictionary_AllIdsAreUnique()
    {
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);

        await sut.LoadAsync();

        var ids = sut.Current.Entries.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task SeedDictionary_ContainsExpectedVocabularyTerms()
    {
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);
        await sut.LoadAsync();

        var terms = sut.Current.Entries.OfType<VocabularyTerm>().Select(v => v.Term).ToList();

        foreach (var expected in new[]
        {
            "webMethods", "Integration Server", "Trading Networks", "Enterprise Gateway",
            "Universal Messaging", "MFT", "GoAnywhere", "AS2", "EDIINT", "Informatica",
            "PowerCenter", "IDMC", "BusinessObjects", "LoadRunner", "SonarQube", "QuerySurge",
            "Spotfire", "Proxmox", "Unraid", "Avalonia", "keystore", "truststore", "PKCS#12", "JKS",
        })
        {
            Assert.Contains(expected, terms);
        }
    }

    [Fact]
    public async Task SeedDictionary_SpokenCommands_MatchBuiltInPhrasesAndEmits_ButHaveDistinctIds()
    {
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);
        await sut.LoadAsync();

        var commands = sut.Current.Entries.OfType<SpokenCommand>().ToList();

        Assert.Contains(commands, c => c.Phrase == "new paragraph" && c.Emits == "\n\n");
        Assert.Contains(commands, c => c.Phrase == "paragraf nou" && c.Emits == "\n\n");
        Assert.Contains(commands, c => c.Phrase == "new line" && c.Emits == "\n");
        Assert.Contains(commands, c => c.Phrase == "linie nouă" && c.Emits == "\n");

        // Deliberately distinct from SpokenCommandsExtensionProcessor.BuiltInDefaults' Ids --
        // see SeedDictionary's own doc comment for why this collision (by Phrase, not Id) is
        // intentional and considered correct.
        Assert.All(commands, c => Assert.StartsWith("seed.spoken-command.", c.Id));
    }

    [Fact]
    public void SeedDictionary_LoadsSuccessfully_ThroughAFreshDictionaryEngineProcessor()
    {
        // Confirms the seed vocabulary terms actually drive real casing correction once
        // constructed into a processor, not just that they deserialize.
        var entries = new List<Soneto.Core.Dictionary.DictionaryEntry>
        {
            new VocabularyTerm { Id = "seed.vocab.webmethods", Term = "webMethods" },
        };
        var processor = new DictionaryEngineProcessor(entries);

        var result = processor.Process(new Soneto.Core.Abstractions.PostProcessResult("I use webmethods daily", []));

        Assert.Equal("I use webMethods daily", result.Text);
    }

    /// <summary>
    /// Independent-verification follow-up (Phase 2 item 10): the seed dictionary's 4
    /// <see cref="SpokenCommand"/> entries deliberately reuse the same phrases as
    /// <see cref="SpokenCommandsExtensionProcessor.BuiltInDefaults"/>'s hardcoded table, which
    /// means -- per that processor's own "file entry wins on phrase collision" policy -- loading
    /// the seed dictionary silently swaps <see cref="Soneto.Core.Abstractions.AppliedRule.Rule"/>
    /// provenance from <c>builtin.spoken-command.*</c> to <c>seed.spoken-command.*</c> for all
    /// four phrases, with no other committed test proving the seed file's actual <c>emits</c>
    /// values still produce the correct output. This test closes that gap: it loads the real
    /// seed dictionary through the real <see cref="DictionaryService"/>, constructs a real
    /// <see cref="SpokenCommandsExtensionProcessor"/> from its entries, and pins both the emitted
    /// text AND the provenance override for all 4 seed spoken commands -- so a future accidental
    /// edit to (or deletion of an entry from) <c>seed-dictionary.json</c> would fail this test
    /// rather than going undetected.
    /// </summary>
    [Fact]
    public async Task SeedDictionary_SpokenCommands_ProduceCorrectOutput_ThroughARealProcessor_AndOverrideBuiltInProvenance()
    {
        var logger = new TestLogger<DictionaryService>();
        var sut = new DictionaryService(logger, _dictionaryPath);
        await sut.LoadAsync();

        var processor = new SpokenCommandsExtensionProcessor(sut.Current.Entries);

        AssertEmitsAndProvenance("new paragraph.", "\n\n.", "seed.spoken-command.en.new-paragraph");
        AssertEmitsAndProvenance("paragraf nou.", "\n\n.", "seed.spoken-command.ro.paragraf-nou");
        AssertEmitsAndProvenance("new line.", "\n.", "seed.spoken-command.en.new-line");
        AssertEmitsAndProvenance("linie nouă.", "\n.", "seed.spoken-command.ro.linie-noua");

        void AssertEmitsAndProvenance(string input, string expectedText, string expectedRuleId)
        {
            var result = processor.Process(new Soneto.Core.Abstractions.PostProcessResult(input, []));

            Assert.Equal(expectedText, result.Text);

            var rule = Assert.Single(result.Applied);
            Assert.Equal(expectedRuleId, rule.Rule);
        }
    }
}
