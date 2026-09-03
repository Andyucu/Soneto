using System.Text.Json.Serialization;

namespace Soneto.Core.Dictionary;

/// <summary>
/// Base of the five dictionary entry types (build plan §6.2's table, translated into a
/// real C# shape per Phase 2 plan §2.4). A single discriminated union keeps
/// <c>dictionary.json</c>'s deserialization simple (a <c>type</c> discriminator field via
/// <see cref="System.Text.Json"/>'s built-in polymorphic serialization support, below) and
/// keeps each entry type's own fields honest instead of one giant record with half its
/// fields always null.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(VocabularyTerm), "vocabularyTerm")]
[JsonDerivedType(typeof(CorrectionPair), "correctionPair")]
[JsonDerivedType(typeof(RegexRule), "regexRule")]
[JsonDerivedType(typeof(SpokenCommand), "spokenCommand")]
[JsonDerivedType(typeof(PerAppOverride), "perAppOverride")]
public abstract record DictionaryEntry
{
    public required string Id { get; init; }      // stable identity for AppliedRule/history correlation
    public bool Enabled { get; init; } = true;
}

/// Feeds hotwords when enabled (future hook, NOT wired in Phase 2 — see §2.1's scope note);
/// also seeds casing correction: if the transcript contains a case-different rendering of
/// Term, correct its casing to match, even with no explicit CorrectionPair for it.
public sealed record VocabularyTerm : DictionaryEntry
{
    public required string Term { get; init; }     // e.g. "webMethods"
}

/// The workhorse. From -> To, subject to the matching algorithm in §2.5.
public sealed record CorrectionPair : DictionaryEntry
{
    public required string From { get; init; }     // e.g. "web methods" / "cloud code"
    public required string To { get; init; }        // e.g. "webMethods" / "Claude Code"
}

/// Power-user escape hatch. Runs as a SEPARATE pass from the Aho-Corasick trie -- see §2.5.
public sealed record RegexRule : DictionaryEntry
{
    public required string Pattern { get; init; }   // e.g. @"\bIS (\d+)\b"
    public required string Replacement { get; init; } // e.g. "IS $1" (.NET Regex.Replace syntax)
}

/// Structural/formatting voice command. Supersedes item 8's SpokenCommandsProcessor's fixed
/// EN/RO table -- see §2.6 for the migration.
public sealed record SpokenCommand : DictionaryEntry
{
    public required string Phrase { get; init; }    // e.g. "new paragraph" / "linie nouă"
    public required string Emits { get; init; }      // literal control-character output, e.g. "\n\n"
}

/// Per-app override -- DATA MODEL ONLY in Phase 2. The selection mechanism (detecting the
/// focused app and choosing a profile) is explicitly Phase 4 scope (§2.1). This entry type
/// exists in the schema now so dictionary.json's shape doesn't need to change later, but
/// DictionaryEngineProcessor/RegexRuleProcessor etc. do not read or apply it in Phase 2 --
/// document this clearly in the processors' own doc comments so nobody assumes per-app
/// overrides are live.
public sealed record PerAppOverride : DictionaryEntry
{
    public required string ProcessName { get; init; }  // e.g. "wt.exe", "konsole"
    public bool AutoCapitalize { get; init; } = true;
    public bool TrailingPunctuation { get; init; } = true;
    // extend as needed; not consumed by anything in Phase 2
}
