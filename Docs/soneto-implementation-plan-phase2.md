# Soneto — Phase 2 Implementation Plan: Dictionary Engine

Companion to `soneto-implementation-plan-phase0-1.md` (Phase 0 spikes + Phase 1 headless daemon, both done as of 2026-09-01 — see `Docs/PROJECT-MEMORY.md` for current status, including the three honestly-documented gaps carried out of Phase 1). This doc does for Phase 2 what that one did for Phase 0/1: a real spec with exact data shapes, matching semantics, and a work-item breakdown with pass criteria, written before implementation rather than derived during it.

Source material: `Docs/dictation-app-build-plan.md` §6 ("The dictionary") and §7 ("Language handling"), and `soneto-implementation-plan-phase0-1.md` §1.16 ("What Phase 2 inherits"). Read both directly if this doc and either of them ever disagree — this doc is the detailed execution plan, those are the design rationale.

---

## 2.1 Definition of done

Per the build plan's own phase summary: **"Full Aho-Corasick engine, NFC + Romanian folding, all five entry types, hot-reload, rule-fired logging. Heavily unit-tested against `Soneto.Corpus` — this is pure logic and should have near-100% coverage. No UI yet; edit the JSON by hand."**

Acceptance, concretely: a test suite of ~100 EN/RO correction-pair test cases passes, including the adversarial ones the build plan calls out by name — `cloud` must not touch `Cloudflare` (full-token-boundary requirement), `ș` and `ş` both match the same rule (diacritic folding), casing is preserved where the transcript's casing should win but honoured where the rule's target casing is explicit (case-insensitive match, case-preserving replace). `dictionary.json` hot-reloads the same way `config.json` already does. Every processed transcript's `AppliedRule` list (scaffolded, unpopulated, since item 1) is now genuinely populated, and a diff between raw and final text is derivable from it.

**Explicitly out of scope for Phase 2** (per the build plan's own phase boundaries — don't scope-creep into these):
- Any UI. `dictionary.json` is hand-edited. Phase 3 builds the Avalonia editor.
- Per-app profiles (§6.5) — that's Phase 4 ("per-app profile table"), not Phase 2. The dictionary engine should be structured so per-app profile selection can slot in later (see §2.9 below) without a redesign, but don't build the profile-selection mechanism itself now.
- SQLite history / rule-fired diff UI (§3, component 8) — Phase 3.
- Word-frequency collision UI warning — this phase implements the *check* (log a warning at dictionary-load time if a new correction pattern collides with a common word), not an interactive "are you sure?" authoring flow, since there's no UI yet.
- Hotword biasing wiring (§6.1, "vocabulary term entries feed hotwords when enabled") — S6 already flagged `modified_beam_search` as experimental/off-by-default with real hallucination risk; don't wire vocabulary-term entries into `AsrConfig`'s hotwords file in this phase. Note the intended future hook in the vocabulary-term entry's own doc comment, but leave it unimplemented.
- Language-profile-driven grammar/dictionary switching (§7.2's "bind a second hotkey as a profile hint") — that needs `SessionController` changes (a second trigger binding) which is explicitly out of Phase 2's "purely additive, `IPostProcessor` only" contract (§1.16). One dictionary/grammar profile applies uniformly in Phase 2; language-aware profile switching is later work.

---

## 2.2 Why this is safe to build without touching Phase 1

`soneto-implementation-plan-phase0-1.md` §1.16, quoted in full since it's the load-bearing constraint for this whole phase:

> Phase 2 (dictionary engine) needs no changes to anything above. It adds `IPostProcessor` implementations at orders 40–70, populates `AppliedRule`, and adds a `dictionary.json` alongside `config.json`. The pipeline, the state machine, and the injection layer stay untouched.
>
> That property — that Phase 2 is purely additive — is the test of whether Phase 1 was built right. If adding the dictionary requires changing `SessionController`, something in the abstractions is wrong.

Concretely, this means:
- `Soneto.Core.Abstractions.IPostProcessor`/`PostProcessResult`/`AppliedRule` (item 1, unchanged since) are the only contract surface this phase touches from outside its own new code.
- `PostProcessorChain` (item 8) already sorts by `Order` and threads `PostProcessResult` stage-to-stage — Phase 2's new processors just need to report the right `Order` (40–70) and get constructed alongside the existing four (10/20/30/40 — wait, see the note below) in whatever composes the chain (currently `Soneto.Daemon/Program.cs`'s `BuildPostProcessors`, item 9/10).
- **Order-numbering correction needed before this phase starts:** item 8 already used order 40 for `TrailingSpaceProcessor`. The build plan's "orders 40–70" range for Phase 2 collides with that. Two options: (a) renumber `TrailingSpaceProcessor` to something outside 40–70 (e.g. 90, "runs last") since it conceptually belongs at the very end of the whole chain anyway (it's about inter-utterance flow, not correction), or (b) shift Phase 2's range to 45–89 or similar. **Recommendation: (a)** — move `TrailingSpaceProcessor` to order 90, since "append a trailing space" is correctly a last-stage concern regardless of what Phase 2 adds in between, and it keeps the build plan's literal "40–70" language accurate for the new work instead of introducing an unexplained gap. This is a real, small Phase 1 file touch (`TrailingSpaceProcessor.Order` and its tests), but it's a one-line-plus-tests change, not a redesign — flag it as work item 0 below rather than silently working around it.
- `SessionController` never imports anything from the new `Dictionary/` namespace directly — it only ever sees `IPostProcessor` through `PostProcessorChain`, exactly as today.

