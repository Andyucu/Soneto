using Soneto.App.ViewModels;

namespace Soneto.App.Tests;

/// <summary>
/// Unit tests for <see cref="PermissionsDoctorLogic"/> -- the pure, side-effect-free decision
/// logic extracted out of <see cref="PermissionsDoctorViewModel"/> specifically so it can be
/// tested without PortAudio, the clipboard, a real <c>SessionController</c>, or the
/// filesystem (§3.15's "extract the pure parts" instruction). Covers the hook check's full
/// red/green/not-tested state machine across every <c>PipelineHost.Started</c>-fired-or-not
/// scenario, and the model-directory resolution order (config override / repo-local dev dir /
/// standard location / not found anywhere).
/// </summary>
public sealed class PermissionsDoctorLogicTests
{
    // ── EvaluateHookCheck ───────────────────────────────────────────────────────────────

    [Fact]
    public void EvaluateHookCheck_PipelineNeverStarted_NoFailureReason_ReturnsNotTested()
    {
        var (status, detail) = PermissionsDoctorLogic.EvaluateHookCheck(
            pipelineEverStarted: false, controllerCurrentlyFaulted: false,
            pipelineFailureReason: null, hookFaultDetail: null);

        Assert.Equal(CheckStatus.NotTested, status);
        Assert.Contains("has not started yet", detail);
    }

    [Fact]
    public void EvaluateHookCheck_PipelineFailedToStart_ReturnsNotTested_WithReasonSurfaced()
    {
        var (status, detail) = PermissionsDoctorLogic.EvaluateHookCheck(
            pipelineEverStarted: false, controllerCurrentlyFaulted: false,
            pipelineFailureReason: "missing ASR model", hookFaultDetail: null);

        Assert.Equal(CheckStatus.NotTested, status);
        Assert.Contains("missing ASR model", detail);
    }

    [Fact]
    public void EvaluateHookCheck_PipelineStartedAndHealthy_ReturnsGreen()
    {
        var (status, detail) = PermissionsDoctorLogic.EvaluateHookCheck(
            pipelineEverStarted: true, controllerCurrentlyFaulted: false,
            pipelineFailureReason: null, hookFaultDetail: null);

        Assert.Equal(CheckStatus.Green, status);
        Assert.Contains("healthy", detail);
    }

    [Fact]
    public void EvaluateHookCheck_PipelineStartedButControllerFaulted_ReturnsRed_WithHookFaultDetail()
    {
        var (status, detail) = PermissionsDoctorLogic.EvaluateHookCheck(
            pipelineEverStarted: true, controllerCurrentlyFaulted: true,
            pipelineFailureReason: null, hookFaultDetail: "device disconnected");

        Assert.Equal(CheckStatus.Red, status);
        Assert.Contains("device disconnected", detail);
    }

    [Fact]
    public void EvaluateHookCheck_PipelineStartedButControllerFaulted_NoHookFaultDetailKnown_StillReturnsRed()
    {
        // Real scenario this covers: SessionController.StartAsync itself transitions straight
        // to Faulted (e.g. the hook failed to start) without ever raising IHotkeySource.Faulted
        // -- see Soneto.Composition.DaemonComposition.BuildAndStartSessionControllerAsync's own
        // doc comment for this exact "started but already Faulted" case.
        var (status, detail) = PermissionsDoctorLogic.EvaluateHookCheck(
            pipelineEverStarted: true, controllerCurrentlyFaulted: true,
            pipelineFailureReason: null, hookFaultDetail: null);

        Assert.Equal(CheckStatus.Red, status);
        Assert.Contains("application log", detail);
    }

    [Fact]
    public void EvaluateHookCheck_NeverReturnsRed_WhenPipelineNeverStarted()
    {
        // Constraint 1's own explicit requirement: a pipeline that simply never started must
        // never be misreported as a hook-specific failure (red) -- it's a distinct, honest
        // "not tested" state, even if some stale hookFaultDetail happens to be set from a
        // previous session's controller instance.
        var (status, _) = PermissionsDoctorLogic.EvaluateHookCheck(
            pipelineEverStarted: false, controllerCurrentlyFaulted: true,
            pipelineFailureReason: "some earlier reason", hookFaultDetail: "some stale detail");

        Assert.Equal(CheckStatus.NotTested, status);
    }

    // ── ResolveModelDirForCheck ─────────────────────────────────────────────────────────

