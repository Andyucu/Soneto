# Soneto — Implementation Plan: Phase 0 (Spikes) + Phase 1 (Headless Daemon)

**Companion to:** `dictation-app-build-plan.md`
**Covers:** everything from empty repo to a daemon you dictate with daily. No UI, no dictionary engine.
**Model:** `parakeet-tdt-0.6b-v3`, int8 ONNX, via sherpa-onnx C# bindings.
**Targets in this phase:** Windows 11 (primary), Fedora KDE Wayland (parity by end of Phase 1).

---

## 0. Gates and conditional sections

Two spikes can invalidate parts of this document. Sections that depend on them are tagged:

| Tag | Depends on | If the spike fails |
|---|---|---|
| `[GATE:S2]` | Romanian accuracy acceptable | Language profile work is dropped; app becomes EN-first with RO as best-effort |
| `[GATE:S5]` | Wayland input works | Linux target drops to X11-session-only, or defers to Phase 4 |

Do not build a tagged section before its gate is green. Everything untagged is safe to build immediately.

---

# PHASE 0 — SPIKES

Throwaway code. One spike per session. Each has a numeric pass criterion, not a vibe.
Put all spike code in `spikes/` and delete it at the end of the phase. Do not let spike code become product code — it will not have the error handling you need and you will not go back and add it.

---

## S1 — ASR latency and correctness

**Status:** ✅ Green (2026-08-31). Implementation in `spikes/s1-asr/`, results in `spikes/s1-asr/README.md`.

- Warm decode, 5s clip: ~200–270 ms (pass, < 400 ms bar), independently reproduced on a second machine.
- Cold model load: ~1.4–1.75 s (informational, within 2–4 s expectation).
- Thread sweep: knee at **4 threads**, not 8 as this doc's example assumed — 16 threads was worst and occasionally exceeded the 400 ms bar. Carry `NumThreads=4` forward as the default seed for §1.6.
- Long-utterance (60s/120s): decoded without truncation or garbage on synthetic long clips; RTF ~0.05–0.08. Caveat: not yet validated on *real* recorded long speech (only TTS-synthesized clips) — re-check before fully trusting the "no >30s degradation" conclusion.
- `OfflineRecognizerConfig` shape confirmed against the actual installed package (1.13.5) via reflection — matches this doc's example exactly, no changes needed.
- Config property names, exact API surface, and full numbers are in `spikes/s1-asr/README.md`.

**Not yet done:** real-recorded (non-TTS) long-utterance validation; thread-knee re-check on the actual target laptop CPU if it differs from the dev desktop used here.

**Question:** does Parakeet v3 int8 run fast enough, in-process, from C#?

**Deliverable:** `spikes/s1-asr/` — console app, WAV path in, text + timings out.

**Method:**
1. `dotnet new console`, add `org.k2fsa.sherpa.onnx` (1.13.5 or later).
2. Download `sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8` from the sherpa-onnx model releases. Extract to `models/`.
3. Build an `OfflineRecognizer` once. **Verify the exact config property names against the installed package** — the shape below is from the documented API but confirm before building the rest of the project on it:

```csharp
var config = new OfflineRecognizerConfig();
config.ModelConfig.Transducer.Encoder = Path.Combine(dir, "encoder.int8.onnx");
config.ModelConfig.Transducer.Decoder = Path.Combine(dir, "decoder.int8.onnx");
config.ModelConfig.Transducer.Joiner  = Path.Combine(dir, "joiner.int8.onnx");
config.ModelConfig.Tokens             = Path.Combine(dir, "tokens.txt");
config.ModelConfig.ModelType          = "nemo_transducer";
config.ModelConfig.NumThreads         = 8;
config.ModelConfig.Debug              = 0;
config.DecodingMethod                 = "greedy_search";
```

4. CLI: `s1-asr <wav> [--repeat N] [--threads N]`.
5. Emit, as CSV: `modelLoadMs, decodeMs (each iteration), audioDurationSec, rtf, text`.

**Pass criteria:**
- Warm decode of a 5-second 16 kHz mono clip: **< 400 ms**
- Cold model load: recorded (expect 2–4 s) — informational, not a gate
- Text is correct and **includes punctuation and capitalisation** with no post-processing

**Also measure:** thread count sweep (2/4/8/16) on your actual CPU. Pick the knee, not the max — more threads past the knee costs power for nothing. Record the result; it becomes the `numThreads` default.

**Also measure — long-utterance behaviour.** Decode a 60 s and a 120 s clip. Check for truncation, garbage tails, and peak memory. The sherpa-onnx/onnx-asr docs warn the practical single-shot limit is ~20–30 s for these models. If quality degrades past 30 s, VAD segmentation becomes a Phase 1 requirement rather than a Phase 2 nicety — see §1.5.

**Fails if:** warm decode > 800 ms. Then re-check you aren't reloading the recognizer per call, then try the fp16 or non-quantised build, then reconsider GPU EP.

---

## S1b — Audio stream open latency and resampling `[GATE]`

**Status:** ✅ Green (2026-08-31). Implementation in `spikes/s1b-audio/`, results in `spikes/s1b-audio/README.md`.

**Gate decision: `OnDemand` stays the default.** Default mic (MME) time-to-first-sample: p50=56ms, p95=58ms — comfortably under the 150ms bar. Confirmed independently on a second run.

- **Real, reproducible hardware finding:** the same physical mic accessed via WASAPI or WDM-KS delivered all-zero buffers on every trial (20/20) — timed out rather than failing fast. Onboard Realtek WDM-KS devices errored on `Pa_StopStream`. A paired Bluetooth headset failed `Pa_StartStream` outright, every trial — never got a timing measurement at all. Not root-caused (plausibly a Windows mic-privacy/exclusivity issue), but concrete evidence for the plan's own interpretation table: Bluetooth-in-the-mix means `WarmIdle` is the real answer for that device class, not a hypothetical.
- **Resampler validated, ~144dB alias suppression** on both 48000→16000 (exact 3:1) and 44100→16000 (147:160), far above the 40dB bar. Output-length correctness (off-by-one at buffer boundaries) also now explicitly tested and passes for both ratios.
- **Correction to this doc's own spec, found and confirmed by independent math check:** the "32-tap" figure below and in §1.5 is wrong for the stated 7.8kHz cutoff / 8kHz Nyquist — a 32-tap Blackman-windowed-sinc filter has a transition band of ~8.25kHz (wider than the whole output Nyquist band), so it cannot produce any real stopband before 8kHz. The correct tap count for a single-stage design at this cutoff is ~1200–1300 taps (verified against the standard Blackman transition-width formula, N ≈ 5.5·Fs/ΔF). **Carry this forward to Phase 1 item 4** — either budget for ~1300 taps per phase, or evaluate a multi-stage/cascaded halfband decimation design (cheaper compute for the same stopband, especially for the exact 3:1 case), rather than building to the "32-tap" figure literally.
- **Known limitation, not yet validated:** `Convolve()` zero-pads at buffer edges, causing a ~14ms edge-taper artifact at the start/end of every whole-buffer resample call. Plausibly harmless in practice since VAD trims leading/trailing silence anyway (§1.5), but this interaction needs to be tested against the real Soneto.Core implementation, not assumed.
- **Explicitly deferred:** the S2-corpus resampled-vs-native WER A/B — S2 doesn't exist yet. Re-run this specific comparison once S2's corpus is recorded.

**Not yet done:** root-causing the WASAPI/WDM-KS silent-capture and Bluetooth start failure; testing on a genuine external USB mic (none was available); the deferred S2 WER A/B.

**Question:** with on-demand capture, how long between key-down and the first real audio sample? This number is now on the critical path and the whole UX depends on it.

**Deliverable:** `spikes/s1b-audio/` — opens a capture stream, timestamps every phase, writes a WAV, closes.

**Method:**
1. For each available input device: log `defaultSampleRate`, supported formats, host API (WASAPI / WDM-KS / ALSA / PulseAudio / PipeWire).
2. Time separately: `Pa_OpenStream`, `Pa_StartStream`, and time-to-first-non-zero-buffer. That third number is the one that matters — drivers happily return "started" before they deliver audio.
3. Repeat 20× per device to get a distribution, not one lucky sample. Report p50 and p95.
4. Test devices you actually use: built-in laptop mic, USB mic/headset, **and a Bluetooth headset if you ever intend to dictate on one**.
5. Implement the polyphase resampler and validate: generate a 48 kHz sweep from 100 Hz to 20 kHz, resample to 16 kHz, FFT the result, confirm no aliased energy above 8 kHz. Then A/B the S2 corpus resampled 48→16 against natively-captured 16 kHz and compare WER.

**Pass criteria:**
- Time-to-first-sample p95 **< 150 ms** on your primary mic
- Resampled WER within 1% absolute of native 16 kHz WER
- No aliasing above 8 kHz in the sweep test

**Interpretation:**
- p95 under 150 ms → `OnDemand` is comfortable; ship it as the default.
- p95 150–400 ms → `OnDemand` works but the ready cue is essential and you will occasionally clip. Consider `WarmIdle` as the default instead.
- p95 > 400 ms, or Bluetooth in the mix → `OnDemand` will be actively annoying on that device. `WarmIdle` is the answer, or pin a specific always-available device.

---

## S2 — Romanian accuracy on your voice `[GATE]`

