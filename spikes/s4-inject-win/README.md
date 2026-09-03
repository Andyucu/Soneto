# S4 — Windows injection matrix

Spike per `Docs/soneto-implementation-plan-phase0-1.md` §"S4 — Windows injection matrix". Throwaway code — see `spikes/s3-hotkey-win/README.md` for the error-handling convention this follows (fail loudly, no investment beyond that).

**Question:** does clipboard-paste injection land correct text, including diacritics, in every app you actually use?

## What this builds

The full injection algorithm from §5.2 / §1.8 of the plan:

1. Save clipboard (`CF_UNICODETEXT` + sequence number, plus a check for non-text formats).
2. Sanitise modifiers: suppress any physically-held Shift/Alt/Ctrl/Win before pasting (reusing the `VK_LCONTROL`-correct pattern S3 established — see `Docs/PROJECT-MEMORY.md`'s note on generic `VK_CONTROL` being ambiguous with a Ctrl-key trigger), re-check physical state before restoring.
3. `SetClipboardData` with retry (3×/20ms — clipboard managers can collide).
4. `SendInput` paste chord (default `ctrl+v`, configurable per profile).
5. Wait `ClipboardRestoreDelayMs` (default 150ms).
6. Check the clipboard sequence number; restore the original clipboard only if unchanged (sequence-number guard), and only if the original had no non-text formats under the `textOnly` policy.

## CLI

```
s4-inject-win countdown [--seconds N] [--text "..."]     # manual: 3s countdown, then inject into whatever has focus
s4-inject-win notepad-selfcheck                            # automated: launch Notepad, inject, read back, verify
s4-inject-win adversarial shift|restore-race|image         # the three required adversarial cases
s4-inject-win launch <profile>|all                          # best-effort per-app matrix, screenshots the result
```

Test string (exact, per the plan):
```
Ăsta e un test: șoseaua Ștefan cel Mare, țară, îngheț — 100% "quoted" & <tagged>.
Line two after a newline.
```

## Results — core algorithm (fully self-verified, real evidence)

### `notepad-selfcheck` — PASS

Launches Notepad, injects, reads the actual edit-control content back, and diffs it against the exact test string.

```
Read back (raw): Ăsta e un test: șoseaua Ștefan cel Mare, țară, îngheț — 100% "quoted" & <tagged>.\rLine two after a newline.
Exact match (modulo trailing newline Notepad may add): True
Diacritics check: PASS -- comma-below ș/ț present=True/True, cedilla ş/ţ present=False/False (must be false), ă/Ă present=True
Original clipboard content restored: True
```

**Diacritic byte-level check confirmed:** `ș`/`ț` present as the comma-below forms (U+0219/U+021B), cedilla forms (U+015F/U+0163) absent. This is the exact check the plan calls for ("check the bytes, not the glyph").

### Latency

Two numbers, and they mean different things — worth being precise about which one the plan's "<200ms" bar is actually gating:

- **`elapsed` (full call, including the mandatory 150ms clipboard-restore-delay wait):** 184.9–210.3ms across all runs. This sits right on the 200ms line — several runs (Notepad baseline 201.3ms, VS Code attempt 201.9ms, Chrome address bar 200.3ms, Terminal 202.3ms, Teams 202.3ms, Outlook 202.5ms, Word 200.4ms) technically exceed it by 1–3ms.
- **`timeToPasteSent` (time until the paste keystroke is actually dispatched, excluding the deliberate post-paste wait):** 34.1–46.8ms across all runs — comfortably under 200ms.

**Honest read:** the ~150ms of the "full" number is a deliberate, fixed wait *after* the paste has already happened (per §1.8 step 10, "a race, not a guarantee" — it exists purely to let the sequence-number guard detect a user's Ctrl+C before restoring). The user's felt latency is the paste itself, which lands in ~35–47ms. The plan's §4 latency budget table lists "Clipboard set + paste synth + restore: 50–120ms" for this whole stage, which doesn't actually leave room for a 150ms fixed wait either — there's a real tension in the plan's own numbers between the default `clipboardRestoreDelayMs: 150` (§1.10 config) and the §4 budget. **Flagging this for Phase 1, not resolving it here:** either the 150ms restore delay needs to come down, or the felt-latency accounting needs to explicitly exclude the post-paste restore wait (which doesn't block the user, since the paste already happened). Don't let this spike's "elapsed" number alone decide a pass/fail without that context.

### Adversarial case 1 — hold Shift during injection — PASS

Synthesizes a physical Shift-down via SendInput before triggering injection.

```
GetAsyncKeyState(VK_LSHIFT) reports held: True
sanitizer: suppressed held modifier Shift
sanitizer: NOT restoring Shift -- released during injection (avoiding stuck modifier)
Sanitiser suppressed Shift before paste chord: True
Typed characters landed lowercase (no stuck Shift): True
```

Confirms the sanitiser correctly suppresses a held Shift and doesn't leave it stuck. Note: this harness synthesizes the Shift release *during* the injection window (it can't hold a key down across the whole sequence from a single-threaded console flow the same way a human would), so it always exercises the "released during injection" branch, not the "still held after paste" branch — that specific branch needs the manual test script (a human physically holding Shift throughout).

**Flake found and fixed (post-review):** independent verification found this test failing intermittently (~30% of runs). Root-caused to the test harness, not the sanitiser: the sanitiser's own log lines (`sanitizer: suppressed held modifier Shift`, `sanitizer: NOT restoring Shift`) were correct on every single run, including the failing ones. The actual cause was `AdversarialTests.RunShiftHold`'s post-type read-back — a single UI Automation read taken immediately after the synthetic "type abc" step, which occasionally raced Notepad's RichEditBox and came back empty or containing a stray U+FFFC (object replacement character), indistinguishable from a real stuck-Shift failure if trusted as a single sample. Fixed by adding `NotepadVerifier.ReadTextStable` — the same "poll until two consecutive reads agree" pattern already used by `WaitForForegroundWindowTitled` for window-title detection — and switching the shift test to use it instead of a single `ReadText` call. Re-verified: 12/12 consecutive runs PASS after the fix (previously ~30% failed); 0/12 flake reproduced.

### Adversarial case 2 — copy-during-restore-window — PASS

Races a synthetic Ctrl+C-equivalent (direct `SetClipboardData`) into the 150ms restore window.

```
Racing: simulating the user hitting Ctrl+C (direct SetClipboardData) inside the restore window...
inject: SKIP restore -- clipboard sequence number changed during restore window (user copied during window)
Sequence-number guard aborted the restore: True
User's copy survived (not overwritten by original-clipboard restore): True
```

This is the most safety-critical behavior in the whole design (per the plan: "silently overwriting something the user just deliberately copied is the worst failure this app can have") and it works correctly.

### Adversarial case 3 — image on clipboard — PASS

Places a synthetic bitmap on the clipboard, then injects under `textOnly` policy.

```
clipboard: synthetic CF_BITMAP placed for image-on-clipboard test
inject: SKIP restore -- textOnly policy and original clipboard had non-text formats (would destroy them); leaving transcript on clipboard
Skipped restore because of non-text formats: True
Logged the reason clearly: True
```

## Known issues, now fixed (post-review, safety-relevant)

Independent code review found two bugs in the restore path (§1.8 step 11) that mattered enough to fix before treating this spike's "clipboard restored" claims as trustworthy:

1. **Restore failure was silently swallowed and misreported.** `Injector.Inject` called `ClipboardManager.RestoreText(...)` (a retry loop that returns `bool` success/failure) but discarded the return value and unconditionally logged `"inject: original clipboard text restored"` regardless of whether the retries actually succeeded. If all 3 attempts failed (e.g. another process held the clipboard open the whole time), the log — and by extension anyone reading it — would have falsely believed the user's original clipboard content was safe. **Fixed:** the return value is now checked; a distinct `InjectionOutcome.RestoreFailed` result and a `"inject: FAILED to restore original clipboard after retries"` log line are produced on genuine failure, so success and failure are never ambiguous in the log or the returned outcome.

2. **TOCTOU race between the sequence-number check and the actual restore write.** The original step-11 code read `GetClipboardSequenceNumber()` standalone (not inside an `OpenClipboard`/`CloseClipboard` critical section), then — sometime later — a completely separate `RestoreText` call independently opened the clipboard and wrote. If the user's Ctrl+C landed in the gap between those two operations, the sequence-number guard would not catch it, and the restore would silently overwrite the user's fresh copy — exactly "the worst failure this app can have" per the plan's own framing. **Fixed:** `ClipboardManager.TryRestoreIfSequenceUnchanged` now opens the clipboard once, reads the sequence number *while still holding it open*, and only proceeds to `EmptyClipboard`/`SetClipboardData` if it still matches the expected value — all inside one open/close critical section, with no gap for a race to land in. `Injector.Inject` still does an early, non-atomic sequence check first (a cheap short-circuit for the already-changed case, purely for early exit and logging) but the actual write is now gated by the atomic check inside the same critical section, not by that earlier read. Re-verified via `adversarial restore-race`: still PASS after the fix, with the guard now demonstrably race-proof rather than merely race-resistant-in-practice.

Also fixed as part of the same review pass (less safety-critical, but correctness/hygiene issues worth noting): `GlobalAlloc`'d memory was leaked on the `GlobalLock`-fails and `SetClipboardData`-fails paths in `ClipboardManager` (ownership of that memory only transfers to the system on a *successful* `SetClipboardData`; both failure paths now call `GlobalFree`), and several `Process.Start()` results across `NotepadSelfCheck`, `AdversarialTests`, and `AppMatrix` were `Kill()`ed (or, for `AppMatrix`, left running) without ever being `Dispose()`d — all now wrapped in `using`.

## Results — per-app launch matrix (`launch all`) — important finding, most results unreliable

**Only two apps are genuinely confirmed from this run: Notepad (programmatic read-back, above) and Chrome-textarea (visually confirmed via screenshot — the actual Chrome window's textarea shows the correct text with correct diacritics).**

**Everything else in the `launch all` run is unreliable and should NOT be read as pass/fail evidence, for a real and important reason:** this spike ran on a live, in-use development desktop with the operator's actual applications already open — a real VS Code project window, a real signed-in Microsoft Teams work account, etc. `AppMatrix.RunOne()` calls `GetForegroundWindow()` immediately after `Process.Start()` + a fixed `SettleMs` sleep, assuming that call returns the newly-launched app's window. On this desktop, several launches (VS Code via the `code` CLI in particular — it hands off to a background service and doesn't reliably steal foreground within the settle window) did not actually bring the new window forward in time. The result: several profiles' `Outcome=Injected` is a **false pass** — the paste landed on a stale, still-foregrounded window left over from an earlier test run (a floating Notepad window), not on the intended target app at all. This was caught by actually looking at the screenshots, not by trusting the "Injected" return value.

**What we did to keep this safe:** stopped further batch automation once this was noticed, verified via `Get-Process` that no real user content was overwritten (Outlook and Chrome had already closed on their own with no lingering draft/window; the Word instance's window title was the generic "Word", i.e. an untitled/unsaved document, not the user's real file), and closed the leftover test Notepad window without saving. **No evidence any real document, email, or chat message was modified or sent** — but this was a genuine near-miss worth stating plainly: automated multi-app UI injection testing against a live, in-use desktop is inherently riskier than the plan's spike section implies, because "whatever has focus" can be a real account, not a clean test target.

**Recommendation for Phase 1 and for finishing S4 properly:** the plan's own method for this spike is actually the safer one — a 3-second countdown (`countdown` mode, already built and working) where a human manually Alt-Tabs to each real target app one at a time, rather than an automated launcher trying to guess/force focus across a batch of apps on a shared desktop. Use `countdown` for the remaining matrix, not `launch all`, and only on a desktop with other real accounts/documents closed first.

## What needs manual verification (step-by-step)

Before treating S4 as fully green, a human should, on a clean desktop (other real work closed):

1. For each of Notepad, VS Code, Chrome (textarea + address bar), Windows Terminal, Teams, Outlook, Word: focus the app, run `s4-inject-win countdown --seconds 3`, switch to the target within the countdown, and visually confirm the exact test string (with correct diacritics) lands correctly.
2. Confirm original clipboard content is restored after each (paste something else first, check it's back).
3. Windows Terminal specifically: confirm whether default `ctrl+v` works or whether `ctrl+shift+v` is required (the plan flags this as a known possible per-app quirk) — this run's terminal screenshot showed an empty prompt, consistent with the plan's own warning, but wasn't reliably attributable given the foreground-detection issue above; needs a clean re-test.
4. Physically hold Shift throughout a real injection (not synthetically released mid-sequence) and confirm no stuck modifier afterward.
5. Record, per app, whether a non-default paste chord or extra delay was needed — this seeds the `perApp` config table (§1.10).

## Deviations / surprises vs. the plan

- The plan's §4 latency budget (50–120ms for clipboard+paste+restore) and §1.10's default `clipboardRestoreDelayMs: 150` are in tension — see "Latency" above.
- `AppLauncher`'s foreground-window assumption is not reliable on a busy real desktop with pre-existing app instances — this is a genuine gap in the spike's automation, not in the injection algorithm itself, and it's the reason the per-app matrix isn't trustworthy from this run.
