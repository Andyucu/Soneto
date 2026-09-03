using System.Text.Json;
using Soneto.Core.Dictionary;

namespace Soneto.Core.Tests.Dictionary;

/// <summary>
/// Phase 2 work item 1 (§2.4/§2.12): the five <see cref="DictionaryEntry"/> subtypes
/// deserialize correctly from a hand-written sample <c>dictionary.json</c> (the
/// <c>type</c>-discriminated polymorphic shape built via <see cref="System.Text.Json"/>'s
/// <c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c> attributes on the base type), and the
/// schema round-trips (serialize back to JSON, deserialize again, same values) -- proving
/// it's genuinely symmetric, not just readable in one direction. No matching/automaton
/// logic here (that's items 2/3); this is purely the data model + JSON shape.
/// </summary>
public class DictionaryEntryTests
{
    private static readonly string SampleAssetPath =
        Path.Combine(AppContext.BaseDirectory, "TestAssets", "Dictionary", "sample-dictionary.json");

    [Fact]
    public void SampleFile_Deserializes_AllFiveEntryTypes_WithCorrectValues()
    {
        var json = File.ReadAllText(SampleAssetPath);

        var doc = JsonSerializer.Deserialize<DictionaryDocument>(json, DictionaryJsonOptions.Create());

        Assert.NotNull(doc);
        Assert.Equal(1, doc!.SchemaVersion);
        Assert.Equal(8, doc.Entries.Count);

        var vocab = Assert.IsType<VocabularyTerm>(doc.Entries[0]);
        Assert.Equal("vocab-webmethods", vocab.Id);
        Assert.Equal("webMethods", vocab.Term);
        Assert.True(vocab.Enabled);

        var vocabDisabled = Assert.IsType<VocabularyTerm>(doc.Entries[1]);
        Assert.Equal("SonarQube", vocabDisabled.Term);
        Assert.False(vocabDisabled.Enabled);

        var correction1 = Assert.IsType<CorrectionPair>(doc.Entries[2]);
        Assert.Equal("corr-cloud-code", correction1.Id);
        Assert.Equal("cloud code", correction1.From);
        Assert.Equal("Claude Code", correction1.To);

        var correction2 = Assert.IsType<CorrectionPair>(doc.Entries[3]);
        Assert.Equal("web methods", correction2.From);
        Assert.Equal("webMethods", correction2.To);

        var regex = Assert.IsType<RegexRule>(doc.Entries[4]);
        Assert.Equal("regex-is-number", regex.Id);
        Assert.Equal(@"\bIS (\d+)\b", regex.Pattern);
        Assert.Equal("IS $1", regex.Replacement);

        var command1 = Assert.IsType<SpokenCommand>(doc.Entries[5]);
        Assert.Equal("cmd-new-paragraph", command1.Id);
        Assert.Equal("new paragraph", command1.Phrase);
        Assert.Equal("\n\n", command1.Emits);

        var command2 = Assert.IsType<SpokenCommand>(doc.Entries[6]);
        Assert.Equal("linie nouă", command2.Phrase);
        Assert.Equal("\n", command2.Emits);

        var perApp = Assert.IsType<PerAppOverride>(doc.Entries[7]);
        Assert.Equal("app-terminal", perApp.Id);
        Assert.Equal("wt.exe", perApp.ProcessName);
        Assert.False(perApp.AutoCapitalize);
        Assert.False(perApp.TrailingPunctuation);
    }

    [Fact]
    public void SampleFile_RoundTrips_SerializeThenDeserialize_ProducesEquivalentDocument()
    {
        var json = File.ReadAllText(SampleAssetPath);
        var options = DictionaryJsonOptions.Create();

        var original = JsonSerializer.Deserialize<DictionaryDocument>(json, options)!;

        var reserialized = JsonSerializer.Serialize(original, options);
        var roundTripped = JsonSerializer.Deserialize<DictionaryDocument>(reserialized, options)!;

        Assert.Equal(original.SchemaVersion, roundTripped.SchemaVersion);
        Assert.Equal(original.Entries.Count, roundTripped.Entries.Count);
        // Records give us structural equality for free -- assert entry-by-entry rather
        // than a single list-equals, so a mismatch names which entry/type diverged.
        for (var i = 0; i < original.Entries.Count; i++)
            Assert.Equal(original.Entries[i], roundTripped.Entries[i]);
    }

    [Fact]
    public void Serialize_WritesExpectedTypeDiscriminator_ForEachConcreteType()
    {
        // Guards the actual on-disk discriminator strings (§2's "clean, obvious value per
        // entry, camelCase" requirement) -- a refactor that silently renames/reorders the
        // [JsonDerivedType] attributes should fail this test, not just the round-trip test
        // (which would still pass even if the discriminator strings changed, as long as
        // they changed consistently).
        // WriteIndented is on for the shared options (matches config.json's convention),
        // so use a compact variant here purely to make the discriminator substring checks
        // below independent of indentation whitespace.
        var options = DictionaryJsonOptions.Create();
        options.WriteIndented = false;

        Assert.Contains("\"type\":\"vocabularyTerm\"",
            JsonSerializer.Serialize<DictionaryEntry>(new VocabularyTerm { Id = "x", Term = "y" }, options));
        Assert.Contains("\"type\":\"correctionPair\"",
            JsonSerializer.Serialize<DictionaryEntry>(new CorrectionPair { Id = "x", From = "a", To = "b" }, options));
        Assert.Contains("\"type\":\"regexRule\"",
            JsonSerializer.Serialize<DictionaryEntry>(new RegexRule { Id = "x", Pattern = "a", Replacement = "b" }, options));
        Assert.Contains("\"type\":\"spokenCommand\"",
            JsonSerializer.Serialize<DictionaryEntry>(new SpokenCommand { Id = "x", Phrase = "a", Emits = "b" }, options));
        Assert.Contains("\"type\":\"perAppOverride\"",
            JsonSerializer.Serialize<DictionaryEntry>(new PerAppOverride { Id = "x", ProcessName = "y" }, options));
    }

    [Fact]
    public void DictionaryEntry_DefaultsEnabledToTrue()
    {
        var term = new VocabularyTerm { Id = "x", Term = "y" };
        Assert.True(term.Enabled);
    }
}
