# Soneto — Project Memory (current-state snapshot)

This file is a **living snapshot** — current facts only, edited in place each session, not
appended to. Full narrative history (why each decision was made, exact bugs found/fixed, review
findings, verbatim test counts per item) lives in `Docs/PROJECT-MEMORY-ARCHIVE.md` (pre-2026-09-03
content) and `Docs/CHANGELOG-ARCHIVE.md` (pre-2026-09-03 detailed build log). This split happened
2026-09-03 after this file grew to ~270KB and `CHANGELOG.md` to ~225KB — useful as a record, but
no longer usable as a quick-orientation reference, which defeated the point of having one. See
"Documentation conventions" at the bottom for the policy going forward.

Read this file first for any Soneto work. Consult the plan docs (`dictation-app-build-plan.md`,
`soneto-implementation-plan-phase{0-1,2,3,4}.md`) for design rationale; consult the archives only
when you need the specific bug/decision history behind a line below.

---

## What Soneto is

Push-to-talk local dictation app (hold hotkey, speak, release, punctuated text lands at cursor).
C#/.NET 10 + Avalonia 11/12. ASR: NVIDIA `parakeet-tdt-0.6b-v3` int8 via `org.k2fsa.sherpa.onnx`
— English + Romanian, no fallback backend (hard constraint). Targets Windows 11 first, Fedora KDE
Wayland for parity, macOS deferred. Repo root `e:/Projects/Soneto`, no git repo.

## Current phase: Phase 4 (Platform Hardening), items 0-4 done (item 1 partial, item 4 automatable half only); item 5 next

