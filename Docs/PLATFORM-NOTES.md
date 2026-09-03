# Soneto — Platform Notes (Windows vs. Linux)

Windows-vs-Linux specifics that don't belong in `ARCHITECTURE.md`'s general shape — the things
a developer switching between `Soneto.Platform.Windows` and `Soneto.Platform.Linux` would
actually trip over. Every finding here traces back to a real spike/item and is cross-referenced
to `PROJECT-MEMORY.md`/`CHANGELOG.md`/the plan doc for the full writeup.

---

## The `WINDOWS` preprocessor-symbol gotcha (item 9's review — read this before touching `Program.cs`)

**What happened:** `Soneto.Daemon/Program.cs` gates every Windows-only code path (`--watch-hotkey`
from item 6, `--inject` from item 7, and the real daemon composition from item 9) behind
`#if WINDOWS`. That symbol was **never actually defined anywhere in the repo** until item 9's
review caught it — confirmed directly via `dotnet build -getProperty:DefineConstants`, which
returned only `TRACE;DEBUG` even on the `net10.0-windows` TFM (the .NET SDK auto-generates
`NET10_0_WINDOWS`, not a bare `WINDOWS`). Every `#if WINDOWS` block was silently dead code on
every build configuration, including the Windows-targeted one — confirmed by inspecting the
compiled DLLs' metadata directly (the Windows-only method names/log strings were provably
absent pre-fix, present post-fix).

