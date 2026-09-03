# Soneto — Architecture (as-built, Phases 1–3)

The as-built reference for what actually got built — not a restatement of the plan's
aspirational design (though the two match closely, since the plan was followed closely). Every
place the built result deviated from the plan's literal wording is called out explicitly,
because that's exactly the kind of thing a future session picking this project up cold needs to
know, and it wasn't synthesized into one place until now.

This doc was originally written after Phase 1 (headless daemon) closed and is updated as later
phases land real architectural decisions — Phase 2 (dictionary engine, purely additive, no
architecture change — see "What Phase 2 inherits" below) and Phase 3 (the Avalonia shell,
which DOES introduce real new architecture: a second executable, SQLite-backed history, and
data/privacy controls — see "The Avalonia shell (Phase 3)" below). Sections not touched by a
later phase are left as Phase 1 originally described them.

Read this together with `Docs/SPIKE-RESULTS.md` (the numbers that justified several of the
decisions below) and `Docs/PLATFORM-NOTES.md` (Windows-vs-Linux specifics that don't belong
here). Full rationale for anything not covered lives in `Docs/soneto-implementation-plan-phase0-1.md`
(Phase 1), `Docs/soneto-implementation-plan-phase2.md` (Phase 2), and
`Docs/soneto-implementation-plan-phase3.md` (Phase 3).

---

## Solution layout

```
soneto.slnx                          (deliberately .slnx, not .sln — see below)
├── src/
│   ├── Soneto.Core/                 net10.0            — platform-agnostic, no OS APIs
│   ├── Soneto.Platform.Windows/     net10.0-windows     — Win32/SharpHook implementations
│   ├── Soneto.Platform.Linux/       net10.0             — evdev/process-call implementations
│   ├── Soneto.Composition/          net10.0;net10.0-windows — shared daemon composition (Phase 3 item 0, see below)
│   ├── Soneto.App/                  net10.0;net10.0-windows — Avalonia desktop shell, WinExe (Phase 3 item 3, see below)
│   └── Soneto.Daemon/               net10.0;net10.0-windows — console host, thin CLI/Serilog wrapper
└── tests/
    ├── Soneto.Core.Tests/           xunit — must pass with no audio device, no model file
    ├── Soneto.Platform.Windows.Tests/
    ├── Soneto.Platform.Linux.Tests/
    └── Soneto.Corpus/               WAVs + reference.tsv, waiting on spike S2 (see SPIKE-RESULTS.md)
```

**Why the split:** `Soneto.Core` carries every abstraction (`Abstractions/`), every pure
algorithm (resampler, VAD wrapper, post-processing chain, the state machine itself), and every
piece of logic that must be testable on a machine with no microphone, no model file, and no
Windows/Linux-specific API — this is a hard rule (`Soneto.Core` references no platform project
and no OS-specific API), verified at item 1 and never relaxed since. The two `Soneto.Platform.*`
projects hold literally everything that has to differ between Windows and Linux — hotkey
capture and text injection — and nothing else; there is no shared "platform abstraction layer"
project between them because the two implementations don't actually share code, only the
`Soneto.Core.Abstractions` contracts they both implement.

**`Soneto.Composition` (added Phase 3 item 0):** the real end-to-end dictation composition —
model resolution, `IConfigService`/`IDictionaryService` creation and hot-reload wiring,
per-platform `IHotkeySource`/`ITextInjector` selection, and the full
`SessionController`/`PostProcessorChain`/audio-capture/VAD wiring — used to live inline in
`Soneto.Daemon/Program.cs` (~900 lines). It is now `Soneto.Composition.DaemonComposition`, a
small `net10.0;net10.0-windows` class library referenced by `Soneto.Daemon` (and, from Phase 3
item 4 onward, by `Soneto.App` too — see `Docs/soneto-implementation-plan-phase3.md` §3.3/§3.16
item 0), so both executables call the exact same composition code instead of forking it. Its
API is a set of plain static factory methods/functions taking an `ILoggerFactory` and returning
constructed instances directly, deliberately NOT an `IServiceCollection` registration helper —
`Soneto.App` (Avalonia) isn't guaranteed to use the same `Microsoft.Extensions.Hosting` DI
container `Soneto.Daemon` does, and an `ILoggerFactory`-in/instances-out shape is the one both a
console host and a hand-rolled Avalonia composition root can trivially satisfy. `Soneto.Daemon`
itself is now a thin composition root in spirit only: it owns Serilog bootstrap/setup, the CLI
surface (`--transcribe`, `--record`, `--watch-hotkey`, `--inject`, etc. — all CLI-harness-only
and staying in `Program.cs` since `Soneto.App` will never need them), and calls into
`Soneto.Composition` for everything that used to be its own ~900-line inline composition block.