**Phase 4 plan** (`Docs/soneto-implementation-plan-phase4.md`, written 2026-09-03): scope
confirmed with the user — macOS deferred entirely (its own future phase); Linux verification via
a new Docker-based harness (spike S7: `uinput`-created virtual keyboard + headless Weston, since
real Fedora/Wayland hardware is still unavailable) rather than waiting further. Covers: closing
most of `[GATE:S5]` via S7, resolving `InjectionConfig.PerApp`/dictionary `PerAppOverride` (both
schema-only since Phase 1/2, never consumed), at least one real per-app injection fallback, and
re-verifying hook-death recovery (already built in Phase 1 item 9 — NOT new work, verification
only). **S7 done (2026-09-03), partial pass — see `spikes/s7-docker-linux/README.md`.** The
real, unmodified `LinuxHotkeySource` was proven working against a real kernel for the first time
ever (a `uinput`-created virtual keyboard + a manually-`mknod`'d event node + a Docker
device-cgroup rule to work around two container-specific access restrictions): real evdev
enumeration/filtering, real epoll capture, 0.38ms/0.00ms Pressed/Released jitter, well under the
30ms bar. Genuine dead end found (not routed around): headless Weston has no `wl_seat` and this
kernel has no `vkms`, so `wl-copy`/real-app-paste verification still needs actual hardware.
**Item 1 (2026-09-03), partially done.** Triaged the `Faulted`-during-`DisposeAsync` finding by
hand: `SessionController.DisposeAsync()` unsubscribes `Faulted` long before disposing the hotkey
source (wide safety margin, not a race) — confirmed harmless in the real integration, no code
changed. **Multi-keyboard hotplug — Phase 1 item 11's own literal "done when" criterion,
previously stated as needing physical hardware — is now genuinely closed**: a second `uinput`
keyboard created mid-session correctly triggered real `inotify`-based re-enumeration,
`RestartAsync` picked up both devices, and the hotkey kept working, including the class's
fd-leak-tolerance fallback firing for real for the first time (worked as designed). Still open,
gated on the same compositor dead end: full diacritics-through-real-paste verification,
EVIOCGRAB-tolerability. Full results: `spikes/s7-docker-linux/README.md`.
**Item 2 done (2026-09-03).** `InjectionConfig.PerApp` is now really resolved and applied,
keyed by the foreground window's owning process name, plus a new `PerAppOverride.Method` field
(per-app injection-method override). Resolution happens inside
`WindowsTextInjector.InjectAsync` right after its own foreground lookup -- not in
`SessionController`/composition -- because `SendInput` targets whatever is foreground at
injection time; the pure lookup/merge lives in
`Soneto.Core/Configuration/PerAppOverrideResolver.cs` (unit-tested with no Win32 call).
Case-insensitive matching is decided in exactly one place: `DaemonComposition` wraps the table
in an `OrdinalIgnoreCase` dictionary once at startup. Windows only -- no portable Wayland
focused-window-to-process lookup exists, so Linux gets no table (documented, not silently
skipped). +16 tests; the item's inherited code didn't compile and was fixed on arrival (two
namespace ambiguities, one non-exhaustive enum switch).
**Item 3 done (2026-09-03).** Dictionary-side `PerAppOverride` (`Soneto.Core/Dictionary/`) is now
really resolved and applied to `PostProcessorChain`, keyed the same way as item 2 (captured
target's process name, `OrdinalIgnoreCase`). `ITextInjector` gained
`TryResolveProcessExecutableName(object? target)` (default impl returns null -- non-breaking for
every pre-existing implementer/test fake); `WindowsTextInjector` overrides it for real,
`LinuxTextInjector` overrides it to explicitly return null (same documented gap as
`CaptureTarget`). **Per-utterance SELECTION, not per-utterance CONSTRUCTION:**
`PostProcessorChain` pre-builds up to four processor-list variants (base;
+`AutoCapitalizeProcessor`; +`TrailingPunctuationProcessor`; +both) once at construction time
(still startup-snapshot-only), and a new `Process(text, processExecutableName)` overload does a
dictionary lookup to pick one of those four already-built lists -- no processor construction, no
file I/O, per utterance. This does NOT extend or conflict with the "no live rebuild" limitation
below: a hot-reloaded config/dictionary file still rebuilds nothing live; only selection among
lists built from the original startup snapshot happens per utterance, exactly analogous to how
item 2's injection-side resolver already re-resolves per injection from a table built once at
startup. `SessionController.cs` touch: one line added at the existing
`_postProcessorChain.Process(...)` call site (resolves the process name, mirroring the shape of
the pre-existing `_capturedTarget`-based `InjectAsync` call) -- a third narrow, deliberate
exception to the "touched only twice" rule below, not a restructuring; all real logic lives in
`Soneto.Core.PostProcessing`/`Soneto.Core.Dictionary`. **Scope call on `AutoCapitalize`/
`TrailingPunctuation` (previously schema-only, zero real consumer):** built two new, real,
narrowly-scoped `IPostProcessor` stages (`AutoCapitalizeProcessor` order 80,
`TrailingPunctuationProcessor` order 85, before `TrailingSpaceProcessor`'s order 90) that are
ONLY ever included when a matching, enabled dictionary `PerAppOverride` profile's own flag is
true -- the default chain (no matching profile) is byte-for-byte unchanged, confirmed by the
full pre-existing suite passing unmodified. Both are deliberately minimal (no abbreviation/
proper-noun handling, no clause-vs-sentence distinction), documented as a first cut, not a claim
of completeness. §2.9's premise (dictionary-backed processors take rule sets via constructor
parameter, not a config singleton) re-confirmed still holds. **Code review, two should-fix
findings, both fixed same session:** neither new processor recorded an `AppliedRule` on a genuine
change (unlike every other text-mutating processor -- flows through to persisted
`HistoryEntry.RulesFired`/History UI diff-highlighting), fixed by recording one only on a genuine
change (mirrors `RegexRuleProcessor`'s pattern); no test proved the new `SessionController` line
with a real (non-null) resolved process name, only the interface default `null` -- fixed with a
new end-to-end `SessionControllerTests` case (fake injector resolves `"wt.exe"`, per-app table
enables `AutoCapitalize`, asserts the actual injected text is capitalized). +36 tests total (28 +
8 from the review fixes), all in `Soneto.Core.Tests`. Full suite: `Soneto.Core.Tests` 533/533,
`Soneto.Platform.Windows.Tests` 100/100 (+2 skips), `Soneto.Platform.Linux.Tests` 55/55,
`Soneto.App.Tests` 60/60; build 0 warnings/0 errors.
**Item 4 done (2026-09-03), automatable half only -- human manual matrix check still open.**
Real hotkey+speech/foreground-app verification against VS Code/Windows Terminal is a
human-only task per this file's own "Live-desktop testing caution" below -- not attempted; no
real foreground app was opened or targeted this item. Built the strongest available
substitute instead: a new, permanent xUnit regression test,
`tests/Soneto.Platform.Windows.Tests/PerAppOverrideEndToEndTests.cs` (`Category=Hardware`,
excluded from the default filter). Mirrors item 7's Permissions Doctor injection self-test
pattern exactly, but in a real WPF off-screen window (this test project already references
WPF via `UseWPF=true`) instead of Avalonia: a per-app table keyed to the TEST PROCESS'S OWN
real, dynamically-resolved executable name maps to `Method=UnicodeSynth`; that window gets
real OS focus via `Activate()`/`Focus()`, then a fresh, throwaway
`WindowsTextInjector.CaptureTarget()` is called immediately after, guaranteeing the captured
target is the test's own window, never an arbitrary foreground app (see below for the honest,
small residual gap in that guarantee). Proves the per-app override is genuinely APPLIED, not
just configured, via a real observable side effect rather than a log string: `UnicodeSynth`
never touches the clipboard, while the base `ClipboardPaste` default (which the test's own
`InjectionOptions` still requests) always does, so the test asserts the real Win32 clipboard
sequence number is unchanged before/after injection -- structural proof the real per-app
resolution branch ran -- combined with a Romanian-diacritic marker string landing byte-correct
in the window. **Flaky initially, fixed, code-review found a BLOCKING gap in that first fix,
fixed again, then re-verified clean -- two rounds, not a single favorable run.** Round 1:
independent test-runner verification found 1 failure in 6 isolated runs
("Activate()"/"Focus()" don't SYNCHRONOUSLY guarantee OS focus has landed by the next
statement; a fixed 150ms post-injection wait was occasionally too short) -- fixed with two
bounded, pumped polls (pump this thread's own Dispatcher, no real yield to another process)
instead of assuming a single call/sleep suffices. Round 2, code review, BLOCKING: both polls'
boolean results were discarded at their call sites -- a timed-out focus poll would silently
fall through to a real `CaptureTarget()`/`InjectAsync` anyway, meaning a genuinely stolen-focus
scenario could send this test's real synthetic keystrokes into whatever else had focus, exactly
the failure this whole self-owned-window pattern exists to prevent -- fixed by hard-failing
(`throw`) BEFORE `CaptureTarget()` if the focus poll times out; the doc comment's prior
"structurally cannot land anywhere but the test's own window" claim was also softened to
honestly acknowledge the residual small window between confirmed-focus and the real
`SendInput` (the same already-accepted gap `PermissionsDoctorViewModel`'s own self-test has).
Re-verified: 10/10 clean isolated runs after round 1's fix, 6/6 more after round 2's fix (16/16
total), plus two clean full-suite re-runs. **Known follow-up gap, flagged not fixed (out of
this item's scope):** code review found the SAME unguarded-focus assumption (single `Focus()`
call assumed synchronous, fixed `Task.Delay(100ms)` assumed sufficient) still exists,
uncorrected, in shipped production code --
`Soneto.App.ViewModels.PermissionsDoctorViewModel.RunInjectionSelfTestAsync` (the "Can
synthesize input" Permissions Doctor check) -- a real user could intermittently see a false red
there on a real machine under load. Not fixed this item (scope); a real follow-up item should
apply the same bounded-poll fix there. Full details/reasoning in
`Docs/soneto-implementation-plan-phase4.md`'s item 4 row. `Docs/MANUAL-TESTS.md` Section A's
per-app checkboxes were deliberately left untouched (only a note added pointing at the new
coverage) -- those rows require real human verification against real apps, not done this
session. Full suite after, zero regressions: `Soneto.Core.Tests` 533/533,
`Soneto.Platform.Windows.Tests` 100/100 (+2 pre-existing skips, new Hardware test correctly
excluded from this default count), `Soneto.Platform.Linux.Tests` 55/55, `Soneto.App.Tests`
60/60; build 0 warnings/0 errors. Next: item 5 (hook-death recovery re-verification, §4.6).

**Phase 1 (Headless Daemon) — items 1-12, code-complete.** Three structurally-blocked gaps
carried forward, not oversights: Linux/Wayland real-hardware verification (needs spike S5 + real
Fedora hardware), the corpus-regression WER assertion (needs spike S2 + a real 60-file EN/RO
corpus, deliberately deferred), and re-verifying items 6/7's original "verified against real
Notepad" claims now that a build-config bug (`WINDOWS` symbol undefined) found in item 9's review
is fixed. Item 10b (24h soak test) also not started.

**Phase 2 (Dictionary Engine) — items 0-10, code-complete, no open gaps.** Purely additive:
`SessionController.cs` has zero references to anything Dictionary-namespaced across all 11 items.

**Phase 3 (Avalonia Shell) — items 0-11, code/docs-complete as of 2026-09-03.** One open gate:
item 11's own literal done-when bar is a human running one continuous end-to-end walkthrough
(real hotkey + real speech) — no agent session can do this (see "Live-desktop testing caution"
below). The walkthrough script is written and ready: `Docs/MANUAL-TESTS.md` Section C.1.

## Locked-in decisions

- ASR `NumThreads` default: **4**, not the plan's literal example of 8 (S1 sweep finding).
- Audio capture-mode default: **OnDemand** (S1b latency finding, p95=58ms vs 150ms bar).
- Solution format: `.slnx`, not `.sln` (deliberate, better choice).
- **`Soneto.App` is the real shipped product; `Soneto.Daemon` stays as a headless CI/scripting
  harness, not deleted.** No IPC between them — `Soneto.App` absorbs the same pipeline
  composition code in-process via a shared `Soneto.Composition.DaemonComposition` library
  (`ILoggerFactory`-in/instances-out static methods, no DI container). Full reasoning in
  `Docs/ARCHITECTURE.md`'s "Why two executables" section.
- **`SessionController.cs` is the designated highest-risk file** — touched only three times on
  purpose beyond its original build: Phase 3 item 2 added `DictationCompleted` (the sole
  pipeline→UI event bridge), item 10 added `AudioSamples` to that event's payload, Phase 4 item 3
  added one line resolving the focused process's executable name and threading it into
  `PostProcessorChain.Process` (mirrors the shape of the pre-existing `_capturedTarget`-based
  `InjectAsync` call; all real per-app selection logic stays in `Soneto.Core.PostProcessing`/
  `Soneto.Core.Dictionary`, not here). Phase 2 never touched it at all (hard rule — a
  dictionary-engine item needing to touch it means the abstractions are wrong, not a normal
  cost).
- **No live rebuild of the post-processor chain / no live re-hook of hotkey config.**
  `PostProcessorChain` is built once at startup from a snapshot of config+dictionary. A
  hot-reloaded `dictionary.json`/`config.json` change requires an app restart to take effect —
  surfaced honestly in the Dictionary editor and Settings UI, not silently implied as live.
  **Still true after Phase 4 item 3's per-app profile selection**: that selection only picks,
  per utterance, among a small number of processor-list variants pre-built once at that same
  startup snapshot — it never rebuilds a processor or re-reads a file at call time, so it's a
  different (narrower) mechanism than a live rebuild, not an exception to this bullet.
- **Two independent data-retention purge policies** (Phase 3 item 10): debug-audio clips purge
  by count (keep-last-N); history text purges by age (daily sweep). Debug-audio retention is
  **off by default**.
- Romanian accuracy validation (S2) deliberately deferred by explicit user decision — assume EN
  is the priority; revisit before shipping RO.

## Recurring bug patterns to watch for (found 9+ times across this project's history)

1. **A background `Timer`/thread touching state that's been superseded** (after `Dispose()`, after
   a restart) — hit in WarmIdle's idle-close timer, the hotkey heartbeat probe, `SessionController`'s
   `_maxDurationTimer`, the Linux reader loop, and others. Default assumption for any new
   background timer: it needs a generation-token or lock-guard. The one confirmed, deliberate
   exception is Phase 3 item 10's `HistoryRetentionSweeper` — verified by two independent
   reviewers that a stale tick there only re-runs an idempotent DB delete with no live resource
   to clobber. Don't assume a new timer is safe without an equivalent explicit trace.
2. **One config value silently used for two logically-different gates** can defeat a safety
   check without anyone noticing (Phase 1 item 5: VAD's `MinSpeechMs` reused as both a
   per-segment filter and the whole-utterance discard threshold, making the discard check
   near-unreachable).
3. **A global input hook that can't tell its own synthetic input from real user input** unless
   explicitly checked (Phase 1 item 7 — `IsEventSimulated`/`LLKHF_INJECTED`).
4. **"The fix works, but is anything real ever calling this code path?"** — error-handling logic
   can be correct while its real-world trigger doesn't actually exist yet (Phase 1 item 10's
   device-lost auto-recovery catch block).
5. **A cited "the plan says X" claim should be checked against the actual plan text**, not a
   paraphrase or a remembered impression (Phase 2 item 8 — an invented doc-comment justification
   that didn't actually appear in the plan).

## Live-desktop testing caution

Never batch-automate synthetic UI/keyboard input against this machine's real, in-use desktop —
one real near-miss occurred during Phase 1 item 4 hardware testing (app-launcher trusted a stale
foreground window), a second during Phase 1 item 7b verification, and a third (unprompted, not
agent-triggered) during Phase 3 item 10's live run. No data was affected in any case, but the
risk is real. Countdown/one-app-at-a-time manual testing only; never a batched automated
launcher. Real end-to-end hotkey+speech verification is always a human's job, not an agent's —
see `Docs/MANUAL-TESTS.md`.

## Gates not yet resolved

- `[GATE:S2]` Romanian accuracy — if RO prose WER > 25%, stop and reconsider (EN-only or evaluate `canary-1b-v2`).
- `[GATE:S5]` Wayland input — if evdev grab is intolerable, fall back to X11-session-only or defer Linux to Phase 4.

## Repo state notes

- `soneto.slnx` at repo root. Layout: `src/Soneto.Core` (platform-agnostic), `src/Soneto.Platform.Windows`
  (net10.0-windows), `src/Soneto.Platform.Linux` (net10.0), `src/Soneto.Composition` (shared
  daemon/app composition), `src/Soneto.App` (the real Avalonia product, `WinExe`), `src/Soneto.Daemon`
  (headless CLI harness) — plus four `tests/` projects. `models/` (gitignored, ~465MB, must be
  re-fetched on a fresh clone) and `spikes/` (S1/S1b/S3/S4, throwaway).
- No git repo, no CI. `dotnet run` on `Soneto.Daemon`/`Soneto.App` always needs an explicit
  `-f net10.0-windows` or `-f net10.0` flag (multi-targeted, can't auto-pick).
- Every `net10.0-windows`-targeted `.csproj` needs the manual `WINDOWS` `DefineConstants` fix
  (the SDK doesn't auto-define a bare `WINDOWS` symbol for that TFM) — copy the pattern from an
  existing csproj rather than re-deriving it; a missing one silently dead-codes an entire
  platform branch (this exact bug shipped invisibly through Phase 1 items 6/7/9 before being
  found).

## Working agreements for this project

- One spike/work-item per session — don't let implementation agents wander into later items.
- Spike code (`spikes/`) is throwaway, no error-handling investment.
- Every `Soneto.Core` abstraction stays platform-agnostic; `Soneto.Core.Tests` must pass with no
  audio device and no model file present.
- Three-agent cycle per item: implementer → independent test-runner verification → code-reviewer.
- Update this file's "Current phase"/"Locked-in decisions" sections **in place** and add one
  compact `CHANGELOG.md` entry after each item lands (see below) — do not let either file regrow
  into a narrative log.

## Documentation conventions (adopted 2026-09-03, after the bloat cleanup)

- **`PROJECT-MEMORY.md`** (this file): current facts only. Edit in place. If a "Locked-in
  decision" or "Recurring pattern" bullet becomes stale, correct or remove it — don't leave
  superseded facts sitting alongside current ones.
- **`CHANGELOG.md`**: one entry per work item, a few lines, not an essay — what shipped, the one
  or two things worth remembering (a real bug found, a deliberate scope decision), final test
  numbers. Full reasoning/narrative belongs in the plan doc's own build-order table row for that
  item (which already carries full detail) or, for anything predating this cleanup, the archives.
- **Archives** (`Docs/PROJECT-MEMORY-ARCHIVE.md`, `Docs/CHANGELOG-ARCHIVE.md`): frozen historical
  record as of 2026-09-03, not maintained further. Consult them for the full story behind any
  item through Phase 3 item 11 — exact bugs, review findings, verbatim reasoning, test counts.
