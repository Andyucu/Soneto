# S3 — Windows global hold-to-talk

Throwaway spike per `Docs/soneto-implementation-plan-phase0-1.md`
§"S3 — Windows global hold-to-talk". Do not build product code on top of
this — no error-handling investment beyond "fail loudly with a clear
message" (see `spikes/s1-asr/README.md` for the same convention).

Question: can you capture press and release globally and stop the key
reaching the focused app?

## What this is

A console app with three real modes plus one internal test helper:

```
s3-hotkey-win listen [--duration SEC] [--leak-keyup] [--trigger KEYCODE] [--verbose]
s3-hotkey-win self-test
s3-hotkey-win block-test
s3-hotkey-win simulate-trigger [--leak-keyup]     (internal helper, see below)
```

- **`listen`** — the long-running interactive mode. Registers a `SimpleGlobalHook`
  on Right Ctrl (`KeyCode.VcRightControl`), suppresses both DOWN and UP by
  default, logs every DOWN/UP with a wall-clock timestamp, a high-resolution
  elapsed-time stamp, and a snapshot of currently-held modifiers
  (`GetAsyncKeyState`). This is what a human uses for the manual test script
  below — focus a real app, press/release, watch the console, confirm nothing
  leaked into the app.
  - `--leak-keyup`: deliberately suppress DOWN but **not** UP, so you can
    observe orphan key-up effects in a real target app. Off by default.
  - `--duration SEC`: auto-exit after N seconds (useful for scripted runs).
    Without it, quit with Escape (not suppressed, always works) or Ctrl+C.
  - `--verbose`: also logs every other key (not suppressed), so you can see
    the hook is alive and dispatching while you're typing normally elsewhere.
  - `--trigger KEYCODE`: override the trigger (any `SharpHook.Data.KeyCode`
    name, e.g. `VcRightShift`), for poking at other candidates if Right Ctrl
    ever turns out to be a bad default.

- **`self-test`** — fully automated, no human, no physical key. Uses
  SharpHook's own `EventSimulator` to synthesize the trigger key at controlled
  instants and measures the actual send-to-callback latency distribution,
  plus verifies `GetAsyncKeyState` correctly reflects a synthesized modifier
  key's physical state. See "What was self-verified" below for exactly what
  this does and does not prove.

- **`block-test`** — fully automated demonstration of deliberately blocking
  the hook callback for ~2 seconds, so you see Windows' hook-timeout failure
  mode once before you build the watchdog. Also automated via `EventSimulator`.

- **`simulate-trigger`** — not a real mode, an internal helper used only to
  cross-check `listen`'s wiring against `self-test`'s (separate process,
  fires one press/release of the trigger and exits). Kept because it's how
  this spike verified `listen` end-to-end without a human at the keyboard;
  not part of the pass criteria.

## Package

`SharpHook` 8.0.0 (latest on NuGet as of this spike; the plan's example used
`SimpleGlobalHook`/`KeyPressed`/`KeyReleased`/`SuppressEvent`, all of which
are exactly what the current API looks like too — see "API surprises" below
for the parts that *did* change or weren't obvious from the plan's summary).

`net10.0-windows` (not plain `net10.0`) — not strictly required by SharpHook
itself (it ships a `net10.0` TFM too), but this spike also P/Invokes
`user32.dll`'s `GetAsyncKeyState` and is Windows-only by definition, so
`-windows` documents that honestly rather than pretending it's portable.

## API surprises vs. the plan's summary

