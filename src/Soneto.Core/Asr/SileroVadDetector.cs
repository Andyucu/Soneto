using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SherpaOnnx;
using Soneto.Core.Configuration;

namespace Soneto.Core.Asr;

/// <summary>
/// Result of running <see cref="SileroVadDetector.Trim"/> on a buffer of 16kHz mono float
/// samples: the leading/trailing-silence-trimmed audio, the boundaries it was trimmed at,
/// and whether the whole buffer should be discarded as effectively empty (plan §1.4's
/// Finalizing state: "VAD trim; if speech &lt; 300 ms → discard").
/// </summary>
/// <param name="TrimmedSamples">
/// The audio from <see cref="SpeechStartSample"/> to <see cref="SpeechEndSample"/>
/// (inclusive of any short internal pauses under <c>MinSilenceMs</c>, which Silero itself
/// treats as still-part-of-the-same-utterance, not a boundary). Empty if no speech was
/// detected at all, or if <see cref="ShouldDiscard"/> is true.
/// </param>
/// <param name="SpeechStartSample">Index into the original buffer where detected speech begins.</param>
/// <param name="SpeechEndSample">Index into the original buffer where detected speech ends (exclusive).</param>
/// <param name="LeadingSilenceTrimmed">Duration trimmed from the start.</param>
/// <param name="TrailingSilenceTrimmed">Duration trimmed from the end.</param>
/// <param name="TotalSpeechDuration">
/// Duration of <see cref="TrimmedSamples"/> (i.e. <c>SpeechEndSample - SpeechStartSample</c>
/// converted to time) — this is what gets compared against the discard threshold, not the
/// sum of individual speech-only sub-segments.
/// </param>
/// <param name="ShouldDiscard">
/// True if <see cref="TotalSpeechDuration"/> is under <see cref="VadConfig.MinUtteranceMs"/>
/// (see <see cref="SileroVadDetector"/>'s class doc comment for why this is a distinct knob
/// from <see cref="VadConfig.MinSpeechMs"/>) — the caller should treat this utterance as
/// empty/near-empty and not send it to the transcriber at all, per plan §1.5's "this is the
/// main defence against decoding empty audio, which is where transducers hallucinate."
/// </param>
public sealed record VadTrimResult(
    ReadOnlyMemory<float> TrimmedSamples,
    int SpeechStartSample,
    int SpeechEndSample,
    TimeSpan LeadingSilenceTrimmed,
    TimeSpan TrailingSilenceTrimmed,
    TimeSpan TotalSpeechDuration,
    bool ShouldDiscard);

