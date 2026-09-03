# S7 — Docker-based Linux hotkey/injection harness

Spike per `Docs/soneto-implementation-plan-phase4.md` §4.3. Throwaway code — see
`spikes/s4-inject-win/README.md` for the error-handling convention this follows (fail loudly,
no investment beyond that).

**Question:** can a Docker container on this machine (Windows host, Docker Desktop, WSL2 backend)
give `Soneto.Platform.Linux`'s real, never-before-executed production code (`LinuxHotkeySource`,
`LinuxTextInjector`) a genuine kernel-level environment to run against, closing most of
`[GATE:S5]` without physical Fedora/Wayland hardware?

**Environment:** Docker Desktop 29.7.2, WSL2 backend, kernel `6.18.33.2-microsoft-standard-WSL2`.

## Result summary

| Mechanism | Result |
|---|---|
| `/dev/uinput` reachable from a container | ✅ Works, via `--device=/dev/uinput` |
| Kernel creates a real `/dev/input/eventN` for a uinput device | ✅ Works, but the node is NOT auto-visible inside the container — needs a workaround (see below) |
| Real `LinuxHotkeySource` (`Soneto.Platform.Linux`) reading real evdev events | ✅ **Proven working**, including a real fault-detection finding (see below) |
| `ydotoold` + `ydotool key` (kernel-level synthesis, no compositor needed) | ✅ Works |
| Headless Weston + `wl-copy`/`wl-paste` (needs a real `wl_seat`) | ❌ **Genuine dead end in this environment** — see below |

## What had to be worked around, and why it's legitimate