The plan's method line reads: *"SharpHook, `SimpleGlobalHook`, subscribe
`KeyPressed` / `KeyReleased`, set `SuppressEvent = true` for the trigger
key."* That shape is confirmed correct against the real 8.0.0 package
(verified by reflecting the installed assembly before writing any real code,
same discipline as S1's sherpa-onnx config check) — but two things weren't
obvious from the plan and cost real debugging time:

1. **`RunAsync(GlobalHookType, useBackgroundThread)`'s returned `Task` does
   not complete just because `useBackgroundThread: true` was passed.** The
   flag only controls *which thread* runs the native event loop; the
   returned `Task` represents the hook's entire run lifetime and only
   completes on `Stop()`/`Dispose()`, regardless of the flag. `await
   hook.RunAsync(..., useBackgroundThread: true)` therefore hangs forever if
   you're expecting it to return once the hook is up. Confirmed empirically:
   a 5-second timeout race against the awaited task always lost, while the
   `HookEnabled` event fired within ~1ms and `IsRunning` was `true` the whole
   time. **Fix used throughout this spike:** fire the task without awaiting
   it (`var hookRunTask = hook.RunAsync(...)`), wait a short fixed delay (or
   subscribe to `HookEnabled`) before proceeding, and only `await
   hookRunTask` (wrapped in try/catch) *after* calling `hook.Stop()` at
   shutdown, to let the run loop wind down cleanly.
2. **`EventSimulator` has no public constructor.** It's `EventSimulator.Create(string
   applicationName, IEventSimulationProvider? simulationProvider = null)` — a
   static factory, not `new EventSimulator()`. Found by reflecting the
   assembly rather than guessing (see the "not obvious from docs, verify
   against the real package" pattern that also caught the S1
   `OfflineRecognizerConfig` shape and cost nothing to check here).

Everything else — `SimpleGlobalHook`'s constructor taking a nullable
`IGlobalHookProvider` (defaults to the real Windows uiohook provider),
`KeyboardHookEventArgs.Data.KeyCode`, `HookEventArgs.SuppressEvent`,
`HookEventArgs.EventTime` (a `DateTimeOffset`) — matched the plan's mental
model with no surprises.

## Results

### Jitter (self-verified, numeric, automated)

`self-test`, 30 synthetic press/release pairs on `VcRightControl`, timed from
immediately before `EventSimulator.SimulateKeyPress`/`SimulateKeyRelease` to
the instant `SimpleGlobalHook`'s `KeyPressed`/`KeyReleased` callback runs
(`Stopwatch`-based, sub-millisecond resolution):

```
DOWN latency (send -> callback), n=30, misses=0: p50=0.31ms p95=0.44ms max=0.69ms
UP   latency (send -> callback), n=30, misses=0: p50=0.61ms p95=0.76ms max=0.88ms
```

**PASS against the plan's < 20ms bar, by a wide margin** (p95 under 1ms, not
under 20ms). Zero missed events across 60 total press/release callbacks.

**Important caveat, stated plainly:** this measures dispatch latency for a
*synthetic, self-injected* keystroke (`SendInput` under the hood) observed
within the *same process* that's also running the hook. It is a legitimate,
reproducible number and a genuine floor on hook overhead, but it is not
identical to a real human's physical keypress: real hardware adds USB HID
polling-interval latency (typically 1-8ms at 125-1000Hz polling rates) before
the keystroke even reaches the point `SendInput`-based synthesis starts from.
**Re-verify this number by physically pressing the key** while a future
version of `listen` logs timestamps, if a tighter bound than "sub-millisecond
plus a few ms of USB jitter, safely under 20ms either way" is ever needed. Not
done here because it requires a human at a physical keyboard, which this spike
run didn't have.

### Held-modifier reading via `GetAsyncKeyState` (self-verified, automated)

```
While Shift held: Shift
After Shift released: (none)
```

**PASS.** `ModifierSnapshot.Read()` (in `NativeMethods.cs`) correctly reports
Shift as held immediately after `EventSimulator.SimulateKeyPress(VcLeftShift)`
and correctly reports it released afterward. This is the exact
`GetAsyncKeyState(VK_SHIFT/VK_MENU/VK_CONTROL/VK_LWIN/VK_RWIN)` pattern §1.8
of the plan needs for the modifier sanitiser — confirmed working, not just
assumed. Also wired into `listen` mode: every trigger DOWN logs a live
`heldModifiers=` snapshot, so a human holding Shift while pressing the
trigger (see manual script below) can watch it appear in real time.

#### `VK_CONTROL`/trigger-key ambiguity — a real finding, now demonstrated

Code review flagged that the Shift-only self-test above never exercises the
one modifier interaction that's actually load-bearing here: our trigger key
*is itself* Right Ctrl. `SelfTest.cs`'s `RunTriggerControlAmbiguityTest`
closes that gap by synthesizing a Right Ctrl (trigger) press and reading both
generic `VK_CONTROL` and `VK_LCONTROL` during the same press:

```
While Right Ctrl (trigger) held: generic VK_CONTROL=True, VK_LCONTROL=False
After Right Ctrl (trigger) released: generic VK_CONTROL=False, VK_LCONTROL=False
```

**This confirms the ambiguity is real:** physically holding Right Ctrl (the
trigger) will always show as generic Control-held, because `VK_CONTROL` does
not distinguish left from right. **Phase 1's §1.8 sanitiser must key off
`VK_LCONTROL` specifically, not generic `VK_CONTROL`**, to correctly
distinguish the trigger key being held from a genuinely-held left Ctrl on the
user's other hand. This is why `ModifierSnapshot.Control` (in
`NativeMethods.cs`) is deliberately read from `VK_LCONTROL`, not generic
`VK_CONTROL` — the generic reading is kept alongside as
`ModifierSnapshot.GenericControlHeld`, clearly labeled as diagnostic-only and
ambiguous with the trigger, and must not be used by any consumer (including
the future §1.8 sanitiser) that needs to tell the two apart.

### Callback pattern — which one to copy for Phase 1, and which not to

`ListenMode.cs` and `BlockDemo.cs`'s hook callbacks (`KeyPressed`/
`KeyReleased`) call `Console.WriteLine` synchronously inside the callback.
That's a deliberate spike-only choice, made purely for human observability —
it's how a human watching the console confirms DOWN/UP were detected in real
time during the manual test script and the block-callback demo. It is **not**
a pattern to carry forward.

The plan is explicit and non-negotiable that Phase 1's real hook callback
must only "set a flag, post to a channel, return" and never do I/O.
`SelfTest.cs`'s callbacks (in `RunJitterTestAsync`) already follow that
discipline correctly — a timestamp read (`Stopwatch.GetTimestamp()`), a field
write, a `SemaphoreSlim.Release()`, and nothing else. **`SelfTest.cs`'s
callback is the pattern to copy for Phase 1's `IHotkeySource`** — it does
zero I/O. **`ListenMode.cs`/`BlockDemo.cs`'s in-callback `Console.WriteLine`
calls are spike-only human-observability affordances and must NOT be copied
into product code.**

### Block-callback demonstration — a real, reproducible finding

`block-test`: synthesizes 4 press/release pairs, deliberately calls
`Thread.Sleep(2000)` inside the very first `KeyPressed` callback before it
returns. Reproduced twice with consistent results:

```
Total DOWN events observed: 4 / 4 sent. Total UP events observed: 3 / 4 sent.
```

**All 4 DOWN events arrived** (the hook was not fully unhooked in either run)
— but **the UP for press #1, sent 150ms into the 2-second block, was silently
dropped**, not delayed. It never reached `KeyReleased` at all, on either run.

This is a real, load-bearing finding for the daemon design, not a
theoretical warning: **the failure mode observed here is narrower but
arguably worse than a full unhook.** A full unhook (no more events at all
until re-registered) is at least self-consistent — DOWN and UP both stop
arriving together. What was actually observed is an **orphan DOWN with no
matching UP ever arriving**, produced by nothing more exotic than the
callback being briefly slow. This is *precisely* the "key stuck down" edge
case §1.4 of the plan already anticipates ("If the hook reports a down with
no matching up for `maxDurationMs`, force-finalise") — this spike provides
direct, reproduced evidence that the condition is real and not just
defensive paranoia, and that it can be triggered by something as mundane as
a 2-second callback stall, not just an exotic OS/session event.

**Not yet observed in this environment:** a full hook unregistration (zero
further DOWN events). The commonly-cited Windows `LowLevelHooksTimeout`
default is 300ms; a 2-second block comfortably exceeds that, so a full
unhook was expected but not what actually happened here — only the
in-flight UP was lost. This may be Windows-version-dependent behavior, or
may depend on exactly which native uiohook code path SharpHook's Windows
provider uses to pump messages. **This should be re-run on the actual
target machine as part of closing S3 out**, and ideally also with a real
*physical* key held down across the block, since a human's natural release
timing is less deterministic than this spike's fixed 150ms delay and might
land differently relative to the block window.

### Suppression into a real target app — NOT self-verifiable, needs a human

This is the plan's primary pass criterion and the one this spike is most
explicit about *not* faking: **"No character or modifier effect reaches the
target app"** fundamentally requires a human to focus Notepad/VS
Code/Chrome/Windows Terminal and type, because a console-mode agent process
has no way to observe another GUI application's internal keystroke buffer —
that's the whole point of testing it. `listen` mode's suppression logic
(`e.SuppressEvent = true`) has been exercised mechanically (both in
`self-test`'s hook wiring and in the `simulate-trigger` cross-check below),
but "mechanically calls the SuppressEvent setter" and "actually prevents an
arbitrary GUI app from seeing the key" are different claims, and only the
second one is the plan's actual pass bar. See the manual test script below.

### `listen` mode wiring cross-check (self-verified, automated, cross-process)

To validate `listen`'s console-mode wiring end-to-end without a human, one
`listen --duration 6 --verbose` instance was run in the background while a
separate `simulate-trigger` process instance fired one synthetic
press/release from *outside* the listening process (i.e. a genuine
cross-process OS-level event, not an in-process callback like `self-test`):

```
[11:52:44.458] [    2028.2ms] DOWN  #1  heldModifiers=(none)
[11:52:44.593] [    2161.7ms] UP    #1
[11:52:45.686] [    3255.0ms] DOWN  #2  heldModifiers=(none)
[11:52:45.824] [    3393.0ms] UP    #2
```

Both synthetic press/release pairs (one plain, one issued a second later)
were correctly detected, in order, with correct DOWN/UP pairing and
plausible timestamps. This confirms `listen`'s actual code path (not just
`self-test`'s) reacts correctly to a real OS-level keyboard event originating
from a different process — as close to "real" as this spike could get
without a physical keyboard or a focused GUI target.

## What was self-verified vs. what needs a human — summary

| Item | Status |
|---|---|
| Hook fires reliably for DOWN/UP on a synthesized Right Ctrl, cross-process | **Self-verified** — see cross-check above |
| Timestamp jitter on the callback < 20ms | **Self-verified, PASS** — p95 0.44ms (DOWN) / 0.76ms (UP), n=30 each, on synthetic events (see caveat re: physical-key jitter above) |
| Held-modifier state readable via `GetAsyncKeyState` | **Self-verified, PASS** — Shift correctly detected held/released via synthesized key |
| Hold trigger 60s, hook doesn't drop | **Not run in this session** — `listen --duration` can do this trivially (`listen --duration 65` while physically holding the key), but needs a human at a physical keyboard to hold it; see manual script |
| Deliberately block callback 2s, observe Windows' failure mode | **Self-verified, automated, reproduced twice** — orphan-UP-drop observed, not a full unhook; see "Block-callback demonstration" above and the caveat about re-testing with a physical key |
| Suppress DOWN, let UP leak, observe effect in a target app | **Implemented (`--leak-keyup`), not self-verifiable** — mechanism exercised, real-world effect on a target app is fundamentally a human-observation task |
| Press/release detected with Notepad/VS Code/Chrome/Windows Terminal focused, and nothing leaks into them | **Not self-verifiable at all** — see manual test script below |
| 30-minute idle survival | **Not run in this session** — needs real wall-clock time; `listen` with no `--duration` will run indefinitely and is ready for this, but nobody left it running for 30 minutes here |
| Lock/unlock cycle survival | **Not run in this session** — needs a real interactive session lock, which an automated/background test cannot trigger meaningfully |

## Manual test script (human required)

Run these with `dotnet run --project spikes/s3-hotkey-win -- listen --verbose`
(no `--duration`, so it runs until you press Escape or Ctrl+C in the console).

1. **Per-app suppression check.** For each of Notepad, VS Code, Chrome (a
   textarea, e.g. a Google Docs page or any `<textarea>`), and Windows
   Terminal:
   - Focus the app, click into a text field.
   - Type a short sentence normally to confirm the baseline works.
   - Hold Right Ctrl for ~1 second, release. Confirm:
     - The console shows exactly one `DOWN #n` and one `UP #n` line.
     - **No character appeared in the target app** and the cursor didn't move.
   - While still focused on the target app, press `Ctrl+A` (Left Ctrl), then
     `Ctrl+Z`/`Ctrl+C`/`Ctrl+V` as appropriate for that app. Confirm these
     still work normally — this checks that suppressing *Right* Ctrl hasn't
     broken *Left*-Ctrl-based shortcuts (they're different key codes, but the
     plan explicitly calls out verifying this rather than assuming it).
   - Now hold **Right Ctrl and press another key at the same time** (e.g.
     Right-Ctrl+A) in each app. Confirm the app doesn't receive a
     Ctrl+A-style command as a side effect of the suppressed Right Ctrl still
     being logically "down" from the app's point of view.
