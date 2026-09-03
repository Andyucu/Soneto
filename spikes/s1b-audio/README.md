# S1b — Audio stream open latency and resampling

Throwaway spike per `Docs/soneto-implementation-plan-phase0-1.md`
§"S1b — Audio stream open latency and resampling `[GATE]`". Do not build
product code on top of this — no error handling investment beyond "fail
loudly with a clear message" (see `spikes/s1-asr/README.md` for the same
convention).

Question: with on-demand capture, how long between key-down and the first
real audio sample — and does the polyphase resampler avoid aliasing speech
energy above 8 kHz when the device doesn't natively support 16 kHz?

## What this is

A console app with two independent parts:

```
s1b-audio [--repeat N] [--trial-timeout-ms MS] [--devices-only] [--sweep-only] [--csv path.csv]
```

1. **Device latency harness** (`DeviceLatencyHarness.cs`, `PortAudioExtras.cs`):
   enumerates every PortAudio input-capable device, logs `defaultSampleRate`,
   `maxInputChannels`, host API name, and whether the device supports 16 kHz
   mono float32 directly (`Pa_IsFormatSupported`). For each device, repeats
   `--repeat` (default 20) open/start/first-non-zero-sample trials and
   reports p50/p95 for each phase.
2. **Resampler + sweep validation** (`PolyphaseResampler.cs`,
   `SweepValidation.cs`, `Fft.cs`): a windowed-sinc polyphase resampler,
   validated by generating a 100 Hz → 20 kHz chirp at 48 kHz (and separately
   at 44.1 kHz), resampling to 16 kHz, and checking for aliasing.

Both run by default; `--devices-only` / `--sweep-only` isolate one half.
stdout carries CSV (`resample,...` rows for the sweep test,
`device_index,...` rows for the latency harness); stderr carries
human-readable progress/diagnostic logging, same convention as `s1-asr`.

## Package

`PortAudioSharp2` 1.0.6 (only version line on NuGet; latest as of this spike).
It's a thin, low-level P/Invoke wrapper — no `IsFormatSupported` or
`GetHostApiInfo` binding is exposed by the package itself. Both are standard
exported symbols of the same native `portaudio` shared library the package
already bundles per-RID, so `PortAudioExtras.cs` adds those two missing
declarations directly rather than pulling in a second audio binding. See the
XML doc comment on `PortAudioExtras` for details.

## Part 1 — Resampler and sweep validation (no hardware required)

### Deviation from the plan's literal "32-tap" figure — documented up front

The plan specifies "windowed-sinc polyphase, **32-tap**, with the
anti-aliasing lowpass at 7.8 kHz." A literal 32-tap filter was implemented
first and **failed the sweep test outright** — at 48 kHz input, a cutoff at
7.8 kHz feeding a 16 kHz output's 8 kHz Nyquist is only a **200 Hz**
transition band, and the standard windowed-sinc rule of thumb
(`N ≈ 5.5·Fs/transitionWidth`) puts the taps required for meaningful
stopband attenuation at **~1300**, not 32. With 32 taps the transition band
is on the order of 8 kHz wide — i.e. the filter barely attenuates anything
by the time it reaches the stopband edge, and the resulting resampled sweep
showed essentially the same energy level near 7.8-8 kHz as in the clean
low-frequency passband (0 dB relative difference — a hard fail).

**This is a real, load-bearing finding, not a spike implementation slip**:
if `Soneto.Core`'s eventual resampler is built to a literal "32 taps"
reading of the plan, it will alias, and that aliasing directly degrades
Parakeet's WER on any device that doesn't natively support 16 kHz (most USB
mics, all Bluetooth headsets, and 44.1 kHz-only audio interfaces). The
implementation here (`PolyphaseResampler.cs`) keeps everything else the plan
asks for — windowed-sinc, Blackman window, polyphase structure, 7.8 kHz
cutoff — but computes the actual tap count needed from the requested
transition width instead of hardcoding 32. It also decouples the
polyphase table's phase count (fixed at 32, independent of the exact
input:output ratio) from the tap count, so the awkward 44100:16000 ratio
(reduces to 441:160) doesn't require a polyphase branch count in the
hundreds — sub-sample positions between the 32 stored phases are linearly
interpolated.