**`Soneto.App` (added Phase 3 item 3, not item 4 as originally sketched):** the real Avalonia
desktop shell — `WinExe`, `net10.0;net10.0-windows`, same TFM/`WINDOWS`-symbol-fix shape as
`Soneto.Daemon.csproj`. The Phase 3 plan (`Docs/soneto-implementation-plan-phase3.md` §3.3/§3.4)
originally sketched `Soneto.App` being created in item 4 (tray icon + main window shell), but
item 3 (design tokens) has its own "done when" bar requiring a kitchen-sink view that actually
renders every token — which needs a window/project to host it. Rather than leave that a
chicken-and-egg problem, item 3 creates the real `Soneto.App` project now, as the minimal
scaffold needed to host one window and run the kitchen-sink view (see §3.16 item 3's row for the
full writeup); item 4 EXTENDS this same already-existing project with the tray icon and the real
nav-rail shell, it does not create a second project. As of item 3, `Soneto.App` references only
`Soneto.Core` (for future items' sake) and `Styles/`'s five design-token resource dictionaries —
it does not yet reference `Soneto.Composition`/`Soneto.Platform.Windows`/`Soneto.Platform.Linux`,
which is item 4's wiring job.

**Deviation from the plan's literal wording: `.slnx`, not `.sln`.** The plan's example solution
tree in §1.2 writes `soneto.sln`. Item 1 used the newer XML-based `.slnx` format instead — a
deliberate, better choice (not an oversight), noted here so it doesn't read as drift against
the spec.

**Why two executables, `Soneto.App` and `Soneto.Daemon`, instead of one — the Phase 3 item 0/3
architecture decision (`soneto-implementation-plan-phase3.md` §3.3), documented here in full per
that section's own explicit instruction.** The build plan's own architecture diagram
(`dictation-app-build-plan.md` §3) draws a single process — `Soneto.App` (Avalonia) hosting both
the UI shell AND `Soneto.Core`'s full dictation pipeline in-process. There is no separate
always-running daemon-plus-IPC design anywhere in either plan doc. But Phase 1/2's real work
landed entirely in `Soneto.Daemon` (a headless console/service host, no UI), which already did
the full real-pipeline composition. Two options existed once Phase 3 needed a UI: (a) keep
`Soneto.Daemon` as the one process that ever runs the real pipeline and build a UI that talks to
it over some local IPC channel, or (b) fold the pipeline into the UI process itself, matching the
plan's original single-process diagram. **Option (a) was rejected — building and securing a
local IPC channel is real, unscoped, un-asked-for complexity that neither plan doc ever
describes or calls for.** Option (b) was chosen: `Soneto.App` absorbs the same real composition
code `Soneto.Daemon` already used (via the shared `Soneto.Composition.DaemonComposition` library
extracted in item 0 — see above), becoming the shipped end-user executable, a single process,
exactly as the build plan's diagram always showed. **`Soneto.Daemon` was deliberately NOT
deleted once `Soneto.App` existed** — it stays as a genuinely useful headless harness: CI-friendly
(no window server needed to build/test/run it), the natural home for `--transcribe`/`--record`/
`--watch-hotkey`/`--inject` and any future scripted verification, and a safety net if
`Soneto.App`'s UI layer ever needs debugging independently of the pipeline. Both executables
call the exact same `Soneto.Composition` entry points — confirmed, item by item through Phase 3
(items 5/6/7/8), to never drift into two forked copies of the same ~40-line "resolve model,
build transcriber, build capture, build chain, build controller, start it" sequence.

---

## Core abstractions (`Soneto.Core.Abstractions`)

Five interfaces, copied member-for-member from plan §1.3 at item 1 with zero drift since
(confirmed independently at item 1's review and never contradicted by any later item).

- **`IHotkeySource`** — global hold-to-talk key capture. `Pressed`/`Released`/`Faulted` events,
  `StartAsync`/`RestartAsync`/`DisposeAsync`. **Threading contract (non-negotiable, per §1.4):**
  the platform hook/callback thread never does work — it sets a flag, posts a command, and
  returns; events are raised from a separate internal consumer thread, and even that consumer's
  handlers must not block for any meaningful duration (an unbounded internal channel can grow
  indefinitely behind a slow handler). `StartAsync`/`RestartAsync`/`DisposeAsync` are
  single-caller/strictly-sequential — implementations don't synchronize overlapping calls to
  these against each other.
- **`IAudioCapture`** — on-demand snapshot semantics for a single push-to-talk utterance:
  `BeginCapture(preRoll)` / `EndCapture()` (returns 16kHz mono float32) / `AbortCapture()`.
  Implementations own the underlying stream lifecycle (open/close per capture mode — see
  "Audio pipeline" below); `LevelChanged` fires at ~20Hz from an internal thread, never the
  real-time audio callback. `StartAsync`/`StopAsync`/`IsRunning` are not synchronized against
  each other — callers drive a given instance from a single thread, strictly sequentially. An
  optional companion `IReadySignal` interface (`WaitForReadyAsync`) lets a capture
  implementation expose "genuinely delivering real audio," distinct from "opened" —
  deliberately not part of the core contract, since a test fake has no meaningful "first real
  sample" concept to implement.
- **`ITranscriber`** — speech-to-text over one already-captured, already-resampled utterance.
  `InitializeAsync` (load + warm-up, must complete before `IsReady`), `TranscribeAsync`
  (16kHz mono float32 in, `TranscriptionResult` out). Implementations own model loading,
  warm-up, and thread configuration.
- **`ITextInjector`** — delivers transcribed text into whatever has focus.
  `CaptureTarget()` (opaque handle at key-down) / `InjectAsync(text, target, opts, ct)`. The
  atomic clipboard-restore pattern (S4's TOCTOU fix — check-and-write as one critical section)
  is explicitly an implementation concern, not part of the interface's shape.
- **`IPostProcessor`** — one ordered stage in the post-processing chain. `Order`, `Name`,
  `Process(PostProcessResult) -> PostProcessResult`. `AppliedRule` is unused in Phase 1 but the
  plumbing exists so Phase 2's dictionary engine drops in at orders 40–70 without touching the
  pipeline (see "What Phase 2 inherits," plan §1.16).

---

## `SessionController` state machine (`src/Soneto.Core/SessionController.cs`)

The heart of the daemon — an explicit `SessionState` enum
(`Initializing → Idle → Recording → Finalizing → Transcribing → Injecting → Cooldown → Idle`,
plus `Faulted`), not scattered booleans, per plan §1.4's own instruction ("every bug you'll hit
here is a state bug") and per §1.15's own note that Item 9 is where an implementation would want
to be clever and must be rejected if it produces booleans/if-chains instead.

**Threading model — single dedicated session worker, one command at a time:**
`IHotkeySource.Pressed`/`Released`/`Faulted` handlers do nothing but post a small
`SessionCommand` struct into an unbounded `Channel<SessionCommand>` and return — the same
"never blocks, just posts and returns" discipline `IHotkeySource` itself requires of its
subscribers. A single worker `Task` drains the channel and fully `await`s each command's entire
handler (including the async capture/VAD/ASR/injection calls) before pulling the next command.
This one fact is what makes every other concurrency property hold without extra locks: edge
case 1 (key-down while not Idle) is just a state check at the top of the handler, since by the
time a second key-down is dequeued the first one's transition has already fully applied; a
stray `Released` mid-`Pressed`-handler just waits in the channel; `DisposeAsync` cancels the
worker's token, completes the channel, and *awaits the worker task before* touching any field
the worker itself only touches (see the `_maxDurationTimer` note below) rather than tearing down
concurrently with in-flight work.

**Why structured this way:** every other component in the daemon (audio capture, VAD, ASR,
post-processing, injection) is already independently thread-disciplined (see each item's own
threading contract above); `SessionController`'s job is purely to sequence calls into them
correctly and reliably as one linear script per utterance, which a single-consumer channel does
for free and a set of booleans/locks would not.

**Edge cases (§1.4's six numbered cases), where each actually lives:**
1. Key-down while not Idle → ignored at the top of `HandleKeyDownAsync`, no queuing.
2. Focus changed during transcription → **handled one layer down, not here.**
   `WindowsTextInjector.InjectAsync` always targets whatever has OS focus *right now*
   (`SendInput` has no way to target a specific non-foreground HWND), so `SessionController`
   passes the captured target straight through unchanged and trusts the injector's own policy
   and logging of both handles.
3. Key stuck down → **is** the "max duration timer" row (`HandleMaxDurationElapsedAsync`), not
   a separate mechanism.
4. Trigger key held during injection → **handled two layers down.** `WindowsHotkeySource`
   ignores self-injected (`IsEventSimulated`/`LLKHF_INJECTED`) trigger-key events, and
   `ModifierSanitizer` excludes whichever VK the configured trigger resolves to from its
   suppress/restore set — `SessionController` needs no extra logic here.
5. Model still warming at first key-down → guarded on `ITranscriber.IsReady`, checked
   synchronously, never blocking the hook/worker thread.
6. Very long transcript → capped at `TranscriptCharCap` (20,000 chars), applied *after*
   post-processing and *before* injection, with the truncation logged.

**A real reconciliation between §1.4's literal table and §1.12's error matrix (item 10):**
§1.4's table says "Recording | audio device lost → Faulted" (permanent). §1.12's error-handling
matrix says the same scenario should be "auto" recoverable (abort, fall back to default device).
The built behavior follows §1.12: `FinalizeRecordingAsync`'s catch block aborts the capture and
takes the normal Cooldown→Idle path, relying on `CaptureDeviceResolver` (already built in item
4b) to fall back to the default device on the next key-down. **Honestly documented, not
overclaimed:** tracing the real `PortAudioCapture`/`CaptureModeController` call chain shows this
catch block is very likely unreachable in production today, since `PortAudioCapture.StopAsync()`
swallows its only failure-prone call rather than rethrowing — it's a defensive backstop against
a currently-hypothetical failure path, not a confirmed, exercised real-hardware mechanism.

**`_maxDurationTimer` disposal ordering (item 9's blocking review fix):** `DisposeAsync` cancels
and *awaits* the worker task before calling `CancelMaxDurationTimer()`, specifically so that
call is never concurrent with the worker thread's own (unsynchronized) reads/writes of that
field — reordering, not locking, closes the race.

---

## Post-processing chain (`Soneto.Core/PostProcessing/`)

Four ordered stages exactly per plan §1.7, each with its own class:

| Order | Class | Behaviour |
|---|---|---|
| 10 | `UnicodeNormalizerProcessor` | NFC normalise; cedilla ş/ţ (U+015F/U+0163) → comma-below ș/ț (U+0219/U+021B). Always on — the plan's prose is treated as authoritative over the unused `PostProcessConfig.NormalizeUnicode` toggle's mere presence. |
| 20 | `SpokenCommandsProcessor` | EN/RO structural commands ("new line"/"new paragraph"/"linie nouă"/"paragraf nou") → `\n`/`\n\n`. Requires punctuation/utterance-boundary context on both sides of the phrase (see below) rather than matching anywhere as a free-standing word. |
| 30 | `WhitespaceCleanerProcessor` | Collapses horizontal whitespace, trims, fixes spacing around punctuation; preserves `\n`, caps 3+ consecutive newlines at 2 — trims *before* capping (see the ordering bug below). |
| 40 | `TrailingSpaceProcessor` | Appends one trailing space if the transcript ends in any non-whitespace character (not literally a "word character" — trailing punctuation counts too, since Parakeet emits sentence-final punctuation) and the option is on. |

`PostProcessorChain` sorts by `Order` and threads a `PostProcessResult` stage-to-stage.
Filler-word stripping is deliberately not here — it needs the (not-yet-built) dictionary's
language awareness and belongs to Phase 2.

**Two real bugs found and fixed, both in the "looks right, mangles a real case" family:**
1. `SpokenCommandsProcessor`'s first pass matched command phrases anywhere they appeared as
   free-standing words — `"my new line of business"` → `"my \n of business"`, directly
   violating §1.13's own emphasized requirement. Fixed by requiring punctuation/clause
   boundaries (start/end of string, or `,.!?;:` with optional whitespace) on both sides,
   leveraging Parakeet's own punctuation output to distinguish an intentional command from
   idiomatic prose — at the accepted cost that some legitimate mid-utterance commands spoken
   without a natural pause no longer fire.
2. `WhitespaceCleanerProcessor`'s original stage ordering ran the 3+-newline cap *before* the
   horizontal-whitespace trim, so whitespace-separated near-blank lines (e.g. `"\n \n \n"`)
   never became literally adjacent and were never capped. Fixed by trimming first.

---

## Configuration (`Soneto.Core/Configuration/`)

`SonetoConfig`/`ConfigPaths`/`ConfigService`/`CaptureModeJsonConverter`, matching plan §1.10's
JSON schema field-for-field. Resolved from `%LOCALAPPDATA%\Soneto\config.json` /
`~/.config/soneto/config.json`. Hot-reloaded via a `System.Threading.Timer`-based 500ms
debounce over `FileSystemWatcher` events, guarded against a dispose/timer race with a shared
lock (`_gate`) so an in-flight watcher callback can never create a timer that outlives a
disposed service. `LoadAsync` never throws — invalid JSON, an unreadable file, and a
permission-denied write path all log an error and keep the previous in-memory config (or
in-memory defaults) rather than propagating, per §1.12's "never crash on a recoverable error"
principle; a defensive top-level try/catch in `Program.cs` is the last line of defense beyond
that. Only `audio.captureMode` gets a soft per-field fallback to `OnDemand` on an unrecognised
value (via a custom `JsonStringEnumConverter`-adjacent `CaptureModeJsonConverter`, not a
try/catch around the whole document) — every other enum (`ReadyCue`/`ResamplerMode`/
`InjectionMethod`/`ClipboardPolicy`) hard-fails the whole file back to the previous config on an
invalid value, an intentional asymmetry documented in-code next to the enum declarations, per
what §1.13 actually specifies (not a parity assumption a future maintainer should make when
adding a new enum).

**`numThreads` default is 4, not the plan's literal `8`.** §1.10's JSON schema example predates
S1's thread-sweep finding; §1.6 itself says the default is "seeded from the S1 sweep result," so
`AsrConfig.NumThreads` defaults to `4` with an XML doc comment pointing back to the discrepancy.

**Config DTOs are kept separate from the runtime abstractions they feed** — `HotkeyConfig` has
the same shape as `Abstractions.HotkeyBinding` but is its own serializable type with a
`ToBinding()` conversion method; `InjectionConfig.ToOptions()` mirrors this for
`InjectionOptions`; `SessionControllerOptions.FromConfig()` mirrors it again one layer up. The
rationale, stated consistently at every layer: JSON serialization/hot-reload/default-value
concerns shouldn't leak into the abstraction contracts `IHotkeySource`/`ITextInjector`/
`SessionController` depend on, keeping those contracts stable if the on-disk schema ever
changes independently.

---

## Windows vs. Linux — architectural shape (see `PLATFORM-NOTES.md` for the specifics)

Both platforms implement the same two interfaces, `IHotkeySource` and `ITextInjector`, with the
same top-level shape (a raw event-reading thread that never does work, posting into a channel
drained by a separate consumer task) but genuinely different mechanisms underneath:

- **Windows** (`WindowsHotkeySource`, `WindowsTextInjector` + `ClipboardManager` +
  `ModifierSanitizer`): SharpHook low-level keyboard hook for capture (with OS-level
  suppression via `SuppressEvent`); clipboard-paste injection via Win32 `SendInput` +
  `OpenClipboard`/`SetClipboardData`, guarded by a clipboard **sequence-number** check.
- **Linux** (`LinuxHotkeySource`, `LinuxTextInjector` + `LinuxClipboardManager` +
  `ClipboardHashGuard`): multi-keyboard `/dev/input/event*` enumeration + `epoll`-multiplexed
  reading for capture (**no OS-level suppression** — see `PLATFORM-NOTES.md`); `wl-copy`/`xclip`
  + `ydotool` process-call injection, guarded by a clipboard **content-hash** comparison (no
  sequence-number equivalent exists on Linux).

Both platform test suites are pure-logic-first: everything that can be unit-tested without a
real hook/kernel/compositor (key-code mapping, device filtering, hash-guard sequencing, backend
selection) is, and everything that genuinely can't (real syscalls, real `ydotool` process
launches) is honestly flagged as compiled-but-unexercised rather than silently assumed working.

---

## What Phase 2 inherits

Per plan §1.16, nothing above needs to change for Phase 2's dictionary engine: it adds
`IPostProcessor` implementations at orders 40–70, populates the already-plumbed `AppliedRule`,
and adds a `dictionary.json` alongside `config.json`. If a future dictionary-engine work item
finds itself needing to change `SessionController` to make this work, treat that as a signal
something in the abstractions above is wrong, not as a normal cost of adding a feature.

---

## The dictionary engine (Phase 2, `Soneto.Core/Dictionary/`)

Confirmed purely additive end to end — `SessionController.cs` has zero references to anything
`Dictionary`-namespaced across all 11 of Phase 2's build-order items, verified explicitly at
Phase 2's close. Five entry types (`VocabularyTerm`, `CorrectionPair`, `RegexRule`,
`SpokenCommand`, `PerAppOverride` — the last accepted by the schema but not yet consumed by
anything, deferred to Phase 4) live in `dictionary.json`, loaded/hot-reloaded/validated by
`DictionaryService` — a sibling to `ConfigService`, same debounced-`FileSystemWatcher`+`Timer`
pattern, same "never throws, keep the previous good state" contract, but with real per-entry
error isolation `ConfigService` doesn't need: one malformed entry (bad regex, empty phrase) is
rejected and logged by index without failing the rest of the file; a duplicate `Id` anywhere
rejects the whole file (previous good config retained), since `AppliedRule` correlation would
otherwise be ambiguous.

Four new `IPostProcessor` stages slot into the existing chain at orders 40/50/60/70, each
independently toggleable in `PostProcessConfig` (`DictionaryEngine`/`RegexRules`/
`FillerWordStripping` — `SpokenCommands` from Phase 1 was retired and replaced in-place by the
Phase 2 extension of the same toggle name): `DictionaryEngineProcessor` (order 40, an
`AhoCorasickAutomaton` trie — the plan's own "single most safety-critical piece" — for
boundary-safe, no-cascade vocabulary/correction matching), `RegexRuleProcessor` (order 50,
deliberately cascading, power-user escape hatch, `MatchTimeout`-bounded against ReDoS),
`SpokenCommandsExtensionProcessor` (order 60, extends Phase 1's structural-command matching with
user/dictionary-file phrases — a same-phrase collision with a built-in silently lets the
user/dictionary entry win, keyed on `Phrase` not `Id`), `FillerWordStripper` (order 70, the one
stage NOT backed by any `dictionary.json` entry type — a small hardcoded EN/RO filler-word list,
since no schema type exists for it).

`src/Soneto.Core/Dictionary/Resources/seed-dictionary.json` ships 24 `VocabularyTerm` + 4
`SpokenCommand` entries (the build plan's own §6.3 list verbatim) as an embedded resource,
written to disk on a genuinely missing `dictionary.json` (mirroring `ConfigService`'s own
first-run-write behavior) and round-tripped through the real parse/validate pipeline rather than
trusted blindly.

**Standing, explicitly-documented limitation carried into Phase 3, not fixed by any Phase 2 or
Phase 3 item:** `PostProcessorChain` is built exactly once, at daemon/app startup, from a
snapshot of both `config.json`'s `PostProcess` section and the loaded dictionary entries. A
hot-reloaded `dictionary.json` (or a `config.json` `PostProcess` toggle change) is validated,
logged, and produces a clear warning that a restart is required for the change to take effect in
the currently-running session — deliberately not "fixed" by rebuilding the chain live, per
`SessionController`'s own "a required change here is a signal something is wrong, not a normal
cost" framing (§1.16). `Soneto.App`'s Dictionary editor (Phase 3 item 7, below) surfaces this
same honest notice in its own UI rather than implying an edit is live.

---

## The Avalonia shell (Phase 3, `Soneto.App`/`Soneto.Composition`)

See "Why two executables" above for the `Soneto.App`/`Soneto.Daemon` split itself. This section
covers what's inside `Soneto.App` once it became the real product (items 3–10).

**Composition-root pattern — services get progressively "eagerized" out of the background
pipeline startup, item by item, as each one needs to be usable independent of whether the real
ASR pipeline ever comes up:** `App.axaml.cs`'s `OnFrameworkInitializationCompleted` constructs
`IConfigService`/`IDictionaryService`/`IHistoryStore` synchronously (via a documented
`Task.Run(...).GetAwaiter().GetResult()` pattern — NOT a bare `.GetAwaiter().GetResult()`, which
deadlocks: Avalonia has already installed its own `SynchronizationContext` on the UI thread by
this point, so a bare blocking call on an `await`-using method tries to resume on the
already-blocked calling thread — a real deadlock hit and fixed during item 7) **before**
`MainWindow` is ever constructed, so History/Dictionary/Settings pages always reflect real
on-disk state immediately, never an empty placeholder that "catches up" once the pipeline starts.
The actual ASR pipeline (`PipelineHost.StartInBackground`, wrapping
`Soneto.Composition.DaemonComposition`'s same entry points `Soneto.Daemon` uses) stays a genuine
fire-and-forget background `Task` — model cold-load takes ~1.7-2.6s, far too slow to block window
construction on, unlike config/dictionary/history's fast local I/O.

**`SessionController.DictationCompleted`** (Phase 3 item 2, the phase's one deliberate,
carefully-scrutinized touch to `SessionController.cs` beyond Phase 1/2's hands-off rule — see
§3.2 above) is the sole bridge from the pipeline to the UI: raised once per completed dictation
(successful injection, failed injection, or an injection exception — never for any of the six
"nothing to inject" discard paths) carrying `RawText`/`FinalText`/`RulesFired`/
`RecordingDuration`/`ProcessingLatency`/`WasInjected`, plus (Phase 3 item 10) `AudioSamples`,
the VAD-trimmed buffer actually transcribed. A throwing subscriber is caught and logged inside
`RaiseDictationCompleted` itself — a real blocking bug found in item 2's own review, since this
event fires BEFORE the mandatory `EnterCooldownAsync()` recovery step, unlike `StateChanged`
which fires after its own state write already landed.

**History (`Soneto.Core/History/`, item 1)** — `SqliteHistoryStore` (Microsoft.Data.Sqlite +
`SQLitePCLRaw.bundle_e_sqlite3`, FTS5 confirmed genuinely available and used, not a `LIKE`
fallback) persists every completed dictation, subscribed fire-and-forget to
`DictationCompleted` in `App.axaml.cs`. One long-lived connection + a `SemaphoreSlim(1,1)` write
gate, plus a SEPARATE read-only connection/gate for `SearchAsync` so a slow search can never
block the hot append path. `IHistoryStore.Changed` (an additive, in-process-only event — not a
cross-process signal) drives `HistoryViewModel`'s live-refresh without it ever needing to talk to
`SessionController`/`PipelineHost` directly. `HistoryView`'s diff highlighting uses
`AppliedRule`'s structured `From`/`To` spans directly, not a general-purpose text-diff library,
per the plan's own explicit instruction to avoid a diff algorithm highlighting a coincidentally-
matching but wrong span.

**Data & privacy (item 10)** — `DataPrivacyConfig` on `SonetoConfig` (opt-in debug-audio
retention, off by default; history auto-delete-after-N-days). Two independent purge policies:
`DebugAudioStore` purges audio clips by count (keep-last-N, oldest-first) since they're "far
larger and more sensitive" than text history; `HistoryRetentionSweeper` purges history rows by
age via a daily `Timer` calling the already-tested `IHistoryStore.PurgeOlderThanAsync` — the
first background `Timer` in this project's history that deliberately does NOT need this
codebase's usual generation-token/lock guard against a stale post-dispose callback (every prior
one did — WarmIdle's idle-close timer, the hotkey heartbeat, `_maxDurationTimer`, the Linux
reader loop), because a late sweep tick only re-runs an idempotent `DELETE ... WHERE
timestamp < cutoff` against SQLite; there is no live resource (a stream, a hook) for a
superseded tick to clobber. Debug-audio WAV files are correlated to a history entry by writing
`{historyId}.wav` only AFTER `IHistoryStore.AppendAsync` returns its real SQLite rowid, so the
filename can never reference a stale or wrong Id. Panic wipe requires two genuinely separate UI
actions (a trigger button, then a distinct modal `ConfirmDialog`'s own Confirm button — neither
is `IsDefault`/`IsCancel`, so no accidental Enter-key or double-click path exists) and empties
both the history store and the debug-audio directory.

**ViewModels (`Soneto.App/ViewModels/`)** are plain, hand-rolled `INotifyPropertyChanged`
classes — no MVVM framework dependency — each with an injectable `uiThreadPost`/settle-timeout
test-facing constructor (a public production constructor defaulting to real
`Dispatcher.UIThread.Post`/real timeouts) so every one is directly unit-testable in
`Soneto.App.Tests` with zero `Avalonia.Headless`/UI-automation harness. This is the phase's
established alternative to the "don't build a `Soneto.App.Tests` UI-automation project" caution
in §3.15 — ViewModel-level logic tests are explicitly fine and expected; driving real synthetic
input against a shared live desktop is the thing that caution actually warns against (see
`Docs/PROJECT-MEMORY.md`, "live-desktop testing caution").