2. **60-second hold.** Focus any target app, press and hold Right Ctrl for a
   full 60 seconds, then release. Confirm the console logs exactly one DOWN
   (at press time) and one UP (at release, ~60s later) — not a stream of
   repeated DOWNs, and not a dropped UP. Confirm nothing leaked into the
   target app during the hold.
3. **Held-Shift check.** Focus any target app. Hold Shift, then press and
   release Right Ctrl while Shift is still held. Confirm the console's
   `heldModifiers=` field on the DOWN line reads `Shift` (not `(none)`), and
   confirm nothing leaked into the target app.
4. **Leak-keyup observation.** Restart with
   `listen --verbose --leak-keyup`. Focus Windows Terminal or VS Code (apps
   that track modifier state internally). Press and release Right Ctrl once.
   Confirm the console logs `(leak-keyup active: NOT suppressing this
   key-up...)`. Then **immediately type a few characters** in the target app
   and watch for anything unusual (stuck-modifier-style behavior, e.g.
   characters appearing as if Ctrl were still held, or no visible effect at
   all — either outcome is a valid observation, just record which one
   happened and in which app). This is the "five minutes now, saves an
   evening later" check from the plan.
5. **30-minute idle survival.** Start `listen --verbose` with no
   `--duration`. Leave it running, doing normal work in other apps, for 30
   minutes. At the end, press Right Ctrl once more and confirm a DOWN/UP
   still appears — the hook should not have silently died from being idle.
