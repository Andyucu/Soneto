# Soneto — Phase 4 Implementation Plan: Platform Hardening

Written before implementation starts, per this project's established convention (see
`Docs/PROJECT-MEMORY.md`'s "Documentation conventions" and how Phase 2/3's own plan docs were
written up front). Per the build plan's own phase breakdown (`dictation-app-build-plan.md`
§10, "Phase 4 — Platform hardening"): "Linux Wayland path productionised, macOS `.app` bundle +
entitlements + permission flow, per-app profile table, per-app injection fallbacks, crash/hook-death
recovery."

**Two scope decisions made explicit up front, both confirmed with the user before this doc was
written (2026-09-03), not assumed:**

1. **macOS is deferred entirely, not included in this phase.** Matches the build plan's own risk
   register ("macOS permissions/notarisation rabbit hole for a .NET app... defer macOS to Phase 4,
   treat as optional") and `Docs/PROJECT-MEMORY.md`'s standing note. There is no
   `Soneto.Platform.MacOS` project, no CGEventTap/NSPasteboard research, no entitlements/notarization
   work anywhere in this plan — macOS becomes its own future phase, designed from scratch when it's
   actually prioritized.
2. **Real Linux hardware is still not available to any agent session, but a Docker-based approach
   is available and should be used instead of waiting.** This is a real change from Phase 1 item
   11's "build best-effort, everything unverified" posture — see §4.2 below for why a container can
   actually close most of that gap for real, not just simulate it.

---

## 4.1 Definition of done

- Spike S7 (Docker-based Linux hotkey/injection harness — see §4.2/§4.3) resolves as much of
  `[GATE:S5]` as a container genuinely can, with an honest, specific list of what still can't be
  closed without physical hardware (a real desktop environment's own Wayland compositor quirks,
  genuine physical hotplug).
- `PerAppOverride` (dictionary schema, defined since Phase 2, never consumed) and
  `InjectionConfig.PerApp` (config schema, defined since Phase 1, never consumed beyond its two
  example entries) are both actually resolved and applied at injection time, keyed off the real
  captured target's process name.
- At least one genuinely different per-app injection fallback works end-to-end (e.g., a
  configured app gets `UnicodeSynth` instead of `ClipboardPaste`, or a configured extra
  `ClipboardRestoreDelayMs`), verified against a real target app, not just unit-tested config
  resolution logic.
- Hook-death recovery (already built — see §4.6) is re-verified against the Docker Linux harness
  and, separately, a real Windows manual kill-the-hook test, rather than re-built from scratch.
- `Docs/PLATFORM-NOTES.md`/`ARCHITECTURE.md`/`MANUAL-TESTS.md` updated to reflect what changed.

---

## 4.2 Why Docker instead of waiting for real Linux hardware

Phase 1 item 11 built `Soneto.Platform.Linux` entirely blind — real syscalls/process launches
(`epoll_wait`, `EVIOCGBIT`, `ydotool`, `wl-copy`) were never executed against a real kernel or
compositor, only reasoned about via known ABI facts and unit-tested at the pure-decision-logic
layer (device filtering, key mapping, hash-guard sequencing). That gap doesn't need to stay open
just because no session has a spare Fedora laptop:

- **`/dev/input/event*` and `/dev/uinput` are real kernel interfaces, not GUI-dependent.** A Linux
  container given `--device=/dev/uinput` (or run privileged) can create a genuine virtual
  keyboard via the kernel's `uinput` ioctl API and inject real key events into it. To
  `LinuxHotkeySource`'s epoll-based reader thread, a uinput-created virtual keyboard is
  indistinguishable from a physical USB one — same device node, same event structs, same
  `EVIOCGBIT` capability bits. This is a genuine integration test through the real kernel input
  subsystem, not a mock at the `LinuxHotkeySource` layer.
- **`ydotoold` itself works by creating a virtual uinput device** to synthesize input — it doesn't
  need a real physical keyboard either, only `/dev/uinput` access, which a container can have.
- **Wayland clipboard/paste needs *some* compositor to talk to**, which a real GUI session
  normally provides. A headless Weston (`weston --backend=headless-backend.so`, or the
  Wayland-native `Xwayland`-free headless mode) running inside the container gives `wl-copy`/
  `wl-paste`/`ydotool` a real Wayland socket to connect to, and a minimal Wayland client (e.g.
  `wtype`'s own test target, or a tiny GTK/foot-terminal text field) can serve as a real paste
  target for a genuine end-to-end injection check.
- **Even physical hotplug is partially testable**: dynamically creating/destroying a uinput device
  mid-session, inside the container, produces the exact same `/dev/input` directory-change +
  udev-adjacent event a real USB keyboard plug/unplug does — `LinuxHotkeySource`'s inotify-based
  re-enumeration logic can be exercised for real, not just unit-tested against a fake filesystem
  watcher.

**What this genuinely cannot close, honestly listed up front so it isn't discovered mid-phase and
mistaken for a regression:** real desktop-environment-specific Wayland compositor behavior (GNOME
Shell's/KDE's own compositor, as opposed to generic Weston), real multi-app GUI behavior (Firefox,
Thunderbird, VS Code — heavy GUI apps are impractical to run headless in this harness), and truly
physical multi-keyboard hotplug (a real second USB device, as opposed to a second uinput-created
virtual one, which — while it exercises the identical code path — isn't the literal same
hardware event a skeptical reader might want proven). These remain real, open gaps for whenever
physical Fedora/Wayland hardware does become available, same as before — this plan closes most
of `[GATE:S5]`, not all of it.

---

## 4.3 Spike S7 — Docker-based Linux hotkey/injection harness (do this first, before any Phase 4 build item)

Prove the mechanism works before building the whole phase's Linux work order around it, per this
project's own Phase 0 spike discipline (don't build product code on an unvalidated assumption).

- **Base image**: Fedora (matching the target OS in `dictation-app-build-plan.md`), with
  `ydotool`/`ydotoold`, `wl-clipboard` (`wl-copy`/`wl-paste`), `weston`, and the .NET 10 runtime
  installed.
- **Container capabilities needed**: `--device=/dev/uinput` (narrower and preferred over
  `--privileged`) plus whatever the container's own non-root user needs group-membership-wise to
  open it (mirrors `scripts/setup-linux.sh`'s existing real-machine setup — reuse that script's
  logic inside the Dockerfile rather than re-deriving it).
- **Prove, in order**: (1) a `uinput`-created virtual keyboard's key events are genuinely
  readable by a raw `epoll`+`read()` loop inside the container (before touching any Soneto code —
  isolate "does the harness itself work" from "does `LinuxHotkeySource` work"); (2) headless
  Weston starts and `wl-copy`/`wl-paste` round-trip real clipboard content through it; (3)
  `ydotoold` starts against the same `/dev/uinput` access and a `ydotool key`/`ydotool type` call
  actually lands in a minimal Wayland text client running under the same Weston instance; (4) a
  real `LinuxHotkeySource` instance, started inside the container, correctly reports Pressed/
  Released for genuine synthetic-but-kernel-real key events from the harness's virtual keyboard,
  with jitter measured (S5's own original pass bar was <30ms — re-apply it here); (5) a real
  `LinuxTextInjector.InjectAsync` call lands text in the Weston-hosted text client, diacritics
  verified at the byte level (reuse the exact S4/Section A test string from
  `Docs/MANUAL-TESTS.md`).
- **Deliberately NOT proven here** (§4.2's honest list): anything requiring a specific desktop
  environment, anything requiring a real heavyweight GUI app, physical hotplug.
- **Output**: `spikes/s7-docker-linux/` (Dockerfile, harness scripts, README with results — same
  shape as S1/S1b/S3/S4's own spike folders), plus an update to `Docs/SPIKE-RESULTS.md`.
- **Gate**: if any of steps (1)-(5) can't be made to work in a container at all (not just
  "imperfectly" — genuinely blocked, e.g. a kernel/capability restriction this environment can't
  work around), stop and fall back to Phase 1 item 11's original posture (best-effort, wait for
  real hardware) rather than forcing the rest of this phase's build order through a broken harness.

---

## 4.4 Per-app profile resolution

Two independent, already-scaffolded-but-unconsumed schema pieces need real resolution logic:

**`InjectionConfig.PerApp`** (`Soneto.Core/Configuration/SonetoConfig.cs`, a
`Dictionary<string, PerAppOverride>` keyed by executable name, already shipping two example
entries — `WindowsTerminal.exe`/`Teams.exe`). `InjectionConfig.ToOptions()` currently has an
explicit doc comment stating "`PerApp` overrides are not applied here — per-app profile
resolution is a Phase 4 concern." This phase closes that: resolve the override by the real
captured target's process name (`ITextInjector.CaptureTarget()`'s existing target-resolution
already knows the foreground window's owning process — confirm what it exposes today before
adding new plumbing) at the point `SessionController`/the composition layer builds
`InjectionOptions` for a given utterance, applying `PasteChord`/`ClipboardRestoreDelayMs`
overrides when a match exists, falling back to the base config otherwise. **New field needed on
`PerAppOverride`, not previously scoped**: an optional injection-method override (so "for
`wt.exe`, use char-by-char" from the build plan's own §"per-app fallback table" framing is
actually expressible — today `PerAppOverride` only has `PasteChord`/`ClipboardRestoreDelayMs`,
no way to force `UnicodeSynth` for one specific app).

**`PerAppOverride` dictionary entries** (`Soneto.Core/Dictionary/`, the fifth entry type, schema-only
since Phase 2, explicitly labeled "not yet active" in the Phase 3 Dictionary editor). Per Phase
2's own §2.9 design note, `DictionaryEngineProcessor`/`RegexRuleProcessor`/
`SpokenCommandsExtensionProcessor`/`FillerWordStripper` already take their rule sets via
constructor parameters rather than reading a config singleton — confirm this still holds, then
add a "which profile is active for this utterance" selector (keyed the same way as the
injection-side resolution, by captured-target process name) that builds a differently-filtered
processor set per focused app, per that section's own forward-looking design intent. This is the
first time `PostProcessorChain` construction becomes utterance-scoped rather than
session-lifetime-scoped — a real, scoped architecture change, not just new config plumbing;
trace whether this conflicts with the still-standing "no live rebuild without restart" limitation
(`Docs/PROJECT-MEMORY.md`) before assuming it's a drop-in change, since per-app selection
happening per-utterance is a different mechanism than the existing "whole chain rebuilds on
config/dictionary file change" one.

---

## 4.5 Per-app injection fallbacks

Once §4.4's resolution mechanism exists, exercise it for real against at least one of the apps
already flagged as a known problem case in `Docs/MANUAL-TESTS.md` Section A (VS Code/Electron is
the standing "known problem case" — Windows Terminal's `ctrl+shift+v` chord is the other
documented candidate). Confirm the configured override is genuinely applied (not just present in
config) via the same programmatic verification style item 7's Permissions Doctor injection
self-test established (a real `ITextInjector` call, real read-back), plus a human manual check
per `Docs/MANUAL-TESTS.md` Section A's existing per-app matrix (which already has open,
never-cleanly-confirmed rows for exactly these apps — this phase is a natural place to close a
few of them, not a new obligation).

