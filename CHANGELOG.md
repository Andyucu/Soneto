# Changelog

All notable progress on Soneto, newest first. One compact entry per work item — what shipped,
what's worth remembering, final test numbers. Full narrative detail (exact bugs, review
findings, verbatim reasoning) lives in each item's own row in the relevant plan doc's build-order
table (`Docs/soneto-implementation-plan-phase{0-1,2,3}.md`), and — for everything through
2026-09-03 — in `Docs/CHANGELOG-ARCHIVE.md` (the pre-cleanup detailed log) and
`Docs/PROJECT-MEMORY-ARCHIVE.md`. See `Docs/PROJECT-MEMORY.md`'s "Documentation conventions" for
the policy this file follows going forward.

---

## 2026-09-03 — Phase 4 item 5: hook-death recovery re-verified against real conditions

Verification only, no redesign — Phase 1 item 9's watchdog (`SessionController.
HandleHookFaultedAsync`: 5 attempts, 1s/2s/4s/8s/16s backoff) was thoroughly unit-tested but
never exercised against a genuinely dead hook/device on either platform. Now it has, on both.

**Windows**: new permanent `HookDeathRecoveryHardwareTests.cs` (`Category=Hardware`) — a real
`WindowsHotkeySource`'s real, installed `SimpleGlobalHook` is genuinely `Stop()`'d out from
under it (reflection, same technique the existing heartbeat tests use), then the test waits
REAL wall-clock time (no reflection-invoked-tick shortcut) for the real 60s-idle heartbeat to
detect it and the real watchdog to reinstall a genuinely new, running hook. 2/2 clean runs,
~62s each. Post-recovery liveness had to be proven via the heartbeat's own probe-key channel
(F24), not the trigger key — `WindowsHotkeySourceTests` already established that
`EventSimulator`-driven input is indistinguishable from `WindowsTextInjector`'s own synthetic
paste-chord modifiers and is deliberately ignored on the trigger-key branch; a known, pre-existing
limitation, not a new one.

**Linux**: `spikes/s7-docker-linux/` extended with `run-devicekill-test.sh` — a real uinput
keyboard is genuinely destroyed mid-session while the real `LinuxHotkeySource` reader thread
polls it. 2/2 clean runs: real EPOLLERR/EPOLLHUP fault, real backoff-mirrored recovery once a
replacement device appears. One real finding — in the test script, not `LinuxHotkeySource`:
`mknod` silently no-op'd on a kernel-recycled device name, leaving a stale node the class
correctly refused to open; fixed with `rm -f` before `mknod`.

No production code changed — both findings were test/harness-script bugs. Not wired into a
permanent `DockerLinux`-trait suite (§4.7's own suggested follow-up, still open); documented the
one-off spike run instead, same precedent as items 0/1. Suite unchanged from item 4's baseline
(new test excluded from default filter): Core 533/533, Windows 100/100 (+2 skips), Linux 55/55,
App 60/60; build clean.

## 2026-09-03 — Phase 4 item 4: real per-app injection fallback verified (automatable half)

Built the strongest available substitute for the real human VS Code/Windows Terminal check
(explicitly a human-only task per this project's live-desktop testing caution — not attempted):
a new, permanent xUnit regression test,
`tests/Soneto.Platform.Windows.Tests/PerAppOverrideEndToEndTests.cs` (`Category=Hardware`, run
deliberately once, excluded from the default filter). Mirrors item 7's Permissions Doctor
injection self-test pattern (real `ITextInjector` call, real read-back, target guaranteed to be
this process's own window) but in a real off-screen WPF window instead of Avalonia. A per-app
table keyed to the *test process's own real, dynamically-resolved executable name* maps to
`Method=UnicodeSynth`; a fresh, throwaway `WindowsTextInjector.CaptureTarget()` is called
immediately after giving that window real OS focus, with no yield point in between — the
captured target can never be an arbitrary foreground app, modulo one honest residual gap noted
below. Proves the override is genuinely *applied*, not just configured, via a real observable
side effect rather than a log string: `UnicodeSynth` never touches the clipboard, while the
base `ClipboardPaste` default (still requested by the test's own options) always does — the
test asserts the real Win32 clipboard sequence number is unchanged before/after injection, plus
a Romanian-diacritic marker string landing byte-correct in the target.

Two rounds of findings, both fixed, before landing clean — not a single favorable run. Round 1
(test-runner verification): 1 failure in 6 isolated runs (`Activate()`/`Focus()` requesting OS
focus does not synchronously guarantee it has landed by the next statement; a fixed 150ms
post-injection settle wait was occasionally too short) — fixed with two bounded, pumped polls
instead of a longer fixed sleep, which would only move the same races further out. Round 2
(code review), BLOCKING: both polls' boolean results were discarded at their call sites, so a
timed-out focus poll would silently fall through to a real `CaptureTarget()`/`InjectAsync`
anyway — in a genuine stolen-focus scenario this could have sent real synthetic keystrokes into
whatever else had focus, exactly what this pattern exists to prevent. Fixed by hard-failing
before `CaptureTarget()` runs if the focus poll times out; also softened an overstated doc
comment claim to honestly note the small residual window between confirmed focus and the real
`SendInput` call (`WindowsTextInjector.InjectAsync` re-fetches the foreground window itself at
send time) — the same already-accepted gap the shipped Permissions Doctor self-test has.
Re-verified: 10/10 clean isolated runs after round 1, 6/6 more after round 2 (16/16 total), two
clean full-suite re-runs. **Follow-up flagged, not fixed (out of scope):** the same
unguarded-focus assumption this test originally had still exists, uncorrected, in shipped
`PermissionsDoctorViewModel.RunInjectionSelfTestAsync` — a real user could intermittently see a
false red there; recorded in `Docs/PROJECT-MEMORY.md` as a standing follow-up.

`Docs/MANUAL-TESTS.md` Section A's per-app checkboxes deliberately untouched (a note was added
pointing at this new coverage, but no row was checked — those require real human verification
against real apps, not done this session).

Suite unchanged from item 3's baseline (new test excluded from default filter): Core 533/533,
Windows 100/100 (+2 skips), Linux 55/55, App 60/60; build clean.

## 2026-09-03 — Phase 4 item 3: dictionary-side PerAppOverride wired into PostProcessorChain

Dictionary schema's `PerAppOverride` (schema-only since Phase 2) is now resolved and applied to
the post-processor chain, keyed the same way as item 2 (captured-target process name,
`OrdinalIgnoreCase`). `ITextInjector` gained `TryResolveProcessExecutableName(object? target)`
(default returns null, non-breaking for existing implementers); `WindowsTextInjector` implements
it for real, `LinuxTextInjector` returns null explicitly (same documented gap as
`CaptureTarget`). Selection, not construction, is per-utterance: `PostProcessorChain` pre-builds
up to four processor-list variants once at construction (still startup-snapshot-only); the new
`Process(text, processExecutableName)` overload just picks among them — no file I/O or processor
construction per utterance, so this doesn't extend the standing "no live rebuild" limitation, it's
a narrower, different mechanism operating within that same fixed snapshot. `SessionController.cs`
touch: one line at the existing `Process` call site (third deliberate exception to "touched only
twice"). Scope call: built two new, real, narrowly-scoped processors —
`AutoCapitalizeProcessor`/`TrailingPunctuationProcessor` — only ever active when a matching,
enabled profile's own flag is true; default chain unchanged (confirmed by the full pre-existing
suite passing unmodified). §2.9's premise re-confirmed still holds.

Code review, two should-fix findings, both fixed same session: (1) neither new processor recorded
an `AppliedRule` on a genuine change (unlike every other text-mutating processor — this flows
through to persisted `HistoryEntry.RulesFired` and the History UI's diff-highlighting) — fixed,
both now record one only on a genuine change, mirroring `RegexRuleProcessor`'s pattern; (2) no
test proved the new `SessionController` line with a real (non-null) resolved process name, only
the interface's default `null` — fixed with a new end-to-end `SessionControllerTests` case
(fake injector resolves `"wt.exe"`, per-app table enables `AutoCapitalize`, asserts the actual
injected text is capitalized).

Tests +36 total (28 original + 8 from the review fixes), all in `Soneto.Core.Tests` (pure logic —
resolver, chain selection, both new processors' `AppliedRule` recording, one new
`SessionController` end-to-end case). Suite: Core 533/533, Windows 100/100 (+2 skips), Linux
55/55, App 60/60; build clean.

## 2026-09-03 — Phase 4 item 2: PerApp injection resolution wired up

`InjectionConfig.PerApp` (schema-only since Phase 1) is now actually resolved and applied,
keyed by the foreground window's owning process name, plus a new `PerAppOverride.Method` field
so a single app can be forced onto `UnicodeSynth`/`ClipboardPaste`. Resolution runs inside
`WindowsTextInjector.InjectAsync`, right after its own fresh foreground-window lookup (that's
the only place that knows where `SendInput` will really land); the pure lookup/merge decision
lives in the new `Soneto.Core/Configuration/PerAppOverrideResolver.cs` and unit-tests without
any Win32 call. `DaemonComposition` wraps the table in an `OrdinalIgnoreCase` dictionary once
at startup — the single place case-insensitive executable matching is decided. Windows only:
no portable way for an unprivileged Wayland client to resolve the focused window's process, so
Linux deliberately gets no table. The inherited code from the prior session didn't compile —
two `InjectionMethod`/`PerAppOverride` namespace ambiguities and a non-exhaustive enum switch,
all fixed; the switch now throws rather than silently falling back. Review fix: the
process-name lookup is now skipped entirely when no overrides are configured, so the default
path adds zero native calls.

Tests +16 (14 resolver, 2 `config.json` round-trip incl. the new `method` field). Suite:
Core 497/497, Windows 100/100 (+2 pre-existing skips), Linux 55/55, App 60/60; build clean.
Noted, not fixed: `System.Text.Json` escapes `+`, so paste chords land in `config.json` as
`ctrl+v` — pre-existing since Phase 1, round-trips fine, readability only.

## 2026-09-03 — Phase 4 item 1: Linux checklist re-run against S7, partial

Triaged item 0's `Faulted`-during-`DisposeAsync` finding by hand: `SessionController.DisposeAsync()`
unsubscribes `Faulted` long before disposing the hotkey source, confirmed harmless in the real
integration, no code changed. **Multi-keyboard hotplug — Phase 1 item 11's own literal "done
when" criterion, previously stated as unclosable without physical hardware — is now genuinely
closed**: created a second `uinput` keyboard mid-session, confirmed the real `inotify` watch
detected it, `RestartAsync` re-enumerated both devices, and the hotkey kept working afterward —
including the class's fd-leak-tolerance fallback (built in Phase 1, never previously exercised)
firing for real and working exactly as designed. Still open, same compositor dead end as item 0:
full diacritics-through-real-paste verification, EVIOCGRAB-tolerability. Full results:
`spikes/s7-docker-linux/README.md`.

## 2026-09-03 — Phase 4 item 0: Spike S7 (Docker Linux hotkey harness), partial pass

Real, unmodified `Soneto.Platform.Linux.LinuxHotkeySource` (built blind in Phase 1 item 11)
proven working against a real kernel for the first time — a `uinput`-created virtual keyboard
inside a Docker container, made visible to the container via a manual `mknod` of the resulting
`/dev/input/eventN` node (Docker doesn't auto-populate dynamically-created device nodes) plus a
`--device-cgroup-rule='c 13:* rmw'` (Docker's device cgroup otherwise blocks it even after
`mknod`). Real evdev enumeration/keyboard filtering, real epoll capture, a real synthesized
press/release cycle firing Pressed/Released at 0.38ms/0.00ms jitter (30ms bar). `ydotoold`/
`ydotool key` also proven mechanically functional, independent of any compositor. **Genuine dead
end, not routed around**: headless Weston exposes no `wl_seat` and this WSL2 kernel has no
`vkms` (virtual DRM), so `wl-copy`/`wl-paste` and confirming injected text lands in a real
Wayland app remain unverified — needs real Fedora hardware or a different virtualization
approach, same gap as before. One real, untriaged finding: `LinuxHotkeySource` raises `Faulted`
during its own `DisposeAsync()` teardown, confirmed dispose-time-specific (not a spontaneous
false-positive) via a control run that stayed idle 8s with no fault. Full results:
`spikes/s7-docker-linux/README.md`. `Docs/SPIKE-RESULTS.md`'s S5 entry and the Phase 4 plan's
build-order table updated.

## 2026-09-03 — Phase 4 plan written (not started)

`Docs/soneto-implementation-plan-phase4.md` written. Scope confirmed with the user: macOS
deferred entirely; Linux verification via a new Docker-based harness (spike S7 —
`uinput`-created virtual keyboard + headless Weston) instead of waiting for physical hardware.
Covers closing most of `[GATE:S5]`, resolving the schema-only-since-Phase-1/2 `InjectionConfig.PerApp`/
dictionary `PerAppOverride`, one real per-app injection fallback, and re-verifying (not
rebuilding) hook-death recovery. S7 is the gating first item.

---

## Phase 3 — Avalonia Shell (2026-09-02 to 2026-09-03)

- **Item 11 (2026-09-03) — docs half done; the demo itself is an open, human-only gap.**
  `Docs/ARCHITECTURE.md` retitled/expanded (two-executable decision, Phase 2 dictionary engine,
  Phase 3 shell architecture). `Docs/MANUAL-TESTS.md` gained Section C.1, a checkbox walkthrough
  script. The actual continuous human walkthrough has not been run by anyone.
- **Item 10 (2026-09-03) — Data & privacy controls, done.** Opt-in debug-audio retention (off by
  default, correlated to a history entry's real SQLite rowid), independent count-bounded audio
  purge + daily age-bounded history sweep, panic wipe gated behind a genuine two-click
  confirmation dialog. One deliberate `SessionController.cs` touch (`AudioSamples` field). A real
  test-infrastructure flake (a settle-timeout race under parallel test load) found and fixed via
  a deterministic TCS-gate. Tests: 481 Core / 60 App / 100+2skip Windows / 55 Linux, all green.
- **Item 9 (2026-09-03) — Permissions Doctor, done.** Five real environment checks (mic, global
  hook, injection self-test, clipboard, model files), each independently re-runnable, verified
  Green/Red for real via a temporary broken-config harness. No blocking issues; 4 should-fix
  items closed. Tests: 467/100+2skip/55/47.
- **Item 8 (2026-09-02) — Settings page, done.** `IConfigService` moved to the eager composition
  root; the second-hotkey language-profile capture built but deliberately left inert (SharpHook
  can't run two concurrent hooks on this machine). Two real blocking bugs found and fixed: an
  unparseable Win/context-menu key alias breaking a live hotkey rebind, and a settings-form race
  during save. Tests: 467/100+2skip/55/35.
- **Item 7 (2026-09-02) — Dictionary editor, done.** `IDictionaryService` moved to the eager
  composition root; a real UI-thread deadlock (blocking on an Avalonia-captured
  `SynchronizationContext`) found and fixed via `Task.Run(...).GetAwaiter().GetResult()`. Write
  path is zero-new-watch-code — reuses the already-running file watcher. Tests: 467/100+2skip/55/25.
- **Item 6 (2026-09-02) — History view (list + FTS5 search + diff), done.** History persistence
  decoupled from live-pipeline success (constructed eagerly, works even if the ASR model never
  loads). Diff highlighting uses `AppliedRule`'s structured spans, not a text-diff library. First
  `Soneto.App.Tests` project. Two real bugs fixed: a stale-slower-refresh race, diff over-matching.
  Tests: 467/100+2skip/55/13.
- **Item 5 (2026-09-02) — Recording HUD + live level meter, done.** First real wiring of the live
  pipeline into `Soneto.App` (`PipelineHost`, fire-and-forget background start). HUD never steals
  focus, verified via a temporary debug harness (deleted after one verification run) rather than
  a real hotkey press. Tests: 467/100+2skip/52 (no App.Tests yet).
- **Item 4 (2026-09-02) — Tray icon + main-window nav-rail shell, done.** Extends the `Soneto.App`
  project (not a second one). Close-to-tray with a real Quit path. One real startup crash
  (XAML `SelectedIndex` firing before a named field was assigned) found and fixed.
- **Item 3 (2026-09-02) — Design tokens, done.** Creates the real `Soneto.App` project (moved up
  from item 4 to resolve a chicken-and-egg dependency). Five token files (colors/typography/
  spacing/elevation/motion), all `{DynamicResource}`-bound. Genuine visual confirmation via
  screenshot.
- **Item 2 (2026-09-02) — `SessionController.DictationCompleted`, done.** The phase's one
  deliberate, carefully-scrutinized touch to the state-machine file beyond item 0. One real
  blocking bug: a throwing subscriber could strand the session permanently in `Injecting` — fixed
  with an internal try/catch before the mandatory cooldown transition. Tests: 466/99+2skip/52.
- **Item 1 (2026-09-02) — `IHistoryStore` + SQLite/FTS5, done.** FTS5 confirmed genuinely
  available on this package set (not the `LIKE` fallback). One long-lived connection + a write
  semaphore, a separate read-only connection so search never blocks the hot append path. One
  blocking dispose-race bug found and fixed. Tests: 455/99+2skip/52.
- **Item 0 (2026-09-02) — `Soneto.Composition` extraction, done.** ~900 lines of `Soneto.Daemon`'s
  real composition logic extracted into a shared library so `Soneto.App` (from item 4 onward)
  calls the same code, not a fork of it. Zero behavior change to `Soneto.Daemon`, confirmed via
  build/test/live-run comparison.

## Phase 2 — Dictionary Engine (2026-09-01 to 2026-09-02)

- **Item 10 (2026-09-02) — seed dictionary + real daemon wiring, done. Phase 2 complete.** 24
  vocabulary + 4 spoken-command seed entries, embedded and round-tripped through real validation
  on first run. Three new independent post-processing toggles. Confirmed `SessionController.cs`
  has zero Dictionary-namespaced references across all 11 items. Tests: 445/99+2skip/52.
- **Item 9 (2026-09-02) — `DictionaryService` (hot-reload/validation), done.** Per-entry JSON
  error isolation (one bad entry doesn't fail the whole file); a duplicate Id rejects the whole
  file. Tests: 436/99+2skip/52.
- **Item 8 (2026-09-02) — word-frequency list + collision warnings, done.** Advisory-only,
  never-blocking authoring-time safety net.
- **Item 7 (2026-09-01) — `FillerWordStripper` (order 70), done.** Not backed by any
  `dictionary.json` entry type — a small hardcoded EN/RO list.
- **Item 6 (2026-09-01) — `SpokenCommandsExtensionProcessor` (order 60), done.** Retires Phase 1's
  `SpokenCommandsProcessor` in place.
- **Item 5 (2026-09-01) — `RegexRuleProcessor` (order 50), done.** Deliberately cascading; one
  real blocking ReDoS/hang gap found and fixed with a bounded `MatchTimeout`.
- **Item 4 (2026-09-01) — `DictionaryEngineProcessor` (order 40), done.** First real `AppliedRule`
  population.
- **Item 3 (2026-09-01) — `AhoCorasickAutomaton`, done.** The plan's own "single most
  safety-critical piece." One non-determinism bug fixed.
- **Item 2 (2026-09-01) — `DiacriticFolder`, done.**
- **Item 1 (2026-09-01) — five-entry-type dictionary data model, done.**
- **Item 0 (2026-09-01) — `TrailingSpaceProcessor` renumbered, done.**

## Phase 1 — Headless Daemon (2026-08-31 to 2026-09-01)

Items 1-12 code-complete. Three honestly-documented, structurally-blocked gaps carried forward:
item 11's Linux/Wayland real-hardware verification (needs S5 + real Fedora hardware), item 12's
corpus-regression assertion (needs S2 + a real 60-file EN/RO corpus, deliberately deferred), and
re-verifying items 6/7's original hardware claims now that item 9 found and fixed a `WINDOWS`
build-symbol bug that had silently dead-coded those code paths. Item 10b (24h soak) not started.

- **Item 12** — corpus-regression harness + `SPIKE-RESULTS.md`/`ARCHITECTURE.md`/
  `PLATFORM-NOTES.md`/`MANUAL-TESTS.md` written for the first time. Genuine WER calculator built
  and tested; the actual regression assertion can't run without S2's corpus. Tests: 203/99+2skip/52.
- **Item 11** — Linux hotkey + injector, built best-effort against an unresolved spike (S5, no
  real hardware available) — explicit user decision to proceed rather than block. `EVIOCGRAB`
  key-suppression deliberately NOT implemented (plan's own caution against a blind grab). A ninth
  instance of the recurring stale-background-thread pattern found and fixed. Tests: 190/99+2skip/52.
- **Item 10** — error handling + watchdog + recovery. One blocking bug (a clipboard-restore
  fallback path that could permanently lose the user's clipboard content) fixed. Tests: 190/97+2skip.
- **Item 9** — `SessionController`, the milestone item. Found and fixed a project-wide build bug
  (`WINDOWS` symbol never defined, silently dead-coding every Windows-only branch) plus a real
  dispose-ordering race. Tests: 187/85+2skip.
- **Item 8** — post-processor chain (4 stages). Two real bugs: a command-matching regex that
  mangled ordinary prose, a whitespace-cleanup ordering bug. Tests: 156/85+2skip (up from 108).
- **Item 7 / 7b / 7c** — `ITextInjector` Windows (base injection, modifier sanitiser, clipboard
  sequence guard). Found a new pattern-class bug: a global hook can't tell its own synthetic
  input from real input without an explicit check.
- **Item 6** — `IHotkeySource` Windows via SharpHook. A heartbeat-timer race (same
  stale-background-thread pattern as item 4c) found and fixed.
- **Item 5** — VAD integration (Silero via sherpa-onnx). One config value reused for two
  different gates, nearly defeating a safety check — fixed with an independent field.
- **Item 4 / 4b / 4c** — resampler, `IAudioCapture` (PortAudio), capture modes + ready cue. A
  real architecture violation found in 4b (resampling done on the real-time audio thread) and
  redesigned to a genuine lock-free ring buffer. Two WarmIdle-timer concurrency bugs in 4c.
- **Item 3 / 3b** — ASR transcriber + model manager, stream-lifecycle stress test. A
  dispose/decode race (use-after-free risk on the native recognizer) found and fixed.
- **Item 2** — config load/save/hot-reload + logging host. A dispose/timer race and an
  incomplete "never throws" exception contract found and fixed.
- **Item 1** — solution scaffold, all 5 core abstractions.

**Spikes** (`spikes/`, throwaway): S1 (ASR, green — NumThreads=4 finding), S1b (audio, green —
OnDemand default + resampler tap-count correction), S3 (hotkey, automatable parts green), S4
(injection, core algorithm green — clipboard TOCTOU fix). S2 (Romanian corpus) deferred by
decision. S5 (Wayland) and S6 (hotword biasing) not started.
