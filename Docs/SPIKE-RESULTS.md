# Soneto — Spike Results (Phase 0)

Consolidated, onboarding-first reference for every Phase 0 spike's actual measured numbers.
Per plan §1.15, every future session working on this project should read this file (and
`ARCHITECTURE.md`) first. This is a synthesis, not a replacement — the full narrative for
each spike still lives in `Docs/soneto-implementation-plan-phase0-1.md` (inline `**Status:**`
sections under each spike) and `Docs/PROJECT-MEMORY.md`/`CHANGELOG.md`; this file exists so
you don't have to reconstruct the numbers from three different documents every time.

**Source of truth note:** every number below is copied verbatim from the primary sources
listed above, not paraphrased from memory. If a number here and the plan doc's own inline
status ever disagree, trust the plan doc/PROJECT-MEMORY and treat this file as stale.

---

## S1 — ASR latency and correctness — ✅ Green (2026-08-31)

**Question:** does Parakeet v3 int8 run fast enough, in-process, from C#?

- **Warm decode, 5s clip:** ~200–270 ms (bar: < 400 ms). Independently reproduced on a
  second machine.
- **Cold model load:** ~1.4–1.75 s (informational, not a gate; within the 2–4 s expectation).
- **Thread sweep (2/4/8/16 threads):** knee at **4 threads**, not 8 as the plan's own example
  originally assumed. 16 threads was worst and occasionally exceeded the 400 ms bar. **This
  is the seed for §1.6's `numThreads` default — carried forward as `NumThreads=4` everywhere
  in the product** (`Soneto.Core.Configuration.AsrConfig`, `SessionController`'s composition,
  every corpus/stress test's transcriber construction). Re-verify on the actual target laptop
  CPU if it ever differs from the Ryzen 7 7700X dev desktop this was measured on.
- **Long-utterance (60 s / 120 s) decode:** no truncation or garbage on synthetic
  (TTS-generated) long clips; RTF ~0.05–0.08. **Caveat, still open:** never validated on real
  recorded (non-TTS) long speech — only synthetic clips.
- **`OfflineRecognizerConfig` shape confirmed** against the real installed package (1.13.5)
  via reflection — matches the plan doc's example property-for-property, no changes needed.
  (This confirmation is why item 3's implementation could proceed directly from the plan's
  example rather than needing its own discovery pass for the transducer/model-type fields —
  though item 3 still had to separately reflect the VAD config shape in item 5, since the plan
  gives no VAD example.)

**Not yet done:** real-recorded (non-TTS) long-utterance validation; thread-knee re-check on
the real target laptop.

## S1b — Audio stream open latency and resampling `[GATE]` — ✅ Green (2026-08-31)

**Question:** with on-demand capture, how long between key-down and the first real audio sample?

**Gate decision: `OnDemand` stays the default capture mode.** Default mic (MME) time-to-first-sample:
**p50=56ms, p95=58ms** (bar: < 150 ms) — comfortably under. Confirmed on a second run.

- **Real hardware finding, not a hypothetical:** the same physical mic accessed via WASAPI or
  WDM-KS delivered all-zero buffers on every trial (20/20) — the stream opens and reports
  "started" but never delivers real audio, timing out rather than failing fast. Onboard
  Realtek WDM-KS devices additionally errored on `Pa_StopStream`. **A paired Bluetooth
  headset failed `Pa_StartStream` outright on every trial** — never produced a single timing
  measurement. Root cause not identified (plausibly a Windows mic-privacy/exclusivity issue),
  but concrete, reproduced evidence that `WarmIdle` is a real near-term need for
  Bluetooth-in-the-mix users, not a theoretical fallback.
- **Resampler validated at ~144 dB alias suppression** on both 48000→16000 (exact 3:1) and
  44100→16000 (147:160 awkward ratio), far above the 40 dB bar. Output-length correctness
  (off-by-one at buffer boundaries) explicitly tested and passes for both ratios.
- **Correction to the plan's own spec (§1.5), confirmed by independent math check:** the
  "32-tap" resampler figure is physically wrong for the stated 7.8 kHz cutoff / 8 kHz Nyquist
  — a 32-tap Blackman-windowed-sinc filter has a ~8.25 kHz transition band, wider than the
  entire output Nyquist band, so it cannot produce any real stopband before 8 kHz. **Correct
  tap count: ~1200–1300 taps per phase** for a single-stage design (per the standard Blackman
  transition-width formula, N ≈ 5.5·Fs/ΔF). Item 4's product implementation later confirmed
  this again independently: **1321 taps/phase at 48 kHz, 1213 taps/phase at 44.1 kHz.**
- **Known limitation from the spike, since resolved in the product (item 4), not just
  documented:** the spike's `Convolve()` zero-pads at buffer edges, causing a ~14ms edge-taper
  artifact at the start/end of every whole-buffer resample call — which would have repeated
  roughly every 10ms throughout a real dictation under a naive stateless port. Item 4's
  product resampler is **stateful/streaming** instead (maintains filter history across calls),
  so zero-padding only ever happens once, at the true start/end of an utterance stream.
- **Explicitly deferred:** the S2-corpus resampled-vs-native WER A/B comparison — S2 doesn't
  exist (see below), so this specific comparison hasn't run either. Re-run once S2's corpus
  exists.

**Not yet done:** root-causing the WASAPI/WDM-KS silent-capture and Bluetooth start failures;
testing a genuine external USB mic (none was available); the deferred S2 WER A/B.

## S2 — Romanian accuracy on your voice `[GATE]` — ⏭️ Deferred by user decision (2026-08-31)

**Question:** is Romanian usable for the user's own speech and vocabulary? (Stated in the
plan as "the highest-value experiment in the project.")