If implementing this phase ever seems to require a `SessionController` change, stop and reconsider the design — that's the signal something upstream was built wrong, per the build plan's own words above.

---

## 2.3 Solution layout

New files live in `src/Soneto.Core/Dictionary/` (a new folder, sibling to the existing `PostProcessing/`, `Asr/`, `Audio/`, `Configuration/`) — per `Docs/dictation-app-build-plan.md`'s own file tree: `"Dictionary/  # Aho-Corasick engine, normalisation, rules"`. Pure platform-agnostic logic throughout — `Soneto.Core`'s hard "no platform assembly referenced" rule applies here exactly as it has for every prior item.

```
src/Soneto.Core/Dictionary/
├── DictionaryEntry.cs           # the 5 entry-type records + the union type/base
├── DictionaryConfig.cs          # dictionary.json DTO (config-vs-runtime split, see §2.7)
├── DiacriticFolder.cs           # match-only fold (ș/ş → ș, ă/â/î → a/a/i), canonical-emit helpers
├── AhoCorasickAutomaton.cs      # trie + failure links + longest-match-first single-pass matcher
├── WordFrequencyList.cs         # bundled EN/RO frequency data + the collision-check lookup
├── DictionaryEngineProcessor.cs # IPostProcessor, order 40 — correction pairs + vocabulary casing
├── RegexRuleProcessor.cs        # IPostProcessor, order 50 — regex rules (separate pass, see §2.5)
├── SpokenCommandsExtensionProcessor.cs  # IPostProcessor, order 60 — supersedes item 8's fixed table (see §2.6)
├── FillerWordStripper.cs        # IPostProcessor, order 70 — um/ăăă stripping (see §2.6)
└── Resources/
    └── seed-dictionary.json     # the §6.3 seed vocabulary, shipped as the default dictionary.json
```

Tests in `tests/Soneto.Core.Tests/Dictionary/`, mirroring `tests/Soneto.Core.Tests/PostProcessing/`'s existing layout convention.

---

## 2.4 Data model — the five entry types

