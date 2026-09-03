# Soneto.Corpus

This directory will hold the WAV files and `reference.tsv` produced by spike S2
(Romanian/English accuracy corpus — see `Docs/soneto-implementation-plan-phase0-1.md`,
§S2 and the Phase 0 exit checklist item "Corpus moved to `tests/Soneto.Corpus/`").

S2 is currently **deferred by user decision** (2026-08-31) — see
`Docs/PROJECT-MEMORY.md` — so this directory is empty for now. Once S2 is run, its
60 WAV files and `reference.tsv` land here and become the basis for Phase 1 work
item 12's corpus regression test.

## Status as of work item 12 (2026-09-01): the harness is ready, only the data is missing

The test harness this directory feeds — `tests/Soneto.Core.Tests/CorpusRegressionTests.cs`,
tagged `[Trait("Category","Corpus")]` per plan §1.13 — is **already built and waiting**. It is
NOT blocked on any code; it is blocked purely on this directory being empty. When S2 actually
runs:

1. Drop the 60 `*.wav` files here, plus a `reference.tsv` in exactly the format spike S2
   specifies (`Docs/soneto-implementation-plan-phase0-1.md`, §S2 step 3): tab-separated
   `filename \t language \t exact expected text`, one row per WAV, no header row.
2. Measure S2's real overall WER once (run the corpus through the harness with the assertion
   temporarily relaxed, or via the S1 CLI directly) and set that real number as
   `CorpusRegressionTests.S2BaselineWer` — it is deliberately `null` right now, with a comment
   explaining why; do not fill it in with a guessed/plausible-looking number.
3. From then on, `dotnet test --filter "Category=Corpus"` runs the full corpus through the real
   `SherpaOnnxTranscriber` and fails if overall WER exceeds that baseline + 2 percentage
   points (plan §1.13's exact tolerance) — the regression check this whole item exists for.

Until then, running that filter deliberately **fails loudly** with a message pointing back
here and at spike S2, rather than silently passing and claiming coverage that doesn't exist.
The bare `dotnet test` default run is unaffected either way (`Category=Corpus` is excluded by
`Soneto.Core.Tests.csproj`'s `VSTestTestCaseFilter` default, same convention as
`SherpaOnnxTranscriberCorpusTests`/`SherpaOnnxTranscriberStressTests`).

The WER calculator itself (`Soneto.Core/Evaluation/WordErrorRateCalculator.cs` — word-level
Levenshtein on lowercased, punctuation-stripped tokens, per plan §1.13's exact spec) is fully
built and unit-tested today (`tests/Soneto.Core.Tests/Evaluation/WordErrorRateCalculatorTests.cs`)
with hand-computed values — it needs no corpus to verify, only the corpus-driven regression
assertion above does.