    [Fact]
    public void ResolveModelDirForCheck_ConfigOverrideSetAndComplete_ReturnsConfigOverrideFound()
    {
        var result = PermissionsDoctorLogic.ResolveModelDirForCheck(
            configModelDirOverride: @"C:\models\custom",
            areRequiredFilesPresent: dir => dir == @"C:\models\custom",
            missingFiles: _ => Array.Empty<string>(),
            findDevModelDirWalkingUp: () => throw new InvalidOperationException("must not be called"),
            standardModelDir: @"C:\standard");

        Assert.Equal(PermissionsDoctorLogic.ModelResolutionOutcome.ConfigOverrideFound, result.Outcome);
        Assert.Equal(@"C:\models\custom", result.Dir);
    }

    [Fact]
    public void ResolveModelDirForCheck_ConfigOverrideSetButIncomplete_ReturnsConfigOverrideMissingFiles()
    {
        var result = PermissionsDoctorLogic.ResolveModelDirForCheck(
            configModelDirOverride: @"C:\models\broken",
            areRequiredFilesPresent: _ => false,
            missingFiles: _ => ["encoder.int8.onnx", "tokens.txt"],
            findDevModelDirWalkingUp: () => throw new InvalidOperationException("must not be called"),
            standardModelDir: @"C:\standard");

        Assert.Equal(PermissionsDoctorLogic.ModelResolutionOutcome.ConfigOverrideMissingFiles, result.Outcome);
        Assert.Equal(@"C:\models\broken", result.Dir);
        Assert.Equal(["encoder.int8.onnx", "tokens.txt"], result.MissingFiles);
    }

    [Fact]
    public void ResolveModelDirForCheck_NoOverride_DevDirComplete_ReturnsDevDirFound()
    {
        var result = PermissionsDoctorLogic.ResolveModelDirForCheck(
            configModelDirOverride: null,
            areRequiredFilesPresent: dir => dir == @"C:\repo\models\parakeet",
            missingFiles: _ => Array.Empty<string>(),
            findDevModelDirWalkingUp: () => @"C:\repo\models\parakeet",
            standardModelDir: @"C:\standard");

        Assert.Equal(PermissionsDoctorLogic.ModelResolutionOutcome.DevDirFound, result.Outcome);
        Assert.Equal(@"C:\repo\models\parakeet", result.Dir);
    }

    [Fact]
    public void ResolveModelDirForCheck_NoOverride_NoDevDir_StandardDirComplete_ReturnsStandardDirFound()
    {
        var result = PermissionsDoctorLogic.ResolveModelDirForCheck(
            configModelDirOverride: "  ",
            areRequiredFilesPresent: dir => dir == @"C:\standard",
            missingFiles: _ => Array.Empty<string>(),
            findDevModelDirWalkingUp: () => null,
            standardModelDir: @"C:\standard");

        Assert.Equal(PermissionsDoctorLogic.ModelResolutionOutcome.StandardDirFound, result.Outcome);
        Assert.Equal(@"C:\standard", result.Dir);
    }

    [Fact]
    public void ResolveModelDirForCheck_NothingFoundAnywhere_ReturnsNotFoundAnywhere()
    {
        var result = PermissionsDoctorLogic.ResolveModelDirForCheck(
            configModelDirOverride: null,
            areRequiredFilesPresent: _ => false,
            missingFiles: _ => Array.Empty<string>(),
            findDevModelDirWalkingUp: () => null,
            standardModelDir: @"C:\standard");

        Assert.Equal(PermissionsDoctorLogic.ModelResolutionOutcome.NotFoundAnywhere, result.Outcome);
        Assert.Null(result.Dir);
    }

    [Fact]
    public void ResolveModelDirForCheck_DevDirIncomplete_FallsThroughToStandardDir()
    {
        // A dev dir existing but missing files (e.g. a corrupted/partial repo checkout) must
        // not short-circuit as "found" -- it should fall through to the standard location
        // exactly like DaemonComposition.BuildAndStartSessionControllerAsync's own real
        // resolution order does.
        var result = PermissionsDoctorLogic.ResolveModelDirForCheck(
            configModelDirOverride: null,
            areRequiredFilesPresent: dir => dir == @"C:\standard",
            missingFiles: _ => Array.Empty<string>(),
            findDevModelDirWalkingUp: () => @"C:\repo\models\incomplete",
            standardModelDir: @"C:\standard");

        Assert.Equal(PermissionsDoctorLogic.ModelResolutionOutcome.StandardDirFound, result.Outcome);
        Assert.Equal(@"C:\standard", result.Dir);
    }
}