**Recommendation for `Soneto.Core` (item 4 in the Phase 1 build order):**
size the filter from the actual transition-width requirement (see
`PolyphaseResampler`'s constructor), not a fixed tap count. At 48 kHz input
this is ~1300 taps; at 44.1 kHz input, ~1200. Both are cheap at these
buffer sizes (a few hundred microseconds of one-time table construction per
resampler instance, and the per-output-sample convolution cost is bounded by
the phase table's per-phase length, not the exact rational ratio).

### Why the sweep test isn't a single plain FFT

A single whole-signal FFT of the resampled output can't, by itself,
distinguish "clean low-frequency chirp energy" from "8-20 kHz content that
aliased down to a low frequency because the anti-aliasing lowpass failed" —
after resampling to 16 kHz, a real signal's spectrum physically cannot show
content "above 8 kHz" (that *is* the new Nyquist); aliased energy folds back
down *into* the 0-8 kHz band and looks, in a plain magnitude spectrum, just
like more chirp.

The decisive test used here (`SweepValidation.Validate`) exploits the fact
that a linear chirp's instantaneous frequency at time `t` is known exactly:

- **Early segment** (`t` where true frequency is 300 Hz - 6 kHz): safely
  in-band; used as the RMS reference level.
- **Late segment** (`t` where true frequency is 9 kHz - 20 kHz): safely
  above the 7.8 kHz cutoff with margin. If the anti-aliasing lowpass worked,
  there is *nothing left* to decimate here — the output should be
  near-silent. If the lowpass is missing or too weak (the "naive
  sample-dropping" failure mode §1.5 warns about explicitly), this content
  aliases straight back into the output band and shows up as RMS energy
  comparable to the early segment.

**Pass criterion:** late-segment RMS at least 40 dB below early-segment RMS.
A full-signal FFT is still computed and reported (`fyi-fullSpectrumPeak...`
in the CSV/log details) purely for documentation of the passband/stopband
shape — it is *not* the pass/fail gate.

### Result

```
resample,inRate,outRate,pass,earlySegmentRmsDbFs,lateSegmentRmsDbFs,suppressionDb
sweep,48000,16000,True,-3.01,-147.01,-144.00
sweep,44100,16000,True,-3.01,-147.06,-144.05
lengths,,,True,,,
```

Both **PASS** by a wide margin — the pass bar is -40 dB suppression;
measured suppression is **~-144 dB** in both cases (i.e. the late segment is
at the FFT's own noise floor, essentially bit-exact silence). Filter sizes
used: `tapsPerPhase=1321` for 48 kHz → 16 kHz, `tapsPerPhase=1213` for
44.1 kHz → 16 kHz (see "Deviation" above for why).

**No aliasing above 8 kHz in the sweep test: PASS**, for both the exact 3:1
ratio (48000 → 16000) and the awkward ratio (44100 → 16000, reduces to
441:160), which the plan calls out as the harder case.

### Known limitation: edge-taper at buffer boundaries (~14ms, first/last)

`PolyphaseResampler.Convolve()` zero-pads whenever the filter's support
window (±`TapsPerPhase`/2 input samples) extends past the start or end of
the input buffer. Concretely, this means the **first and last ~14ms** of
every whole-buffer `Resample()` call (`TapsPerPhase/2 / inRate` — ~660
samples / 48000 Hz ≈ 13.75ms at 48kHz→16kHz, ~606/44100 ≈ 13.7ms at
44.1kHz→16kHz) are filtered against a truncated (zero) context rather than
real samples, rather than the filter's steady-state response. This is a
standard edge-tapering artifact of any FIR/windowed-sinc filter applied to
a finite buffer with zero-padding at the boundary, not a bug specific to
this implementation.

The sweep test above deliberately routes around this: its early/late
segment boundaries exclude the first/last portion of the signal precisely
so the aliasing-suppression measurement isn't contaminated by edge effects
(the segment margin is now derived from `TapsPerPhase` at runtime rather
than a hardcoded constant — see `edgeMarginSec` in
`SweepValidation.Validate`, which also asserts the margin leaves a valid
segment rather than silently trusting it). That means nothing in this
spike's sweep test actually measures how bad the edge-taper artifact is in
practice.

**This should be validated, not assumed, when the real `Soneto.Core`
resampler is built in Phase 1.** It's plausible the effect is already
mitigated for real dictation audio: §1.5's VAD step discards leading and
trailing silence/transients, and a 14ms taper sitting inside a VAD-trimmed
silence margin would be inaudible/inconsequential. But that's a hypothesis,
not a measured fact — VAD trimming and the resampler boundary don't
necessarily line up (e.g. if resampling happens before VAD, or if VAD's own
trim margin is itself only a few ms), and speech content that happens to
start exactly at the capture buffer's first sample would be directly
affected. Recommendation: when building the real resampler, explicitly test
WER/quality impact of a signal that starts speech immediately at t=0 (no
lead-in silence for the taper to eat into), rather than assuming VAD
trimming makes this a non-issue.

### Output-length correctness (§1.13 requirement)

Added `SweepValidation.ValidateOutputLengths()`, which asserts
`Resample()`'s output length exactly matches the expected
`floor(inLen / (inRate/outRate))` sample count for both ratios, at input
lengths one sample either side of several whole-second boundaries (the
classic off-by-one spot). Result: **PASS**, no length mismatches for either
ratio (`lengths,,,True,,,` row in the CSV output above).

### What's deferred

The plan's method also asks to "A/B the S2 corpus resampled 48→16 against
natively-captured 16 kHz and compare WER." **Skipped explicitly** — S2
(`spikes/s2-corpus/`) has not been run yet, so there is no corpus to A/B
against. This is deferred until S2 exists; the pass criterion "resampled WER
within 1% absolute of native 16 kHz WER" is **not yet evaluated** and should
not be considered green until that comparison actually runs.

## Part 2 — Device enumeration and open/start/first-sample latency

Hardware **was** available and testable in this environment (a real
Windows 11 desktop, not a sandboxed/headless CI box) — 21 raw PortAudio
device entries, of which 9 are input-capable across MME, DirectSound,
WASAPI, and WDM-KS host APIs on this machine. No physical USB mic or wired
headset was plugged in beyond the machine's default analog/onboard
microphone path (surfaced under multiple host APIs, see below) and a
paired Bluetooth headset ("Nothing Ear (open)") was present and its
Bluetooth Hands-Free profile endpoint was enumerated, though it never
successfully opened (see below) — so the Bluetooth case the plan explicitly
asks for ("and a Bluetooth headset if you ever intend to dictate on one")
is present in the raw data but did not produce a usable latency number.

Raw results: `results/device-latency-summary.csv` (p50/p95 per device) and
`results/device-latency-trials.csv` (all 20 per-device trial rows).
Reproduce with:

```powershell
dotnet run --project spikes/s1b-audio --devices-only --repeat 20 --trial-timeout-ms 1500 --csv trial-results.csv
```

### Per-device results (20 trials each, 1500ms first-sample timeout)

| Device | Host API | Default rate | 16k mono f32 native? | open p50/p95 (ms) | start p50/p95 (ms) | firstSample p50/p95 (ms) | Notes |
|---|---|---|---|---|---|---|---|
| 1 — Microphone (streamplify Mic.) **[default input]** | MME | 44100 | Yes | 16.9 / 17.9 | 0.2 / 0.3 | **56.2 / 58.3** | Real audio captured every trial |
| 0 — Microsoft Sound Mapper - Input | MME | 44100 | Yes | 17.7 / 18.9 | 0.4 / 0.4 | 57.3 / 59.4 | Routes to the same physical mic as device 1 |
| 4 — Primary Sound Capture Driver | DirectSound | 44100 | Yes | 17.3 / 17.9 | 0.5 / 0.6 | 78.2 / 78.7 | |
| 5 — Microphone (streamplify Mic.) | DirectSound | 44100 | Yes | 17.3 / 17.8 | 0.5 / 0.5 | 78.4 / 78.8 | Same physical mic, DirectSound path |
| 9 — Microphone (streamplify Mic.) | **WASAPI** | 48000 | No | 17.0 / 17.7 | 0.8 / 1.2 | **timed out, 20/20 trials** | Opened and started fine; every callback buffer was all-zero for the full 1.5s wait — see hypothesis below |
| 19 — Microphone (streamplify Mic.) | **WDM-KS** | 48000 | No | 0.2 / 0.7 | 5.0 / 6.4 | **timed out, 20/20 trials** | Same silent-buffer symptom as WASAPI |
| 10 — Stereo Mix (Realtek HD Audio Stereo input) | WDM-KS | 48000 | No | — | — | — | `Pa_StopStream` errored every trial (see below) |
| 11 — Line In (Realtek HD Audio Line input) | WDM-KS | 44100 | No | — | — | — | Same `Pa_StopStream` error |
| 12 — Microphone (Realtek HD Audio Mic input) | WDM-KS | 44100 | No | — | — | — | Same `Pa_StopStream` error |
| 17 — Headset (Bluetooth Hands-Free, "Nothing Ear (open)") | WDM-KS | 8000 | No | — | — | — | `Pa_StartStream` errored every trial — see Bluetooth note below |

### Primary/default mic — pass criterion

**PASS.** The system default input device (index 1, `Microphone
(streamplify Mic.)` via MME) is the one `IAudioCapture` would resolve on a
plain "use the system default" policy per §1.5's "resolve the device fresh
on every key-down" rule. Its time-to-first-non-zero-sample:

- **p50 = 56.2 ms, p95 = 58.3 ms — well under the 150 ms bar.**

This number includes the full open→start→first-real-audio path, matching
the plan's framing exactly ("time between key-down and the first real audio
sample"). Every one of the 20 trials on this device succeeded with a real
non-zero buffer (no silent/muted trials).

### WASAPI and WDM-KS silence — a real finding, not a harness bug

Devices 9 and 19 are **the same physical microphone** as the working device
1, just exposed through different PortAudio host APIs (WASAPI and WDM-KS
respectively). Both opened and started without error, but never delivered a
non-zero sample across 20 trials each (1.5s wait per trial — comfortably
longer than the 56-78ms seen on the working paths). This was re-run twice
with consistent results.

**Working hypothesis, not confirmed:** WASAPI and WDM-KS are the two host
APIs that route through the modern Windows audio privacy/session stack;
MME and DirectSound are legacy Win32 paths that have historically bypassed
some of those checks. If this machine's "Let desktop apps access your
microphone" privacy setting is off, or another app/service holds the
device in a mode that starves WASAPI shared-mode capture, that would
produce exactly this symptom — a stream that opens, starts, and reports
"active" while every callback buffer is silence. **This was not verified
by toggling the setting** (out of scope for a spike, and not this
machine's own settings to change casually); it's recorded here as the most
plausible explanation and a concrete thing to check before assuming the
product's `IAudioCapture` will work identically across host APIs. This
matters directly for Phase 1: if `Soneto.Core.Platform.Windows` ends up
preferring WASAPI (the modern, lower-latency API) over whatever PortAudio's
default happens to pick, it could silently produce empty recordings on a
machine where this setting is off, and it would look exactly like "the mic
didn't hear you" rather than a clear error.

### WDM-KS "Error stopping/starting PortAudio Stream" — real driver-exclusivity friction

Devices 10, 11, 12 (Realtek onboard Stereo Mix / Line In / Mic, all via
WDM-KS) opened successfully but threw on `Pa_StopStream` every single
trial. WDM-KS is kernel-streaming and typically expects **exclusive**
device access; it's plausible another process (or PortAudio's own
MME/DirectSound handles opened moments earlier in the same run) was still
holding a lock on the underlying hardware path. This is consistent with
WDM-KS's known reputation for being the least forgiving host API on
Windows for shared/rapid open-close cycles — exactly the usage pattern
`OnDemand` capture mode requires (open on every key-down, close on every
key-up). **This is itself informative for the capture-mode decision below**:
if the product ever needs a WDM-KS-only device, on-demand's rapid
open/close cycle may be actively hostile to it.

### Bluetooth — `Pa_StartStream` failed every trial

Device 17, the paired Bluetooth headset's Hands-Free (HFP) endpoint,
enumerated at `defaultSampleRate=8000` (immediately visible as a red flag —
narrowband telephony-quality HFP, exactly what §1.5 warns about: "Bluetooth
mic profile switch... A2DP audio you were listening to drops to telephony
quality"). It failed at `Pa_StartStream` on all 20 trials, before ever
reaching the point where a first-sample timing could be measured. This is
**consistent with, though doesn't by itself prove,** the plan's own
prediction: "Opening a BT headset mic triggers an HFP/HSP profile switch...
If you ever dictate through AirPods or a BT headset, on-demand will feel
broken." A hard `Pa_StartStream` failure is arguably a *worse* outcome than
the 300-800ms delay the plan describes — on this device, on-demand doesn't
just feel broken, it doesn't open at all via PortAudio's default stream
parameters. Whether a different `suggestedLatency` or explicit HFP
profile negotiation would fix this is unexplored (would require pairing
attempts and Bluetooth stack debugging out of scope for this spike) — the
finding stands regardless: **this device is not a case for `OnDemand`.**

## Interpretation, per the plan's table

| p95 | Verdict |
|---|---|
| < 150ms | `OnDemand` comfortable, ship as default |
| 150-400ms | `OnDemand` works, ready cue essential |
| > 400ms, or Bluetooth in the mix | `WarmIdle` is the answer |

**This machine's data points in both directions simultaneously**, and the
plan's own interpretation table anticipates exactly that combination:

- The **default input device** (MME path, the one a naive "system default"
  resolution would actually use) is solidly in the "< 150ms, comfortable"
  band — p95 58.3ms.
- **Bluetooth is in the mix** on this machine (paired, enumerated, and
  fails outright via PortAudio rather than merely being slow) — which per
  the plan's own row ("p95 > 400ms, **or Bluetooth in the mix**") already
  tips the recommendation toward `WarmIdle`, independent of the default
  device's good number.

**Recommendation for the capture-mode default:** keep `OnDemand` as
implemented per §1.5's already-locked decision for the common case (it
comfortably clears the latency bar on the default/onboard mic path), but
this spike's data reinforces that `WarmIdle` needs to be genuinely
available and easy to switch to — not a hypothetical fallback — because the
moment a user's default input device is a Bluetooth headset (a real,
present-on-this-machine scenario, not a theoretical one), `OnDemand`
doesn't degrade gracefully, it fails to start the stream at all with the
tested defaults. This doesn't overturn the plan's already-recorded decision
(`OnDemand` default, `WarmIdle` fallback) — it corroborates it with a
concrete failure case rather than a hypothetical one.

## What's left for S1b to be fully green

- [x] Device enumeration: `defaultSampleRate`, supported format probe, host
      API logged per device
- [x] Open / start / first-non-zero-buffer timings measured separately, 20×
      per device, p50/p95 reported
- [x] Real hardware tested on this machine (onboard mic via 4 host APIs,
      one paired Bluetooth headset) — not simulated/fabricated
- [x] Time-to-first-sample p95 < 150ms on the primary/default mic: **PASS**
      (58.3ms)
- [x] Polyphase resampler implemented (windowed-sinc, Blackman window,
      7.8kHz anti-aliasing cutoff, tap count sized from the actual
      transition-width requirement — see "Deviation" above)
- [x] Sweep test (100Hz-20kHz chirp, 48kHz and 44.1kHz → 16kHz, FFT-based
      aliasing check): **PASS**, ~144dB of late-segment suppression on both
      ratios (bar is 40dB)
- [x] Output-length correctness / off-by-one-at-buffer-boundaries test
      (§1.13): **PASS**, see "Output-length correctness" above
- [ ] Edge-taper artifact (~14ms at buffer start/end, see "Known limitation"
      above) is documented but its real-world impact (interaction with VAD
      trimming) is not measured by this spike -- carried forward to Phase 1
- [ ] **S2 corpus WER A/B (resampled 48→16 vs native 16kHz) — explicitly
      deferred, S2 hasn't been run yet.** This is the one pass criterion
      from the plan that is genuinely not yet evaluated, as opposed to
      evaluated-and-passing.
- [ ] WASAPI/WDM-KS silent-capture hypothesis (privacy setting / exclusive
      lock) not confirmed by directly toggling the setting — recorded as a
      finding, not closed out
- [ ] Bluetooth `Pa_StartStream` failure not root-caused (would need
      Bluetooth stack / HFP profile debugging, out of scope for this spike)
- [ ] Not tested on a second machine / a genuine external USB mic — this
      machine's only tested "non-default" input paths are the same onboard
      mic through different host APIs plus one Bluetooth headset

Overall: **S1b passes its two directly-measurable numeric pass criteria**
(time-to-first-sample p95 on the primary mic; no aliasing above 8kHz in the
sweep test) on this machine. The third pass criterion (resampled vs native
WER) is deferred pending S2, not failed.