/// <summary>
/// Silero VAD (via sherpa-onnx's <see cref="VoiceActivityDetector"/>) trimming component,
/// per plan §1.5: "Silero from the sherpa-onnx package. Trim leading and trailing silence
/// before inference... If total speech after trim is under 300 ms, discard and log — this
/// is the main defence against decoding empty audio, which is where transducers
/// hallucinate." Operates on a whole in-memory buffer of 16kHz mono float samples (the same
/// shape <c>IAudioCapture.EndCapture()</c> produces) — not a streaming/incremental API. The
/// actual wiring between capture and the transcriber is item 9's <c>SessionController</c>
/// (Finalizing state), not built here.
///
/// <para>
/// <b>sherpa-onnx API shape, confirmed by reflecting over the actual installed package
/// (1.13.5) rather than assumed:</b> the plan doesn't give a config example for VAD the way
/// it does for the ASR recognizer, so this was verified from scratch. The real surface is
/// <c>SherpaOnnx.VoiceActivityDetector</c> (constructed from a <c>VadModelConfig</c> — which
/// nests a <c>SileroVadModelConfig</c> with <c>Model</c>/<c>Threshold</c>/
/// <c>MinSilenceDuration</c>/<c>MinSpeechDuration</c>/<c>WindowSize</c>/
/// <c>MaxSpeechDuration</c> fields — plus a buffer-size-in-seconds float), fed via
/// <c>AcceptWaveform(float[])</c> in fixed-size window chunks, flushed at end-of-stream via
/// <c>Flush()</c>, and drained via the <c>IsEmpty()</c>/<c>Front()</c>/<c>Pop()</c>
/// trio — <c>Front()</c> returns a <c>SpeechSegment</c> with a sample-index <c>Start</c> and
/// a <c>Samples</c> array. This matches what sherpa-onnx's own C#/Python VAD demos do
/// (feed in <c>WindowSize</c>-sized chunks, not arbitrary-sized ones) and is NOT the same
/// shape as <see cref="SherpaOnnxTranscriber"/>'s single-call <c>AcceptWaveform</c> +
/// <c>Decode</c> — worth calling out since it would be easy to assume they matched.
/// </para>
///
/// <para>
/// <b>Model file provenance:</b> unlike the ~640MB Parakeet ASR model (which needs
/// <see cref="ModelManager"/>'s full download/SHA-256-verify/extract story), the Silero VAD
/// ONNX model is a plain ~630KB file with no separate download mechanism in the
/// sherpa-onnx NuGet package itself (confirmed by inspecting the installed package's
/// contents — it ships compiled native/managed bindings only, no bundled model asset).
/// Given the size difference (3+ orders of magnitude smaller), this ships as a plain
/// embedded resource in <c>Soneto.Core</c> (<c>Asr/Resources/silero_vad.onnx</c>,
/// <c>EmbeddedResource</c> in the csproj) rather than reusing <see cref="ModelManager"/>'s
/// download machinery — no first-run network fetch, no hash-verify-on-download dance, and
/// it's available offline from a fresh checkout the same way <c>warmup-en.wav</c> is.
/// Fetched from the same k2-fsa/sherpa-onnx release feed the ASR model uses
/// (<c>https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx</c>,
/// fetched 2026-08-31, SHA-256 <c>9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6</c>
/// computed independently at download time) — the file is committed to source (unlike the
/// gitignored ~465MB ASR model archive) since it's small enough to treat as ordinary source.
/// <b>Licensing:</b> upstream Silero VAD (<c>snakers4/silero-vad</c> on GitHub) is
/// MIT-licensed — verified against the upstream repo's <c>LICENSE</c> file, not assumed —
/// which permits redistributing the compiled ONNX weights as a committed binary in this
/// repo.
/// </para>
///
/// <para>
/// <b>Whole-utterance discard floor is a separate config field from
/// <see cref="VadConfig.MinSpeechMs"/>, not a reuse of it:</b> the plan's prose gives three
/// distinct numbers in the same paragraph — Silero's per-segment <c>threshold: 0.5</c>,
/// Silero's per-segment <c>minSpeechDurationMs: 250</c> filter, and a separate
/// whole-utterance "if total speech after trim is under 300 ms, discard" floor. An earlier
/// version of this component conflated the last two by reusing
/// <see cref="VadConfig.MinSpeechMs"/> for both purposes, which made the discard check
/// below structurally near-unreachable: since Silero's own native <c>MinSpeechDuration</c>
/// filter already refuses to emit any segment shorter than <see cref="VadConfig.MinSpeechMs"/>,
/// and <c>TotalSpeechDuration</c> spans from the first segment's start to the last segment's
/// end, <c>TotalSpeechDuration</c> is by construction always &gt;= the length of any single
/// native segment that produced it — so "total speech &lt; MinSpeechMs" could basically only
/// ever be true when zero segments were found at all, a case the "no speech detected" branch
/// already handles on its own. <see cref="VadConfig"/> now has a dedicated
/// <see cref="VadConfig.MinUtteranceMs"/> field (default 300, matching the plan's literal
/// number) for this whole-utterance floor, kept independent of
/// <see cref="VadConfig.MinSpeechMs"/> (which continues to drive Silero's native per-segment
/// filter only) so the discard check is an actually-reachable, functioning safety net rather
/// than dead code.
/// </para>
/// </summary>
public sealed class SileroVadDetector : IDisposable
{
    private const string VadModelResourceName = "Soneto.Core.Asr.Resources.silero_vad.onnx";
    private const int SampleRate = 16000;