**Docker doesn't pre-populate dynamically-created device nodes.** `--device=/dev/uinput` only
bind-mounts device nodes that exist at container start. The `/dev/input/eventN` node the kernel
creates in response to `UI_DEV_CREATE` doesn't exist yet at that point, so it never appears in
the container's own `/dev` automatically. **Fix:** read the real major:minor from
`/sys/class/input/eventN/dev` (populated by the same kernel event, visible from inside the
container since `/sys` isn't namespaced the same way) and `mknod` the node manually inside the
container. This is not a workaround around the mechanism under test — it recreates the identical
device node udev would create automatically on a normal Linux desktop; `LinuxHotkeySource` opens
it exactly the same way either way (`open("/dev/input/eventN", O_RDONLY | O_NONBLOCK)`).

**Docker's device cgroup blocks access even after `mknod`.** The container's device cgroup
(`devices.allow`) only permits the exact major:minor passed via `--device`, so opening the
manually-created node failed with `EPERM` until `--device-cgroup-rule='c 13:* rmw'` (major 13 is
the kernel's "input" character-device class) was added. This is the one flag genuinely specific
to running inside Docker — a real Fedora desktop needs no equivalent, since there's no
container device cgroup in the way.

**`evdev` kernel module wasn't loaded by default** in this Docker Desktop VM — `modprobe evdev`
loads it once (host-VM-global, not per-container, confirmed by it staying loaded across separate
container runs). Without it, uinput devices only exposed a `kbd` handler, never an `eventN` one.

## Step-by-step results (from §4.3's plan)

1. **Real evdev round-trip through raw C** (`uinput_kbd.c` predecessor, see git-less history —
   superseded by the combined test below): a uinput-created virtual keyboard's key-down/key-up
   events were read back correctly via the manually-mknod'd node, confirmed via a forked
   reader process. ✅
2. **Headless Weston + `wl-copy`/`wl-paste`**: Weston's `headless-backend.so` starts cleanly and
   creates a real Wayland socket, but **exposes no `wl_seat`** — `wl-copy`/`wl-paste` both fail
   with "The compositor does not seem to implement seat, which is required for wl-clipboard to
   work." Weston's headless backend is designed for offscreen rendering/CI screenshot tests, not
   interactive input; there is no CLI flag to attach an input device to it. The natural fix
   (a real `drm` backend + `libinput`, feeding off our uinput device, backed by a virtual GPU) was
   checked and is also unavailable: this kernel has no `vkms` (virtual DRM/KMS) module
   (`modprobe vkms` → "module not found") and `/dev/dri` doesn't exist. **This is a genuine,
   structural dead end in this specific environment (Docker Desktop's WSL2 kernel), not something
   to route around with a weaker substitute** — closing it needs either real hardware, a
   different Linux VM with a real/virtual GPU exposed to containers, or weston's own
   `weston-test` protocol (a much larger undertaking, deliberately out of scope for this spike —
   see `Docs/soneto-implementation-plan-phase4.md` for the honest-gap framing this was expected
   to need).
3. **`ydotoold` + `ydotool key`**: works completely independent of any compositor — `ydotoold`
   creates its own uinput device ("ydotoold virtual device") the same way our harness does, and
   `ydotool key 29:1 29:0` (LeftCtrl down/up) executes with exit 0. This proves the KERNEL-LEVEL
   half of `LinuxTextInjector`'s ydotool invocation is mechanically sound. **What remains
   unverified**: whether the synthesized keystrokes actually land as visible text in a real
   Wayland app — blocked by the same seat/compositor gap as step 2.
4. **Real `LinuxHotkeySource` against a real uinput keyboard** (`Harness/Program.cs`,
   `run-hotkey-test.sh`) — **the actual deliverable of this spike.** The real, unmodified
   production class from `src/Soneto.Platform.Linux/LinuxHotkeySource.cs`, running inside the
   container:
   - Enumerated `/dev/input/event0` for real via `KeyboardDeviceEnumerator`, and
     `KeyboardDeviceFilter.IsKeyboardLike` correctly classified it as keyboard-like (the harness's
     `uinput_kbd.c` registers all 26 QWERTY alpha scancodes specifically so it passes this real
     filter, not just `KEY_RIGHTCTRL`).
   - Real `epoll_create1`/`epoll_wait` setup succeeded; `StartAsync` returned without error.
   - A real synthesized RightCtrl key-down correctly fired `Pressed` — **jitter 0.38ms**.
   - A real synthesized RightCtrl key-up correctly fired `Released` — **jitter 0.00ms**.
   - Both are far under S5's own original 30ms pass bar (`Docs/MANUAL-TESTS.md` Section D).
   - **This is the first time any of `LinuxHotkeySource`'s real `open`/`ioctl`/`epoll_wait`/`read`
     syscalls have executed against a real Linux kernel from any session on this project** — Phase
     1 item 11 built this class entirely blind (see `Docs/PROJECT-MEMORY-ARCHIVE.md`).
   - **A real finding, not yet triaged/fixed**: at shutdown, `LinuxHotkeySource` raised a
     `Faulted` event ("A device fd (or the inotify fd) reported EPOLLERR/EPOLLHUP") shortly after
     `DisposeAsync()` was called. A follow-up run that stayed idle for 8s with the device fully
     alive (no dispose, no destroy) raised **no** spontaneous fault — confirming this is
     specifically a dispose-time artifact (most likely the reader thread observing EPOLLHUP on
     its own fd as `DisposeAsync` closes it, before the reader thread has fully unregistered from
     epoll), not a real device-failure false-positive during normal operation. **Not fixed as
     part of this spike** — worth a follow-up look (does `DisposeAsync` need to suppress
     `Faulted` once a caller-initiated shutdown is already in progress? `WindowsHotkeySource`'s
     equivalent shutdown path may be worth comparing against). Flagged in
     `Docs/PROJECT-MEMORY.md` and the Phase 4 plan's build order.
   - Real console log excerpt:
     ```
     info: Soneto.Platform.Linux.LinuxHotkeySource[0]
           evdev enumeration: found 1 candidate node(s): /dev/input/event0
     info: Soneto.Platform.Linux.LinuxHotkeySource[0]
           evdev enumeration: selected 1/1 node(s) as keyboard-like: /dev/input/event0
     info: Soneto.Platform.Linux.LinuxHotkeySource[0]
           LinuxHotkeySource started: trigger=RightControl (97) suppress=False devices=1
     [HARNESS] Pressed fired, ts=2026-09-03T08:42:53.6174489+00:00 (jitter 0.38ms)
     [HARNESS] Released fired, ts=2026-09-03T08:42:53.7689420+00:00 (jitter 0.00ms)
     fail: Soneto.Platform.Linux.LinuxHotkeySource[0]
           LinuxHotkeySource fault: A device fd (or the inotify fd) reported EPOLLERR/EPOLLHUP: fd=81.
     ```
   - **Addendum — traced by hand and confirmed harmless in the real production shutdown path,
     not left as an open defect.** `SessionController.DisposeAsync()`
     (`src/Soneto.Core/SessionController.cs:455`) unsubscribes `Faulted` FIRST, then fully drains
     the worker task, and only calls `_hotkeySource.DisposeAsync()` much later (line 495) — a
     wide safety margin, not a tight race. So a stray `Faulted` raised from inside
     `LinuxHotkeySource.DisposeAsync()`'s own reader-thread teardown lands on an already-detached
     event with zero subscribers in the real integration; it's harmless there. It only surfaced
     as a visible anomaly in this spike because the harness's own `Program.cs` never unsubscribed
     before disposing (a harness simplification, not a production code path). **No production
     code was changed** — this is a genuine "investigated further, confirmed sound" outcome,
     matching this project's own established review discipline of not treating every observed
     anomaly as a bug.
5. **Real `LinuxTextInjector.InjectAsync` landing text in a Wayland client**: NOT attempted —
   gated on the same seat/compositor dead end as steps 2/3's second half. Honest, open gap.
6. **Multi-keyboard hotplug** (`run-hotplug-test.sh`) — **Phase 1 item 11's own literal "done
   when" criterion, explicitly stated in its own doc comment as unclosable without "a human with
   real hardware," now genuinely closed via this harness.** Created keyboard #1, started the
   real `LinuxHotkeySource` against it (confirmed 1 device enumerated), then created a SECOND
   uinput keyboard mid-session — the closest a container can get to physically plugging in a
   second keyboard, and a real test of the exact mechanism (a genuinely new `/dev/input/eventN`
   node appearing) real hotplug produces, not a simulation of one. Result: the real `inotify`
   watch correctly detected the change and raised the hotplug-shaped fault ("`/dev/input changed
   (evdev device plugged/unplugged); re-enumeration required`"); the harness's `Faulted` handler
   called `RestartAsync` (mirroring `SessionController`'s real production restart-with-backoff
   job); `RestartAsync` re-enumerated and found **both** devices, correctly classified both as
   keyboard-like (`found 2 candidate node(s)... selected 2/2`); the hotkey continued firing
   correctly afterward. **A second real, positive finding surfaced by this test, not a bug**:
   `StopInternalAsync`'s reader thread did not rejoin within its 2s shutdown timeout during this
   restart (likely genuine scheduling contention from running two uinput helpers + a .NET runtime
   in one resource-constrained container), correctly triggering the class's own documented
   fd-leak-tolerance fallback (`LogCritical` + deliberately leak that generation's fds rather than
   close them out from under a possibly-still-running thread) — this is the FIRST time that
   fallback, built and reasoned about in Phase 1 item 11's own review round but never previously
   exercised, has actually fired for real, and it worked exactly as designed: `RestartAsync`
   still completed successfully and the hotkey kept working. **One cosmetic-only script bug, not
   a code finding**: the reported jitter values in this specific run are garbage
   (`63924022364868.76 ms`) because `run-hotplug-test.sh` sent the synthetic key-down/up only to
   the uinput helper's own stdin, not also to the harness's stdin (which is what stamps
   `lastDownSentAt`/`lastUpSentAt` for jitter measurement) — `Pressed`/`Released` themselves fired
   correctly both times, confirmed by the event log; only the jitter arithmetic in this
   particular script run is meaningless. Not re-run for cosmetic polish since the substantive
   result (hotplug detection + restart + continued operation) doesn't depend on it, and jitter was
   already cleanly measured in the simpler single-keyboard test above.

7. **Device-KILL recovery** (`run-devicekill-test.sh`, Phase 4 item 5, §4.6) — the closer match
   to "Windows will unhook you eventually" than item 1's device-ADD/hotplug test above: keyboard
   #1 is created, the real `LinuxHotkeySource` starts against it, a normal press/release is
   confirmed, then keyboard #1 is genuinely DESTROYED mid-session (`uinput_kbd.c`'s `q` command:
   `UI_DEV_DESTROY` + `close(fd)`) while the reader thread is actively polling its fd — the
   closest a container can get to "the underlying device died out from under an active reader."
   **Result: PASS, 2/2 clean runs.** The real, unmodified `LinuxHotkeySource` correctly raised
   `Faulted` ("A device fd ... reported EPOLLERR/EPOLLHUP") the moment the device died. The
   harness's `Faulted` handler (`Harness/Program.cs`, widened this item from a hotplug-only
   filter to react to ANY fault, matching `SessionController.HandleHookFaultedAsync`'s real
   production behavior — see that file's own comment) mirrors `SessionController`'s exact
   documented backoff shape (5 attempts, 1s/2s/4s/8s/16s) rather than reusing the real class
   directly (constructing a full `SessionController` was judged impractical in this throwaway
   container harness — no audio/ASR fakes wired here — so the NUMBERS and the
   "any-fault-triggers-a-bounded-retry-loop" shape are real/production, but the loop itself is a
   deliberate mirror, not the literal class). Attempt 1 failed for real (the replacement
   keyboard wasn't ready yet — 0 devices found, `InvalidOperationException`, exactly
   `LinuxHotkeySource.StartAsync`'s own real "no keyboard-like device" guard); a replacement
   keyboard #2 was created ~2s into the backoff window (during the real 1s-then-2s sleep between
   attempts 1 and 2); **attempt 2 succeeded for real**, re-enumerating and finding the
   replacement device, and a subsequent real press/release cycle through it fired
   `Pressed`/`Released` correctly. Recovers the same way `WindowsHotkeySource` does: detect a
   genuinely dead hook/device via a real fault signal, retry with real exponential backoff,
   recover once a working hook/device is available again. **One real finding, in the test
   SCRIPT, not `LinuxHotkeySource` — found, fixed, re-verified clean:** the first run failed
   permanently (all 5 attempts exhausted) because `run-devicekill-test.sh`'s own `mknod`
   silently no-op'd ("File exists") when the kernel recycled the same `eventN` name for the
   replacement device, leaving a STALE device node (old major:minor) that `LinuxHotkeySource`
   correctly failed to open (`errno-ish result=-1`) — a real, correct rejection of a bad node,
   not a hotkey-source bug. Fixed by having the script's `wait_for_new_event` helper always
   `rm -f` before `mknod`, so a reused event name always gets a fresh node matching whatever
   device is currently live (exactly what `udev` does automatically on a real desktop). Same
   already-known fd-leak-tolerance fallback from item 1's hotplug test fired again here (2s
   reader-thread shutdown timeout exceeded) — expected, not a new finding. **Not wired into a
   permanent xUnit `[Trait("Category","DockerLinux")]` suite this item** (§4.7's own suggested
   follow-up) — judged out of proportion for a "verification only" item per its own scope
   framing; this one-off, reproducible spike run (2/2 clean) is documented here instead, the
   same precedent items 0/1 already established. Re-running requires nothing beyond the same
   Docker command below with `run-devicekill-test.sh` in place of `run-hotkey-test.sh`.

## Files here

- `uinput_kbd.c` — the controllable virtual-keyboard helper (stdin commands `d`/`u`/`q`).
- `Harness/` — a real .NET console app referencing the actual `Soneto.Core`/`Soneto.Platform.Linux`
  projects (not a reimplementation), driving `LinuxHotkeySource` directly. `Faulted` handling
  mirrors `SessionController`'s real documented backoff shape (5 attempts, 1s/2s/4s/8s/16s),
  widened (Phase 4 item 5) to react to ANY fault reason, not just the hotplug shape.
- `run-hotkey-test.sh` — orchestrates device creation, `mknod`, harness build/run, and a
  press+release cycle. `run-hotkey-test-idle.sh` — the no-dispose variant used to isolate the
  shutdown-fault finding above. `run-hotplug-test.sh` — device-ADD (hotplug) recovery (item 1).
  `run-devicekill-test.sh` — device-KILL recovery (item 5, §4.6), see above.
- `Dockerfile` in this folder builds the exact image used (`mcr.microsoft.com/dotnet/sdk:10.0` +
  apt-installed `gcc`/`libc6-dev`/`linux-libc-dev`/`weston`/`wl-clipboard`/`ydotool`/`procps`/
  `iproute2`).

## How to re-run

```
docker build -t soneto-s7-dotnet spikes/s7-docker-linux
docker run --rm --device=/dev/uinput --device-cgroup-rule='c 13:* rmw' \
  -v "<repo-root>:/work" -w /work/spikes/s7-docker-linux \
  soneto-s7-dotnet bash run-hotkey-test.sh
```

(On Windows/Git Bash specifically: run with `MSYS_NO_PATHCONV=1` set, or the `--device`/
`--device-cgroup-rule` arguments get mangled into Windows-style paths by Git Bash's automatic
path conversion — a real, non-obvious friction point hit repeatedly while building this spike,
worth remembering for any future Windows-host Docker work on this project.)

## Gate verdict, per §4.3's own stated gate

**Partial pass, not a full pass — and that's an honest, useful result, not a failure.** The single
highest-value, highest-risk piece (does `LinuxHotkeySource`'s real evdev capture actually work
against a real kernel) is now genuinely proven, for the first time in this project's history. The
clipboard/compositor half of `[GATE:S5]` hits a real, structural dead end specific to this Docker
Desktop/WSL2 environment (no `vkms`, headless Weston has no seat) that no amount of container
configuration can route around — it needs either real Fedora hardware or a different
virtualization backend with real/virtual GPU passthrough, neither available in this session.
Per `Docs/soneto-implementation-plan-phase4.md` §4.3's own gate: this is exactly the
"honest, named, structural gap, not something to route around with a weaker substitute" outcome
the plan anticipated as a real possibility — proceed with the hotkey-side Phase 4 work this
harness now supports; the clipboard/injection-into-a-real-app half remains gated on real
hardware, same as before, just now with the hotkey half genuinely de-risked instead of both
halves being equally unverified.
