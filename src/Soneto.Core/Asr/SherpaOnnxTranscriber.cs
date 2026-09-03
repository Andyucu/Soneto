using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SherpaOnnx;
using Soneto.Core.Abstractions;
using Soneto.Core.Wav;

namespace Soneto.Core.Asr;

/// <summary>
/// <see cref="ITranscriber"/> implementation over sherpa-onnx / Parakeet v3 int8, promoted
/// from <c>spikes/s1-asr/Program.cs</c>'s confirmed <c>OfflineRecognizerConfig</c> API
/// shape (see Docs/soneto-implementation-plan-phase0-1.md §S1 and
/// Docs/PROJECT-MEMORY.md) — with real error handling this time, per the plan's principle
/// that "the daemon never exits on a recoverable error."
///
/// <see cref="TranscribeAsync"/> serialises decode calls behind a <see cref="SemaphoreSlim"/>
/// per plan §1.4's threading model ("ASR: serialised behind a SemaphoreSlim(1,1) — one
/// decode at a time"). Note the underlying native <c>Decode</c> call is a blocking,
/// non-cancellable operation once started — the <see cref="CancellationToken"/> passed to
/// <see cref="TranscribeAsync"/> can only pre-empt a decode that hasn't started yet.
/// </summary>
public sealed class SherpaOnnxTranscriber : ITranscriber
{
    private const string WarmupResourceName = "Soneto.Core.Asr.Resources.warmup-en.wav";

    private readonly ILogger<SherpaOnnxTranscriber> _logger;
    private readonly string _modelDir;
    private readonly int _numThreads;
    private readonly string _decodingMethod;
    private readonly SemaphoreSlim _decodeGate = new(1, 1);

    private OfflineRecognizer? _recognizer;
    private volatile bool _isReady;

    public SherpaOnnxTranscriber(
        ILogger<SherpaOnnxTranscriber> logger,
        string modelDir,
        int numThreads = 4,
        string decodingMethod = "greedy_search")
    {
        _logger = logger;
        _modelDir = modelDir;
        _numThreads = numThreads;
        _decodingMethod = decodingMethod;
    }

    public bool IsReady => _isReady;

    public Task InitializeAsync(CancellationToken ct) => Task.Run(() => Initialize(ct), ct);

    private void Initialize(CancellationToken ct)
    {
        // Plan §1.12, "Model files missing": fail loud at startup, not at first use.
        var missing = ModelManager.MissingFiles(_modelDir);
        if (missing.Count > 0)
        {
            throw new ModelFilesMissingException(
                $"Model dir '{_modelDir}' is missing required file(s): {string.Join(", ", missing)}.");
        }

        ct.ThrowIfCancellationRequested();

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = Path.Combine(_modelDir, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(_modelDir, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(_modelDir, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(_modelDir, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = _numThreads;
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = _decodingMethod;

        _logger.LogInformation(
            "Loading ASR model from {ModelDir} (numThreads={NumThreads}, decodingMethod={DecodingMethod})",
            _modelDir, _numThreads, _decodingMethod);

        var loadSw = Stopwatch.StartNew();
        OfflineRecognizer recognizer;
        try
        {
            recognizer = new OfflineRecognizer(config);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to construct the sherpa-onnx OfflineRecognizer from model dir '{_modelDir}'. "
                + "This usually means a model file is corrupt or from an incompatible sherpa-onnx "
                + "version.", ex);
        }
        loadSw.Stop();
        _logger.LogInformation("ASR cold model load completed in {LoadMs:F0}ms", loadSw.Elapsed.TotalMilliseconds);

        // Warm-up per plan §1.6: decode a bundled ~1s clip of REAL speech, never silence
        // (silence lets the TDT decoder skip work via blank-frame early exit and warms up
        // the wrong code path). Logged separately from cold-load time.
        string warmupText;
        double warmupMs;
        try
        {
            var warmupClip = LoadWarmupClip();
            var warmupSw = Stopwatch.StartNew();
            using var warmupStream = recognizer.CreateStream();
            warmupStream.AcceptWaveform(warmupClip.SampleRate, warmupClip.Samples);
            recognizer.Decode(warmupStream);
            warmupText = warmupStream.Result.Text ?? string.Empty;
            warmupSw.Stop();
            warmupMs = warmupSw.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex) when (ex is not ModelFilesMissingException)
        {
            recognizer.Dispose();
            throw new InvalidOperationException(
                "ASR warm-up decode threw an exception on a known-good real-speech clip — the model "
                + "or tokens file appears wrong. Refusing to mark the transcriber ready.", ex);
        }

        _logger.LogInformation(
            "ASR warm-up decode completed in {WarmupMs:F0}ms: \"{WarmupText}\"", warmupMs, warmupText);

        if (string.IsNullOrWhiteSpace(warmupText))
        {
            recognizer.Dispose();
            throw new InvalidOperationException(
                "ASR warm-up decode produced empty output on a known-good real-speech clip — the "
                + "model or tokens file appears wrong. Refusing to mark the transcriber ready "
                + "(plan §1.6: fail loud at startup, not at first use).");
        }

        _recognizer = recognizer;
        _isReady = true;
    }

    private static WavReader.WavData LoadWarmupClip()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(WarmupResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded warm-up resource '{WarmupResourceName}' not found in {asm.FullName}.");
        return WavReader.Read(stream, WarmupResourceName);
    }

    public async Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> samples16k, CancellationToken ct)
    {
        if (!IsReady || _recognizer is null)
            throw new InvalidOperationException("Transcriber is not ready; call InitializeAsync first.");

        await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the gate: the object may have been disposed
            // concurrently between the IsReady check above and actually getting the gate.
            var recognizer = _recognizer
                ?? throw new ObjectDisposedException(nameof(SherpaOnnxTranscriber));
            return await Task.Run(() => Decode(recognizer, samples16k), ct).ConfigureAwait(false);
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    private static TranscriptionResult Decode(OfflineRecognizer recognizer, ReadOnlyMemory<float> samples16k)
    {
        var audioDuration = TimeSpan.FromSeconds(samples16k.Length / 16000.0);
        var sw = Stopwatch.StartNew();

        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(16000, samples16k.ToArray());
        recognizer.Decode(stream);
        string text = stream.Result.Text ?? string.Empty;

        sw.Stop();
        return new TranscriptionResult(text, audioDuration, sw.Elapsed, string.IsNullOrWhiteSpace(text));
    }

    public async ValueTask DisposeAsync()
    {
        _isReady = false;
        // Synchronize with any in-flight or about-to-start decode: acquiring the gate
        // before disposing the native recognizer prevents a use-after-dispose race
        // against native memory (see TranscribeAsync's re-check after acquiring the gate).
        await _decodeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
        finally
        {
            _decodeGate.Release();
            _decodeGate.Dispose();
        }
    }
}