6. **Lock/unlock cycle.** With `listen` still running from step 5 (or a fresh
   instance), lock the workstation (Win+L), wait a few seconds, unlock, then
   press Right Ctrl once. Confirm a DOWN/UP still appears. If it doesn't,
   that's a real S3 failure finding, not a spike artifact — record exactly
   what happened (nothing logged at all? exception? process still running?).

Record pass/fail for each step directly in this README (or in
`Docs/SPIKE-RESULTS.md` per the Phase 0 exit checklist) once run — this spike
intentionally leaves these six rows unfilled rather than guessing at an
outcome.

## What's left for S3 to be fully green

- [x] Press/release detected globally, with correct suppression logic wired
      and unit-level-verified (self-test, cross-process check)
- [x] Timestamp jitter < 20ms — **PASS, 0.44ms/0.76ms p95** (caveat: measured
      on synthetic events; physical-key confirmation not yet done)
- [x] Held-modifier state readable via `GetAsyncKeyState` — **PASS**
- [x] Block-callback failure mode observed and documented (orphan-UP-drop,
      reproduced twice; full-unhook variant not observed in this environment,
      needs re-test on target machine / with a physical key)
- [ ] Press/release confirmed with Notepad, VS Code, Chrome, Windows Terminal
      focused, with zero character/modifier leakage — **needs a human**, see
      manual script step 1
- [ ] 60-second hold confirmed with a real target app and a physical key —
      **needs a human**, see manual script step 2
- [ ] Held-Shift-during-trigger confirmed against real hardware (self-test
      already confirms the read mechanism works against a synthesized key) —
      **needs a human**, see manual script step 3
- [ ] Leak-keyup effect observed in a real target app (Windows Terminal
      and/or an IDE) — **needs a human**, see manual script step 4
- [ ] 30-minute idle survival — **needs real wall-clock time**, see manual
      script step 5
- [ ] Lock/unlock cycle survival — **needs a real interactive session lock**,
      see manual script step 6

Overall: **S3's two directly-measurable, fully-automatable pass criteria
(jitter, held-modifier reading) both PASS with real numbers**, and the
block-callback failure mode was successfully reproduced and is documented as
a genuine design input for §1.4's watchdog/force-finalise logic. The
remaining criteria are not failed — they are pass criteria that are, by
their own nature, impossible for a console-mode agent process to certify
without a human physically at the keyboard, and are written up above as an
explicit manual test script rather than silently skipped or assumed passing.
