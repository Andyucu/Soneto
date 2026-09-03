# Push-to-Talk Dictation App — Build Plan

**App name:** `Soneto`
**Goal:** Hold a key anywhere on the OS, speak, release, and have clean punctuated text land in whatever app has focus. 100% local. Parakeet only. English + Romanian.
**Targets, in priority order:** Windows 11 → Fedora KDE (Wayland) → macOS.

---

## 0. Assumptions (correct me if any are wrong)

| # | Assumption | Impact if wrong |
|---|---|---|
| A1 | Windows is the daily driver; Linux is Fedora KDE 42 on Wayland; macOS is "nice to have" | If macOS becomes primary, a native Swift front-end over CoreML/ANE Parakeet is materially better and the stack recommendation flips |
| A2 | No hard requirement for GPU inference; CPU int8 is acceptable | Changes the runtime EP choice only, not the architecture |
| A3 | Single-user desktop app, no server component, no telemetry | — |
| A4 | You want to own and extend this, not ship it commercially | Affects licensing/notarisation effort only |

---

## 1. The model decision — read this first

You said "Parakeet only." One correction that changes everything downstream:

- **`parakeet-tdt-0.6b-v2` is English-only.** It will not do Romanian. The blog post you linked uses v2.
- **`parakeet-tdt-0.6b-v3` is the one you want.** 600M params, FastConformer-TDT, trained on NVIDIA's Granary corpus, and it covers 25 European languages **including Romanian (`ro`)** and English (`en`). It also emits **punctuation and capitalisation natively**, plus word-level timestamps. Licence CC-BY-4.0, commercial use permitted, attribution required.

Two consequences:

1. **You do not need a separate punctuation/restoration model.** The blog's "cleanup" stage exists because Apple's engine and Whisper-family models need help. v3 doesn't. Budget zero latency for this and only add an optional LLM pass later (see §7.3).
2. **Language detection is automatic and implicit.** There is no language token you can feed the TDT decoder to *force* Romanian or English. You get what it detects. Design around this — see §8.

**Distribution to use:** the sherpa-onnx int8 export — `sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8`, which ships as `encoder.int8.onnx` (~622 MB), `decoder.int8.onnx` (~12 MB), `joiner.int8.onnx` (~6 MB), `tokens.txt` (~92 KB). Roughly 640 MB on disk. Download on first run, verify by SHA-256, never commit to the repo.

---

## 2. Language & framework recommendation

### Recommendation: **C# / .NET 10 + Avalonia 11**

Not because it's your comfort zone (though it is — ModernRemote is already .NET 10), but because of one specific fact that removes the single biggest risk in this project:

> **sherpa-onnx ships an official C# NuGet package (`org.k2fsa.sherpa.onnx`) with prebuilt native runtimes for win-x64, linux-x64, osx-x64 and osx-arm64, and it supports NeMo offline TDT transducers directly.**

That means you never implement the TDT greedy decoding loop, the mel-filterbank feature extractor, or the encoder/decoder/joiner state plumbing yourself. You call `OfflineRecognizer` with three ONNX paths and get text back. Silero VAD is in the same package. This is worth weeks.

The rest of the stack:

| Concern | Library | Notes |
|---|---|---|
| ASR | `org.k2fsa.sherpa.onnx` | Offline transducer, `model_type = "nemo_transducer"` |
| Audio capture | `PortAudioSharp2` | What the official sherpa-onnx dotnet mic examples use; cross-platform |
| Global hook + input synthesis | `SharpHook` (libuiohook) | One API for both capture and simulation on Win/macOS/X11. **Not sufficient on Wayland** — see §5 |
| UI | Avalonia 11 (+ FluentAvalonia or raw) | MVVM; you already think in XAML |
| Storage | SQLite + `Microsoft.Data.Sqlite`, FTS5 for history search | Dictionary stays as a plain JSON file |
| Packaging | Portable ZIP + winget (Win), Flatpak/RPM (Fedora), `.app` + notarisation (macOS) | Mirrors your ModernRemote distribution model |

### The honest alternative: **Rust + Tauri 2**

Genuinely competitive, and better on three axes: binary size (~15 MB vs ~80 MB self-contained .NET), cold start, and the quality of the low-level input crates (`rdev`, `enigo`, `global-hotkey`, `cpal`, `ort`). If this were a greenfield project with no existing skill investment, I'd lean Rust.

**Pick Rust if:** you want the smallest possible always-resident daemon, or you specifically want the Rust practice.
**Pick .NET if:** you want this working in weeks rather than months and want to reuse ModernRemote patterns. That's the recommendation.

