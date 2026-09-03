# Soneto — Manual Test Checklist

Per plan §1.13: "the S4 injection matrix, re-run before every release." This is the actual,
re-runnable manual checklist for a human at a real keyboard — no agent session has been able to
close any item on this list, because every item here needs one or more of: real audio hardware,
a real focused GUI application, physical key-hold timing, or real elapsed wall-clock time (idle
survival, lock/unlock cycles). Work through this before every release; check off what passes,
note what fails with enough detail to file a real bug.

Cross-references: `Docs/ARCHITECTURE.md` (what each of these is testing), `Docs/PLATFORM-NOTES.md`
(why some of these are Windows-only or Linux-only right now), `Docs/SPIKE-RESULTS.md` (the
numbers these checks originally validated at the spike stage).

---

## Section A — S4 injection matrix (Windows)

The exact test string (use it verbatim, don't paraphrase — diacritics and punctuation are the
point):

```
Ăsta e un test: șoseaua Ștefan cel Mare, țară, îngheț — 100% "quoted" & <tagged>.
Line two after a newline.
```

Run via `s4-inject-win countdown --seconds 3 --text "<the string above>"` (or the equivalent
`Soneto.Daemon --inject` path once re-verified per Section C below), focusing each target app
during the 3-second countdown. **Use `countdown` mode one app at a time, not `launch all`** — the
automated batch launcher has a known unreliable foreground-window-detection bug (see
`SPIKE-RESULTS.md`'s S4 entry) that produced false-positive results on a live desktop. Close
other real accounts/documents before running this section.

For each app, confirm:
- [ ] Text lands exactly, including the newline
- [ ] `ș`/`ț` are the comma-below Unicode forms (U+0219/U+021B), not cedilla (U+015F/U+0163) —
      check actual bytes if in doubt, not just how the glyph looks on screen
- [ ] Original clipboard content is restored afterward (paste something else first, confirm
      it's back)
- [ ] Injection feels instant (no perceptible lag before text appears)
- [ ] Record whether a non-default paste chord or extra delay was needed (seeds `perApp` config)

| # | App | Notes | Status |
|---|---|---|---|
| 1 | Notepad | Baseline — only one confirmed via full programmatic verification so far | ☐ |
| 2 | VS Code | Electron — the known problem case | ☐ |
| 3 | Chrome — address bar | | ☐ |
| 4 | Chrome — a textarea | Only this Chrome target has a screenshot-confirmed pass on record | ☐ |
| 5 | Windows Terminal | May need `ctrl+shift+v` instead of the default chord — unconfirmed, check both | ☐ |
| 6 | Microsoft Teams | Electron, slow input handling — allow extra settle time before judging failure | ☐ |
| 7 | Outlook (desktop) | Rich text target | ☐ |
| 8 | Word | Rich text — autocorrect may mangle diacritics, watch for it | ☐ |

**Standing gap as of this checklist's writing:** only Notepad (programmatic read-back) and
Chrome-textarea (screenshot) have ever actually passed a clean, attributable run. VS Code,
Chrome address bar, Windows Terminal, Teams, Outlook, and Word have never been cleanly confirmed
— their earlier "Injected" results came from the unreliable `launch all` batch run and should
not be trusted.

**Phase 4 item 4 note (2026-09-03) — automated coverage added, rows below still open, none
closed by this note.** `tests/Soneto.Platform.Windows.Tests/PerAppOverrideEndToEndTests.cs`
(`Category=Hardware`) now proves `InjectionConfig.PerApp` resolution (§4.4) is genuinely applied
at real injection time — a real `WindowsTextInjector.InjectAsync` call, per-app table keyed to
the test process's own real executable name, `Method=UnicodeSynth`, verified via the real
clipboard-sequence-number side effect (unchanged) plus real Romanian-diacritic marker text
landing correctly. This is real, permanent regression coverage for the *mechanism* (§4.4), run
against Soneto's own self-owned, off-screen window — it does **not** touch VS Code, Windows
Terminal, or any other real foreground app, so it does not close row 2 or row 5 above. Those
rows (and every other row in this table) still require a real human running the checklist
against real apps — not yet done as of this note.

---

## Section B — S4 adversarial cases (Windows)

- [ ] **Held-Shift during injection.** Physically hold Shift down through an entire real
      injection (not released mid-sequence — the automated spike test can't hold a key across
      the whole sequence the way a human can). Confirm the paste chord isn't corrupted into
      `Ctrl+Shift+V`, and confirm typing a few characters immediately afterward is NOT all
      uppercase (no stuck Shift).
- [ ] **Copy during the clipboard-restore window.** Inject, then hit Ctrl+C in the target app
      within the ~150ms restore-delay window. Confirm the sequence-number guard aborts the
      restore and your fresh copy survives (i.e. pasting afterward gives you what you just
      copied, not the dictation transcript).
- [ ] **Non-text clipboard content first.** Put an image on the clipboard (e.g. a screenshot),
      then trigger an injection. Confirm `textOnly` policy skips restoration (the image is
      NOT replaced by the transcript text) and that a log line explains why.

---

## Section C — Real hardware / real human, standing gaps carried from items 4b/6/7/9/10/11

No agent session can produce real audio near a microphone or safely drive unsupervised
synthetic input against a live desktop — these gaps are honestly documented in
`PROJECT-MEMORY.md`/`CHANGELOG.md` item-by-item and are collected here as one checklist.

- [ ] **Real speech capture end-to-end (item 4b/4c).** Run `--record` (or a live dictation)
      while actually speaking into the microphone and confirm real, non-silent audio is
      captured correctly through the resample/ring-buffer pipeline — every prior verification
      confirmed the pipeline mechanics work, but every buffer tested was silence.
- [ ] **Ready/failure cue audibility (item 4c).** Confirm the ~880Hz ready tone and ~220Hz
      failure tone are both actually audible through real speakers/headphones, not just
      "played" per the logs.
- [ ] **Hotkey leak/suppression against real focused apps (S3, item 6).** Press/release the
      trigger with Notepad, VS Code, Chrome, and Windows Terminal focused; confirm no character
      or modifier effect reaches any of them (this is the actual suppression-into-a-real-app
      test — automated `EventSimulator` tests exercise the mechanism but not a real focused
      target).
- [ ] **60-second physical hold of the trigger key** — confirm the hook doesn't get dropped.
- [ ] **Physical Shift held while pressing the trigger** — confirm `GetAsyncKeyState` correctly
      reads the held-modifier state (this is the direct input to §1.8's sanitiser).
- [ ] **Observe a leaked orphan key-up on a real target app** (terminal/IDE) — deliberately
      suppress key-down but let key-up leak once, and see what the app does with it. Five
      minutes now saves an evening of confusion later, per the plan's own framing.
- [ ] **30-minute idle survival** — leave the daemon running idle for 30 minutes, then confirm
      the hotkey still works normally.
- [ ] **Lock/unlock cycle survival** — lock the workstation, wait, unlock, confirm the hotkey
      and a full dictation still work normally afterward.
- [ ] **Full end-to-end dictation (item 9's own milestone, still open).** Press the real
      hotkey, speak into a real mic, release, and watch punctuated text land in a real focused
      app — the actual product experience, only ever exercised piecewise (mechanics proven,
      full loop with a human voice never run) as of this writing.
- [ ] **Re-verify items 6/7's hardware claims against the fixed `WINDOWS` build (item 9's
      review finding).** The `WINDOWS` preprocessor symbol that gates `--watch-hotkey`/
      `--inject` was undefined and silently dead code when items 6/7's original
      "verified end-to-end against real Notepad" results were recorded — see
      `Docs/PLATFORM-NOTES.md`'s writeup. Re-run `--watch-hotkey` and `--inject` now that the
      symbol is correctly defined and confirm the same results reproduce before trusting those
      claims again.
- [ ] **24-hour soak test (work item 10b, separate from this doc's scope but listed for
      visibility).** Daemon runs 24h with lock/unlock and sleep/wake cycles, ~200 dictations,
      no memory growth, no hook loss, no leaked audio streams. Not yet run.

---

## Section C.1 — Phase 3 end-to-end demo (item 11, one continuous walkthrough)

Per `Docs/soneto-implementation-plan-phase3.md` §3.16 item 11's own done-when bar: **one
continuous walkthrough, not separate disconnected checks.** Every piece below has already been
verified in isolation (Phase 3 items 0–10, see `PROJECT-MEMORY.md`/`CHANGELOG.md`) — dictation
mechanics, History persistence/search/diff, the Dictionary editor's write path, Settings, and the
Permissions Doctor's five checks. What has never been run is the whole chain back-to-back by one
person in one sitting, which is the actual thing this item exists to confirm. No agent session
can do this (needs a real hotkey press + real speech into a real microphone — see the standing
live-desktop-testing caution). Run `Soneto.App.exe` (not `Soneto.Daemon`) for all of this — see
`Docs/ARCHITECTURE.md`'s "Why two executables" section for why `Soneto.App` is the real product.

- [ ] **Launch.** Start `Soneto.App.exe` with the real ASR model present. Confirm the tray icon
      appears and the main window shows, nav rail defaulting to History.
- [ ] **Dictate a real utterance that exercises a real dictionary rule.** Hold the configured
      trigger key (default Right Ctrl), speak a phrase containing a term/phrase your
      `dictionary.json` corrects (the seed dictionary's `webMethods`/`Integration Server`/etc.
      vocabulary terms work, or use a phrase matching one of the 4 seed spoken commands), release.
      Confirm the Recording HUD appeared while held and disappeared on release, with a
      live-updating level meter and elapsed counter.
- [ ] **Confirm the text lands correctly** in whatever had real OS focus, with the dictionary
      correction/spoken-command effect actually applied (not just plausible-looking raw ASR
      output).
- [ ] **Confirm it appears in History** with a **correct diff** — select the new entry, confirm
      the Raw/Final columns render with the corrected span genuinely highlighted (not the whole
      transcript, not a coincidentally-matching but wrong span elsewhere in the text).
- [ ] **Edit the dictionary rule you just exercised**, in the Dictionary editor (e.g. change a
      `CorrectionPair`'s `To` value, or add a new one). Confirm the "changes apply on next
      restart" notice is visible and honest — the edit should NOT retroactively change the
      History entry you just made, and should NOT take effect in the still-running session.
- [ ] **Confirm Settings reflects real state** — open the Settings page, confirm every displayed
      value (hotkey, ASR thread count, capture mode, etc.) matches what's actually in
      `config.json` / what the app is actually running with, not stale/default placeholder text.
- [ ] **Confirm the Permissions Doctor reflects real state** — open the Permissions page, confirm
      all five checks (Mic access, Global hook active, Can synthesize input, Clipboard
      read/write, Model files present & hashed) report real, live-computed results (Green given
      the dictation you just successfully completed above), not stale/cached values from launch.
- [ ] **Restart `Soneto.App.exe`** and confirm the dictionary edit from the step above now IS in
      effect (closing the loop on the "next restart" notice actually being true, not just
      displayed).
- [ ] **Data & privacy controls (item 10), same session:** in Settings, enable debug-audio
      retention, dictate one more utterance, and confirm a WAV file actually appears (named by
      the new History entry's Id) in the debug-audio directory (`%LOCALAPPDATA%\Soneto\
      debug-audio\` on Windows). Set history auto-delete to a small number of days for a quick
      check of the mechanism (a full real multi-day wait isn't practical here — confirming the
      setting saves and the sweep doesn't error/crash the app is sufficient for this pass; the
      purge logic itself is already covered by real automated tests against temp databases).
      Trigger Panic Wipe from Settings, confirm the confirmation dialog requires a genuine second
      click (not satisfied by Enter or a stray double-click), confirm History is empty and the
      debug-audio directory is emptied afterward.

If any step fails, capture exact repro steps + a screenshot/log excerpt and file it against
Phase 3 item 11 in `Docs/soneto-implementation-plan-phase3.md`. A clean pass through every box
above is what actually closes out item 11 and, with it, Phase 3's main build order.

---

## Section D — Linux (S5, Phase 1 item 11 — nothing here has ever been run on real hardware)

Everything in this section requires real Fedora KDE Wayland hardware, which no session working
on this project has had access to. `src/Soneto.Platform.Linux/`'s pure decision logic (device
filtering, key mapping, hash-guard sequencing, backend selection) is unit-tested; none of the
actual syscalls/process launches below have ever executed against a real kernel/compositor.

- [ ] Run `scripts/setup-linux.sh` on a real machine and confirm every step reports green
      (input group membership, `/dev/uinput` udev rule, `ydotoold` unit).
- [ ] Press/release detected correctly with jitter < 30ms (S5's own pass bar).
- [ ] Confirm whether the trigger key leaking through to the focused app (no `EVIOCGRAB`
      suppression is implemented — see `PLATFORM-NOTES.md`) is actually tolerable in practice.
- [x] Multi-keyboard hotplug: plug in a second keyboard mid-session, confirm the hotkey still
      works from either keyboard without a restart. **Closed via spike S7's Docker harness
      (2026-09-03)**, not real hardware -- a second `uinput`-created virtual keyboard mid-session
      correctly triggered real `inotify` re-enumeration and the hotkey kept working afterward. A
      real physical-USB-device version of this test is still open (a container's uinput device
      is kernel-real but not literally a physical device plug event) -- see
      `spikes/s7-docker-linux/README.md`.
- [ ] Repeat Section A/B's S4 test string and adversarial cases against Kate, Konsole, Firefox,
      VS Code, and Thunderbird (S5's own five target apps), via `wl-copy`/`ydotool`.
- [ ] Diacritics land correctly (same byte-level `ș`/`ț` check as Section A).

---

## Notes for whoever runs this

- If a step fails, capture: the exact repro steps, a screenshot/log excerpt, and whether it's a
  regression (worked before) or a first-time failure — file it against the relevant item number
  from `soneto-implementation-plan-phase0-1.md` §1.14's build-order table.
- Do not batch-automate Section A/D against a live, in-use desktop — S4's spike already produced
  one real (harmless, but real) near-miss doing exactly that. One app at a time, countdown mode,
  other real accounts/documents closed first.
- This checklist should grow, not just get re-run unchanged — if a future item introduces a new
  "can't verify from an agent session" gap, it belongs here, not just in that item's own
  changelog entry.