    /// <summary>
    /// Silero VAD's standard analysis window at 16kHz (32ms/frame) — matches sherpa-onnx's
    /// own C#/Python VAD demo defaults. Not exposed via <see cref="VadConfig"/> since the
    /// plan doesn't call it out as a tunable and there's no reason to vary it.
    /// </summary>
    private const int WindowSize = 512;

    /// <summary>
    /// Forces Silero to end a segment and start a new one after this many seconds of
    /// continuous speech, regardless of silence. Set to 20s to match S1's own confirmed
    /// finding (Docs/soneto-implementation-plan-phase0-1.md §1.5, "practical single-shot
    /// limit ... roughly 20-30 seconds") — a single continuous speech run longer than this
    /// wouldn't decode cleanly in one shot anyway, so there's no benefit to letting Silero's
    /// internal buffer grow past it. Multiple forced segments from one long utterance are
    /// transparently spanned by <see cref="Trim"/> (it uses the first segment's start and
    /// the last segment's end), so this doesn't affect trimming correctness for anything
    /// under item 5's scope (VAD-based long-utterance segmentation is explicitly deferred
    /// to whichever future item needs it, per plan §1.5's "if single-shot degrades past
    /// ~30s, implement VAD-based segmentation" — not this one).
    /// </summary>
    private const float MaxSpeechDurationSeconds = 20f;

    /// <summary>
    /// Internal native ring-buffer capacity for the VAD's own segment-building buffer.
    /// Must comfortably exceed <see cref="MaxSpeechDurationSeconds"/> plus a silence margin.
    /// </summary>
    private const float BufferSizeSeconds = 30f;

    private static readonly object ExtractLock = new();
    private static string? s_extractedModelPath;

    private readonly ILogger<SileroVadDetector> _logger;
    private readonly VadConfig _config;
    private readonly VoiceActivityDetector? _vad; // null when VadConfig.Enabled is false

    public SileroVadDetector(ILogger<SileroVadDetector> logger, VadConfig config)
    {
        _logger = logger;
        _config = config;

        if (!config.Enabled)
        {
            _logger.LogInformation("VAD disabled via config; SileroVadDetector will pass audio through untrimmed.");
            _vad = null;
            return;
        }

        string modelPath = EnsureModelExtracted();

        var vadConfig = new VadModelConfig
        {
            SampleRate = SampleRate,
            NumThreads = 1,
            Provider = "cpu",
            Debug = 0,
        };
        vadConfig.SileroVad.Model = modelPath;
        vadConfig.SileroVad.Threshold = (float)config.Threshold;
        vadConfig.SileroVad.MinSilenceDuration = config.MinSilenceMs / 1000f;
        vadConfig.SileroVad.MinSpeechDuration = config.MinSpeechMs / 1000f;
        vadConfig.SileroVad.WindowSize = WindowSize;
        vadConfig.SileroVad.MaxSpeechDuration = MaxSpeechDurationSeconds;

        _logger.LogInformation(
            "Loading Silero VAD model from {ModelPath} (threshold={Threshold}, "
            + "minSilenceMs={MinSilenceMs}, minSpeechMs={MinSpeechMs})",
            modelPath, config.Threshold, config.MinSilenceMs, config.MinSpeechMs);

        _vad = new VoiceActivityDetector(vadConfig, BufferSizeSeconds);
    }