**Status:** ⏭️ Deferred by user decision (2026-08-31). Not run — the user has decided to assume Romanian accuracy is acceptable for now and prioritize English. This gate is **not resolved with data**, just deliberately postponed: revisit before actually shipping/relying on Romanian dictation for real use, and before building the language-profile work that depends on this gate's actual WER numbers (§7.2 of the build plan, §S2's interpretation table). Does not block Phase 1 (the headless daemon is language-agnostic at the ASR layer either way) or English-only usage.

**Question:** is Romanian usable for *your* speech and *your* vocabulary? This is the highest-value experiment in the project.

**Deliverable:** `spikes/s2-corpus/` — 60 WAV files, a `reference.tsv`, and a WER score per language.

**Method:**

1. Write 30 English and 30 Romanian sentences. Not generic ones — **your actual dictation content**. Suggested mix per language:
   - 10 sentences of ordinary prose (email, Teams message)
   - 10 sentences dense with your technical vocabulary (webMethods, Trading Networks, Integration Server, keystore, Enterprise Gateway, GoAnywhere, AS2)
   - 5 short utterances, 2–4 words ("da, mulțumesc", "yes, done", "restart the service") — these stress language auto-detect hardest
   - 5 code-switched sentences ("am făcut deploy pe Integration Server și am restartat serviciul")
2. Record each at 16 kHz mono, normal speaking pace, your usual mic, your usual room. One file per sentence, named `ro-001.wav` etc.
3. `reference.tsv`: `filename \t language \t exact expected text`
4. Transcribe all with the S1 harness. Compute WER (Levenshtein on tokens after lowercasing, stripping punctuation) per file and per bucket.
5. Additionally record, per file, the **detected language** implied by the output script — you have no API for this, so classify the output text and compare to the reference language.

**Pass criteria:**

| Bucket | Target WER |
|---|---|
| EN prose | < 8% |
| RO prose | < 15% |
| EN technical | < 20% (dictionary will fix the rest) |
| RO technical | < 30% (dictionary will fix the rest) |
| Short utterances — **language identified correctly** | > 80% |
| Code-switched | informational only — expect this to be bad |

**Interpretation:**
- Prose WER within target and short-utterance language ID above 80% → build the language profile work as planned.
- Prose WER fine but language ID unreliable → keep the feature, but the profile-hint hotkeys (§ Phase 4) become mandatory rather than optional, and the HUD must show detected language.
- RO prose WER > 25% → **stop and reconsider.** Options: accept EN-only dictation, or evaluate `canary-1b-v2` (which *does* accept an explicit language token, at ~2× the parameters and latency). This would change the model decision, so find out now.

**Keep the corpus.** It becomes `tests/Soneto.Corpus/` and every future change gets regression-tested against it.

---

## S3 — Windows global hold-to-talk

**Status:** 🟡 Automatable parts green (2026-08-31); manual verification pending. Implementation in `spikes/s3-hotkey-win/`, results in `spikes/s3-hotkey-win/README.md`.