---

## 4.6 Hook-death recovery — verification only, this is NOT new build work

**Read `Docs/PROJECT-MEMORY.md`'s "Locked-in decisions"/Phase 1 item 9 history before assuming
this needs building.** The build plan's Phase 4 line ("Windows will unhook you eventually —
detect and re-register") describes a mechanism that Phase 1 item 9 already built and tested: a
heartbeat/fault-detection watchdog plus `IHotkeySource.RestartAsync` with a documented
exponential-backoff policy (up to 5 attempts, 1s/2s/4s/8s/.../16s, ~31s worst case, transitioning
to a permanent `Faulted` state only after exhausting them — see `SessionController.cs`'s own
class doc comment, "Watchdog backoff shape"). **This phase's job here is exclusively
verification against real conditions the original implementation couldn't test**, not a
redesign: a real Windows manual test (kill/interfere with the hook process-externally, e.g. via
a debugger detach or a deliberate driver-level interference, and confirm the documented backoff
actually re-registers), and — via §4.3's harness — the equivalent Linux check (kill/restart the
uinput-backed reader thread's underlying device mid-session, confirm `LinuxHotkeySource`
recovers the same way `WindowsHotkeySource` does). If this verification surfaces a real bug,
fix it narrowly; do not restructure the existing watchdog design without a concrete finding
that requires it.