**Status: not run.** The user explicitly decided (2026-08-31) to proceed on the assumption
Romanian accuracy is acceptable for now, without running S2's actual WER measurement, and to
prioritize English. This is a **deliberate scope decision, not a data-backed gate pass** — see
`Docs/PROJECT-MEMORY.md`'s "Key decisions locked in so far" for the full rationale and the
exact wording of the decision. Revisit before actually shipping/relying on Romanian dictation
for real use, and before building the language-profile work that depends on this gate's real
WER numbers (build plan §7.2, S2's own pass-criteria/interpretation table).

**Does not block:** Phase 1 (the daemon is language-agnostic at the ASR layer regardless of
this decision) or English-only usage. **Does block:** the language-profile work, and — the
direct concern of this work item — the corpus regression test's actual assertion (see
`tests/Soneto.Corpus/README.md` and `Docs/soneto-implementation-plan-phase0-1.md` §S2/§1.13
for what's built and waiting vs. what's genuinely blocked).

## S3 — Windows global hold-to-talk — 🟡 Automatable parts green (2026-08-31); manual verification pending

**Question:** can you capture press and release globally and stop the key reaching the focused app?

**Self-verified, with real numbers** (via SharpHook's own `EventSimulator`, real `SendInput`
events, not mocked):
- **Timestamp jitter:** p50 ≈ 0.2–0.5ms, p95 ≈ 0.5–1.9ms, max < 3.4ms (bar: < 20 ms) — passes
  by more than an order of magnitude.