**Self-verified, with numbers (via SharpHook's own `EventSimulator`, real `SendInput` events, not mocked):**
- Timestamp jitter: p50 ≈ 0.2–0.5ms, p95 ≈ 0.5–1.9ms, max < 3.4ms across multiple runs — passes the < 20ms bar by more than an order of magnitude.
- `SuppressEvent` correctly set for both DOWN and UP on the trigger key in normal operation (never one without the other, per the plan's rule).
- `listen` mode confirmed end-to-end cross-process (a separate `simulate-trigger` process fires a real synthetic press, `listen` logs correctly paired DOWN/UP).

**Two real findings that change Phase 1 design, both independently reproduced and confirmed (not speculation):**

1. **The block-callback failure mode is not what the plan's wording implies.** The plan says "confirm Windows unhooks you" when the callback is blocked 2s. What actually happens (4 independent runs, deterministic every time): the hook **stays alive** — it dispatches new DOWN/UP events completely normally within ~300ms of the callback unblocking — but the one key-up event that happened to arrive *while the callback was busy* is **silently and permanently dropped**, never delivered at any point, not merely delayed. Net effect: an orphan DOWN with no matching UP, while the hook itself keeps working fine afterward. **Consequence for §1.4 edge case 3 ("key stuck down"):** a watchdog that only checks hook liveness (heartbeat/ping) will miss this failure mode entirely, since the hook reports itself healthy throughout. The `maxDurationMs` force-finalize timer on the DOWN event itself — already specified in §1.4 — is the correct and necessary defense; this finding validates it's necessary, not optional. A true full-unhook (zero further events at all) was NOT observed in this environment, contrary to the commonly-cited ~300ms `LowLevelHooksTimeout` unhook behavior — worth a retest on the real target machine.
2. **Generic `VK_CONTROL` is ambiguous with the trigger key itself.** Since the trigger is Right Ctrl, reading generic `VK_CONTROL` during/around a trigger press always reports "held" — it can't distinguish "the user is also physically holding some Ctrl key" from "the trigger key that's currently down is itself a Ctrl key." Demonstrated directly: during a trigger press, generic `VK_CONTROL` = True, `VK_LCONTROL` = False. **Consequence for §1.8's modifier sanitiser:** it must key off `VK_LCONTROL` specifically, not generic `VK_CONTROL`, or it will falsely believe the user is holding Ctrl on every single dictation.

**Also documented:** `SelfTest.cs`'s callback (timestamp read + field write + semaphore release, zero I/O) is the pattern to copy for Phase 1's `IHotkeySource`. `ListenMode.cs`/`BlockDemo.cs`'s synchronous `Console.WriteLine`-in-callback is a spike-only observability affordance and must NOT be carried into product code — the plan's "never does work" rule for the hook callback thread is non-negotiable.

**Still needs manual/human verification** (documented as a step-by-step script in `spikes/s3-hotkey-win/README.md`, cannot be automated from an agent environment):
- Press/release detected while Notepad, VS Code, Chrome, Windows Terminal have focus, with **no character or modifier effect** reaching them (the actual suppression-into-a-real-GUI-app test).
- 60-second physical hold of the trigger key.
- Holding physical Shift while pressing the trigger, on real hardware.
- Observing the effect of a leaked orphan key-up (`--leak-keyup` flag) on a real target app (terminal/IDE).
- 30-minute idle survival.
- Lock/unlock cycle survival.
- Retest of the block-callback finding on the actual target machine to check whether a true full-unhook is ever observed there (not seen in this dev environment).

**Question:** can you capture press and release globally and stop the key reaching the focused app?

**Deliverable:** `spikes/s3-hotkey-win/` — console app that logs `DOWN`/`UP` with timestamps.

**Method:** SharpHook, `SimpleGlobalHook`, subscribe `KeyPressed` / `KeyReleased`, set `SuppressEvent = true` for the trigger key. Trigger: Right Ctrl.

**Pass criteria:**
- Press/release detected with the target app focused (test with Notepad, VS Code, Chrome, Windows Terminal)
- **No character or modifier effect reaches the target app** — verify Right Ctrl doesn't break `Ctrl+`-anything in the target
- Timestamp jitter on the callback < 20 ms
- Hook survives 30 minutes idle and a lock/unlock cycle

**Also test:**
- Hold the key for 60 seconds. Confirm the hook doesn't get dropped. Then deliberately block the callback for 2 seconds and confirm Windows unhooks you — you need to see this failure mode once so you build the watchdog knowing what it looks like.
- **Suppress key-down but let key-up leak**, deliberately, once. Observe what a terminal and an IDE do with the orphan key-up. This is the bug you'll otherwise spend an evening on later; five minutes of seeing it now saves that.
- Hold **Shift** while pressing the trigger. Confirm you can read the held-modifier state correctly via `GetAsyncKeyState` — this is the input to the sanitiser in §1.8.

---

## S4 — Windows injection matrix

**Status:** 🟡 Core algorithm green (2026-08-31); per-app matrix needs a clean manual re-run. Implementation in `spikes/s4-inject-win/`, results in `spikes/s4-inject-win/README.md`.

**Core injection algorithm fully self-verified, real evidence:**
- Diacritics land correctly as comma-below Unicode (U+0219/U+021B), not cedilla forms — the exact byte-level check the plan calls for, confirmed via Notepad self-check with programmatic read-back.
- All three required adversarial cases pass with real evidence: held-Shift correctly suppressed and not left stuck; the clipboard sequence-number guard correctly aborts restore when the user copies something during the restore window (independently confirmed as a genuinely atomic check after a fix — see below); non-text clipboard content (an image) correctly skips restoration under `textOnly` policy rather than destroying it.
- Latency: felt latency (time-to-paste-sent) is ~35-47ms, comfortably fast. The literal "elapsed" number (including the mandatory 150ms post-paste clipboard-restore-delay wait) sits right at/slightly over the 200ms bar (182-211ms observed) — **this is not a real miss**, the extra time is a background safety wait that happens after the user already sees their text land, but it does reveal a real inconsistency in the plan's own numbers: §4's latency budget (50-120ms for this whole stage) doesn't leave room for §1.10's default 150ms `clipboardRestoreDelayMs`. Flagged for Phase 1 to resolve (lower the delay, or make clear the budget excludes the async restore wait).

**Two real bugs found by code review and fixed, both in the safety-critical clipboard-restore path (§1.8 step 11):**
1. Restore failure was silently swallowed and misreported — if the retry loop failed, the code still logged "restored" as if it succeeded. Fixed: failure is now logged accurately and surfaced via a new `RestoreFailed` outcome.
2. A genuine TOCTOU race existed between the sequence-number check and the actual restore write (they were separate, non-atomic operations) — a user's Ctrl+C landing in that gap wouldn't have been caught. **This traces back to a gap in the plan's own §1.8 pseudocode, not something the spike introduced.** Fixed with a real atomic implementation: the sequence check now happens while holding the clipboard open, in the same critical section as the write. Carry this exact pattern (open-check-write-close as one atomic block) into Phase 1's `Soneto.Core` implementation — don't copy the plan's literal pseudocode structure verbatim, it has this race built in.

**Important operational finding, not a code bug:** the automated per-app launch matrix (`launch all`) has an unreliable foreground-window-detection bug — it trusts `GetForegroundWindow()` immediately after launching each app without verifying the returned window actually belongs to the just-launched process. On a real, in-use desktop (this one had a real VS Code project and a real signed-in Teams work account open), this produced **false-positive "Injected" results** — several profiles' pastes actually landed on a stale leftover window, not the intended target. Caught only by inspecting screenshots, not by trusting the return value. **No real content was affected** (verified: no lingering drafts, no evidence anything was overwritten or sent), but this is a genuine near-miss worth internalizing: automated multi-app UI injection testing against a live desktop is riskier than the plan's spike section implies. **Only Notepad (programmatic verification) and Chrome-textarea (screenshot-verified) are confirmed from that run.** VS Code, Chrome address bar, Windows Terminal, Teams, Outlook, and Word all need a clean, human-supervised re-test using the `countdown` mode (3s countdown, human Alt-Tabs to target) one app at a time — not the automated batch launcher — per the manual test script in the README.

**Not yet done:** clean per-app manual verification (6 apps); confirming whether Windows Terminal needs `ctrl+shift+v` instead of the default chord (plan flags this as a known possible quirk, inconclusive from the unreliable batch run); physically holding Shift throughout a real injection (the automated test necessarily releases it mid-sequence).

**Question:** does clipboard-paste injection land correct text, including diacritics, in every app you actually use?

**Deliverable:** `spikes/s4-inject-win/` — takes a string, injects into the currently focused window after a 3-second countdown.

**Method:** save clipboard → set Unicode text → `SendInput` Ctrl+V → wait 150 ms → restore.

**Test string (use exactly this):**
```
Ăsta e un test: șoseaua Ștefan cel Mare, țară, îngheț — 100% "quoted" & <tagged>.
Line two after a newline.
```

**Test matrix — all must pass:**

| App | Notes |
|---|---|
| Notepad | baseline |
| VS Code | Electron — the known problem case |
| Chrome (address bar + a textarea) | two different targets |
| Windows Terminal | paste may need Ctrl+Shift+V |
| Microsoft Teams | Electron, slow input handling |
| Outlook (desktop) | rich text target |
| Word | rich text, autocorrect may mangle |

**Pass criteria:**
- Diacritics correct in all seven (`ș` and `ț` must be U+0219 / U+021B, comma-below — check the bytes, not the glyph)
- Original clipboard restored in all seven
- Injection completes < 200 ms
- Record which apps need a non-default paste chord or extra delay → this becomes the seed of the per-app profile table

**Three adversarial cases, all must pass:**

1. **Hold Shift during injection.** Confirm the sanitiser prevents `Ctrl+Shift+V`, and confirm no modifier is left stuck afterwards — type a few characters immediately after and check they're not all uppercase.
2. **Copy something during the restore window.** Inject, and hit Ctrl+C in the target within the 150 ms delay. Confirm the sequence-number guard aborts the restore and your copy survives.
3. **Put an image on the clipboard first** (screenshot to clipboard), then inject. Confirm `textOnly` policy skips restoration rather than replacing the image with text, and that it logs why.

---

## S5 — Fedora KDE Wayland input `[GATE]`

**Status:** ⬜ Not started

**Question:** can you do S3 and S4 on Wayland at all?

**Deliverable:** `spikes/s5-linux/` — same two behaviours, Wayland session.

**Method:**
1. **Capture:** open `/dev/input/event*`, identify the keyboard device by capability bits, read `input_event` structs, filter `EV_KEY` for the trigger. Requires the user in the `input` group (`sudo usermod -aG input $USER`, re-login).
2. **Suppression:** evdev reading does *not* suppress — the compositor still sees the key. Options: `EVIOCGRAB` on the device (grabs everything from that keyboard, so you must re-emit the keys you don't want — invasive), or pick a trigger key that is harmless if it leaks. **Test whether Right Ctrl leaking is actually tolerable** before building the grab path.
3. **Injection:** `wl-copy` for clipboard, `ydotool key ctrl+v` for the paste. Requires `ydotoold` running and a udev rule for `/dev/uinput`:
   ```
   KERNEL=="uinput", GROUP="input", MODE="0660", OPTIONS+="static_node=uinput"
   ```
4. Repeat the S4 test string into: Kate, Konsole, Firefox, VS Code, Thunderbird.

**Pass criteria:**
- Press and release both detected, jitter < 30 ms
- Diacritics land correctly in all five apps
- Setup is scriptable — one `setup-linux.sh` and a re-login, no manual fiddling

**Fails if:** you can't get press/release cleanly, or the key leak is intolerable and `EVIOCGRAB` breaks your keyboard. Then: **X11 session fallback** (SharpHook + XTEST both just work), documented as a requirement, or Windows-only for v1. Decide explicitly and write it down — don't leave it ambiguous.

---

## S6 — Hotword biasing sanity check

**Status:** ⬜ Not started

**Question:** is `modified_beam_search` on Parakeet v3 usable, or does it hallucinate as reported?

**Deliverable:** a number.

**Method:** run the full S2 corpus twice — once `greedy_search`, once `modified_beam_search` with a 20-entry hotwords file of your technical vocabulary. Compare per-file.

**Record:**
- Empty-output rate under beam search
- Rate of outputs > 2× the greedy length (hallucination proxy)
- WER delta on the technical buckets
- Latency delta

**Interpretation:** if the empty/garbage rate is above ~2%, biasing stays off by default permanently and the post-processing dictionary carries everything. If it's clean and technical WER improves meaningfully, it becomes an opt-in setting with the fallback guard described in the build plan. Either way this is a 2-hour spike that settles a design question you'd otherwise argue with yourself about for weeks.

---

## Phase 0 exit checklist

- [x] S1 green — warm decode number recorded, thread count chosen, long-utterance behaviour known
- [x] S1b green — time-to-first-sample p95 recorded per device, capture mode default chosen, resampler validated
- [ ] S2 green or gate decision made and written down
- [ ] S3 green
- [ ] S4 green — per-app quirk list recorded
- [ ] S5 green or fallback decided and written down
- [ ] S6 measured — biasing default decided
- [ ] `docs/SPIKE-RESULTS.md` written, with actual numbers
- [ ] Corpus moved to `tests/Soneto.Corpus/`
- [ ] `spikes/` deleted

---

# PHASE 1 — HEADLESS DAEMON

## 1.1 Definition of done

A console daemon that starts, loads the model, registers a global hotkey, and for the rest of its life: hold Right Ctrl → speak → release → punctuated text appears at your cursor in under 600 ms. Config from JSON. Structured logs with per-stage timings. Survives a week of daily use without a restart.

**Explicitly out of scope for Phase 1:** any GUI, the dictionary engine, history storage, per-app profiles, language profiles, LLM polish, macOS.

---

## 1.2 Solution layout

```
soneto.sln
├── src/
│   ├── Soneto.Core/                  net10.0
│   ├── Soneto.Platform.Windows/      net10.0-windows
│   ├── Soneto.Platform.Linux/        net10.0
│   └── Soneto.Daemon/                net10.0   (console host)
└── tests/
    ├── Soneto.Core.Tests/            xunit
    └── Soneto.Corpus/                WAVs + reference.tsv (from S2)
```

**Package references:**

| Project | Packages |
|---|---|
| Soneto.Core | `org.k2fsa.sherpa.onnx`, `PortAudioSharp2`, `Microsoft.Extensions.Logging.Abstractions`, `System.Text.Json` |
| Soneto.Platform.Windows | `SharpHook`, `TextCopy` (or direct Win32 P/Invoke) |
| Soneto.Platform.Linux | none — raw P/Invoke + process calls to `wl-copy`/`ydotool` |
| Soneto.Daemon | `Microsoft.Extensions.Hosting`, `Serilog.Extensions.Hosting`, `Serilog.Sinks.File`, `Serilog.Sinks.Console` |

**Hard rule:** `Soneto.Core` references no platform project and no OS-specific API. `Soneto.Core.Tests` must pass on any machine with no audio device and no model file present.

---

## 1.3 Abstractions

```csharp
namespace Soneto.Core.Abstractions;

// ── Audio ────────────────────────────────────────────────
public interface IAudioCapture : IAsyncDisposable
{
    bool IsRunning { get; }
    event EventHandler<AudioLevelEventArgs>? LevelChanged;   // ~20 Hz, for later HUD
    Task StartAsync(AudioDeviceId? device, CancellationToken ct);
    Task StopAsync();
    /// Snapshot from (now - preRoll) to now, then keep appending until EndCapture.
    void BeginCapture(TimeSpan preRoll);
    /// Returns 16 kHz mono float32 in [-1, 1].
    ReadOnlyMemory<float> EndCapture();
    void AbortCapture();
}

// ── ASR ──────────────────────────────────────────────────
public interface ITranscriber : IAsyncDisposable
{
    bool IsReady { get; }
    Task InitializeAsync(CancellationToken ct);      // load + warm-up
    Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> samples16k, CancellationToken ct);
}

public sealed record TranscriptionResult(
    string Text,
    TimeSpan AudioDuration,
    TimeSpan DecodeTime,
    bool IsEmpty);

// ── Hotkey ───────────────────────────────────────────────
public interface IHotkeySource : IAsyncDisposable
{
    event EventHandler<HotkeyEventArgs>? Pressed;
    event EventHandler<HotkeyEventArgs>? Released;
    event EventHandler<HotkeyFaultEventArgs>? Faulted;   // hook died
    Task StartAsync(HotkeyBinding binding, CancellationToken ct);
    Task RestartAsync(CancellationToken ct);
}

public sealed record HotkeyBinding(string Key, bool Suppress);

// ── Injection ────────────────────────────────────────────
public interface ITextInjector
{
    /// Opaque handle to the window that had focus at key-down.
    object? CaptureTarget();
    Task<InjectionOutcome> InjectAsync(string text, object? target, InjectionOptions opts, CancellationToken ct);
}

public sealed record InjectionOptions(
    InjectionMethod Method,          // ClipboardPaste | UnicodeSynth
    string PasteChord,               // "ctrl+v" | "ctrl+shift+v" | "cmd+v"
    TimeSpan PreDelay,
    TimeSpan ClipboardRestoreDelay,
    bool RestoreClipboard);

public enum InjectionOutcome { Injected, TargetLost, ClipboardFailed, SynthFailed, PermissionDenied }

// ── Post-processing ──────────────────────────────────────
public interface IPostProcessor
{
    int Order { get; }
    string Name { get; }
    PostProcessResult Process(PostProcessResult input);
}

public sealed record PostProcessResult(string Text, IReadOnlyList<AppliedRule> Applied);
public sealed record AppliedRule(string Processor, string Rule, string From, string To);
```

`AppliedRule` is unused in Phase 1 but the plumbing exists so Phase 2 drops in without touching the pipeline.

---

## 1.4 SessionController state machine

The heart of the daemon. Implement as an explicit state machine, not scattered booleans — every bug you'll hit here is a state bug.

**States:** `Initializing → Idle → Recording → Finalizing → Transcribing → Injecting → Cooldown → Idle`, plus `Faulted`.

| From | Trigger | Guard | Actions | To |
|---|---|---|---|---|
| Initializing | model loaded + hook started | — | log ready | Idle |
| Initializing | model load failed | — | log fatal | Faulted |
| Idle | key down | `IsReady` | `CaptureTarget()`; `BeginCapture(preRoll)`; start max-duration timer | Recording |
| Idle | key down | `!IsReady` | log warn, beep | Idle |
| Recording | key up | elapsed ≥ `minDurationMs` | `EndCapture()` | Finalizing |
| Recording | key up | elapsed < `minDurationMs` | `AbortCapture()`; log "too short" | Cooldown |
| Recording | max duration timer (default 120 s) | — | `EndCapture()`; log truncation | Finalizing |
| Recording | audio device lost | — | `AbortCapture()` | Faulted |
| Finalizing | — | — | VAD trim; if speech < 300 ms → discard | Transcribing / Cooldown |
| Transcribing | result | `!IsEmpty` | run post-processor chain | Injecting |
| Transcribing | result | `IsEmpty` | log "no speech" | Cooldown |
| Transcribing | exception or timeout (10 s) | — | log error | Cooldown |
| Injecting | outcome = Injected | — | log success + full timings | Cooldown |
| Injecting | outcome ≠ Injected | — | log failure; leave text on clipboard as fallback; log that it did so | Cooldown |
| Cooldown | 150 ms elapsed | — | — | Idle |
| any | hook faulted | — | attempt `RestartAsync`, up to 5× with backoff | Idle / Faulted |

**Edge cases that must be handled explicitly:**

1. **Key down while not Idle.** Ignore it. Do not queue. Do not start a second recording. Log at debug.
2. **Focus changed during transcription.** You captured the target at key-down. Decide the policy and make it configurable — default: inject into whatever has focus *now*, because the user probably switched deliberately; but log both handles so you can tell what happened. `targetLostPolicy: "current" | "abort"`.
3. **Key stuck down.** If the hook reports a down with no matching up for `maxDurationMs`, force-finalise. Some Wayland/RDP situations swallow the key-up.
4. **Trigger key held during injection.** If the trigger is a modifier (Right Ctrl) and it's somehow still physically down when you synthesise Ctrl+V, you can get modifier soup. Before injecting, synthesise a key-up for the trigger key. Cheap insurance.
5. **Model still warming at first key-down.** Guard on `IsReady`, don't block the hook thread waiting.
6. **Very long transcript.** Cap at e.g. 20 000 chars before clipboard set; log truncation.

**Threading model:**
- Hook callback thread: sets a flag, posts to a `Channel<SessionCommand>`, returns. **Never** does work. Non-negotiable on Windows.
- One dedicated session worker consuming the channel.
- Audio callback thread (PortAudio): writes into the ring buffer only. Lock-free single-producer/single-consumer.
- ASR: serialised behind a `SemaphoreSlim(1,1)` — one decode at a time.

---

## 1.5 Audio pipeline

### Capture mode — on-demand (decided)

**The stream is closed while idle.** It opens on hotkey-down and closes on hotkey-up. The microphone privacy indicator is lit only while you are actually dictating.

Consequences, stated plainly because they are real:

- **Pre-roll is gone.** Whatever you say in the window between the key going down and the stream producing its first buffer is lost. In practice this means you must learn to press, wait for the cue, then speak. Every on-demand dictation tool has this property and users adapt, but it is a genuine cost and it is why the always-on design existed.
- **Stream-open latency is now on the critical path.** It must be measured, not assumed — see S1b below.
- **Bluetooth microphones are a problem.** Opening a BT headset mic triggers an HFP/HSP profile switch: 300–800 ms of delay, an audible pop, and the A2DP audio you were listening to drops to telephony quality. If you ever dictate through AirPods or a BT headset, on-demand will feel broken. Mode C (below) exists for exactly this case.

**Three modes in config; `OnDemand` is the default:**

| Mode | Behaviour | Indicator | Pre-roll |
|---|---|---|---|
| `OnDemand` | Open on key-down, close on key-up | Lit only while dictating | None |
| `WarmIdle` | Open on key-down; stay open for `idleCloseMs` (default 90 s) after key-up; close on timeout | Lit during a dictation burst, off when you walk away | Full, from the 2nd utterance of a burst onward |
| `AlwaysOn` | Stream open for daemon lifetime | Always lit | Always |

`WarmIdle` is worth knowing about because it costs almost nothing to implement once `OnDemand` works — it is `OnDemand` plus a close-timer — and it removes both the clipped-syllable problem and the Bluetooth pop for the way people actually dictate, which is several utterances in a row. It keeps your stated requirement (mic off when you're not using it) while only paying the open cost once per burst. Ship `OnDemand` as the default per the decision; try `WarmIdle` in week two and see which you prefer.

### Readiness cue — mandatory in OnDemand

Because the mic is not live at the instant you press the key, the daemon **must** signal when it actually is. Phase 1: a short, quiet sine blip (~40 ms, 880 Hz) on stream-ready, played on a separate output stream so it never enters the capture. Phase 3 replaces it with the HUD. Config: `readyCue: "sound" | "none"`.

Also emit a distinct, lower failure tone if the stream fails to open — silence is the worst possible feedback here, because you'll talk for ten seconds into nothing.

### Stream configuration and resampling

**Do not request 16 kHz from the device.** Many USB, headset and Bluetooth mics expose only 44 100 or 48 000 Hz, and PortAudio's conversion layer is unreliable on native ALSA and WASAPI — you get `paInvalidSampleRate` or, worse, a silently wrong stream.

Correct sequence:

1. Query the device's `defaultSampleRate` via `Pa_GetDeviceInfo`.
2. Probe `Pa_IsFormatSupported` for 16 kHz mono float32. If supported, use it directly (no resampling — the common case for built-in laptop mics).
3. Otherwise open at the device native rate, mono, float32, and resample in-process to 16 kHz.
4. Log which path was taken and the actual negotiated rate. Every audio bug you will ever have starts with not knowing this.

**Resampler:** windowed-sinc polyphase, ~~32-tap~~ **~1200–1300 taps per phase** (32 is physically incapable of the transition below — corrected by S1b, see its entry above), with the anti-aliasing lowpass at 7.8 kHz. Two notes:

- 48 000 → 16 000 is exact 3:1 decimation. Still needs the lowpass first — naive sample-dropping aliases speech energy above 8 kHz straight back into the band Parakeet cares about. This is a real accuracy hit, not a theoretical one.
- 44 100 → 16 000 is the awkward ratio (147:160 after reduction) and needs the general polyphase path.

Do **not** use linear interpolation "because it's speech." It is cheap in CPU and expensive in WER, and you would be introducing error ahead of the one component whose accuracy you're gating the whole project on. If you'd rather not write it, `libsamplerate` is BSD-2 licensed and fine to bundle.

**Buffer:** `framesPerBuffer = 512` at the native rate. Accumulate into a plain growable `float` list at 16 kHz — with on-demand capture there is no ring buffer and no wrap-around logic to get wrong. Cap growth at `maxDurationMs`.

### Everything else

**Level metering:** RMS over each buffer → dBFS, raised as `LevelChanged` at ~20 Hz. Unused in Phase 1; wire it now so Phase 3's VU meter is free.

**VAD:** Silero from the sherpa-onnx package. Trim leading and trailing silence. Config: `threshold: 0.5`, `minSilenceDurationMs: 300`, `minSpeechDurationMs: 250`. If total speech after trim is under 300 ms, discard and log — this is the main defence against decoding empty audio, which is where transducers hallucinate. **VAD matters more in on-demand mode, not less:** the first 50–150 ms after a cold stream open is frequently a driver click or DC settling transient, and you do not want that hitting the encoder.

**Device changes:** since the stream is closed while idle, resolve the device fresh on every key-down. If the configured device is gone, fall back to the system default and log it — don't fail the dictation.

### Utterance length ceiling `[verify in S1]`

`maxDurationMs` is set to 120 s in the config, but **do not assume a single decode call handles that.** sherpa-onnx and onnx-asr both warn that the practical single-shot limit for these models is roughly 20–30 seconds, and long audio should be segmented with VAD. Two things follow:

1. **Test this in S1.** Decode a 60-second and a 120-second clip. Check for truncation, garbage tails, or memory spikes.
2. **If single-shot degrades past ~30 s**, implement VAD-based segmentation: split on silence boundaries into ≤25 s chunks, decode sequentially, join with a space. This changes `TranscriptionResult` to carry per-chunk timings and changes nothing else.

Also, decode time scales linearly: at RTF ≈ 0.06, a 60-second utterance takes ~3.6 s to transcribe. Beyond ~15 s of recorded audio, emit a "processing" cue after key-up so a long dictation doesn't look like a crash.

---

## 1.6 Model management

**Resolution order for the model directory:** config path → `%LOCALAPPDATA%\Soneto\models\` (Win) / `~/.local/share/soneto/models/` (Linux) → error.

**First run:** if absent, download the sherpa-onnx int8 archive with a resumable HTTP request, show progress on the console, verify SHA-256 against a hash pinned in source, extract, verify all four files present. On hash mismatch: delete and retry once, then fail with a clear message. **Never** run inference against unverified weights.

**Warm-up:** immediately after constructing the recognizer, decode a bundled **1-second WAV of real speech** (embedded resource in `Soneto.Core`, e.g. someone saying "test one two"). Not silence — silence lets the TDT decoder take blank-frame early-exit paths and skips allocation of the full-vocabulary joiner kernels, so you'd warm up the wrong code path and still eat the cost on the user's first real dictation. Assert the warm-up output is non-empty; if it is empty, the model or tokens file is wrong and you want to know at startup, not at first use. Log warm-up time separately.

**Thread count default:** `min(8, Environment.ProcessorCount - 2)`, overridable, seeded from the S1 sweep result.

---

## 1.7 Post-processing chain (Phase 1 stub)

Three processors only. Ordered, each with a config toggle.

| Order | Name | Behaviour |
|---|---|---|
| 10 | `UnicodeNormalizer` | NFC normalise. Map cedilla `ş`/`ţ` (U+015F/U+0163) → comma-below `ș`/`ț` (U+0219/U+021B). Always on. |
| 20 | `SpokenCommands` | Structural formatting commands → control characters. EN: `new line` → `\n`, `new paragraph` → `\n\n`. RO: `linie nouă` → `\n`, `paragraf nou` → `\n\n`. Small fixed table in Phase 1; user-extensible in Phase 2. |
| 30 | `WhitespaceCleaner` | Collapse runs of **horizontal** whitespace, trim, remove space before `,.!?;:`, ensure single space after. |
| 40 | `TrailingSpace` | Append a single trailing space if the transcript ends in a word character and the option is on (default on — it makes consecutive dictations flow). |

**Ordering hazard, and the reason `SpokenCommands` runs before `WhitespaceCleaner`:** the command processor emits `\n` and `\n\n`, and a naive whitespace cleaner will collapse them straight back into single spaces. `WhitespaceCleaner` must therefore treat `\n` as significant — collapse spaces and tabs, preserve newlines, and cap consecutive newlines at two. Put this in the unit tests explicitly, because it is the kind of thing that silently regresses and you won't notice until you dictate a multi-paragraph email.

Parakeet v3 already emits sentence punctuation, so `SpokenCommands` handles **structure only** — line and paragraph breaks. Don't add `"comma"` → `,` or `"period"` → `.`; you'd be fighting the model, and you'd corrupt any sentence that legitimately contains the word.

Filler-word stripping is deliberately **not** here. It needs the dictionary's language awareness (`ăăă` vs `um`) and belongs in Phase 2.

---

## 1.8 Injection — Windows

```
 1. target = GetForegroundWindow()                       [at key-down]
 2. (at inject) if target lost and policy=current → target = GetForegroundWindow()
 3. seqBefore = GetClipboardSequenceNumber()
 4. inspect clipboard formats; save per §"Clipboard preservation" below
 5. SetClipboardData(CF_UNICODETEXT, transcript)         retry 3× / 20 ms
 6. heldMods = SanitizeModifiers()                       see below
 7. Sleep(opts.PreDelay)                                 default 20 ms
 8. SendInput: ctrl down, V down, V up, ctrl up          (or configured chord)
 9. RestoreModifiers(heldMods)
10. Sleep(opts.ClipboardRestoreDelay)                    default 150 ms
11. if GetClipboardSequenceNumber() == seqAfterOurSet → restore
    else → skip restore, log "user copied during window"
```

### Modifier sanitising (step 6)

Two separate problems, both real:

1. **The trigger key itself.** If Right Ctrl is your hotkey and it's suppressed, you must suppress **both** down and up. A leaked orphan key-up confuses apps that track modifier state themselves (IDEs, terminal emulators, games). Suppress both or neither — never one.
2. **Modifiers the user is physically holding.** If you're holding Shift while dictating (common in an IDE), your synthetic `Ctrl+V` becomes `Ctrl+Shift+V`, which in most apps is paste-without-formatting and in a terminal is something else entirely.

```csharp
// 6. before the paste chord
var held = new List<VirtualKey>();
foreach (var mod in new[] { VK_SHIFT, VK_MENU, VK_CONTROL, VK_LWIN, VK_RWIN })
    if ((GetAsyncKeyState(mod) & 0x8000) != 0) { held.Add(mod); SendKeyUp(mod); }

// ... paste chord ...

// 9. after — re-check physical state before restoring
foreach (var mod in held)
    if ((GetAsyncKeyState(mod) & 0x8000) != 0) SendKeyDown(mod);
```

The re-check in step 9 matters. A synthetic key-up desynchronises the *logical* keyboard state that apps read from the input queue, while `GetAsyncKeyState` still reports the *physical* key as down — so you do need to restore. But if the user released the key during your paste (likely, since the whole sequence is ~200 ms), restoring blindly leaves a **stuck modifier** and the user's next keystroke does something bizarre. Re-check, then restore only what's still physically held.

### Clipboard preservation (steps 3–5, 11)

**Sequence-number guard.** Capture `GetClipboardSequenceNumber()` before you touch anything, and again right after your own `SetClipboardData`. At restore time, if the current number doesn't match the one your write produced, someone else changed the clipboard during your 150 ms window — almost certainly the user hitting Ctrl+C. **Abort the restore.** Silently overwriting something the user just deliberately copied is the worst failure this app can have, and it's invisible until they paste and get their transcript instead.

**Non-text formats.** If the clipboard holds an image, an Excel range, or a file selection (`CF_DIBV5`, `CF_HDROP`, or app-private formats), a text-only backup destroys it. Policy, via `clipboardPolicy`:

| Value | Behaviour |
|---|---|
| `textOnly` *(default)* | Back up and restore `CF_UNICODETEXT`. If non-text formats are present, **skip restoration entirely** and log — leaving the transcript on the clipboard is a smaller loss than silently replacing an image with text |
| `never` | Don't restore at all; transcript stays on the clipboard |
| `bestEffort` | Attempt full `IDataObject` round-trip |

**Do not implement `bestEffort` in Phase 1.** Full OLE round-trip has to cope with delayed rendering, where the source app supplies data only on request and may have closed by the time you restore. It's a genuine rabbit hole for a case that costs you nothing to decline.

### Other notes

- Step 5's retry loop is not optional. Clipboard managers (Ditto, Windows clipboard history, Flow Launcher, Copilot) hold the clipboard and will collide with you.
- Step 10's delay is a race, not a guarantee. If the target is slow you restore before it pastes. Per-app configurable from the start; S4 tells you which apps need more.
- **Fallback:** `UnicodeSynth` — `SendInput` with `KEYEVENTF_UNICODE`, one `INPUT` pair per UTF-16 code unit, batched ~50 with a 5 ms gap. Slow, but touches neither the clipboard nor the modifier state. Selected per-app via config.

## 1.9 Injection — Linux `[GATE:S5]`

Same algorithm, minus the clipboard sequence number (no equivalent — use a content hash comparison instead: hash what you wrote, hash again before restoring, skip if it differs). `wl-copy` / `wl-paste` for clipboard (X11: `xclip` or direct XFixes), `ydotool key 29:1 47:1 47:0 29:0` for the paste chord. Detect session type from `XDG_SESSION_TYPE` at startup and select the implementation; log which one was chosen.

**Multi-keyboard enumeration — do not assume one device.** A laptop in a dock routinely exposes 3–6 `EV_KEY` nodes: internal keyboard, external USB or Bluetooth keyboard, ACPI power button, consumer-control/media keys, and sometimes a virtual node from the trackpad firmware. If you open only `event0` you will silently miss every keypress from whichever keyboard the user actually types on.

- Enumerate all `/dev/input/event*`, read the capability bitmask via `EVIOCGBIT`, and keep every node that reports `EV_KEY` **and** contains standard alphanumeric scancodes (`KEY_A` through `KEY_Z`). The alphanumeric test is what filters out power buttons and media-key nodes, which also claim `EV_KEY`.
- Open all matching nodes non-blocking and multiplex with `epoll` on a single reader thread. Don't spawn a thread per device.
- **Handle hotplug.** Watch `/dev/input` with inotify for `IN_CREATE`/`IN_DELETE` and re-enumerate. Otherwise plugging in a keyboard mid-session silently stops the hotkey working, and undocking makes you read from a dead fd.
- Log the full device list and which nodes were selected at startup. This is the first thing you'll want when it doesn't work on a machine that isn't yours.

Ship `scripts/setup-linux.sh` doing: `input` group membership, the `/dev/uinput` udev rule, a `ydotoold` user systemd unit, and a verification pass that prints red/green for each. This script is a deliverable, not an afterthought — it's the difference between "works on my machine" and "works after a reboot".

---

## 1.10 Configuration

`%LOCALAPPDATA%\Soneto\config.json` / `~/.config/soneto/config.json`. Hot-reloaded via `FileSystemWatcher` with a 500 ms debounce. Invalid JSON → log error, keep the previous config in memory, never crash.

```jsonc
{
  "hotkey": { "key": "RightControl", "suppress": true },
  "audio": {
    "deviceId": null,                 // null = system default, resolved per key-down
    "captureMode": "OnDemand",        // OnDemand | WarmIdle | AlwaysOn
    "idleCloseMs": 90000,             // WarmIdle only
    "preRollMs": 0,                   // ignored in OnDemand; 300 recommended for WarmIdle/AlwaysOn
    "readyCue": "sound",              // sound | none
    "minDurationMs": 250,
    "maxDurationMs": 120000,
    "longUtteranceCueMs": 15000,      // "still processing" cue beyond this
    "resampler": "polyphase",         // polyphase | none (none = fail if device won't do 16k)
    "vad": { "enabled": true, "threshold": 0.5,
             "minSilenceMs": 300, "minSpeechMs": 250 }
  },
  "asr": {
    "modelDir": null,
    "numThreads": 8,
    "decodingMethod": "greedy_search",
    "hotwordsEnabled": false,
    "timeoutMs": 10000
  },
  "postProcess": {
    "normalizeUnicode": true,
    "spokenCommands": true,
    "cleanWhitespace": true,
    "trailingSpace": true
  },
  "injection": {
    "method": "ClipboardPaste",
    "pasteChord": "ctrl+v",
    "preDelayMs": 20,
    "clipboardRestoreDelayMs": 150,
    "clipboardPolicy": "textOnly",    // textOnly | never | bestEffort (not in Phase 1)
    "sanitizeModifiers": true,
    "targetLostPolicy": "current",
    "perApp": {
      "WindowsTerminal.exe": { "pasteChord": "ctrl+shift+v" },
      "Teams.exe": { "clipboardRestoreDelayMs": 300 }
    }
  },
  "logging": { "level": "Information", "retainDays": 7 }
}
```

The `perApp` block exists in Phase 1 with exactly the two behaviours above — chord and delay. The full profile system is Phase 4. Seed it from your S4 findings.

---

## 1.11 Logging and instrumentation

Serilog, structured, console + rolling file (`logs/soneto-.log`, daily, 7-day retention).

Every session gets a `SessionId` (short GUID) attached to every log line, and completes with one summary line:

```
Session {SessionId} completed  audio={AudioMs}ms  vad={VadMs}ms  decode={DecodeMs}ms
  post={PostMs}ms  inject={InjectMs}ms  total={TotalMs}ms  chars={CharCount}
  target={ProcessName}  outcome={Outcome}
```

This single line is your entire performance dashboard for Phase 1. When something feels slow in daily use you grep for it and you know which stage.

`--verbose` adds per-buffer audio levels and the raw pre-post-processing transcript.

---

## 1.12 Error handling matrix

| Failure | Detection | Behaviour | Recovery |
|---|---|---|---|
| Model files missing | startup check | download prompt on console | auto |
| Model hash mismatch | startup | delete, retry once, then fatal | manual |
| No audio device | startup / poll | log error, daemon stays alive, hotkey no-ops with a beep | auto on device return |
| Audio device removed mid-record | PortAudio callback error | abort session, fall back to default device | auto |
| Hook dies (Windows) | heartbeat: no events for 60 s + a test-event probe | re-register, up to 5× with exponential backoff | auto |
| Decode throws | try/catch | log with audio duration, discard, Cooldown | auto |
| Decode exceeds 10 s | CTS timeout | cancel, log, discard | auto |
| Clipboard set fails after retries | return value | fall back to UnicodeSynth for this session, log | auto |
| Paste synth fails | return value | leave text on clipboard, log clearly that it did | manual |
| `ydotoold` not running (Linux) | startup probe | log actionable message pointing at `setup-linux.sh` | manual |
| Config invalid | parse error | keep previous config, log | auto on next valid save |
| Audio stream fails to open | `Pa_OpenStream` error | failure tone, log device + host API, abort session | auto next attempt |
| Audio stream left open on an error path | soak test / mic indicator stays lit | `IAudioCapture` closes in `finally`, always | — (prevented) |
| Bluetooth mic profile switch delay | time-to-first-sample > 400 ms | log warning suggesting `WarmIdle` | manual |

**Principle:** the daemon never exits on a recoverable error. A dictation app that dies silently in the background is worse than one that beeps at you.

---

## 1.13 Testing

**Unit (`Soneto.Core.Tests`) — must run with no audio device and no model:**
- `SessionController` state machine: drive with a fake hotkey source, fake capture, fake transcriber. Every row of the §1.4 table gets a test, including all six edge cases.
- **Resampler:** 48 k → 16 k and 44.1 k → 16 k against a reference implementation; sweep test asserting no energy above 8 kHz; length correctness (off-by-one at buffer boundaries is the classic bug here).
- **Capture buffer:** growth cap at `maxDurationMs`, correct behaviour on abort, no allocation churn during a long capture.
- `UnicodeNormalizer`: cedilla→comma-below both cases, NFC idempotence, no mangling of EN text.
- `SpokenCommands`: EN and RO tables, case-insensitivity, and — critically — that a sentence *containing* the words in a non-command context isn't mangled.
- `WhitespaceCleaner`: punctuation spacing, multi-space collapse, **and that `\n` and `\n\n` survive intact** while three or more newlines collapse to two.
- Config: invalid JSON keeps previous, hot-reload debounce, unknown `captureMode` falls back to `OnDemand` with a warning.

**Leak/stress tests** (tagged, excluded from the default run):
- 1 000 sequential decodes against a fake or real transcriber; assert working set flat from iteration 50.
- 500 open/capture/close audio cycles; assert no leaked PortAudio streams (`Pa_GetStreamCount` or handle count) and no file-descriptor growth on Linux.

**Corpus regression:** a test that runs the full S2 corpus through the real transcriber and asserts WER stays within the S2 baseline + 2%. Tagged `[Trait("Category","Corpus")]`, excluded from the default run, executed manually before any ASR-layer change. This catches "I upgraded sherpa-onnx and everything got worse."

**Manual test script** (`docs/MANUAL-TESTS.md`) — the S4 injection matrix, re-run before every release.

---

## 1.14 Build order

Twelve work items, dependency-ordered. Each has a demo you can actually run.

| # | Item | Done when |
|---|---|---|
| 1 | Solution scaffold, projects, CI build | ✅ Local build/test green (2026-08-31); CI not set up — no git repo/remote exists yet in this workspace, so there's nowhere to attach it. `dotnet build soneto.slnx` and `dotnet test soneto.slnx` are both clean (0 warnings, 0 errors) as the substitute. See `Docs/PROJECT-MEMORY.md` for the full scaffold summary. |
| 2 | Config load/save/hot-reload + logging host | ✅ Done (2026-08-31). `Soneto.Core/Configuration/` (SonetoConfig, ConfigPaths, ConfigService, CaptureModeJsonConverter) + Serilog host wiring in `Soneto.Daemon/Program.cs`. Independently verified (build/test clean, live daemon demo) and code-reviewed with 2 blocking fixes (a dispose/timer race, an incomplete "never throws" exception contract that could crash the daemon on a permissions failure) + 6 should-fix items (mislabeled startup log, unused logging-config fields, enum casing, debounce test strength, watcher error handling) all applied and re-verified. 7/7 tests pass. See `Docs/PROJECT-MEMORY.md` for full details. |
| 3 | `ITranscriber` + sherpa-onnx impl + model manager | ✅ Done (2026-08-31). `--transcribe file.wav` works end-to-end against the real model (cold load ~1.7-1.8s, warm-up ~70ms, decode ~190ms for a 3.8s clip, RTF ~0.049). Real error handling per §1.12: model download with genuine HTTP range-resume, SHA-256 verify-before-extract, hash-mismatch retry-once-then-fail, post-extraction file-presence check. Independently verified and code-reviewed with 1 blocking fix (a dispose/decode-semaphore race that could use-after-free the native recognizer) + 5 should-fix items (tar stdout deadlock risk, unhandled tar-not-found exception, no download stall protection, shared temp filename collision risk, a dev-only model-discovery footgun tightened before it could be reused in item 9) all applied and re-verified. Default test suite (25 tests) confirmed passing with no model file present; 1 `Category=Corpus` test passes against the real model. |
| 3b | **Stream lifecycle + native memory** | ✅ Done (2026-08-31). Audit confirmed every `OfflineStream` (`Decode()` and warm-up in `Initialize()`) was already correctly wrapped in `using` from item 3 — no fix needed. 1000-iteration real-decode stress test added (`Category=Corpus`, excluded from default run): working set flat, max deviation 0.11-0.18% across independent runs (bar: 5%). Independently verified the methodology is well-calibrated for the defect it targets (a missing `using` would fail by ~iteration 225, not hide in noise), with an honest documented caveat that a much smaller leak (<~45KB/decode) wouldn't be caught within 1000 iterations — not a concern for what this test actually targets. Two minor review fixes applied (a `Process` handle leak in the sampling helper itself, and a doc-comment caveat on the `WorkingSet64` measurement's limitations). |
| 3c | VAD segmentation for long audio `[if S1 showed >30 s degrades]` | a 120 s clip transcribes correctly via chunked decode; per-chunk timings logged |
| 4 | Polyphase resampler | ✅ Done (2026-08-31). `Soneto.Core/Audio/PolyphaseResampler.cs` — built as a **stateful streaming** resampler (not the spike's whole-buffer design), maintaining filter-history state across calls so ~512-frame buffer-by-buffer resampling (per §1.5) produces bit-identical output to one-shot whole-signal resampling — verified via reflection-based scratch tests proving genuine incremental processing, not a "buffer everything until Flush()" shortcut. Both ratios (48k→16k exact 3:1, 44.1k→16k awkward 147:160) verified at ~144dB alias suppression (bar: 40dB), matching S1b's validated numbers. Length correctness, a mathematically-known reference-signal (pure tone) check, and history-buffer-bounded-under-pathological-1-sample-chunks all independently verified. Code review found no blocking issues; 3 should-fix items applied (a latent tap-count bug in the untested upsampling path, allocation churn in the real-time capture path, missing thread-safety doc). 51/51 tests pass, 0 warnings/errors. |
| 4b | `IAudioCapture` (PortAudio), on-demand | ✅ Done (2026-08-31). `Soneto.Core/Audio/PortAudioCapture.cs` + `SpscFloatRingBuffer.cs`. `--record 5` works end-to-end on real hardware (device resolution, format selection, negotiated-rate logging, WAV output). **First-pass review found a real §1.4 threading-model violation** — the PortAudio real-time callback took a lock and did resampling work directly, with measured tail-latency spikes to 40% of the buffer budget in a path untested on this dev machine's default device. Redesigned to a genuine lock-free single-producer/single-consumer ring buffer (monotonic-counter design, correctly sidesteps the classic full/empty ambiguity) with a dedicated consumer thread doing all resampling/RMS/buffer work — verified correct via a real multi-threaded stress test (70 total tests, including concurrent producer/consumer races and deliberate backpressure/drop scenarios). Also restored "first non-zero buffer" as the time-to-first-sample metric (a prior simplification had regressed to "first buffer arrival," which would have masked this exact machine's known WASAPI-silent-buffer failure mode from S1b). **Honest gap:** no agent session on this machine can physically produce sound near the microphone, so real non-silent speech capture through this pipeline has never been end-to-end verified by anyone yet — only "device opens, real audio callbacks fire, data flows correctly through the lock-free path" is confirmed. A human should run `--record` while speaking to close this gap before fully trusting the capture path. |
| 4c | Capture modes + ready cue | ✅ Done (2026-08-31). `Soneto.Core/Audio/CaptureModeController.cs` (OnDemand/WarmIdle/AlwaysOn orchestration) + `PreRollRingBuffer.cs` (full pre-roll from the 2nd utterance of a WarmIdle burst onward, verified via exact sample-count math, not just logs) + `AudioCuePlayer.cs` (880Hz ready / 220Hz failure tones, separate output stream, never touches the input path). Two independent reviews found matching **blocking** issues: a real TOCTOU race in the WarmIdle idle-close timer (`Timer.Dispose()` doesn't guarantee an already-dequeued callback won't still fire — could silently kill a stream mid-recording of a brand-new utterance) — this is the **fourth** time this project has caught a concurrency-discipline gap in a first implementation pass (after S3's hook callback, S4's clipboard atomicity, item 4b's audio callback); and `ReadyCue.None` incorrectly silencing the failure tone too, contradicting the plan's explicit "silence is the worst possible feedback" reasoning for stream-open failures specifically. Both fixed (generation-token guard on the timer; failure cue now unconditional) and proven via a deterministic reflection-based test that fails without the fix and passes with it. Also caught and fixed a real defect the failure-cue fix exposed at its only call site (`Program.cs`'s CLI demo was nulling out the cue player entirely under `ReadyCue.None`, which would have silently defeated the whole fix). 101/101 tests pass. **Honest gap carried from item 4b:** no agent session can produce real audio, so the ready/failure cues' actual audibility and genuine non-silent capture through `WarmIdle`/pre-roll remain unverified by a human ear/microphone. |
| 5 | VAD integration | ✅ Done (2026-08-31). `Soneto.Core/Asr/SileroVadDetector.cs` wraps sherpa-onnx's Silero VAD (API shape reverse-engineered via reflection — no plan example existed for it, same as item 3's ASR config discovery). VAD model (~630KB) committed as a source-tree embedded resource, not a `ModelManager` download — confirmed reasonable given the plan's "never commit to repo" language is stated specifically about the ~640MB ASR model's size, not as an absolute rule, and this codebase already has precedent (`warmup-en.wav`). **Code review found the whole-utterance discard check — the plan's own "main defence against decoding empty audio" — was structurally near-unreachable**, because the first implementation reused the same `MinSpeechMs` value as both Silero's per-segment filter AND the whole-utterance discard floor; since total speech duration is by construction always ≥ any segment that passed the per-segment filter, the discard check could almost never fire. Fixed with a new, genuinely independent `VadConfig.MinUtteranceMs` field (default 300, matching the plan's literal number) — proven by a test that was confirmed to fail against the pre-fix logic and pass against the fix. Also fixed a cross-process race + weak length-only integrity check on the extracted model's temp file path (now user-scoped cache dir + SHA-256 content validation + atomic move, mirroring item 3's download-safety pattern). 108/108 tests pass, all hardware/model-free in the default run (the embedded VAD model needs no download or live audio device). |
| 6 | `IHotkeySource` Windows | ✅ Done (2026-09-01). `Soneto.Platform.Windows/WindowsHotkeySource.cs` promotes S3's spike to real product code, carrying forward both its corrections (VK_LCONTROL not generic VK_CONTROL; the block-callback silent-drop finding informs why the heartbeat/watchdog design matters). Callback-thread discipline confirmed clean by two independent reviews — the best instance of this pattern seen across all items so far. **Found a fifth instance of this project's recurring concurrency-bug pattern anyway**, one level down from the hook callback: a `Timer.Dispose()` race in the heartbeat mechanism where a stale, still-in-flight probe check could fire a spurious `Faulted` event against a just-restarted, healthy hook. Fixed with the same generation-token pattern that fixed item 4c's WarmIdle timer, proven via a genuine concurrent race test (not a sequential reflection test, which the implementer correctly recognized wouldn't actually prove this particular race) confirmed to fail pre-fix and pass post-fix. 146 tests pass (108 + 38), including real OS-level `EventSimulator`-driven end-to-end suppression and modifier-detection tests. **Confirmed environmental limitation for future test authoring:** SharpHook/uiohook doesn't support two concurrent hook instances safely in one process on this machine — independently reproduced by two separate agents. **Standing honest gaps:** physical key/focused-app leak testing, physical Shift-held-during-trigger, 30-minute idle survival, and lock/unlock survival all need a human at a keyboard; the heartbeat's real synthetic-keystroke-injection side effect (a real F24 press system-wide every ~60-75s of inactivity) is documented as untested against real-world macro tools/RDP configurations. |
| 7 | `ITextInjector` Windows | ✅ Done (2026-09-01). `Soneto.Platform.Windows/WindowsTextInjector.cs` + `ClipboardManager.cs`, promoting S4's spike (deliberately deferring the modifier sanitiser to 7b and the sequence-guard/policy to 7c, per this project's incremental split). `--inject` verified end-to-end against real Notepad via UI Automation, ~203ms elapsed, correct diacritic byte encoding. **Found a genuinely deterministic bug, not a hypothetical:** configuring `LeftControl`/`LeftShift` as the hotkey trigger (both explicitly supported) broke every single paste, since the injector's own synthetic paste-chord modifiers were indistinguishable from real keystrokes to the hook, causing self-inflicted suppression and phantom hotkey events. Fixed with the technically correct mechanism — SharpHook's `IsEventSimulated` (backed by Windows' `LLKHF_INJECTED` flag) — so the hook now ignores its own synthetic/injected trigger-key events entirely, at the honest cost of two hotkey tests that can no longer use synthetic input to simulate "physical" presses (marked skipped with a clear explanation, not silently weakened). Also fixed: a missing app-switch diagnostic (§1.8's "log both handles" requirement), cancellation leaving the clipboard permanently clobbered with no restore attempt, `ClipboardPolicy.Never` being silently ignored, a blocking `Thread.Sleep` in the async retry path, `SendInput` splitting the paste chord across multiple calls (reopening the exact interleaving risk the API exists to close), and a flaky hardware test (now 8/8 stable after switching a fixed sleep to a polling readiness check). 163 tests pass + 2 documented skips, 0 warnings/errors. |
| 7b | Modifier sanitiser | ✅ Done (2026-09-01). `Soneto.Platform.Windows/ModifierSanitizer.cs`: correctly generalizes item 6's `VK_LCONTROL`-not-generic left/right disambiguation to Shift/Alt/Win, suppresses physically-held modifiers before the paste chord, re-checks physical state before restoring (per S4's spike, avoiding a stuck modifier if released mid-injection), and skips whichever modifier VK exactly matches the configured hotkey trigger. **Two independent reviews both found the same real (if narrow) gap the plan's own literal pseudocode calls out**: excluding Control entirely missed that the paste chord's own hardcoded Left-Ctrl key-up can desync a target app's logical modifier state for a physically-held Left Control unrelated to the trigger — fixed by adding `VK_LCONTROL` (not generic Control, not Right Control — matching exactly what the chord itself synthesizes) to the sanitiser's set. Also fixed: the sanitiser's own trigger-alias resolution didn't cover `HotkeyKeyMapper`'s raw-`KeyCode`-name fallback path, closed by making both resolve through one shared source of truth instead of two independently-maintained alias tables; and `ModifierSanitizer` was untestable from the test project (no `InternalsVisibleTo`), now fixed with real unit tests reaching the pure trigger-resolution logic directly. **A genuine near-miss occurred during hardware verification**: a test's synthetic input briefly interacted with the real live desktop in this shared session (a stray paste landed containing real desktop-app text) — no harmful action resulted, but flagged transparently and the fix pass deliberately avoided further live hardware testing. 189 tests pass + 2 documented skips (from item 7), 0 warnings/errors. |
| 7c | Clipboard sequence guard + policy | ✅ Done (2026-09-01). `Soneto.Platform.Windows/ClipboardManager.cs`'s atomic `RestoreUnicodeTextWithSequenceGuard`/`Async` open-check-write-close as one critical section per attempt (the S4 spike's proven TOCTOU fix, carried forward faithfully); `Save()` flags non-text clipboard formats via the spike's validated CF_UNICODETEXT/CF_TEXT/CF_OEMTEXT/CF_LOCALE "text family" allow-list. `WindowsTextInjector` skips restoration (log only) under `textOnly`/`bestEffort` when non-text formats were present, and logs Restored/SkippedSequenceChanged/Failed distinctly without ever silently claiming success on a real failure. Code review found and fixed a blocking bug where the restore's own `EmptyClipboard()` call bumped the very sequence counter the guard watches, which could misreport a transient write failure as a user-copy event — see `Docs/PROJECT-MEMORY.md` for the full writeup. `bestEffort`'s full OLE round-trip remains explicitly out of scope for Phase 1. Both done-when criteria proven via tests using real clipboard mutations, not mocks. 193 tests pass (up from 191), 0 warnings/errors. |
| 8 | Post-processor chain (4 stubs) | ✅ Done (2026-09-01). `Soneto.Core/PostProcessing/` — `UnicodeNormalizerProcessor`/`SpokenCommandsProcessor`/`WhitespaceCleanerProcessor`/`TrailingSpaceProcessor` + `PostProcessorChain`. Two review round-trips caught real issues: an independent test-runner verification found `SpokenCommandsProcessor`'s first-pass matching rule violated the plan's own "critically, a sentence containing the words in a non-command context isn't mangled" requirement (fixed via a punctuation/utterance-boundary match rule leveraging Parakeet's own punctuation output); code review then found a newline-cap-vs-trim ordering bug in `WhitespaceCleanerProcessor` that missed whitespace-separated near-blank lines. Both fixed and re-verified. See `Docs/PROJECT-MEMORY.md` for the full writeup. 241 tests pass (156 Core + 85 Windows), 0 warnings/errors. |
| 9 | **`SessionController`** | ✅ Done (2026-09-01). `Soneto.Core/SessionController.cs` — single-worker `Channel<SessionCommand>` state machine per §1.4, wiring `IHotkeySource`/`CaptureModeController`/`SileroVadDetector`/`ITranscriber`/`PostProcessorChain`/`ITextInjector` together; all 16 table rows + all 6 edge cases have real tests (31 total). Real Windows daemon composition wired into `Soneto.Daemon/Program.cs`. Code review found 2 blocking issues, both fixed: (1) the `WINDOWS` preprocessor symbol gating all Windows-only code in `Program.cs` was never actually defined anywhere in the repo, meaning `--watch-hotkey`/`--inject` (items 6/7) AND this item's own daemon wiring were all silently dead code on every build — **items 6/7's "verified against real Notepad" claims are flagged unconfirmed pending re-verification, see `Docs/PROJECT-MEMORY.md`'s ⚠️ callout**; (2) an eighth instance of this project's recurring timer-disposal race (`DisposeAsync` vs. the worker thread over `_maxDurationTimer`), fixed by reordering rather than locking. 187/187 Core tests, 85/85 Windows tests, 0 warnings/errors. Genuine human-in-the-loop end-to-end verification (real hotkey press, real mic, real target app) remains an open manual-verification gap, same category as items 4b/6/7's own documented gaps. |
| 10 | Error handling + watchdog + recovery | ✅ Done (2026-09-01). Reconciled a real §1.4-vs-§1.12 discrepancy (audio device lost mid-record now auto-recovers to Idle instead of permanent Faulted, per §1.12's matrix) and implemented the previously-unbuilt `UnicodeSynth` injection fallback (§1.8's exact spec) for when clipboard-set fails after retries. Most of §1.12's matrix was already correctly handled by items 2-9. Code review found and fixed 1 blocking bug (the new UnicodeSynth fallback could silently lose the user's clipboard content) + 1 doc-comment honesty correction (the device-lost catch is a defensive backstop, not confirmed reachable against real hardware yet — see `Docs/PROJECT-MEMORY.md`). All four done-when scenarios (kill hook, unplug mic mid-record, delete model, corrupt config) have real test coverage, independently verified. 190/190 Core + 97/97 Windows tests pass, 0 warnings/errors, both TFMs. |
| 10b | **24-hour soak** | daemon runs 24 h with lock/unlock and sleep/wake cycles, ~200 dictations, no memory growth, no hook loss, no leaked audio streams |
| 11 | Linux hotkey + injector `[GATE:S5]` | 🟡 Code built (2026-09-01) — `Soneto.Platform.Linux/LinuxHotkeySource`+`LinuxTextInjector` per §1.9's spec, 52 unit tests for everything genuinely testable without real hardware, code-reviewed with 5 should-fix bugs found and fixed (incl. a stale-background-thread race). **This item's actual done-when ("end-to-end dictation on Fedora KDE; hotplug verified") is NOT met** — S5 itself is unresolved and needs real Linux/Wayland hardware this session never had access to; built as best-effort code per an explicit user decision after being asked, with every hardware-dependent gap honestly documented rather than claimed verified. See `Docs/PROJECT-MEMORY.md` for the full writeup. `EVIOCGRAB` suppression deliberately not implemented, per the plan's own "test leak-tolerance before building the grab path" caution. |
| 12 | Corpus regression test + docs | 🟡 Docs done, code done (2026-09-01). All four docs written and independently fact-checked against primary sources (every spot-checked claim confirmed accurate). `WordErrorRateCalculator` + `CorpusRegressionTests` (`Category=Corpus`) built as real, reusable infrastructure — but the actual regression ASSERTION remains blocked on spike S2 (corpus never recorded, deferred by decision 2026-08-31); the test correctly fails loud with an actionable message rather than fabricating a baseline. Code review found no blocking issues, 2 should-fix items applied (tokenizer contraction/hyphen behavior documented, malformed-corpus-line handling hardened). 203/203 Core + 97/97 Windows + 52/52 Linux tests pass. **Phase 1's main build order (1-12) is now code-complete**, with this item, item 11 (needs S5), and the item 6/7 re-verification concern (see `Docs/PROJECT-MEMORY.md`) as the three remaining honest gaps. |

Item **10b** is the one that's easy to skip and shouldn't be. Every failure mode in this app is a slow one — a native handle leak, a hook that dies after the third lock/unlock, an audio stream that isn't closed on the error path so the mic indicator stays lit. None of them show up in a ten-minute test, and all of them are exactly what makes a background daemon feel untrustworthy.

**Item 9 is the moment the project becomes real.** Items 10–12 are what make it something you keep using instead of abandoning after a week of papercuts. Don't skip them to get to the UI.

---

## 1.15 Working with Claude Code on this

One work item per session. Start each with:

```
Read docs/ARCHITECTURE.md and docs/SPIKE-RESULTS.md.
We are on Phase 1, work item N: <name>.

Scope: <the one item>. Do not touch other projects.
Do not add a GUI. Do not add the dictionary engine.
Do not add a second ASR backend or a fallback model.

Acceptance: <the "done when" cell>.
Write the unit tests in the same session.
Show me the interface implementation before the wiring.
```

Two rules that will save you the most time:

**Item 3 first, and make it show you the config code.** The sherpa-onnx C# API surface is the one thing in this plan I'd verify rather than trust — property names may differ from the shape in §S1. Get that confirmed against the real package before nine other files depend on it.

**Item 9 is where it will want to be clever.** The state machine table in §1.4 is the specification; hand it that table verbatim and require that every row has a corresponding test. If it produces a controller built on booleans and `if` chains instead of an explicit state enum, reject it and re-ask — you will be debugging this code in six months.

---

## 1.16 What Phase 2 inherits

Phase 2 (dictionary engine) needs no changes to anything above. It adds `IPostProcessor` implementations at orders 40–70, populates `AppliedRule`, and adds a `dictionary.json` alongside `config.json`. The pipeline, the state machine, and the injection layer stay untouched.

That property — that Phase 2 is purely additive — is the test of whether Phase 1 was built right. If adding the dictionary requires changing `SessionController`, something in the abstractions is wrong.