### What I'd rule out

- **Electron/Node** — you'd be running a 600 MB model through ONNX Runtime Node bindings inside a 200 MB runtime. Worst of both worlds for a latency-sensitive daemon.
- **Python + Qt** — fine for the spikes (and I recommend using it for exactly that, §10 Phase 0), miserable to distribute.
- **Native per-platform (Swift + C# + GTK)** — three codebases, three dictionary engines. Only justified if macOS is primary.

---

## 3. Architecture

Eight components. The three in bold are where all the real difficulty lives.

```
┌─────────────────────────────────────────────────────────┐
│  Soneto.App (Avalonia)   tray · history · dictionary · settings
└────────────────────────┬────────────────────────────────┘
                         │ in-process, MVVM
┌────────────────────────┴────────────────────────────────┐
│  Soneto.Core  (net10.0, no platform deps)                │
│                                                          │
│  IHotkeySource ──► SessionController ──► ITextInjector   │
│                          │                               │
│              IAudioCapture ──► ring buffer (16k mono f32)│
│                          │                               │
│                    ITranscriber (sherpa-onnx)            │
│                          │                               │
│                    IPostProcessor chain                  │
│                      ├ LanguageProfiler                  │
│                      ├ DictionaryEngine                  │
│                      ├ CommandExpander                   │
│                      └ (optional) LlmPolisher            │
│                          │                               │
│                    IHistoryStore (SQLite)                │
└──────────────────────────────────────────────────────────┘
        ▲                    ▲                    ▲
  Soneto.Platform.Windows  .Linux              .MacOS
```

**Component list:**

1. **Shell** — Avalonia main window, tray icon, and a small always-on-top HUD that appears while recording (level meter + elapsed timer + detected language chip).
2. **Hotkey** *(hard)* — hold-to-talk. Must capture key-down and key-up globally, and must **suppress** the key from reaching the focused app.
3. **Audio** — 16 kHz mono float32, with a **300 ms pre-roll ring buffer** that's always filling. When the key goes down you prepend the pre-roll, so you don't clip the first syllable when you start talking a beat before the key lands. This one detail is the difference between "usable" and "annoying".
4. **VAD** — Silero (bundled with sherpa-onnx). Trims leading/trailing silence before inference. Cuts latency and stops the model hallucinating on empty audio.
5. **ASR** — warm `OfflineRecognizer`, loaded once at startup, never torn down.
6. **Post-processing** — the dictionary chain (§6).
7. **Injection** *(hard)* — get text into the focused app (§5).
8. **History** — SQLite + FTS5, searchable, copy-per-entry, shows which dictionary rules fired.

---

## 4. Latency budget

Target: **key release → text visible ≤ 600 ms** for a 5-second utterance. Wispr Flow's felt latency is roughly this; the blog's Parakeet number (0.27 s for inference alone) is consistent with hitting it.

| Stage | Budget | Notes |
|---|---|---|
| Key-up detected | < 5 ms | hook thread must be free of any work |
| Audio finalise + VAD trim | 20–40 ms | |
| ASR inference (5 s audio, int8, 8 threads CPU) | 200–400 ms | RTF ≈ 0.05–0.08 warm |
| Dictionary pass | < 5 ms | Aho-Corasick, single pass |
| Clipboard set + paste synth + restore | 50–120 ms | dominated by the safety delay before restoring the clipboard |
| **Total** | **~450–600 ms** | |

**Non-negotiables to hit this:**
- Model loaded at app start, kept resident. A cold load is 2–4 s; never do it on the hotkey path.
- Hook callback does nothing but flip a flag and signal — all work on a worker thread. Blocking a low-level keyboard hook on Windows for >~300 ms makes Windows silently unhook you.
- No GPU by default. CUDA/DirectML gives you maybe 100 ms on a 5-second clip and costs VRAM you're already spending on Ollama. Keep it as an opt-in setting.

---

## 5. The hard part: hotkey + injection, per platform

This is where the project actually lives or dies. Build the matrix below as spikes *before* writing any UI.

### 5.1 Hotkey capture

| Platform | Mechanism | Gotchas |
|---|---|---|
| Windows | `WH_KEYBOARD_LL` via SharpHook | Must suppress the key. Hook silently dies if the callback is slow. Won't fire over UAC-elevated windows unless you also run elevated — decide whether you care |
| macOS | `CGEventTap` via SharpHook | Requires **Input Monitoring** *and* **Accessibility** permissions; app must be a proper signed `.app` bundle or the permission won't stick across rebuilds |
| Linux / X11 | libuiohook via SharpHook | Works |
| **Linux / Wayland (your Fedora KDE)** | **libuiohook does not work** — no global key grab exists on Wayland by design | Two options: (a) read `/dev/input/event*` directly via evdev, requires membership in the `input` group; (b) XDG `GlobalShortcuts` portal — works with KDE, but gives you *activation*, not clean press/release semantics for hold-to-talk. **Plan on evdev.** |

**Default key: Right Ctrl (hold).** Rationale: `fn` isn't addressable off Apple hardware; Right Ctrl is present on all three, rarely used, and is a modifier so it won't type a character if suppression fails. Make it fully configurable, and let the user bind a *second* key (see §8).

### 5.2 Text injection

**Primary strategy everywhere: clipboard + synthetic paste.**

```
save current clipboard (all formats you can)
  → set clipboard to transcript (CF_UNICODETEXT / NSPasteboard / wl-copy)
  → synthesise Ctrl+V (Cmd+V on macOS)
  → wait ~150 ms
  → restore original clipboard
```

Why this and not per-character key synthesis:
- **Romanian diacritics.** `ă â î ș ț` and their uppercase forms are not on a US keyboard layout. Character-by-character synthesis has to go through Unicode injection paths that are inconsistent (`ydotool type` in particular is weak here). The clipboard carries UTF-16/UTF-8 cleanly on all three OSes.
- **Speed.** A 40-word transcript typed char-by-char at a safe inter-key delay takes 1–2 seconds. Paste is instant.
- **Electron.** This is the failure the blog post hits: macOS Accessibility `AXUIElement` set-value returns success and does nothing in Cursor/VS Code/Slack. Paste works.

**Per-platform paste synthesis:**

| Platform | Mechanism | Gotchas |
|---|---|---|
| Windows | `SendInput` | Fallback: `SendInput` with `KEYEVENTF_UNICODE` per char for apps that block paste. Also watch for apps with clipboard-history managers hooking you |
| macOS | `CGEventCreateKeyboardEvent` + `CGEventKeyboardSetUnicodeString` | Accessibility permission required |
| Linux X11 | XTEST (`libxdo`) | Straightforward |
| Linux Wayland | `wl-copy` + **ydotool** (uinput) | Needs `ydotoold` running and a udev rule granting access to `/dev/uinput`. Ship a `setup-linux.sh` and a permissions doctor screen |

**Add a per-app fallback table.** Some apps genuinely misbehave. The config should let you say "for `wt.exe`, use char-by-char" or "for `teams.exe`, add 80 ms delay before paste."

### 5.3 Permissions doctor

Build a settings page that actively *tests* each capability and shows red/green:
mic access · global hook active · can synthesise input · clipboard read/write · model files present & hashed · `/dev/uinput` writable · ydotoold running. This will save you hours of "why did nothing happen" over the life of the project.

---

## 6. The dictionary

Two mechanisms, as the blog says — but the balance between them is different for Parakeet TDT, and you need to know why.

### 6.1 Layer 1 — engine biasing (hotwords). Treat as experimental.

sherpa-onnx supports contextual biasing (`hotwords_file` + `hotwords_score`) but **only under `modified_beam_search`**, and NeMo TDT support for modified beam search only landed in early 2026 (PR #3077). There is an open issue reporting that `modified_beam_search` with NeMo TDT/Parakeet **hallucinates or returns empty text roughly 20% of the time**, while `greedy_search` is clean.

**Therefore:**
- Default decoding: `greedy_search`. No biasing.
- Hotword biasing: a settings toggle, off by default, labelled experimental, with an automatic sanity check — if beam-search output is empty or >2× longer than the greedy output for the same audio, fall back to greedy and log it.
- Keep the hotword list short (< 30 entries). Long biasing lists make transducers drift and invent text on quiet audio.

Re-test this when you build; it's an active area of the project and may have improved.

### 6.2 Layer 2 — post-processing. This is the guaranteed path and where you invest.

Entry types:

| Type | Example | Notes |
|---|---|---|
| Vocabulary term | `webMethods`, `Trading Networks`, `GoAnywhere` | Feeds hotwords when enabled; also seeds casing correction |
| Correction pair | `web methods` → `webMethods`, `cloud code` → `Claude Code` | The workhorse |
| Regex rule | `\bIS (\d+)\b` → `IS $1` | Advanced tab, power-user escape hatch |
| Spoken command | `"punct nou"` / `"new paragraph"` → `\n\n` | Voice punctuation & formatting |
| Per-app override | terminal profile: no auto-capitalisation, no trailing period | See §6.5 |

**Matching algorithm:**

1. **Normalise to NFC** first. Non-negotiable for Romanian.
2. **Romanian diacritic equivalence.** `ș` (U+0219, comma-below) and `ş` (U+015F, cedilla) are different codepoints that render nearly identically, and the same applies to `ț`/`ţ`. Real-world Romanian text mixes them constantly. Build a fold function that treats them as equal for *matching*, and always *emit* the correct comma-below forms. Also fold `ă â î` to `a a i` for match-only comparison so someone can add a rule without typing diacritics.
3. **Case-insensitive match, case-preserving replace.** If the transcript says "Webmethods" at the start of a sentence, don't force it to lowercase `webMethods` — but if the rule target has explicit internal casing, honour it.
4. **Longest-match-first, single pass.** Build an Aho-Corasick automaton over all patterns and run once, so a replacement's output can never be re-matched by another rule (that way lies infinite cascades).
5. **Glue-tolerant boundaries.** For multi-word patterns, match on `\s*|-` between the parts, so `webmethods`, `web-methods` and `web methods` all hit.
6. **Never corrupt real words.** Require full-token boundaries. A rule for `cloud` must not touch `Cloudflare`. On rule creation, run the pattern against a bundled word frequency list for both EN and RO and show a warning if it collides with a common word.

**Persistence:** a single `dictionary.json` in the app config dir, hot-reloaded via `FileSystemWatcher`. Editable both in the UI and by hand — you'll want to version it in a Git repo and sync it between machines.

**Observability:** every history entry stores `{raw, final, rulesFired[]}`. The history view shows a diff. Without this you cannot tell whether the dictionary is doing anything, and you'll waste time on rules that never fire.

### 6.3 Seed dictionary

Pre-populate it from your own vocabulary. You will otherwise spend the first week fighting the same twenty words: webMethods, Integration Server, Trading Networks, Enterprise Gateway, Universal Messaging, MFT, GoAnywhere, AS2, EDIINT, Informatica, PowerCenter, IDMC, BusinessObjects, LoadRunner, SonarQube, QuerySurge, Spotfire, Proxmox, Unraid, Avalonia, keystore, truststore, PKCS#12, JKS.

### 6.4 Grammar

Parakeet v3 already gives you punctuation and capitalisation. Beyond that:
- **Deterministic rules only, by default:** collapse double spaces, strip filler (`um`, `ăăă`), fix spacing before punctuation, sentence-case the first word, drop a trailing period in single-word utterances.
- **No LLM in the default path.** See §7.3.

### 6.5 Per-app profiles

Detect the focused window's process name and apply a profile. This matters more than it sounds for how you work:
- **Terminal** (`wt.exe`, `konsole`) — no capitalisation, no trailing punctuation, no smart quotes.
- **Teams / Outlook** — full punctuation, sentence case, expand `"semnătură"` → your signoff.
- **IDE** — code-mode dictionary (`camel case` → camelCase transform, `dot` → `.`).

---

## 7. Language handling (EN + RO)

### 7.1 The constraint

Parakeet v3 auto-detects language per utterance. There is no exposed way to *force* a language in the TDT decoder path. So:

- **You cannot force it.** Don't design a "language selector" that pretends to.
- **Short utterances detect worse than long ones.** A two-second "da, mulțumesc" is a coin flip.
- **Code-switching within one utterance will be rough.** Romanian technical speech is full of English nouns ("am făcut deploy pe Integration Server") and the model will make one choice for the whole clip.

### 7.2 What to do instead

1. **Detect the output language post-hoc** (a small character-n-gram classifier on the transcript is enough — RO has unmistakable markers) and use that to select which dictionary profile and grammar rules to apply.
2. **Bind a second hotkey as a profile hint.** Right Ctrl = EN profile, Right Alt = RO profile. It doesn't change the ASR, but it selects post-processing deterministically, and you know which language you're about to speak.
3. **Maintain a shared "never translate" term list** — your technical vocabulary should survive in English regardless of which profile is active.
4. **Measure it.** Record 30 Romanian sentences in your own voice and your own domain vocabulary, transcribe, and compute WER before you build any UI. If Romanian WER is unacceptable for your actual speech, you need to know that in week one, not month two. This is the single highest-value experiment in the whole plan.

### 7.3 Optional LLM polish pass

You already run Ollama in the homelab. A cleanup pass is tempting. Constraints if you build it:
- **Off by default, and only for utterances over ~15 words.** Short dictation is where latency is felt.
- **Hard timeout of 800 ms**, then emit the un-polished text.
- **A ruthless system prompt:** fix punctuation and obvious disfluency only; never add, remove, reorder or translate content; return only the corrected text.
- **Store both versions** in history so you can audit whether it's helping or quietly rewriting what you said. It will sometimes rewrite what you said. That's the actual risk.

---

## 8. Data & privacy

- Audio is **never written to disk** by default. Add an explicit "keep last 10 clips for debugging" toggle for when you're tuning the dictionary, with auto-purge.
- History is local SQLite. Offer "auto-delete after N days" and a panic-wipe button.
- No network calls at runtime other than the one-time model download and an optional update check.
- Attribution: Parakeet is CC-BY-4.0 — put NVIDIA's attribution in the About box and the README.

---

## 9. Repository layout

```
soneto/
├── src/
│   ├── Soneto.Core/                 # net10.0 — no platform refs
│   │   ├── Audio/                  # ring buffer, resampler, VAD
│   │   ├── Asr/                    # sherpa-onnx wrapper, model manager
│   │   ├── Dictionary/             # Aho-Corasick engine, normalisation, rules
│   │   ├── Pipeline/               # SessionController state machine
│   │   └── Abstractions/           # IHotkeySource, ITextInjector, ...
│   ├── Soneto.Platform.Windows/
│   ├── Soneto.Platform.Linux/       # X11 + Wayland/evdev/uinput
│   ├── Soneto.Platform.MacOS/
│   ├── Soneto.App/                  # Avalonia
│   └── Soneto.Cli/                  # headless daemon — also the spike harness
├── tests/
│   ├── Soneto.Core.Tests/
│   └── Soneto.Corpus/               # your recorded EN + RO test clips + expected text
├── models/                         # gitignored, populated on first run
├── scripts/
│   ├── setup-linux.sh              # ydotoold, udev rule, input group
│   └── fetch-models.ps1 / .sh
└── docs/
    ├── ARCHITECTURE.md
    ├── PLATFORM-NOTES.md
    └── DICTIONARY.md
```

**Design rule:** `Soneto.Core` must build and its tests must pass with no platform assembly referenced. Every OS-specific behaviour sits behind an interface with a `Null*` implementation for tests. This is what lets you develop the dictionary engine on any machine and only touch platform code when you're deliberately doing platform work.

---

## 10. Phased plan

### Phase 0 — Spikes (target: 1 week, throwaway code)

Do these in **Python** if it's faster for you; the point is answering questions, not writing product code. Each spike is a yes/no gate.

| ID | Spike | Success criterion |
|---|---|---|
| S1 | Parakeet v3 int8 via sherpa-onnx C# — WAV in, text out | Warm inference on a 5 s clip < 400 ms; cold load time measured |
| S2 | **Romanian accuracy on your voice** | 30 self-recorded RO sentences, WER computed. Gate: is this usable at all? |
| S3 | Global hold-to-talk hotkey with suppression, Windows | Key held/released detected, character does not reach Notepad |
| S4 | Clipboard paste injection into VS Code, Chrome, Teams, Outlook, Windows Terminal | Text with `ăâîșț` lands correctly and unmangled in all five |
| S5 | **Fedora KDE Wayland: evdev read + ydotool inject** | Same as S3+S4 on Wayland. Highest-risk item in the project |
| S6 | Hotword biasing under `modified_beam_search` on v3 | Does it hallucinate? Measure the empty/garbage rate over 50 clips |

**If S5 fails or is too ugly**, decide now: ship Windows-only first, or fall back to X11 session on Fedora. Don't discover this in month three.

### Phase 1 — Headless vertical slice (1–2 weeks)

`Soneto.Cli` only. No UI at all.
Hold key → record → VAD trim → transcribe → clipboard paste. Config from a JSON file. Console logging with timings for every stage.

**Acceptance:** you can dictate a paragraph into VS Code from a terminal-launched daemon, end-to-end under 600 ms, and you'd genuinely use it. If you wouldn't use it yet, the UI won't fix that.

### Phase 2 — Dictionary engine (1 week)

Full Aho-Corasick engine, NFC + Romanian folding, all five entry types, hot-reload, rule-fired logging. Heavily unit-tested against `Soneto.Corpus` — this is pure logic and should have near-100% coverage. No UI yet; edit the JSON by hand.

**Acceptance:** a test suite of ~100 EN and RO pairs passes, including the adversarial ones (`cloud` must not touch `Cloudflare`; `ș` vs `ş` both match; casing preserved).

### Phase 3 — Avalonia shell (2 weeks)

Tray icon, main window, searchable history (FTS5) with per-entry copy and rule-fired diff, live level meter, settings (hotkey, model, threads, language profile bindings), permissions doctor. Define **design tokens first** — colour, type scale, spacing, radius, border, shadow, motion — and have every view pull from them. That advice from the blog post is correct and worth following.

### Phase 4 — Platform hardening (2 weeks)

Linux Wayland path productionised, macOS `.app` bundle + entitlements + permission flow, per-app profile table, per-app injection fallbacks, crash/hook-death recovery (Windows will unhook you eventually — detect and re-register).

### Phase 5 — Polish (1–2 weeks)

Recording HUD, first-run wizard including model download with progress and hash verification, optional LLM polish pass, dictionary import/export, auto-start on login.

### Phase 6 — Distribution (1 week)

Portable ZIP + winget manifest (same model as ModernRemote), Fedora RPM or Flatpak, macOS notarised DMG if you're going there. GitHub Actions matrix build for all three.

**Realistic total: 8–11 weeks of evenings-and-weekends effort.** Phase 0 + 1 gets you something you use daily in 2–3 weeks, which is the part that matters.

---

## 11. Risk register

| Risk | Likelihood | Mitigation |
|---|---|---|
| Romanian WER unacceptable on your domain vocabulary | Medium | S2 gates the whole project. If bad, the dictionary layer carries more weight, or you accept EN-only dictation |
| Wayland input/injection is too fragile | **High** | S5 early. Fallback: X11 session, or Windows-only v1 |
| Hotword biasing hallucinates (known open issue) | High | Greedy by default; post-processing is the guaranteed path. Already designed around |
| macOS permissions/notarisation rabbit hole for a .NET app | Medium | Defer macOS to Phase 4; treat as optional |
| Windows low-level hook silently dies | Medium | Watchdog + auto re-register; keep hook callback trivially fast |
| Clipboard clobbering annoys you in daily use | Medium | Restore original clipboard; add a "never touch clipboard" mode using char-synth as fallback |
| Model download (640 MB) fails or gets corrupted | Low | Resumable download + SHA-256 verify + re-download on mismatch |
| Scope creep into a Wispr Flow clone | **High** | Phases 0–2 are the product. Everything after is optional |

---

## 12. Opening prompt for Claude Code

Hand it this repo plan plus:

```
Read docs/ARCHITECTURE.md and PLATFORM-NOTES.md in this repo.

We are building a local push-to-talk dictation app in .NET 10 with Avalonia.
The ASR engine is NVIDIA parakeet-tdt-0.6b-v3 (multilingual, includes Romanian),
run via the sherpa-onnx C# NuGet package (org.k2fsa.sherpa.onnx) using the
int8 ONNX export. Do not substitute Whisper or any other model.

Start with Phase 0 spike S1 ONLY: a console project (src/Soneto.Cli) that
loads the model once, transcribes a 16kHz mono WAV passed as an argument,
and prints the text plus timings for: model load, feature extraction,
inference, total. Include a --repeat N flag so I can measure warm inference
separately from cold load.

Do not build UI. Do not build the hotkey or injection layer yet.
Do not add a second ASR backend.

Show me the model-loading and recognizer configuration code before you write
the rest, so I can check the model_type and decoding_method are right.
```

Then work the spike table in order. The discipline that matters: **one spike per session, each with a measurable pass/fail**, and don't let it start writing the Avalonia app until S1–S5 have all gone green.

---

## 13. Key references

- Model card & language list: `huggingface.co/nvidia/parakeet-tdt-0.6b-v3`
- sherpa-onnx NeMo transducer models (int8 download): `k2-fsa.github.io/sherpa/onnx/pretrained_models/offline-transducer/nemo-transducer-models.html`
- sherpa-onnx C# API + dotnet examples: `k2-fsa.github.io/sherpa/onnx/csharp-api/`
- Hotwords / contextual biasing docs: `k2-fsa.github.io/sherpa/onnx/hotwords/`
- Known NeMo TDT + modified_beam_search issue: `github.com/k2-fsa/sherpa-onnx/issues/3267`
- Reference implementation to read (not copy): `github.com/per-simmons/murmur-youtube` — the `windows/` folder is C# + Avalonia + Parakeet