    /// <summary>
    /// User-scoped cache directory for the extracted VAD model — deliberately NOT the shared
    /// system temp directory (<c>Path.GetTempPath()</c>), which on a shared/multi-user Linux
    /// box is often world-writable, meaning a same-length file placed there by another local
    /// process/user could otherwise be silently accepted and loaded into the native VAD.
    /// Mirrors <see cref="ModelManager"/>'s own use of
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> for the ASR model cache.
    /// </summary>
    private static string ResolveModelCacheDir()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return OperatingSystem.IsWindows()
            ? Path.Combine(baseDir, "Soneto", "vad")
            : Path.Combine(baseDir, "soneto", "vad");
    }

    /// <summary>
    /// Extracts the embedded Silero VAD ONNX model to a stable path on disk exactly once
    /// per process (the native library needs a real file path, not a byte buffer). Reused
    /// across instances/calls via a static cache — idempotent and safe to call repeatedly;
    /// re-extracts only if the cached file is missing or its SHA-256 content hash doesn't
    /// match the embedded resource (content-based, not just length-based, so a same-length
    /// file left by something else on a shared machine can't be silently trusted — mirrors
    /// item 3's <c>ModelManager</c> hash-verify story for the same class of concern).
    ///
    /// <para>
    /// Written via a process-unique-named temp file followed by an atomic
    /// <see cref="File.Move(string, string, bool)"/> onto the final path (same pattern as
    /// <see cref="HttpModelArchiveDownloader"/>'s own fix for the equivalent bug), so two
    /// Soneto processes racing to extract at the same time (e.g. two daemon instances, or a
    /// demo racing a live daemon) can't corrupt each other's write — the in-process
    /// <see cref="ExtractLock"/> only protects against races within a single process.
    /// </para>
    /// </summary>
    private static string EnsureModelExtracted()
    {
        lock (ExtractLock)
        {
            if (s_extractedModelPath != null && File.Exists(s_extractedModelPath))
                return s_extractedModelPath;

            var asm = Assembly.GetExecutingAssembly();
            using var resourceStream = asm.GetManifestResourceStream(VadModelResourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded VAD model resource '{VadModelResourceName}' not found in {asm.FullName}.");

            using var resourceBytesStream = new MemoryStream();
            resourceStream.CopyTo(resourceBytesStream);
            byte[] resourceBytes = resourceBytesStream.ToArray();
            byte[] resourceHash = SHA256.HashData(resourceBytes);

            string cacheDir = ResolveModelCacheDir();
            Directory.CreateDirectory(cacheDir);
            string targetPath = Path.Combine(cacheDir, "silero-vad.onnx");

            bool needsWrite = true;
            if (File.Exists(targetPath))
            {
                byte[] existingHash = SHA256.HashData(File.ReadAllBytes(targetPath));
                needsWrite = !existingHash.AsSpan().SequenceEqual(resourceHash);
            }

            if (needsWrite)
            {
                // Process-unique intermediate name (mirrors HttpModelArchiveDownloader's
                // fix for the same class of bug) so a concurrent Soneto process extracting
                // at the same time writes to its own file, then File.Move onto the final
                // path atomically (same filesystem, so this is a rename, not a copy) rather
                // than risking a half-written file at targetPath being read concurrently.
                string tempPath = Path.Combine(
                    cacheDir, $"silero-vad.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
                try
                {
                    File.WriteAllBytes(tempPath, resourceBytes);
                    File.Move(tempPath, targetPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
            }

            s_extractedModelPath = targetPath;
            return targetPath;
        }
    }

    /// <summary>
    /// Trims leading and trailing silence from <paramref name="samples16k"/> using the
    /// configured threshold/min-silence/min-speech parameters, per plan §1.5. If
    /// <see cref="VadConfig.Enabled"/> is false, passes the audio through untrimmed
    /// (<see cref="VadTrimResult.ShouldDiscard"/> always false in that case — VAD being off
    /// means the caller has opted out of this defence entirely, not that everything is
    /// speech).
    /// </summary>
    public VadTrimResult Trim(ReadOnlyMemory<float> samples16k)
    {
        if (_vad is null)
        {
            return new VadTrimResult(
                samples16k, 0, samples16k.Length, TimeSpan.Zero, TimeSpan.Zero,
                TimeSpan.FromSeconds(samples16k.Length / (double)SampleRate),
                ShouldDiscard: false);
        }

        float[] samples = samples16k.ToArray();

        // VoiceActivityDetector is stateful; reset before each independent buffer so a
        // previous call's leftover internal state can't bleed into this one.
        _vad.Reset();

        // One reusable scratch buffer for every 32ms window, instead of allocating a fresh
        // array per iteration (e.g. ~94 allocations for a 3s utterance) -- AcceptWaveform
        // only reads from it synchronously, so it's safe to overwrite and reuse each pass.
        var window = new float[WindowSize];

        int offset = 0;
        while (offset + WindowSize <= samples.Length)
        {
            samples.AsSpan(offset, WindowSize).CopyTo(window);
            _vad.AcceptWaveform(window);
            offset += WindowSize;
        }

        // Feed the tail, zero-padded to a full window, so trailing audio shorter than one
        // window is still analysed instead of silently dropped (the padding itself is
        // harmless -- it's treated as a few extra milliseconds of trailing silence, which
        // MinSilenceDuration already tolerates).
        if (offset < samples.Length)
        {
            Array.Clear(window);
            samples.AsSpan(offset).CopyTo(window);
            _vad.AcceptWaveform(window);
        }

        _vad.Flush();

        int? firstStart = null;
        int lastEnd = 0;
        while (!_vad.IsEmpty())
        {
            SpeechSegment segment = _vad.Front();
            int start = segment.Start;
            int end = start + segment.Samples.Length;
            firstStart ??= start;
            lastEnd = end;
            _vad.Pop();
        }

        if (firstStart is null)
        {
            _logger.LogInformation(
                "VAD detected no speech in {DurationMs:F0}ms buffer; discarding entirely.",
                samples.Length / (double)SampleRate * 1000.0);
            return new VadTrimResult(
                ReadOnlyMemory<float>.Empty, 0, 0,
                LeadingSilenceTrimmed: TimeSpan.FromSeconds(samples.Length / (double)SampleRate),
                TrailingSilenceTrimmed: TimeSpan.Zero,
                TotalSpeechDuration: TimeSpan.Zero,
                ShouldDiscard: true);
        }

        int start2 = firstStart.Value;
        int end2 = Math.Min(lastEnd, samples.Length);
        if (end2 < start2)
            end2 = start2;

        var trimmed = new float[end2 - start2];
        Array.Copy(samples, start2, trimmed, 0, trimmed.Length);

        var leadingTrimmed = TimeSpan.FromSeconds(start2 / (double)SampleRate);
        var trailingTrimmed = TimeSpan.FromSeconds((samples.Length - end2) / (double)SampleRate);
        var totalSpeech = TimeSpan.FromSeconds(trimmed.Length / (double)SampleRate);

        // See class doc comment: the discard threshold is the dedicated MinUtteranceMs
        // floor, deliberately distinct from MinSpeechMs (which drives Silero's native
        // per-segment filter above, not this whole-utterance check).
        bool discard = totalSpeech.TotalMilliseconds < _config.MinUtteranceMs;

        _logger.LogInformation(
            "VAD: speech detected from {StartMs:F0}ms to {EndMs:F0}ms (discarding {LeadMs:F0}ms leading, "
            + "{TrailMs:F0}ms trailing silence); total speech {SpeechMs:F0}ms{Discard}",
            leadingTrimmed.TotalMilliseconds,
            leadingTrimmed.TotalMilliseconds + totalSpeech.TotalMilliseconds,
            leadingTrimmed.TotalMilliseconds,
            trailingTrimmed.TotalMilliseconds,
            totalSpeech.TotalMilliseconds,
            discard ? $" -- DISCARDING (< {_config.MinUtteranceMs}ms minimum)" : "");

        return new VadTrimResult(
            trimmed, start2, end2, leadingTrimmed, trailingTrimmed, totalSpeech, discard);
    }

    public void Dispose() => _vad?.Dispose();
}
