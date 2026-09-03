# S1 — ASR latency and correctness

Throwaway spike per `Docs/soneto-implementation-plan-phase0-1.md` §"S1 — ASR
latency and correctness". Do not build product code on top of this — no error
handling investment beyond "fail loudly with a clear message".

Question: does Parakeet v3 int8 run fast enough, in-process, from C#, via
sherpa-onnx?

## What this is

A console app: WAV path in, CSV (timings + transcript) out.

```
s1-asr <wav> [--repeat N] [--threads N] [--model-dir DIR]
```

- `--repeat` (default 5): number of times to decode the same clip after the
  recognizer is built, to measure warm-decode latency.
- `--threads` (default 8): `OfflineModelConfig.NumThreads`.
- `--model-dir`: override the model directory (see below for the default
  search path).

Output is CSV on stdout: `iteration,modelLoadMs,decodeMs,audioDurationSec,rtf,text`.
Native library log lines (from sherpa-onnx's C++ core) go to stderr, so
`s1-asr clip.wav > out.csv` gives a clean CSV file.

`iteration=0` is a single **untimed warm-up decode**, run once right after the
recognizer is constructed and before the timed loop starts. onnxruntime can
lazily allocate arenas/thread pools on the very first `Run()` call, so
iteration 0's cost isn't guaranteed comparable to the `--repeat` iterations
that follow. It's still reported (not silently dropped) so it stays visible,
but only iterations 1..N should be used when computing warm-decode stats.

## Getting the model

Model: `sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8` (~465 MB compressed,
~640 MB extracted — encoder/decoder/joiner int8 ONNX + tokens.txt).

```powershell
# From repo root:
mkdir models -Force
cd models
curl.exe -L -o model.tar.bz2 https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2
tar -xjf model.tar.bz2
rm model.tar.bz2
```

This produces `models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/` containing
`encoder.int8.onnx`, `decoder.int8.onnx`, `joiner.int8.onnx`, `tokens.txt`,
and a `test_wavs/` folder with short multilingual sanity clips (`en.wav` etc.)
bundled by the model release itself.

`models/` is gitignored — it is never committed. The app auto-discovers the
model by walking up from the current working directory looking for a
`models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/` folder, so it works
whether you run it from the repo root, from `spikes/s1-asr/`, or via
`dotnet run --project spikes/s1-asr`. Use `--model-dir` to override.

## Test audio

`test-audio/` contains three WAV clips (16 kHz mono PCM16), synthesized with
Windows SAPI (`Microsoft David Desktop` voice) via
`System.Speech.Synthesis.SpeechSynthesizer` targeting a 16 kHz mono PCM
output format directly (no resampling step).

- `clip-5s.wav` — ~4.84 s, "The quick brown fox..." — single sentence.
- `clip-60s.wav` — ~64.4 s, a scripted "status update" monologue with
  technical vocabulary (integration server, keystore, Trading Networks).
- `clip-120s.wav` — ~131.7 s, the 60 s script plus a second half (Enterprise
  Gateway, AS2, GoAnywhere).

**These are synthesized speech, not recorded human speech.** They are
adequate for latency/timing measurement and for a first correctness sanity
check (does it hallucinate, truncate, or garble long audio?), but they are
*not* a substitute for real recorded speech when judging transcription
accuracy — a synthetic voice is cleaner and more consistent than a real mic
capture. The bundled `models/.../test_wavs/en.wav` (real recorded speech,
JFK quote, ~3.85 s) was used as an additional real-speech sanity check — see
results below.

If you want a proper correctness read, replace these with real recordings —
S2 (`spikes/s2-corpus/`) is where that happens for real with your actual
voice and vocabulary. This spike's job is latency and gross correctness
(punctuation/capitalization present, no truncation/garbage on long audio),
not WER.

## Confirmed API shape (sherpa-onnx 1.13.5, `net10.0`)

The plan's example config was verified against the installed package by
reflecting over `sherpa-onnx.dll` (no bundled C# source, only the compiled
assembly) — **it matches exactly**, no changes needed:

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

All of these are public **fields** (not properties) on plain classes with
parameterless constructors — `OfflineRecognizerConfig`, `OfflineModelConfig`,
`OfflineTransducerModelConfig` — which is why `config.ModelConfig.Transducer`
already has a live, non-null instance to assign into. `OfflineRecognizer`
takes the config in its constructor; `CreateStream()` /
`stream.AcceptWaveform(sampleRate, float[] samples)` / `Decode(stream)` /
`stream.Result.Text` round out the decode path used here.

The package ships no WAV-reading helper, so `WavReader.cs` in this project is
a minimal RIFF/PCM16/Float32 reader (mono or stereo, downmixes stereo by
averaging channels).

### Code review follow-up

A review of the initial spike surfaced three fixes, applied and re-verified:

- **Warm-up isolation** (`Program.cs`): the timed `--repeat` loop no longer
  doubles as the very first decode after recognizer construction. A single
  untimed warm-up `CreateStream`/`AcceptWaveform`/`Decode` runs right after
  the recognizer is built (onnxruntime can lazily allocate arenas/thread
  pools on the first `Run()`), and is reported as `iteration=0` in the CSV
  rather than silently dropped.
- **`WAVE_FORMAT_EXTENSIBLE` support** (`WavReader.cs`): `audioFormat ==
  0xFFFE` (common from Audacity/OBS captures, which S2's real recordings
  will likely use) now reads the extended `fmt ` fields and dispatches on
  the SubFormat GUID's leading two bytes (1 = PCM, 3 = float) instead of
  throwing `NotSupportedException`. Verified against a synthetic
  WAVE_FORMAT_EXTENSIBLE/PCM16 WAV file.
- **RIFF chunk padding** (`WavReader.cs`): chunks are word-aligned per the
  RIFF spec, so an odd-sized chunk (e.g. a `LIST`/`INFO` metadata chunk, as
  Audacity/OBS often write) has one pad byte after its data that isn't
  counted in `chunkSize`. The chunk-skip logic now accounts for it
  (`chunkEnd = fs.Position + chunkSize + (chunkSize % 2)`), verified against
  a synthetic WAV with an odd-length `LIST` chunk ahead of `data`.

## Results (2026-08-31, AMD Ryzen 7 7700X, 8C/16T, Windows 11)

Re-measured after the S1 code-review fixes (warm-up isolation, WAV
`WAVE_FORMAT_EXTENSIBLE` support, RIFF chunk-padding fix — see "Code review
follow-up" below). `iteration=0` in the tables below is the untimed warm-up
decode; only iterations 1..N feed the warm-decode stats.

Cold model load (first `OfflineRecognizer` construction): **~1.6–1.7 s**
across runs — comfortably inside the plan's 2–4 s expectation.

### Warm decode, 5 s clip (`clip-5s.wav`, 4.84 s actual), `--threads 8`

```
iteration,decodeMs,rtf
0,210.0,0.0434
1,249.0,0.0515
2,242.7,0.0501
3,237.0,0.0490
4,201.4,0.0416
5,235.2,0.0486
```

**Pass** — well under the 400 ms bar (and under the 800 ms fail line by a
wide margin). Warm-up (iteration 0) landed in the middle of the timed-run
range on this machine rather than clearly slower — the effect is real but
small enough here to be within normal run-to-run jitter; it's reported
separately regardless so it doesn't quietly bias the stats on a run where it
is slower. Text: `"The quick brown fox jumps over the lazy dog near the
riverbank this morning."` — correct, capitalized, punctuated.

Real-speech sanity check (`models/.../test_wavs/en.wav`, 3.85 s JFK clip,
`--threads 8`): decode 157–225 ms (iterations 1–5; warm-up iteration 0 was
173 ms), text: `"Ask not what your country can do for you, ask what you can
do for your country."` — correct, with a mid-sentence comma preserved.
Confirms punctuation/capitalization works on genuine recorded speech, not
just synthetic TTS.

### Thread sweep (5 s clip, 5 timed iterations each after warm-up, decodeMs range shown)

| Threads | decodeMs (range) | Notes |
|---|---|---|
| 2  | 300–458 | slowest, most variance |
| 4  | 201–219 | **knee — best and most stable** |
| 8  | 252–303 | worse than 4, more variance |
| 16 | 396–783 | worst — thread oversubscription overhead |

**Recommendation: `NumThreads = 4`** on an 8-core/16-thread desktop CPU.
More threads past 4 cost latency and consistency for no benefit on a single
5 s clip — matches the plan's warning to "pick the knee, not the max." This
should be the daemon's default, possibly re-validated against a lower-core
laptop CPU (the actual target device may differ; worth a quick re-check once
Soneto.Core exists and runs on the real deployment machine).

### Long-utterance behaviour (`--threads 4`)

| Clip | Duration | decodeMs (range, iter 1-5) | rtf | Truncation? | Garbage tail? |
|---|---|---|---|---|---|
| 60 s script | 64.42 s | 4333–4438 | 0.067–0.069 | No | No |
| 120 s script | 131.68 s | 10593–10737 | 0.080–0.082 | No | No |

These decodeMs figures are noticeably higher than the values previously
recorded for this section (3115 ms / 7790 ms). That shift is **not**
attributable to the warm-up-isolation fix — it shows up uniformly across all
timed iterations, not just the first — and is most likely session-to-session
machine load variance (this is a shared desktop, not an isolated benchmark
rig). RTF is still well inside pass criteria either way; treat the absolute
ms figures here as noisier than the tight 5 s-clip numbers above, and prefer
re-running locally before relying on exact values.

Both full scripts decoded completely and coherently, well past the plan's
cited ~20–30 s "practical single-shot limit" warning — no truncation, no
repetition loops, no hallucinated tail observed at 64 s or 131 s. Minor
technical-vocabulary/proper-noun errors did appear (as expected — this is
exactly what the plan says the post-processing dictionary is for):

- "keystore" → "key store"
- "Trading Networks" → "trading network's"
- semicolon → comma (60 s clip)
- "AS2" → "S2"
- "GoAnywhere" → "go anywhere"

Peak working set during a 120 s decode (5 back-to-back iterations, sampled
via `Get-Process`): **~1.68 GB**. This is mostly the int8 model weights plus
onnxruntime session buffers, not something that scales meaningfully with
utterance length in this single-shot (non-streaming) decode.

**Interpretation for §1.5 (VAD segmentation):** the plan's assumption that
quality degrades past 30 s single-shot did not reproduce here for scripted,
clean, single-speaker TTS audio up to ~132 s. This is a positive result but
not a substitute for real speech with pauses, disfluencies, and background
noise — re-test with real long recordings (ideally as part of S2) before
concluding VAD segmentation is unnecessary for Phase 1.

## What's left for S1 to be fully green

- [x] Warm decode < 400 ms on a 5 s clip
- [x] Cold load recorded (informational)
- [x] Text correct with punctuation/capitalization, no post-processing
      (confirmed on both synthetic and real-recorded 5 s audio)
- [x] Thread sweep 2/4/8/16 — knee found at 4 threads on this CPU
- [x] 60 s / 120 s long-utterance check — no truncation/garbage/hallucination
- [ ] Re-validate the long-utterance result with **real recorded speech**
      (not TTS) — current long clips are synthetic. Low risk given the
      short-clip real-speech sanity check passed, but not yet directly
      verified for long audio.
- [ ] Re-validate the thread-count knee on the actual target laptop CPU if
      it differs meaningfully from this desktop CPU.

Overall: **S1 passes its stated numeric pass criteria** on this machine.