---

## 4.7 Testing

- **S7's harness itself becomes real, reusable Linux integration-test infrastructure**, not a
  one-off spike artifact — once proven, wire it into a `Soneto.Platform.Linux.Tests`-adjacent
  suite (or a new, clearly-separated project/trait, e.g. `[Trait("Category","DockerLinux")]`,
  excluded from the default filter the same way `Category=Corpus` already is) so future Linux
  work doesn't regress to "unit-tested pure logic only, nothing real" once this phase closes.
- Per-app resolution logic (§4.4) is pure decision logic (given a process name + config, which
  override applies) — unit-test it directly in `Soneto.Core.Tests`, no real injection needed for
  the resolution logic itself, only for the end-to-end fallback check in §4.5.
- Re-run this project's own established regression discipline: full `dotnet build`/`dotnet test`
  after every item, watch specifically for this project's two most recurring bug shapes (a
  background timer/thread touching superseded state; one config value silently serving two
  different gates — see `Docs/PROJECT-MEMORY.md`'s "Recurring bug patterns") in any new code this
  phase adds, especially §4.4's new per-utterance processor-set construction.

---

## 4.8 Build order

Following the same "one work item per session, each with a real demo/test" discipline the prior
three phases established.

| # | Item | Notes |
|---|------|-------|
| 0 | Spike S7 — Docker-based Linux hotkey/injection harness (§4.3) | ✅ Done (2026-09-03), partial pass — see `spikes/s7-docker-linux/README.md` for full results. **The real, unmodified `LinuxHotkeySource` was proven working against a real kernel for the first time**: a `uinput`-created virtual keyboard, opened via a manually-`mknod`'d `/dev/input/eventN` node (Docker doesn't auto-populate dynamically-created device nodes) plus a `--device-cgroup-rule='c 13:* rmw'` (Docker's device cgroup blocks access to a manually-created node otherwise), gave real evdev enumeration, real epoll-based capture, and a real Pressed/Released cycle at 0.38ms/0.00ms jitter (well under S5's 30ms bar). `ydotoold`/`ydotool key` also proven mechanically functional, independent of any compositor. **Genuine dead end found, not routed around**: headless Weston exposes no `wl_seat` (no CLI option to attach input), and this kernel has no `vkms` (virtual DRM) to enable a real `libinput`-backed `drm` backend instead — so `wl-copy`/`wl-paste` and "does injected text land in a real Wayland app" remain unverified, same as before S7, needing real hardware. **One real, not-yet-triaged finding**: `LinuxHotkeySource` raises `Faulted` during its own `DisposeAsync()` teardown (confirmed dispose-time-specific via a no-dispose control run, not a spontaneous false-positive) — flagged for a follow-up look, not fixed as part of this spike. `Docs/SPIKE-RESULTS.md`'s S5 entry updated to reflect the partial closure. |
| 1 | Re-run Phase 1 item 11's / `Docs/MANUAL-TESTS.md` Section D's Linux checklist against the S7 harness | 🟡 Partially done (2026-09-03). The `LinuxHotkeySource.DisposeAsync()` fault finding from item 0 was triaged first, by hand: traced `SessionController.DisposeAsync()`'s real unsubscribe-then-drain-then-dispose ordering and confirmed the stray fault lands on an already-detached event with zero subscribers in the real integration -- harmless, not a bug, no code changed. **Multi-keyboard hotplug -- Section D's own explicit checklist item, and Phase 1 item 11's literal "done when" criterion -- is now genuinely closed**: a second `uinput` keyboard created mid-session correctly triggered the real `inotify` watch, `RestartAsync` re-enumerated both devices, and the hotkey continued working afterward (full results: `spikes/s7-docker-linux/README.md`, step 6). **Still open, gated on the same compositor dead end item 0 found**: the full S4 diacritics test string through a real Wayland paste, and confirming EVIOCGRAB-tolerability -- both need real hardware or a different virtualization approach. |
| 2 | `InjectionConfig.PerApp` resolution wired into real injection call sites (§4.4) | ✅ Done (2026-09-03). Includes the new injection-method-override field on `PerAppOverride` (`InjectionMethod?  Method`), so §4.4's "for `wt.exe`, use char-by-char" case is finally expressible. **Where resolution happens, and why:** inside `WindowsTextInjector.InjectAsync` itself, immediately after its own fresh `GetForegroundWindow()` lookup -- NOT in `SessionController`/the composition layer -- because `SendInput` always lands in whatever is foreground AT INJECTION TIME, which can differ from the target captured at key-down (that divergence is already logged there). The pure lookup/merge decision was pulled out into `Soneto.Core/Configuration/PerAppOverrideResolver.cs` so it unit-tests with no Win32 call at all, mirroring `KeyboardDeviceFilter`/`InjectionOutcomeMapper`'s precedent; only the real process-name lookup (`GetWindowThreadProcessId` + `Process.GetProcessById(...).ProcessName + ".exe"`) stays Windows-side. `DaemonComposition.CreatePlatformHotkeySourceAndTextInjector` now takes the loaded `SonetoConfig` and wraps `Injection.PerApp` in a fresh `StringComparer.OrdinalIgnoreCase` dictionary exactly once at startup -- the resolver applies no comparer of its own, so that composition-root wrap is the single place case-insensitive executable matching is decided. Both callers (`Soneto.App/PipelineHost.cs`, `Soneto.Daemon/Program.cs`) updated. **Honest scope limits, both documented in code:** (a) Linux is NOT covered -- there's no portable way for an unprivileged Wayland client to resolve the focused window's owning process, so `LinuxTextInjector` gets no table; (b) `ProcessName + ".exe"` can diverge from the real executable name for some packaged/UWP host processes, and a PID-reuse race between the two calls could in principle resolve the wrong still-running process (worst case: one injection uses the wrong paste chord/delay -- never wrong text) -- both deliberately not defended against, both written down rather than left implicit. **Found and fixed on arrival:** the item's code as left by the prior session did not compile -- `InjectionMethod` was ambiguous between the `Abstractions` and `Configuration` namespaces at the `UnicodeSynth` branch, `PerAppOverride` was ambiguous between `Core.Configuration` and the unrelated `Core.Dictionary` entry type in `DaemonComposition`, and the enum-mapping switch was non-exhaustive (CS8524). The switch now throws `ArgumentOutOfRangeException` on an out-of-range value rather than silently falling back to the base method -- that silent shape is this project's own bug pattern #2. **Review finding, fixed:** the process-name lookup was unconditional, so a user with no overrides configured still paid a `GetWindowThreadProcessId` P/Invoke plus a `Process` handle open/close per injection -- value-identical to the pre-Phase-4 path but not call-identical, contradicting the class's own doc comment. Now guarded by `_perApp is { Count: > 0 }`: zero new native calls on the default path. Review confirmed no other consumer inside `InjectAsync` still reads the un-overridden `opts` (all of `Method`/`RestoreClipboard`/`SanitizeModifiers`/`TriggerKey`/`PreDelay`/`PasteChord`/`ClipboardRestoreDelay`/`Policy` read `effectiveOpts`), and that the shared table is construct-once/read-only, so concurrent injections are safe. **Tests: +16, all green.** 14 in the new `tests/Soneto.Core.Tests/PerAppOverrideResolverTests.cs` -- reference-identical fall-through for no-table/empty-table/null-process-name/no-match (asserted with `Assert.Same`, since `WindowsTextInjector` also uses that reference identity to decide whether to log an override at all), per-field merge isolation, `ClipboardRestoreDelayMs = 0` honoured as a real value rather than treated as unset, both directions of the `Method` mapping, the out-of-range-throws contract, comparer-ownership asserted from both sides, and the shipped default table resolved through the real `ToOptions()` base. Plus 2 in `ConfigServiceTests` round-tripping `perApp` (including the new `method` field, `"unicodeSynth"` camelCase) through a real `config.json` read -- a gap the pure resolver tests structurally could not catch. Full suite after: `Soneto.Core.Tests` 497/497, `Soneto.Platform.Windows.Tests` 100/100 (+2 pre-existing skips), `Soneto.Platform.Linux.Tests` 55/55, `Soneto.App.Tests` 60/60; build 0 warnings/0 errors. **Incidental pre-existing finding, NOT fixed (out of scope, flagged to the user):** `System.Text.Json`'s default encoder escapes `+`, so every paste chord written to `config.json` -- including the top-level `injection.pasteChord`, since Phase 1 -- appears as `"ctrl+shift+v"`. It round-trips correctly; it's a readability wart in a file users are told to hand-edit, not a behaviour bug. |
| 3 | `PerAppOverride` dictionary-entry resolution wired into `PostProcessorChain` construction (§4.4) | ✅ Done (2026-09-03). §2.9's premise re-confirmed by reading the current code: `DictionaryEngineProcessor`/`RegexRuleProcessor`/`SpokenCommandsExtensionProcessor`/`FillerWordStripper` all still take their rule sets via constructor parameters, not a config singleton. **Selection mechanism, mirroring item 2's shape exactly:** `ITextInjector` gained a new interface method, `TryResolveProcessExecutableName(object? target)`, with a default implementation returning `null` (non-breaking for every pre-existing implementer, real or test fake) -- `WindowsTextInjector` overrides it for real (delegates to its existing internal `TryGetProcessExecutableName`), `LinuxTextInjector` overrides it explicitly to return `null` (documented the same way its `CaptureTarget` already documents that gap), matching this item's own scoping note about no portable Wayland focused-process resolution. A new `Soneto.Core.Dictionary.PerAppOverrideResolver` (deliberately much smaller than the injection-side one -- no field-merge needed, just "is there an enabled profile for this process") does the pure lookup, unit-tested directly. **Where per-utterance selection actually happens, and why it does NOT conflict with the standing "no live rebuild" limitation:** `PostProcessorChain` gained an optional second constructor argument (the dictionary-side per-app table) and pre-builds, ONCE at construction time (i.e. still only at startup, from the same fixed config+dictionary snapshot as before), up to four processor-list variants (base; +AutoCapitalize; +TrailingPunctuation; +both). A new `Process(string text, string? processExecutableName)` overload does a dictionary lookup and picks one of those four already-built lists -- no processor construction, no I/O, no file re-read happens per utterance; only SELECTION among already-built, startup-built lists is utterance-scoped, exactly analogous to how the injection-side `PerAppOverrideResolver` already re-resolves an override on every injection from a table built once at startup. A hot-reloaded `config.json`/`dictionary.json` still does not rebuild anything live -- unchanged, pre-existing gap. **`SessionController.cs` touch, small and mechanical as instructed:** one line added at the existing `_postProcessorChain.Process(result.Text)` call site, resolving `_textInjector.TryResolveProcessExecutableName(_capturedTarget)` first (mirroring the shape of the pre-existing `_textInjector.InjectAsync(text, _capturedTarget, ...)` call a few lines below) and threading the result into the new two-argument `Process` overload. All actual selection/filtering logic lives in `Soneto.Core.PostProcessing`/`Soneto.Core.Dictionary`, not in `SessionController` itself. **The `AutoCapitalize`/`TrailingPunctuation` scope call: (a) — built real, minimal, narrowly-scoped processors.** Two new `IPostProcessor` stages, `AutoCapitalizeProcessor` (order 80: uppercases the first letter and the first letter after `. ! ?`+whitespace, Unicode-letter-aware for Romanian) and `TrailingPunctuationProcessor` (order 85, before `TrailingSpaceProcessor` at 90 so it composes cleanly with that processor's own "runs last" contract: appends `.` unless the text already ends in terminal punctuation). Both are ONLY ever included in a per-app-widened processor list when a matching, enabled `PerAppOverride` profile's own flag is `true` -- the base/default chain (no dictionary `PerAppOverride` entries, or none matching the focused app) is byte-for-byte unchanged from before this item, confirmed by the full pre-existing test suite passing unmodified. `DaemonComposition.BuildDictionaryPerAppTable` builds the table once at startup (filters to enabled `PerAppOverride` entries, `OrdinalIgnoreCase`-keyed by `ProcessName`, mirroring item 2's exact precedent). **Honest, deliberate simplification, documented in both processors' own doc comments:** neither is a full grammar-aware implementation (no abbreviation/proper-noun handling for capitalization, no sentence-vs-clause distinction for punctuation) -- a first real cut, same spirit as `FillerWordStripper`'s own "small list, extend from real usage" framing, not a claim of completeness. **Code review, two should-fix findings, both fixed same session:** (1) neither new processor recorded an `AppliedRule` on a genuine change, unlike every other text-mutating processor in the codebase (`DictionaryEngineProcessor`/`RegexRuleProcessor`/`FillerWordStripper`/`SpokenCommandsExtensionProcessor`) -- not cosmetic, since `AppliedRule` flows through to persisted `HistoryEntry.RulesFired` and `Soneto.App`'s History UI diff-highlighting; fixed by having both processors append an `AppliedRule(Name, fixedRuleId, From, To)` only when a genuine change occurs (mirrors `RegexRuleProcessor`'s "only record on genuine change" pattern -- `AutoCapitalizeProcessor` records one per changed letter, `TrailingPunctuationProcessor` records one insertion with `From=""`/`To="."`). (2) no test exercised the new `SessionController` line with a real (non-null) resolved process name -- every existing test only ever hit `ITextInjector`'s default `null` return, which is indistinguishable from "this wiring doesn't exist at all"; fixed with a new `SessionControllerTests` case (`FakeTextInjector` extended with a settable `ProcessExecutableNameToReturn`) that resolves `"wt.exe"`, builds a `PostProcessorChain` with a per-app table enabling `AutoCapitalize` for that process, and asserts the actual FINAL INJECTED TEXT is capitalized -- proving the wiring at the `SessionController` call site itself, not just `PostProcessorChain` in isolation. **Tests: +36 total** (28 from the original pass, +8 from the two review fixes), all in `Soneto.Core.Tests` (pure logic only, per this item's own testing note in §4.7 -- no real injection/platform call needed for the resolution/selection logic itself): 6 in the new `Dictionary/PerAppOverrideResolverTests.cs` (null/empty/no-match/match fall-through, both directions of case-sensitivity ownership -- mirrors `Configuration.PerAppOverrideResolverTests`' precedent), 10 in the new `PostProcessing/AutoCapitalizeProcessorTests.cs` (6 original + 4 `AppliedRule` cases: genuine single change, multiple changes, no-change records nothing, appends after pre-existing `Applied` entries), 8 in the new `PostProcessing/TrailingPunctuationProcessorTests.cs` (5 original + 3 `AppliedRule` cases: genuine insertion, already-terminated records nothing, appends after pre-existing entries), 7 new cases added to the existing `PostProcessing/PostProcessorChainTests.cs` covering the two-argument `Process` overload (no-table/no-match/null-process-name all provably reach the identical base chain, each of the four `(AutoCapitalize, TrailingPunctuation)` combinations, and composition with the real `TrailingSpaceProcessor`), and 1 new end-to-end `SessionControllerTests` case (the review fix above). Full suite after: `Soneto.Core.Tests` 533/533 (497+36), `Soneto.Platform.Windows.Tests` 100/100 (+2 pre-existing skips), `Soneto.Platform.Linux.Tests` 55/55, `Soneto.App.Tests` 60/60 -- zero regressions in any pre-existing test; build 0 warnings/0 errors. |
| 4 | At least one real per-app injection fallback verified end-to-end (§4.5) | ✅ Done (2026-09-03), automatable half only — the human manual matrix check remains genuinely open (see below). **Real end-to-end hotkey+speech/foreground-app verification against VS Code/Windows Terminal is a human-only task per `Docs/PROJECT-MEMORY.md`'s "Live-desktop testing caution" — not attempted this item, no real foreground app was opened or targeted.** Instead, built the strongest available substitute: a real, permanent xUnit regression test, `tests/Soneto.Platform.Windows.Tests/PerAppOverrideEndToEndTests.cs` (`Category=Hardware`, excluded from the default filter, run deliberately once — passed first try). **Design, mirroring item 7's Permissions Doctor injection self-test pattern exactly:** a per-app table keyed to the TEST PROCESS'S OWN real, dynamically-resolved executable name (`Process.GetCurrentProcess().ProcessName + ".exe"` — never hardcoded) maps to `Method=UnicodeSynth`. A real, normally-rendered but off-screen WPF window/`TextBox` (WPF chosen over Avalonia since `Soneto.Platform.Windows.Tests` already references it via `UseWPF=true` for `WindowsTextInjectorNotepadSelfCheckTests`' UI Automation dependency, and adding a new Avalonia reference to a platform-hardware test project for one test wasn't worth it) is given real OS focus via ordinary `Activate()`/`Focus()`, and a FRESH, throwaway `WindowsTextInjector.CaptureTarget()` is called immediately after with no yield point in between — guaranteeing the captured target is this test's own window, never an arbitrary foreground app. A dedicated STA thread with a live `Dispatcher` message pump (`DispatcherFrame`/`PushFrame`) is required since WPF windows need STA + a running message loop to actually receive the real `SendInput` keyboard events the test sends into them. **"Genuinely applied, not just configured" is proven via a real observable side effect, not a log string match:** `UnicodeSynth` never touches the clipboard at all; the base `ClipboardPaste` default (which the test's own `InjectionOptions` deliberately still requests) always does. The test reads the real Win32 clipboard sequence number (`ClipboardManager.GetSequenceNumber()` — the exact mechanism `WindowsTextInjector.InjectAsync` itself already logs) immediately before and after injection and asserts it is UNCHANGED — structural proof the real per-app-resolved `UnicodeSynth` branch ran, not a silent fall-through to the base `ClipboardPaste` path (which would always bump the sequence number). Combined with a marker string containing real Romanian diacritics (ș/ț/î/ă — the exact class of problem `UnicodeSynth` exists to fix for problem apps like VS Code, per this section's own framing) landing byte-correct in the `TextBox`, this is a full, real, round-trip exercise of `WindowsTextInjector.InjectAsync`'s unmodified per-app resolution logic (built in item 2), not a mock. **Honest residual gap, NOT fully closed even after the blocking fix below (code review finding):** `WindowsTextInjector.InjectAsync` re-fetches `GetForegroundWindow()` itself at actual `SendInput` time, not the earlier `CaptureTarget()` handle -- so there is a small residual window (roughly `PreDelay` + the clipboard-sequence-number read, tens of milliseconds) between this test confirming focus and the real send where OS focus could in principle still drift. Same already-accepted risk `PermissionsDoctorViewModel.RunInjectionSelfTestAsync`'s own doc comment describes for the shipped production self-test this mirrors -- not new, not realistically triggerable without something else actively stealing focus at that exact instant, but the test's own doc comment previously overstated this as "structurally cannot land anywhere but the test's own window," which review correctly flagged as an overclaim; softened to state the guarantee honestly. **Real result — flaky initially, fixed, re-reviewed, fixed again, then re-verified clean, not just a single favorable run.** The first isolated run passed, but independent test-runner verification found it flaky: 1 failure out of 6 isolated runs, `Marker text did not land... Final text: ""`. Root cause, confirmed by trace: `Activate()`/`Focus()` REQUEST OS focus but do not synchronously GUARANTEE it has landed by the very next statement (window activation can be a few message-pump cycles behind the call that requested it), and the original fixed `PumpFor(150ms)` post-injection wait was occasionally not enough for WPF's own async input processing to finish — a longer fixed sleep would only have moved the same two races further out, not removed them. First fix: two bounded, pumped `PollUntil` polls (same "pump this thread's own `Dispatcher` via `DispatcherFrame`/`DispatcherTimer`" technique the test already used for `PumpUntilComplete` — a bounded wait on this thread's OWN message queue, not a real yield that could let OS focus drift to another process): (1) before `CaptureTarget()`, poll until `GetForegroundWindow() == hwnd && textBox.IsKeyboardFocusWithin` (3s timeout, 15ms interval); (2) after injection, poll until the marker text is actually found in `textBox.Text` (2s timeout, 25ms interval) instead of a single fixed sleep-then-check. **BLOCKING finding from code review on that first fix (fixed same session):** both `PollUntil` results were discarded at their call sites — a timed-out focus poll would silently fall through to a real `CaptureTarget()`/`InjectAsync` anyway, meaning a genuinely stolen-focus scenario (something else transiently grabbing foreground during the 3s window) would send this test's real synthetic marker keystrokes into whatever ELSE had focus, exactly the "never touch the live desktop unsupervised" failure this whole pattern exists to prevent, and exactly what the class's own doc comment claimed couldn't happen. Fixed by capturing the focus-poll's bool result and hard-failing (`throw new InvalidOperationException`) BEFORE `CaptureTarget()` ever runs if it timed out — never falls through; the marker-poll's result is now also captured explicitly (not silently re-derived downstream) for the same clarity reason, though it isn't itself a safety hole (it can only ever produce a legitimate `landed=false` test failure, never touch another app). Re-verified after this fix: **6/6 clean isolated runs** (10/10 after the first fix, 6/6 more after the blocking fix — 16/16 total clean runs across both rounds), plus two full default-suite re-runs confirming zero regressions each time. **Known follow-up gap, flagged not fixed (out of this item's scope):** code review also found the exact same unguarded-focus assumption (a single `Focus()` call assumed to synchronously land keyboard focus, plus a fixed `Task.Delay(100ms)` assumed sufficient for async paste processing) still exists, UNCORRECTED, in shipped production code -- `Soneto.App.ViewModels.PermissionsDoctorViewModel.RunInjectionSelfTestAsync` (the very pattern this test mirrors) -- meaning a real user could intermittently see a false red on the "Can synthesize input" Permissions Doctor check on a real machine under load. Not fixed here (out of item 4's scope); recorded in `Docs/PROJECT-MEMORY.md` as a standing follow-up. **Docs — `Docs/MANUAL-TESTS.md` Section A explicitly NOT touched beyond a new note**: no checkbox/row status changed (per this item's own constraint — those represent real human verification against real apps, which did not happen this session); a note was added after the existing table pointing at this new automated coverage and explicitly stating it does not close the VS Code/Windows Terminal rows. **Full suite after, zero regressions:** `Soneto.Core.Tests` 533/533, `Soneto.Platform.Windows.Tests` 100/100 (+2 pre-existing skips, new Hardware test correctly excluded from this default count), `Soneto.Platform.Linux.Tests` 55/55, `Soneto.App.Tests` 60/60; build 0 warnings/0 errors. |
| 5 | Hook-death recovery re-verification (§4.6), Windows + Linux-via-harness | ✅ Done (2026-09-03), both real, both PASS, zero redesign. **Windows — new permanent `[Trait("Category","Hardware")]` xUnit test**, `tests/Soneto.Platform.Windows.Tests/HookDeathRecoveryHardwareTests.cs`: a real `WindowsHotkeySource` wired into a real `SessionController` (fakes only for capture/VAD-passthrough/transcriber/injector, since the fault happens while `Idle` and never touches them) has its real, installed `SimpleGlobalHook` genuinely `Stop()`'d out from under it (reflection into the private `_hook` field, same technique `WindowsHotkeySourceHeartbeatTests` already established — never touches any other app/window). The test then waits REAL wall-clock time (no reflection-invoked `OnHeartbeatTick` shortcut) for the real 60s-idle-threshold + up-to-15s-timer-period + ~770ms-probe-wait heartbeat to detect the dead hook and for the REAL `SessionController.HandleHookFaultedAsync` watchdog to call the REAL `WindowsHotkeySource.RestartAsync` and install a genuinely new, running hook (confirmed structurally: new hook instance, `IsRunning=true`) — 2/2 clean runs, ~62s each, matching the documented backoff shape exactly (recovered on attempt 1, no visible `Faulted`/`StateChanged` transition needed since a successful recovery never leaves `Idle`). **Real, already-known limitation surfaced, not a new bug**: `WindowsHotkeySourceTests`' own doc comment already established that `EventSimulator`-driven synthetic input is indistinguishable from `WindowsTextInjector`'s own synthetic paste-chord modifiers (`IsEventSimulated`), and `WindowsHotkeySource` deliberately ignores any TRIGGER-key-coded simulated event — so no automated test can synthesize a "real" post-recovery trigger press. Worked around honestly: "genuinely working again" is proven via the heartbeat's own probe-key channel (F24), which deliberately does NOT apply that filter — a real synthetic F24 press/release is sent post-recovery and the reinstalled hook's real `OnKeyPressed`/`OnKeyReleased` callbacks are confirmed to observe it (same native callback pipeline the trigger path uses). **Linux — `spikes/s7-docker-linux/` harness extended with `run-devicekill-test.sh`**, the closer match to "Windows will unhook you eventually" than item 1's device-ADD/hotplug test: a real uinput keyboard is genuinely DESTROYED (`UI_DEV_DESTROY`+`close(fd)`) mid-session while the real, unmodified `LinuxHotkeySource` reader thread is actively polling it. Result: PASS, 2/2 clean runs — real `Faulted` (EPOLLERR/EPOLLHUP) fires immediately, the harness's `Faulted` handler (widened this item from a hotplug-only filter to react to ANY fault — matching `SessionController`'s real unconditional behavior) mirrors `SessionController.HandleHookFaultedAsync`'s exact documented backoff numbers (5 attempts, 1s/2s/4s/8s/16s — a full `SessionController` was judged impractical to construct in this throwaway container harness, so the numbers/shape are real-production but the loop is a deliberate mirror, not the literal class), attempt 1 fails for real (no replacement device yet), a replacement keyboard created ~2s into the backoff window is found on attempt 2, and a real press/release cycle through it fires `Pressed`/`Released` correctly. **One real finding, in the test script, not `LinuxHotkeySource` — found, fixed, re-verified clean**: the first run failed permanently because the script's own `mknod` silently no-op'd on a recycled `eventN` name, leaving a stale device node that `LinuxHotkeySource` correctly refused to open; fixed by `rm -f` before `mknod` in the script. Full details, log excerpts, and the standing fd-leak-tolerance-fallback note (fired again here, same as item 1, not a new finding): `spikes/s7-docker-linux/README.md`'s new "Device-KILL recovery" section. **Not wired into a permanent `[Trait("Category","DockerLinux")]` xUnit suite this item** (§4.7's own suggested follow-up, still open) — judged out of proportion for a "verification only" item; the one-off, reproducible spike run (2/2 clean) is documented instead, the same precedent items 0/1 already established. **No production code changed** — both findings were test/harness-script bugs, not `WindowsHotkeySource`/`LinuxHotkeySource`/`SessionController` bugs; per this item's own "fix narrowly if something real is found, don't redesign" instruction, nothing in the watchdog/backoff/restart design needed touching. Full suite after: `Soneto.Core.Tests` 533/533, `Soneto.Platform.Windows.Tests` 100/100 (+2 pre-existing skips, new Hardware test correctly excluded), `Soneto.Platform.Linux.Tests` 55/55, `Soneto.App.Tests` 60/60; build 0 warnings/0 errors. |
| 6 | Docs closeout | `PLATFORM-NOTES.md`/`ARCHITECTURE.md`/`MANUAL-TESTS.md` updated; `PROJECT-MEMORY.md`/`CHANGELOG.md` entries per the (now-compact) convention. |

---

## 4.9 Working with Claude Code on this

Same three-agent cycle as every prior phase (implementer → independent test-runner verification →
code-reviewer — see `Docs/PROJECT-MEMORY.md`'s "Working agreements"). Item 0 (S7) in particular
should be treated with the same spike-discipline rigor Phase 0's S1-S6 got: throwaway-quality
harness code is fine, but the RESULT (does the mechanism genuinely work) needs to be trustworthy,
not optimistic. If a docker/container-specific detail turns out to need root-level host
configuration this environment can't grant (a kernel module, a specific capability the sandbox
disallows), treat that the same way Phase 1 item 11 treated "no real hardware" — an honest,
named, structural gap, not something to route around with a weaker substitute that quietly
stops testing the real mechanism.