Per build plan §6.2's table, translated into a real C# shape. A single discriminated union (`DictionaryEntry` abstract record + five concrete subtypes) keeps `dictionary.json`'s deserialization simple (a `type` discriminator field, matching `System.Text.Json`'s polymorphic serialization support) and keeps each entry type's own fields honest instead of one giant record with half its fields always null.

```csharp
namespace Soneto.Core.Dictionary;

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
```

`AppliedRule` (already scaffolded, item 1): `record AppliedRule(string Processor, string Rule, string From, string To)`. Phase 2 is the first phase that actually constructs these — `Rule` should carry the matched `DictionaryEntry.Id`, `From`/`To` the actual matched span and its replacement (not the whole entry's pattern, if the match was a fold/case-insensitive variant of it — log what genuinely matched, not the canonical rule text, so the history diff in a later phase is honest about what happened).

---

## 2.5 Matching algorithm — the six numbered rules from §6.2, made concrete

The build plan's six rules, in order, with the actual implementation approach for each:

**1. Normalise to NFC first.** Already exists as `UnicodeNormalizerProcessor` (item 8, order 10) — runs before the dictionary engine in the chain by construction (10 < 40), so `DictionaryEngineProcessor` can assume its input is already NFC-normalized. Don't re-normalize inside the dictionary engine; trust the chain's ordering, but add a debug-mode assertion or a doc-comment-documented assumption so a future reordering of the chain doesn't silently break this.

**2. Romanian diacritic equivalence — match-only fold, canonical emit.** `ș` (U+0219) vs `ş` (U+015F), `ț` (U+021B) vs `ţ` (U+0163) must match each other; `ă`/`â`/`î` fold to `a`/`a`/`i` for match purposes only, never for the emitted replacement text. Build `DiacriticFolder.FoldForMatching(string)` as a pure function distinct from `UnicodeNormalizerProcessor`'s job (that processor CORRECTS cedilla→comma-below in the actual output text; this one is a throwaway matching-only transform that never touches what gets emitted). The Aho-Corasick automaton is built over FOLDED pattern text and matches against FOLDED input text, but the actual replacement substituted into the real (unfolded, NFC, comma-below-diacritic) output string uses each `CorrectionPair.To`'s literal text, which the rule author is responsible for writing with correct canonical diacritics (validate this at dictionary-load time — see §2.8's collision-check sibling: also warn if a `CorrectionPair.To` contains a cedilla-form diacritic, since `UnicodeNormalizerProcessor` runs BEFORE this stage in the chain and won't re-clean text this stage emits afterward).

**3. Case-insensitive match, case-preserving replace.** Match ignoring case. On replace: if the matched span in the ORIGINAL transcript was all-lowercase, all-uppercase, or Title-Case, and the rule's `To` doesn't already have "explicit internal casing" (build plan's phrase) — i.e. the rule's `To` is itself all one case — apply the transcript's casing pattern to the replacement. If the rule's `To` has genuine internal mixed case (like `webMethods` or `Claude Code`'s "Cl"/"C" capitals), honour the rule's casing verbatim regardless of how the transcript spelled the match. Concretely: `"cloud code"` → `Claude Code` (rule's mixed case wins); `"WEBMETHODS"` fully shouted → still `webMethods` (rule's explicit internal casing wins over even all-caps input) — this is exactly the build plan's own example ("Webmethods" at a sentence start should NOT force lowercase). Write this as an explicit decision table in the implementation's doc comment, since "does the rule's target have explicit internal casing" is a judgment call worth being precise about (suggested test: a `To` value counts as having explicit internal casing if it contains both an uppercase and a lowercase letter — i.e. it's not all-one-case itself).

**4. Longest-match-first, single pass.** This is what makes Aho-Corasick the right structure instead of a naive sequential find-and-replace loop: build the automaton over ALL `CorrectionPair.From` values (folded, per rule 2) plus `VocabularyTerm.Term` values (for casing correction) at dictionary-load time, run the WHOLE input text through it ONCE, and at each position prefer the longest matching pattern (standard Aho-Corasick "output the deepest/most-specific match at this position" behavior — most Aho-Corasick implementations naturally support this via the trie depth at the matching node; if hand-rolling, make sure overlapping matches at the same start position resolve to the longest one, not the first one inserted). **A replacement's OUTPUT text is never re-fed through the automaton** — this is the build plan's own explicit reasoning ("that way lies infinite cascades"). Structure `DictionaryEngineProcessor.Process` as: run the matcher once over the input, collect all non-overlapping matches (resolving overlaps by longest-match-first, then by earliest-start-position), build the output string by splicing in replacements at each match span, and return — no loop that re-scans the output.

**5. Glue-tolerant boundaries.** For multi-word `From` patterns, `webmethods`/`web-methods`/`web methods` must all match one rule. Implementation approach: at dictionary-load time, when inserting a multi-word pattern into the trie, also insert normalized variants where internal whitespace/hyphen runs are collapsed to a single canonical separator, and have the matcher's tokenization step treat runs of `\s` or `-` between word characters as equivalent to that canonical separator before matching (i.e., normalize the INPUT's internal glue characters the same way, not just the pattern — both sides need to agree on one canonical form for the comparison to work). Don't literally expand every pattern into 2^n variants for n glue points; normalize both sides to one canonical form instead (simpler, and avoids a combinatorial blowup for a pattern with many word-boundary points).

**6. Never corrupt real words — full-token boundaries.** A rule for `cloud` must not match inside `Cloudflare`. Require that every match starts and ends at a token boundary (defined the same way `WordErrorRateCalculator`'s tokenizer defines "word" for consistency within the codebase — Unicode letter/digit runs, per item 12's own tokenization choice — or, more precisely for THIS use case, require that the character immediately before a match (if any) and immediately after (if any) is NOT a Unicode letter or digit; this is stricter than whitespace-only boundary checking and is exactly what rejects `Cloudflare` containing `cloud` as a substring). This is the single most safety-critical piece of this whole matching engine — **build the adversarial test for this FIRST, before anything else in the matcher, and keep it passing through every subsequent change** (mirroring how this project's Windows-side `SpokenCommandsProcessor`, item 8, needed exactly this kind of boundary discipline and got it wrong on a first pass — see `Docs/PROJECT-MEMORY.md`'s item 8 entry for the full history of that bug and fix, which is directly analogous to this requirement).

### Regex rules run as a separate, later pass — not through the trie

`RegexRule` entries (order 50, its own processor, AFTER `DictionaryEngineProcessor` at order 40) apply `System.Text.RegularExpressions.Regex.Replace` sequentially, each rule against the OUTPUT of the previous regex rule (regex rules genuinely can cascade against each other — that's a deliberate, documented difference from the trie-based correction pairs' single-pass guarantee, since regex is explicitly the "advanced tab, power-user escape hatch" and a power user asking for sequential regex application is a reasonable expectation, unlike accidental cascading in the common-case correction-pair path). Document this asymmetry clearly — the trie pass's single-pass-no-cascade guarantee does NOT extend to the regex pass, and that's intentional, not an oversight. Order 50 (after correction pairs) means regex rules see already-dictionary-corrected text, which matches the build plan's implicit ordering (correction pairs are "the workhorse," regex is the escape hatch for what the workhorse can't express — it should compose with the workhorse's output, not race it).

---

## 2.6 Spoken commands and filler stripping — extending/superseding Phase 1's stubs

Two pieces of item 8 (Phase 1) were explicitly built as placeholders pending this phase:

**`SpokenCommandsProcessor` (item 8, order 20) had a small FIXED EN/RO table** ("new line"/"new paragraph"/"linie nouă"/"paragraf nou" only), with its own doc comment stating "the table is not user-extensible (that's Phase 2's dictionary engine)." Phase 2's `SpokenCommand` entries need to genuinely extend this — but item 8's processor already runs at order 20, BEFORE `UnicodeNormalizerProcessor`'s... no wait, order 10 is `UnicodeNormalizerProcessor`, order 20 is `SpokenCommandsProcessor`, both before the dictionary engine's order-40 slot. Two design choices, pick one and document why: (a) keep item 8's fixed table running at order 20 exactly as-is (it's small, EN/RO structural commands, already correctly boundary-tested per its own item-8 fix), and ADD a new `SpokenCommandsExtensionProcessor` (order 60, this phase) that handles only the USER-DEFINED `SpokenCommand` entries from `dictionary.json`, keeping the two tables genuinely separate rather than merging them; or (b) fully retire item 8's fixed table and migrate its 4 built-in EN/RO phrases into the phase-2 seed dictionary (§2.3's `seed-dictionary.json`) as ordinary `SpokenCommand` entries, deleting `SpokenCommandsProcessor` from the chain. **Recommendation: (b)**, since maintaining two separate spoken-command mechanisms with different extensibility is exactly the kind of divergence that causes bugs later (a user adding a custom command that happens to collide with a hardcoded one, applied at a different chain position with different boundary rules) — but this means work item 6 below includes deleting/retiring `SpokenCommandsProcessor` and moving its 4 phrases into the seed dictionary, with `PostProcessorChain`'s composition in `Program.cs` updated accordingly. This is a real Phase 1 file being retired by Phase 2 work, which is worth calling out explicitly rather than discovering mid-implementation — it happens in work item 6 (§2.12), in the same session as building its replacement, specifically so there's no window where spoken commands stop working. Work item 0 only handles the unrelated, self-contained `TrailingSpaceProcessor` order fix.

**Filler-word stripping** ("strip filler (`um`, `ăăă`)", build plan §6.4) was explicitly deferred from item 8 ("needs the dictionary's language awareness... belongs in Phase 2"). Build `FillerWordStripper` (order 70) as its own small processor: a fixed-ish list of EN/RO filler tokens (`um`, `uh`, `ăăă`, `păi` — extend from real usage, this is genuinely a short, low-risk list unlike spoken commands) removed when they appear as free-standing tokens (same full-token-boundary discipline as §2.5's rule 6 — don't strip "um" out of "album"). Keep this simple and NOT part of the Aho-Corasick trie (it's a much simpler token-membership check, no multi-word patterns, no glue-tolerance needed) — a separate small processor is more honest about its own simplicity than forcing it through the general-purpose engine.

---

## 2.7 Config & persistence

`dictionary.json` lives alongside `config.json` — same directory (`ConfigPaths.Resolve()`'s directory), same `%LOCALAPPDATA%\Soneto\` / `~/.config/soneto/` resolution logic. Add a `DictionaryPaths.Resolve()` following `ConfigPaths`'s exact pattern (same override-then-OS-default logic, same platform-agnostic API usage, no platform project reference).

Hot-reload: reuse `ConfigService`'s established pattern (`FileSystemWatcher` + debounce + "never throws, keeps previous on any failure" contract) rather than inventing a new one — either generalize `ConfigService` to watch a second file, or build a sibling `DictionaryService` with the identical contract (debounce window, invalid-JSON-keeps-previous behavior, a `DictionaryChanged` event). **Recommendation: a sibling `DictionaryService`**, not a generalized dual-file watcher — `ConfigService`'s tests/contract are already established and battle-tested (item 2, with two blocking fixes from its own review history); duplicating its proven shape for a second file is lower-risk than modifying it to watch two files with two different failure/schema-validation stories (dictionary.json's "invalid JSON" story is more complex than config.json's, since a dictionary entry can be individually well-formed-JSON but still fail the Aho-Corasick build step, e.g. a `RegexRule.Pattern` that isn't valid regex syntax — that's a validation failure ConfigService's simpler schema never had to handle).

**Validation on load (before the automaton is (re)built), each failure logged clearly and falling back to the previous good dictionary, matching `ConfigService`'s own "never crash on a bad file" principle:**
- Every `RegexRule.Pattern` must compile as a valid `System.Text.RegularExpressions.Regex` — catch `RegexParseException` at load time, not at first-match time.
- Duplicate `DictionaryEntry.Id` values across the file → reject the whole file (ambiguous `AppliedRule` correlation otherwise).
- A `CorrectionPair`/`SpokenCommand` whose `From`/`Phrase` is empty or whitespace-only → reject that entry (log which one, keep the rest) rather than the whole file, unlike the duplicate-ID case which is a structural problem with the file as a whole.

`PostProcessorChain`'s construction (in `Program.cs`'s `BuildPostProcessors`) needs the loaded `DictionaryConfig`/entries threaded into `DictionaryEngineProcessor`/`RegexRuleProcessor`/`SpokenCommandsExtensionProcessor`/`FillerWordStripper`'s constructors, and needs to react to `DictionaryChanged` the same way hot-reloaded `config.json` changes are handled elsewhere in `Program.cs` (rebuild the affected processors' internal automaton/rule-set, not the whole chain) — check exactly how live config changes currently propagate (or don't) into the constructed `PostProcessorChain` today, since as of item 9/10 the chain is built once at daemon startup from a snapshot of config; Phase 2 needs to decide whether dictionary hot-reload actually rebuilds a live processor's internal state or requires a daemon restart, and that decision should be made explicitly and documented, not left implicit. **Recommendation:** make `DictionaryEngineProcessor` (and its siblings) support a live `Rebuild(IReadOnlyList<DictionaryEntry>)` method that `DictionaryService.DictionaryChanged`'s handler calls, rather than requiring a restart — hot-reload without a restart is the whole point of the build plan's own "hot-reloaded via FileSystemWatcher" requirement for `dictionary.json`.

---

## 2.8 Word-frequency collision warning

Build plan §6.2 rule 6's authoring-time safety net: "On rule creation, run the pattern against a bundled word frequency list for both EN and RO and show a warning if it collides with a common word." No UI exists yet, so this phase implements it as a **load-time log warning**, not an interactive prompt: when `DictionaryService` loads/reloads `dictionary.json`, for each `CorrectionPair.From`/`VocabularyTerm.Term`, check it against a bundled EN+RO word-frequency list (a simple embedded resource — a few thousand of the most common words in each language is enough, doesn't need to be exhaustive) and log a `Warning`-level message if the pattern IS one of those common words verbatim (not "contains" — a multi-word pattern containing a common word as one of several tokens is fine and expected; this check is specifically for a single-word pattern that IS itself a common word, which is the actual `cloud`-vs-`Cloudflare`-style risk the build plan is warning about). This is advisory only — it doesn't block loading, it just gives a human editing `dictionary.json` by hand a signal in the logs that a rule might be risky.

---

## 2.9 Leaving room for Phase 4's per-app profiles without building them now

Per §2.1's scope boundary, `PerAppOverride` entries exist in the schema but aren't consumed. To keep this genuinely future-proof rather than just deferred-and-forgotten: `DictionaryEngineProcessor`/friends should accept their rule-set via a constructor parameter (already true, per §2.7) rather than reading a config singleton directly, so that a future Phase 4 "which profile is active" selector can construct a differently-filtered `IPostProcessor` set per focused app without changing these classes' own internals — this is really just "keep doing what item 8 already does" (its four processors already take narrow constructor parameters, not a whole config object) rather than new work, but worth stating as an explicit design constraint for this phase's new processors too.

---

## 2.10 Seed dictionary

Ship `src/Soneto.Core/Dictionary/Resources/seed-dictionary.json` as the default `dictionary.json` (copied to the real config location on first run, same "first-run" pattern the ASR model download already establishes conceptually, though this is a small embedded resource, not a network download — mirrors how `warmup-en.wav`/`silero_vad.onnx` are already committed embedded resources per item 3/5's precedent). Populate it with build plan §6.3's literal vocabulary list: `webMethods`, `Integration Server`, `Trading Networks`, `Enterprise Gateway`, `Universal Messaging`, `MFT`, `GoAnywhere`, `AS2`, `EDIINT`, `Informatica`, `PowerCenter`, `IDMC`, `BusinessObjects`, `LoadRunner`, `SonarQube`, `QuerySurge`, `Spotfire`, `Proxmox`, `Unraid`, `Avalonia`, `keystore`, `truststore`, `PKCS#12`, `JKS` — as `VocabularyTerm` entries (casing-correction only, no explicit `CorrectionPair` needed for a term that's already correctly-cased vocabulary, not a mis-transcription pattern), plus the `SpokenCommand` entries migrated from item 8's fixed table per §2.6's recommendation (b).

---

## 2.11 Testing

Per the build plan's own bar: "near-100% coverage," pure logic, no audio device or model needed (same `Soneto.Core.Tests` constraint every prior item has honored).

- **The full-token-boundary adversarial test FIRST** (§2.5, rule 6) — `cloud` must not touch `Cloudflare`, `SonarQube` must not touch a word containing it as a substring, etc. Build this before anything else in the matcher and keep it in the regression suite permanently.
- **Diacritic folding**: `ș`/`ş` both match the same rule; `ț`/`ţ` both match; `ă`/`â`/`î` folding works for match purposes; the REPLACEMENT text always emits canonical comma-below forms regardless of which folded variant matched.
- **Casing**: the decision-table cases from §2.5 rule 3 — lowercase input + explicit-mixed-case rule target → rule casing wins; all-caps input + explicit-mixed-case rule target → rule casing still wins; lowercase input + all-lowercase rule target + transcript's original casing pattern was Title-Case at a sentence start → casing-preservation applies (test this exact "Webmethods at sentence start shouldn't force lowercase" example from the build plan verbatim).
- **Longest-match-first, no cascading**: a correction whose OUTPUT text happens to contain another rule's pattern must NOT get re-matched — construct a deliberate test for this (e.g. rule A: `"foo"` → `"bar baz"`, rule B: `"baz"` → `"qux"` — input `"foo"` must produce `"bar baz"`, NOT `"bar qux"`).
- **Glue-tolerant boundaries**: `webmethods`/`web-methods`/`web methods` all hit the same rule.
- **Regex rules cascade (deliberately, unlike correction pairs)** — a dedicated test proving regex rule B can see regex rule A's output, contrasted with a test proving the trie-based pass does NOT do this for correction pairs (the two tests side by side make the intentional asymmetry from §2.5 explicit and regression-proof).
- **~100 EN/RO correction-pair test cases** overall (per the build plan's literal number) — a mix of the project's own seed dictionary entries (§2.10) plus synthetic cases covering the boundary/casing/glue rules above at scale, not just one example of each.
- **Spoken commands**: migrated built-ins (from item 8) plus new user-defined examples, using the SAME punctuation/utterance-boundary matching rule item 8 already validated (don't regress that fix — see `Docs/PROJECT-MEMORY.md`'s item 8 entry for exactly what that rule is and why it exists) if `SpokenCommandsExtensionProcessor`/the migrated commands reuse or reimplement similar matching logic.
- **Filler stripping**: `um`/`ăăă` stripped as free-standing tokens; NOT stripped as substrings of real words (`album` unaffected).
- **Word-frequency collision warning**: a test asserting the log fires for a genuinely common single word, and does NOT fire for a legitimate multi-word technical term that happens to contain a common word as one token.
- **Hot-reload**: mirroring `ConfigServiceTests`' established pattern — invalid `dictionary.json` keeps the previous rule set and logs; a `RegexRule` with unparseable regex syntax is rejected at load with the rest of the file's valid entries still applied; debounce behavior on rapid successive writes.
- **`PostProcessorChain` end-to-end**: the full chain (all Phase 1 processors + all new Phase 2 processors, in the corrected order per §2.2's renumbering) produces the right output for a representative multi-feature input (diacritics + a correction pair + a spoken command + filler stripping + the trailing-space processor now correctly running last).

---

## 2.12 Build order

Following the same "one work item per session, each with a real demo/test" discipline `soneto-implementation-plan-phase0-1.md` §1.15 established for Phase 1.

| # | Item | Done when |
|---|---|---|
| 0 | **Phase 1 reconciliation**: move `TrailingSpaceProcessor` to order 90 (clears the order-40 collision for the whole phase; self-contained, no dependency on later items) | ✅ Done (2026-09-01). Pure renumbering; no test asserted the literal order value (`PostProcessorChain` sorts dynamically), so only doc comments changed. Repo-wide grep confirmed nothing else assumed order 40. `Soneto.Core.Tests` 203/203 (Phase 1 baseline, unchanged). |
| 1 | Data model + `dictionary.json` schema (§2.4) | ✅ Done (2026-09-01). `DictionaryEntry`/`DictionaryDocument` implement the 5 entry types verbatim against this doc's §2.4 spec (independently confirmed line-by-line, including doc comments). `[JsonPolymorphic]`/`[JsonDerivedType]` polymorphism, object-wrapper JSON root. Code review empirically verified failure-mode exceptions are clear/actionable; flagged (now documented, not yet built) that item 9 will need per-entry error isolation since whole-document deserialization is all-or-nothing, and that `DictionaryDocument` has no structural equality. 207/207 `Soneto.Core.Tests` (203 + 4 new), 0 warnings/errors. |
| 2 | `DiacriticFolder` (§2.5 rule 2) | ✅ Done (2026-09-01). `FoldChar`/`FoldForMatching`/`IsCanonicalForm` — pure, stateless, allocation-free, case-preserving. Independent verification programmatically confirmed every fold mapping against real Unicode codepoints (no visual-glyph shortcuts). Code review found no blocking issues; one should-fix applied (documented the implicit NFC-normalized-input assumption this class relies on from `UnicodeNormalizerProcessor`, order 10, plus a regression test for the decomposed-combining-mark edge case). 230/230 `Soneto.Core.Tests`. |
| 3 | `AhoCorasickAutomaton` — the core trie + matcher (§2.5 rules 4-6) | ✅ Done (2026-09-01). Generic, dictionary-model-agnostic `AhoCorasickAutomaton<TValue>` matching on a fold-then-lowercase key (composing `DiacriticFolder`) while returning original-text-referencing spans. Full-token-boundary test built and passing FIRST, per this section's own instruction. An unusually thorough independent verification pass (17 extra adversarial tests) confirmed all of rules 4/5/6 including position-arithmetic for glue-stripping. Code review confirmed amortized-linear complexity (no hidden quadratic trap) and found one real should-fix — colliding canonical-match-keys now fail fast with a clear `ArgumentException` at construction instead of a silently non-deterministic tie-break. 269/269 `Soneto.Core.Tests`. |
| 4 | `DictionaryEngineProcessor` (order 40) — wires the automaton + folding + casing rules into a real `IPostProcessor` | ✅ Done (2026-09-01). Implements rule 3's case-preserving-replace decision (explicit-internal-casing replacement wins verbatim; otherwise the original span's own casing pattern — Title-Case/all-caps/lowercase — is applied). `AppliedRule` genuinely populated for the first time in this project. Caught a real inconsistency in the build plan's own "Webmethods" worked example (doesn't exercise its own casing-adoption branch) and substituted a valid test. 43 tests across two verification rounds confirmed correct; code review found zero should-fix issues. 312/312 `Soneto.Core.Tests`. |
| 5 | `RegexRuleProcessor` (order 50) | ✅ Done (2026-09-01). Regex rules apply in constructor order, cascading deliberately (opposite of item 4's no-cascading guarantee, explicitly documented as intentional in both classes); malformed patterns rejected at construction, not first match; `AppliedRule` populated per match occurrence. Code review found a real blocking gap (no `MatchTimeout` — a ReDoS/catastrophic-backtracking pattern could hang the entire single-worker-thread daemon indefinitely and silently) — fixed with a bounded 250ms timeout, careful commit-or-skip semantics, and a real catastrophic-backtracking regression test. 342/342 `Soneto.Core.Tests`. |
| 6 | `SpokenCommandsExtensionProcessor` (order 60) — user-defined commands from `dictionary.json`; implements §2.6's recommendation (b) in full: retires `SpokenCommandsProcessor` (item 8) in the SAME session, migrating its 4 built-in EN/RO phrases into this processor's own bundled default entries | ✅ Done (2026-09-02). Migrated built-ins behave identically to old item-8 behavior; item 8's punctuation/utterance-boundary matching ported verbatim (not routed through `AhoCorasickAutomaton`, whose boundary rule is weaker); `SpokenCommandsProcessor.cs`/tests removed, `Program.cs` updated, no window where spoken commands were broken. **Caught and fixed a real second-order regression the order-20→60 move introduced**: spoken commands now run after `WhitespaceCleanerProcessor` instead of before it, so a freshly-emitted newline would never get its surrounding-whitespace cleanup — fixed with a small local cleanup, independently re-verified. Code review found and fixed one real should-fix (silent user-vs-user phrase collision, now fails fast at construction). 358/358 `Soneto.Core.Tests`. See `Docs/PROJECT-MEMORY.md` for the full writeup. |
| 7 | `FillerWordStripper` (order 70) | ✅ Done (2026-09-02). `um`/`uh`/`ăăă`/`păi` stripped as free tokens; adversarial substring words (`album`, `plumber`, `Wuhan`, `împăiat`) unaffected. Deliberately NOT backed by `dictionary.json` (no entry type exists for filler words, documented divergence from items 4/5/6). Inherited item 6's order-70-runs-after-order-30 local-cleanup responsibility; code review judged and fixed one real cleanup gap (filler before terminal punctuation — plausible for a push-to-talk app specifically) while leaving a genuinely low-frequency one (asymmetric single comma) documented-but-unfixed. 403/403 `Soneto.Core.Tests`. All four dictionary-adjacent processors (orders 40/50/60/70) now exist. |
| 8 | `WordFrequencyList` + collision-warning logging (§2.8) | ✅ Done (2026-09-02). Hand-curated, non-exhaustive embedded EN (483)/RO (362) common-word lists; advisory-only `DictionaryCollisionWarnings.Check` fires for a common single-word pattern (`"cloud"`), never for a multi-word term even if one token is common. Built as a trivial one-line call site for item 9 to wire in (`DictionaryService` doesn't exist yet). Code review found no blocking issues, 2 small fixes applied (duplicate word-list lines removed, an unqualified "never throws" doc claim softened to be literally accurate). 424/424 `Soneto.Core.Tests`. |
| 9 | `DictionaryPaths` + `DictionaryService` hot-reload (§2.7) | ✅ Done (2026-09-02). `DictionaryPaths.Resolve()` mirrors `ConfigPaths` verbatim (override-then-OS-default, no platform reference). `DictionaryService` mirrors `ConfigService`'s shape/contract closely (500ms-debounce `FileSystemWatcher`+`Timer`, `_disposed`-under-the-same-lock race-closing pattern, "never throws" discipline) rather than generalizing `ConfigService` to a dual-file watcher, per this section's own recommendation. Closed item 1's noted per-entry-error-isolation gap for real: `entries` is parsed as raw `JsonElement`s and deserialized one at a time in its own try/catch, so a malformed entry (bad `type` discriminator, missing required field) is logged/skipped by index without failing the rest of the file. All three validation rules implemented exactly as specified: unparseable `RegexRule.Pattern` -> reject that one entry; empty/whitespace `CorrectionPair.From`/`SpokenCommand.Phrase` -> reject that one entry; duplicate `Id` anywhere in the file -> reject the WHOLE file, previous good dictionary retained. New `DictionaryConfig`/`RejectedDictionaryEntry` runtime types (plain data holders, not services) carry the validated entries plus per-rejection diagnostics -- the config-vs-runtime split this doc's original file-tree sketch called `DictionaryConfig.cs`. `DictionaryCollisionWarnings.Check` (item 8) now has its real call site, firing on every load/reload. **Design decision worth flagging: no `Rebuild()` method was added to the four processor classes.** The plan's own §2.7 language calling this a "recommendation" (not a hard requirement) was taken at face value, and the actual `PostProcessorChain`-swapping wiring is explicitly item 10's job per this table's own build order -- adding `Rebuild()` now would have been scope creep into item 10 with no consumer yet to justify the API surface. Instead, live-rebuild-without-restart is demonstrated at the level that IS this item's job: a test writes a changed `dictionary.json`, waits for the debounced `DictionaryChanged` event, then constructs a FRESH `DictionaryEngineProcessor` from the reloaded `Current` entries and proves its output differs correctly from one built from the old entries -- no daemon restart anywhere in the test. **First-run divergence from `ConfigService`, deliberate:** a missing `dictionary.json` does NOT get written to disk (unlike `config.json`'s defaults-write) -- `Current` just starts at `DictionaryConfig.Empty` in memory, since there's no seed dictionary yet (item 10) and writing an empty file now would just be something item 10's seed-write would immediately have to detect-and-skip past. 12 new tests, 436/436 `Soneto.Core.Tests` (up from 424), 0 warnings/errors on the full `soneto.slnx` build. Independent test-runner verification confirmed all 10 of §2.7/§2.11's pass criteria directly (per-entry isolation genuinely sandwiches a bad entry between two good ones, not "whole file is one bad entry"; duplicate-Id rejection retains the previous good `Current`; debounce collapses 4 rapid writes into 1 reload through the real `FileSystemWatcher` path; the live-rebuild test goes through real file-write + debounce, not a direct `LoadAsync` shortcut). Code review found no blocking issues; 2 should-fix items applied same day: (1) `IDictionaryService.LoadAsync`'s doc comment wrongly claimed a first-run write-then-load, copied from `IConfigService` without updating for this item's deliberately different first-run behavior -- corrected to match the class's own accurate doc comment; (2) `DictionaryService`'s (and, found to be a pre-existing shared gap, `ConfigService`'s) file-read path only caught `IOException`, not `UnauthorizedAccessException`, contradicting both interfaces' own explicit "never throws on an unreadable file" contract -- fixed in both classes together (`catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)`), re-verified 436/436 green after. |
| 10 | Seed dictionary (§2.10) + full end-to-end `PostProcessorChain` demo | ✅ Done (2026-09-02), Phase 2 complete (all 11 build-order items 0-10 done). `src/Soneto.Core/Dictionary/Resources/seed-dictionary.json` (embedded resource, `SeedDictionary` static loader class mirroring `WordFrequencyList`'s pattern) ships §6.3's literal 24-term vocabulary list as `VocabularyTerm` entries plus 4 `SpokenCommand` entries. **Real `DictionaryService` behavior change (not purely additive):** `LoadAsync`'s first-run path now writes the seed dictionary to disk and loads it through the exact same parse/validate pipeline as any other load (genuine `ConfigService` parity, superseding item 9's "start empty, write nothing" behavior, which was explicitly deferred pending this item); first-run loads still don't raise `DictionaryChanged`, matching `ConfigService`'s own precedent. One item-9 test asserting the old behavior was updated in place. **Design decision:** the 4 seed `SpokenCommand` entries deliberately share their `Phrase`s with `SpokenCommandsExtensionProcessor.BuiltInDefaults` (distinct `Id`s: `seed.spoken-command.*` vs. `builtin.spoken-command.*`) — per that processor's own documented "phrase collision, user/file entry wins" policy, the seed entries silently take over `AppliedRule` provenance for these 4 commands once loaded, judged correct (not a bug) since that's exactly what this section calls for; `BuiltInDefaults` itself is untouched, remaining as a fallback for the rarer case dictionary.json is unreadable/unwritable. **`PostProcessConfig` gained 3 new toggles** (`DictionaryEngine`, `RegexRules` as two SEPARATE switches rather than one shared "dictionary" toggle — the trie-based workhorse and the regex escape hatch have different cascading semantics worth isolating independently — plus `FillerWordStripping`, needed since that processor has no `dictionary.json`-backed entry type to gate it otherwise), all defaulting to `true`. `Program.cs` now constructs a real `DictionaryService` (sibling registration/load/watch to `IConfigService`'s existing pattern) and threads its `Current.Entries` into `BuildPostProcessors`, so `DictionaryEngineProcessor`/`RegexRuleProcessor`/`SpokenCommandsExtensionProcessor` see the real dictionary instead of an empty list. **Honest, deliberate limitation:** `SessionController.cs` was read and intentionally NOT changed — `PostProcessorChain` is still built once at daemon startup from a config/dictionary snapshot (a pre-existing gap for `config.PostProcess` since item 9, not newly introduced here); a hot-reloaded `dictionary.json` now logs a clear `LogWarning` that a daemon restart is required to take effect, per this section's own instruction to treat "needs a `SessionController` change" as a signal to stop rather than a challenge to solve. End-to-end test added to `PostProcessorChainTests.cs`: all 7 processors, corrected order 10→30→40→50→60→70→90, one transcript exercising diacritics + a dictionary correction + a cascading regex rule + a spoken command + comma-bounded filler stripping + trailing space, hand-traced stage-by-stage before running and correct on the first run. 7 new tests, 443/443 `Soneto.Core.Tests` (up from 436), 0 warnings/errors across the full `soneto.slnx` (Core/Windows/Linux all green: 443 + 99 [2 hardware-gated skips] + 52). **Addendum:** independent test-runner verification found the item was functionally correct but flagged two genuine test-COVERAGE gaps (not bugs), both closed same day: (1) the full-chain test above proved processor ORDERING but used synthetic dictionary entries, not the real seed file — closed with a new `PostProcessorChainTests` test that loads the real `seed-dictionary.json` through a real `DictionaryService` first-run and proves a real seed `VocabularyTerm` casing correction and a real seed `SpokenCommand` both compose correctly through the exact 7-processor chain `Program.cs` builds; (2) nothing proved the 4 seed `SpokenCommand` entries' actual `emits` values still produce correct output despite silently overriding built-in provenance on load — closed with a new `SeedDictionaryTests` test that runs all 4 real seed phrases through a real `SpokenCommandsExtensionProcessor` and pins both the emitted text and the `seed.spoken-command.*` provenance. 2 more new tests, 445/445 `Soneto.Core.Tests` (up from 443), 0 warnings/errors, full `soneto.slnx` still green. **Final code review (post gap-fix) found no blocking/should-fix issues** — confirmed both new tests are genuinely load-bearing on the real seed file, the first-run write path never bypasses validation or throws, the two new independent toggles are safely uncoupled, `LoadAsync()`/`StartWatching()` sequencing closes off any watcher/first-run-write TOCTOU, and `SessionController.cs` has zero `Dictionary`-namespaced references across all 11 items — confirming §1.16's "purely additive" bar held for the whole phase. **Phase 2 is complete.** |

Each item should get the same implementer → test-runner → code-reviewer cycle the Phase 1 items used, given this project's now well-established (12-items-deep) pattern of that cycle catching real bugs on nearly every item.

---

## 2.13 Working with Claude Code on this

Same discipline as `soneto-implementation-plan-phase0-1.md` §1.15. Start each work-item session with:

```
Read Docs/ARCHITECTURE.md, Docs/PLATFORM-NOTES.md, and this file
(Docs/soneto-implementation-plan-phase2.md).
We are on Phase 2, work item N: <name>.

Scope: <the one item>. Do not touch Soneto.Platform.Windows/.Linux
unless the item explicitly requires it (items 0/9/10 are the only ones
that plausibly do, via Program.cs's chain composition).
Do not build any UI. Do not build per-app profile selection (Phase 4).
Do not wire vocabulary terms into ASR hotwords (explicitly deferred).

Acceptance: <the "done when" cell>.
Write the unit tests in the same session.
```

**The one rule that will save the most time, mirroring Phase 1's own §1.15 advice:** item 3 (the Aho-Corasick automaton) is where correctness matters most and is easiest to get subtly wrong — hand it §2.5's six rules verbatim and require the full-token-boundary adversarial test to exist and pass BEFORE considering any other part of that item done, the same way Phase 1's own history shows exactly this class of boundary-matching bug (item 8's `SpokenCommandsProcessor`) getting through a first implementation pass and only caught by an independent verification agent, not the implementer's own testing.