- `SuppressEvent` correctly set for both DOWN and UP on the trigger key, every time (never one
  without the other, per the plan's rule).
- `listen` mode confirmed end-to-end cross-process (a separate `simulate-trigger` process
  fires a real synthetic press; `listen` logs correctly paired DOWN/UP).

**Two real findings that changed Phase 1's design, both independently reproduced across 4 runs:**

1. **The block-callback failure mode is not what the plan's original wording implied.** The
   plan said "confirm Windows unhooks you" when the callback is blocked 2s. What actually
   happens, deterministically every time: the hook **stays alive** and dispatches new
   DOWN/UP events normally within ~300ms of the callback unblocking — but the one key-up event
   that arrived *while the callback was busy* is **silently and permanently dropped**, never
   delivered, not merely delayed. Net effect: an orphan DOWN with no matching UP, while the
   hook itself keeps working. **Consequence:** a watchdog based only on hook liveness/heartbeat
   would miss this failure mode entirely, since the hook reports healthy the whole time. §1.4's
   `maxDurationMs` force-finalize timer on the DOWN event is the actual, necessary defense —
   confirmed necessary, not optional. A true full-unhook was never observed in this dev
   environment (worth a retest on the real target machine).
2. **Generic `VK_CONTROL` is ambiguous with the trigger key itself.** With Right Ctrl as the
   trigger, reading generic `VK_CONTROL` during a trigger press always reports "held" —
   demonstrated directly: during a trigger press, `VK_CONTROL` = True, `VK_LCONTROL` = False.
   §1.8's modifier sanitiser **must key off `VK_LCONTROL` specifically**, never generic
   `VK_CONTROL`, or it will falsely believe the user is holding Ctrl on every single dictation.
   Carried forward into item 6 (hotkey source) and generalized to Shift/Alt/Win in item 7b
   (modifier sanitiser).

**Still needs manual/human verification** (documented step-by-step in `spikes/s3-hotkey-win/README.md`):
press/release detected with no leak into Notepad/VS Code/Chrome/Windows Terminal; 60-second
physical hold; holding physical Shift while pressing the trigger; observing a leaked orphan
key-up in a real app; 30-minute idle survival; lock/unlock cycle survival; retest of the
block-callback finding on the real target machine.

## S4 — Windows injection matrix — 🟡 Core algorithm green (2026-08-31); per-app matrix needs a clean manual re-run

**Question:** does clipboard-paste injection land correct text, including diacritics, in every app you actually use?

**Core injection algorithm fully self-verified, real evidence:**
- **Diacritics confirmed correct at the byte level:** comma-below Unicode (U+0219/U+021B),
  not cedilla forms (U+015F/U+0163) — via Notepad self-check with programmatic read-back. This
  is the exact "check the bytes, not the glyph" test the plan calls for.
- **All three required adversarial cases pass with real evidence:** held-Shift correctly
  suppressed and never left stuck; the clipboard sequence-number guard correctly aborts
  restore when the user copies something during the restore window (confirmed genuinely
  atomic after a fix, see below); non-text clipboard content (an image) correctly skips
  restoration under `textOnly` policy rather than destroying it.
- **Latency, two numbers that mean different things:** felt latency (`timeToPasteSent`,
  time until the paste keystroke is actually dispatched) is **~35–47ms** — comfortably fast.
  The literal "elapsed" number (including the mandatory 150ms post-paste
  clipboard-restore-delay wait) sits right at/slightly over the 200ms bar (**182–211ms
  observed**). **This is not a real miss** — the extra time is a background safety wait that
  happens after the user already sees their text land — but it did reveal a real inconsistency
  in the plan's own numbers: §4's latency budget (50–120ms for the whole clipboard+paste+restore
  stage) doesn't leave room for §1.10's default 150ms `clipboardRestoreDelayMs`. Flagged for
  Phase 1, not resolved by the spike itself; Phase 1's implementation ships the 150ms default
  as documented and treats the felt-latency number as the one that actually matters to the user.

**Two real bugs found by code review and fixed, both in the safety-critical clipboard-restore
path (§1.8 step 11) — carried forward into the product (items 7/7c), not just fixed in the spike:**
1. **Restore failure was silently swallowed and misreported** — a failed retry loop still
   logged "restored" as if it succeeded. Fixed with a distinct `RestoreFailed` outcome.
2. **A genuine TOCTOU race** existed between the sequence-number check and the actual restore
   write (separate, non-atomic operations in the plan's own §1.8 pseudocode — a gap in the
   plan, not something the spike introduced). A user's Ctrl+C landing in that gap wouldn't
   have been caught. Fixed with a real atomic implementation: the sequence check now happens
   while holding the clipboard open, in the same critical section as the write. **This exact
   pattern (open-check-write-close as one atomic block) is what item 7c's
   `ClipboardManager.RestoreUnicodeTextWithSequenceGuard` implements in the real product** —
   not the plan's literal pseudocode structure, which has the race built in.

**Important operational finding, not a code bug:** the automated per-app launch matrix
(`launch all`) has an unreliable foreground-window-detection bug (trusts `GetForegroundWindow()`
immediately after launching each app without verifying the window actually belongs to that
process). On a real, in-use desktop (a real VS Code project and a real signed-in Teams work
account were open), this produced **false-positive "Injected" results** — several profiles'
pastes actually landed on a stale leftover window. Caught only by inspecting screenshots. **No
real content was affected** (verified: no lingering drafts, nothing overwritten/sent), but this
is a genuine near-miss: automated multi-app UI injection testing against a live desktop is
riskier than the plan's spike section implies. **Only Notepad (programmatic verification) and
Chrome-textarea (screenshot-verified) are confirmed from that run** — VS Code, Chrome address
bar, Windows Terminal, Teams, Outlook, Word all still need a clean, human-supervised
`countdown`-mode re-test, one app at a time, on a desktop with other real accounts/documents
closed first.

**Not yet done:** clean per-app manual verification (6 of 7 apps); confirming whether Windows
Terminal needs `ctrl+shift+v` (inconclusive from the unreliable batch run); physically holding
Shift throughout a real injection (the automated test necessarily releases it mid-sequence).

## S5 — Fedora KDE Wayland input `[GATE]` — 🟡 Partially closed via S7 (2026-09-03), real hardware still needed for the rest

**Question:** can you do S3 and S4 on Wayland at all?

**Status: still not run on real hardware — but the hotkey-capture half is no longer purely
theoretical.** No real Linux/Wayland hardware has been available to any agent session working on
this project. Item 11 (`src/Soneto.Platform.Linux/`) built the corresponding production code
(evdev multi-keyboard capture, `wl-copy`/`ydotool` injection) **ahead of** this gate, by an
explicit user decision mirroring the S2 deferral pattern. **S7** (`spikes/s7-docker-linux/`, a
Phase 4 spike — see `Docs/soneto-implementation-plan-phase4.md` §4.3) then used a Docker
container with `uinput`-created virtual keyboards to give the REAL, unmodified
`LinuxHotkeySource` a genuine kernel input environment to run against for the first time: real
`open`/`ioctl(EVIOCGBIT)`/`epoll_wait`/`read` syscalls, real evdev enumeration and keyboard-like
filtering, a real synthesized press/release cycle correctly firing `Pressed`/`Released` with
0.38ms/0.00ms jitter (S5's own 30ms bar), **and real multi-keyboard hotplug** — Phase 1 item 11's
own literal "done when" criterion, previously stated as unclosable without physical hardware —
now genuinely closed: a second uinput keyboard created mid-session correctly triggered the real
`inotify`-based re-enumeration, `RestartAsync` picked up both devices, and the hotkey kept
working afterward, including a first-ever real exercise of the class's fd-leak-tolerance
fallback (worked exactly as designed). **What S7 could NOT close**, a genuine dead end in this
specific Docker Desktop/WSL2 environment (no `vkms` virtual GPU, headless Weston exposes no
`wl_seat`): `wl-copy`/`wl-paste` clipboard round-trips and confirming injected text actually
lands in a real Wayland app. Whether trigger-key leakage (no `EVIOCGRAB` suppression implemented)
is actually tolerable still needs real hardware, same as before S7. See
`spikes/s7-docker-linux/README.md` for full results, including a `Faulted`-during-`DisposeAsync`
observation traced by hand and confirmed harmless in the real `SessionController` integration
(not a bug -- `SessionController` unsubscribes long before disposing the hotkey source).

## S6 — Hotword biasing sanity check — ⬜ Not started

**Question:** is `modified_beam_search` on Parakeet v3 usable, or does it hallucinate as reported?

**Status: not run.** Needs S2's corpus to exist first (S6's method is "run the full S2 corpus
twice — once greedy, once beam search with a hotwords file — and compare"), so it's
transitively blocked on the same S2 deferral above. `SonetoConfig.AsrConfig.HotwordsEnabled`
defaults to `false` pending this measurement, per the plan's stated default ("biasing is
opt-in/experimental pending S6, due to a known NeMo TDT hallucination issue").