**Why this matters beyond "it was a bug":** items 6 and 7's changelog/PROJECT-MEMORY entries
both contain detailed, specific-looking "verified end-to-end against real Notepad" results
(hook DOWN/UP counts, injection outcomes, elapsed-time numbers, diacritic byte-level checks)
that could only have come from the real branch actually running — which was provably
unreachable from the committed build config at the time those entries were written. The
discrepancy was never fully explained (either an undocumented local override was used, or the
build config changed since) — **items 6/7's hardware-verification claims are flagged
unconfirmed in `PROJECT-MEMORY.md`'s standing ⚠️ callout, pending someone re-running them
against the now-fixed build.** The state-machine *logic* those items built is unaffected (their
own unit test suites don't depend on the `WINDOWS` symbol), only the "we ran this against real
hardware and it worked" claims need re-confirmation.

**The fix:** `Soneto.Daemon.csproj` now explicitly defines `WINDOWS` for the `net10.0-windows`
TFM, with the missing-symbol root cause and its blast radius documented directly in the csproj
comment so it can't silently regress unnoticed again. **If you ever add a new `#if WINDOWS`
block anywhere, verify it's reachable with `dotnet build -getProperty:DefineConstants` before
trusting anything it gates — this is exactly the kind of thing that looks correct, compiles
clean, and is entirely inert.**

---

## `VK_LCONTROL`, not generic `VK_CONTROL` (S3, item 6, item 7b)

**Finding (S3):** with Right Ctrl as the hotkey trigger, reading the generic `VK_CONTROL` virtual
key during/around a trigger press always reports "held," regardless of whether the user is also
physically holding some *other* Ctrl key — demonstrated directly (`VK_CONTROL`=True,
`VK_LCONTROL`=False during a trigger press). Generic `VK_CONTROL` can't distinguish "the trigger
key that's currently down is itself a Ctrl key" from "the user is additionally holding Ctrl."

**Why it matters:** any modifier-sanitiser logic that reads `VK_CONTROL` instead of the specific
`VK_LCONTROL`/`VK_RCONTROL` will falsely believe the user is holding Ctrl on *every single*
dictation whenever the trigger itself is a Ctrl key, and will suppress/restore it incorrectly.

**Where it's applied in the real product:** `WindowsHotkeySource` (item 6) reads
`VK_LCONTROL` specifically for its trigger-key detection; `ModifierSanitizer` (item 7b)
generalizes the same left/right-specific discipline to Shift/Alt/Win — it excludes whichever
specific VK the configured trigger resolves to (not a generic modifier family) from its
suppress/restore set. A follow-up review found the sanitiser's own trigger-alias table
initially didn't cover `HotkeyKeyMapper`'s raw-`KeyCode`-name fallback path (e.g.
`"VcLeftShift"`), reopening the same class of bug at a different layer — fixed by having the
sanitiser delegate trigger resolution to `HotkeyKeyMapper.ToKeyCode` directly, one shared source
of truth instead of two independently-maintained alias tables.

**A related, distinct finding, item 7:** `WindowsHotkeySource`'s hook observes **all**
keyboard events system-wide, including the injector's own synthetic `SendInput` calls. If a
hotkey trigger is configured as `LeftControl`/`LeftShift` (both explicitly supported aliases),
the injector's own paste-chord `Ctrl`/`Shift`-down collides with the trigger and gets suppressed
by the hook, producing phantom hotkey events and breaking every paste — 100% reproducible, not
load-dependent, invisible with the default `RightControl` trigger since it never collides with
the paste chord. **Fixed with `IsEventSimulated`** (backed by Windows' `LLKHF_INJECTED` flag):
the hook ignores any trigger-coded event that is self-injected. Cost: two hook tests can no
longer use synthetic input to simulate "a physical press" and are marked `[Skip]` with an
explanation, since synthetic input is now indistinguishable from the injector's own output by
design.

---

## Clipboard restore guard: sequence number (Windows) vs. content hash (Linux)

Windows has `GetClipboardSequenceNumber()`, a monotonically-incrementing OS counter bumped on
every clipboard write from any process — the natural "did something else touch the clipboard
during my restore window" check. **Linux has no equivalent.** `LinuxClipboardManager`/
`ClipboardHashGuard` instead hash (SHA-256) the clipboard content before and after the restore
window and skip the restore if the hash differs — functionally the same guarantee ("don't
silently overwrite something the user just deliberately copied"), different mechanism because
the platform primitive doesn't exist.

**The atomicity lesson carries across both platforms, not just Windows.** S4's spike found a
genuine TOCTOU race between the sequence-number check and the actual restore write when they're
implemented as two separate operations (check, then later, separately, write) — a user's Ctrl+C
landing in the gap wouldn't be caught. The fix (open-check-write-close as one atomic critical
section) is what `ClipboardManager.RestoreUnicodeTextWithSequenceGuard` (Windows, item 7c) does.
A related, self-inflicted variant of the same "looks atomic but isn't" bug turned up later in
the *same* Windows code: the restore retry loop called `EmptyClipboard()` — which itself bumps
the sequence counter regardless of whether the subsequent `SetClipboardData` succeeds — *before*
allocating/writing the replacement text, so a transient write failure (another clipboard manager
briefly holding the clipboard, exactly what the retry loop exists to absorb) could look like a
user-copy event and abandon the restore with the clipboard left genuinely empty. Fixed by
narrowing `EmptyClipboard()` to run only immediately before `SetClipboardData`, and treating any
failure after a successful `EmptyClipboard()` as immediately fatal rather than looping back into
an already-invalidated sequence check.

---

## `EVIOCGRAB` not implemented — trigger key leaks through on Linux, by deliberate choice

Reading `/dev/input/event*` does **not** suppress the key at the compositor level — the
focused app still sees it. The only real suppression mechanism, `EVIOCGRAB`, grabs the *entire*
device (not just the trigger key), meaning the implementation would have to re-emit every other
key on that device itself — invasive, and the plan explicitly says to test whether trigger-key
leakage is actually tolerable *before* building the grab path, since a botched grab can break
someone's keyboard.

**What this means in practice today:** `LinuxHotkeySource` accepts `HotkeyBinding.Suppress` but
never calls `EVIOCGRAB` — it logs a one-time startup warning that the trigger key **will leak
through** to whatever app has focus, pending spike S5. This was a deliberate scope decision (no
hardware existed to validate a grab implementation safely), not an oversight — do not read the
absence of `EVIOCGRAB` as "leaking is fine," only as "nobody has run S5 yet to find out whether
it's tolerable." If/when S5 runs and confirms leakage is intolerable for the chosen trigger key,
`EVIOCGRAB` becomes the next thing to build, with real hardware available to validate it against.

---

## Multi-keyboard enumeration is mandatory, not an edge case

A laptop in a dock routinely exposes 3–6 `EV_KEY`-capable nodes under `/dev/input`: the internal
keyboard, an external USB/Bluetooth keyboard, the ACPI power button, consumer-control/media
keys, and sometimes a virtual node from trackpad firmware. **Opening only `event0` silently
misses every keypress from whichever keyboard the user actually types on** — this isn't a rare
misconfiguration, it's the normal shape of a real docked laptop.

`LinuxHotkeySource`/`KeyboardDeviceEnumerator`/`KeyboardDeviceFilter` enumerate every
`/dev/input/event*` node, read its capability bitmask via `EVIOCGBIT`, and keep only nodes that
report `EV_KEY` **and** contain standard alphanumeric scancodes (`KEY_A`–`KEY_Z`) — the
alphanumeric test is specifically what filters out power buttons and media-key nodes, which also
claim `EV_KEY` but aren't real keyboards. All matching nodes are opened non-blocking and
multiplexed with a single `epoll` on one reader thread (not one thread per device). An `inotify`
watch on `/dev/input` triggers re-enumeration on hotplug — filtered to `event*`-named changes
specifically (an early version fired a full restart on *any* `/dev/input` directory change,
including irrelevant ones, fixed in item 11's review).

**Honest status:** the filtering/mapping/hotplug-event-parsing *logic* is unit-tested (pure
functions over synthetic data). The actual `open`/`ioctl`/`epoll_wait`/`inotify_*` syscalls this
relies on have never been executed against a real Linux kernel — see S5's status in
`SPIKE-RESULTS.md`.

---

## `ydotoold`/`/dev/uinput` setup requirements and `scripts/setup-linux.sh`

`ydotool`'s paste-chord/Unicode-synth injection requires a running `ydotoold` daemon with access
to `/dev/uinput` (the kernel's virtual-input-device interface) — neither exists by default on a
fresh install. `scripts/setup-linux.sh` handles the three one-time prerequisites the plan calls
a real deliverable, not an afterthought:

1. Add the user to the `input` group (`sudo usermod -aG input $USER`) — required to open
   `/dev/input/event*` nodes for evdev capture. Takes effect only on the next login session.
2. Install the udev rule granting `/dev/uinput` group access:
   `KERNEL=="uinput", GROUP="input", MODE="0660", OPTIONS+="static_node=uinput"`.
3. Install/enable a `ydotoold` user systemd unit.

The script prints red/green for each step and is idempotent (safe to re-run). **`LinuxTextInjector`
also probes for the `ydotoold` Unix domain socket on first injection attempt** (path from
`YDOTOOL_SOCKET`, falling back to the documented default `/tmp/.ydotool_socket`) and logs a
clear, actionable message pointing at this script if it's missing — per §1.12's error-handling
matrix row for exactly this case, rather than surfacing a cryptic process-launch/exit-code
failure later. This is a best-effort existence check, not a real connection probe (unlike
Windows' heartbeat, which sends a harmless real keystroke to self-test — doing the equivalent on
Linux was judged unsafe with no hardware available to confirm it's actually harmless there).

**Honest status: this script has never been executed.** It's written carefully against
documented Fedora/systemd/udev conventions, not against a real run — the script's own top
comment says so explicitly. Treat its verification pass as the real source of truth once someone
actually runs it, not this note.

---

## No Wayland "foreground window" concept — `CaptureTarget()` is always `null` on Linux

Windows' `GetForegroundWindow()` has no portable, security-model-respecting Wayland equivalent
callable from an unprivileged process. `LinuxTextInjector.CaptureTarget()` therefore always
returns `null` — injection always targets whatever the compositor currently has focused at
paste time, which is exactly the `targetLostPolicy: "current"` behaviour Windows already
defaults to (see `ARCHITECTURE.md`'s edge-case-2 note). No Wayland-specific window-tracking is
attempted; this was an explicit scope decision, not a gap waiting to be filled.

---

## Suppression: Windows has it, Linux (currently) doesn't

Windows' low-level keyboard hook (`SharpHook`, `SuppressEvent = true`) genuinely prevents the
trigger key from reaching the focused application — confirmed via `EventSimulator`-driven tests.
Linux evdev reading is passive; see the `EVIOCGRAB` section above. **If you're testing/debugging
on Linux and the trigger key appears to "type into" the focused app, that's the current expected
behaviour, not a regression** — it's the open question S5 exists to resolve.
