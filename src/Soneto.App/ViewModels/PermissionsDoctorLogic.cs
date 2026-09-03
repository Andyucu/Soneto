namespace Soneto.App.ViewModels;

/// <summary>
/// Phase 3 item 9 (§3.13): the red/green/pending/not-tested status a single Permissions
/// Doctor check can be in — the same "Success/Warning/Danger" design-token vocabulary
/// §3.7 already defines for exactly this purpose (<see cref="NotTested"/> maps to the
/// Warning token, not Danger — see <c>PermissionsDoctorView.axaml</c>'s own comment for why
/// a pipeline that simply hasn't started yet is not the same signal as a real failure).
/// </summary>
public enum CheckStatus
{
    /// <summary>Not yet run this session (or currently running).</summary>
    Pending,

    /// <summary>The check ran for real and the capability is confirmed working.</summary>
    Green,

    /// <summary>The check ran for real and the capability is confirmed broken.</summary>
    Red,

    /// <summary>
    /// Deliberately distinct from <see cref="Red"/> — the check could not meaningfully run
    /// (e.g. the "global hook active" check when the real dictation pipeline never started
    /// this session, or the mic check when the device is legitimately busy with a real
    /// in-progress recording). Reporting this as Red would be a false negative; reporting it
    /// as Green would be a false positive. See the class doc comments on
    /// <see cref="PermissionsDoctorViewModel"/> for the specific scenarios that produce this.
    /// </summary>
    NotTested,
}

/// <summary>
/// Item 9's pure, side-effect-free decision logic — extracted out of
/// <see cref="PermissionsDoctorViewModel"/> specifically so it can be unit-tested directly
/// (per §3.15's "extract the pure parts" instruction) without touching PortAudio, the
/// clipboard, a real <c>SessionController</c>, or the filesystem. Every method here is a
/// plain function of its inputs.
/// </summary>
public static class PermissionsDoctorLogic
{
    /// <summary>
    /// The "global hook active" check's entire state machine (constraint 1 of this item's
    /// own spec: never construct a second <c>IHotkeySource</c> — observe the real, shared
    /// one's health instead, and report an honest "not tested" state, not a false red, when
    /// the real pipeline never started this session at all).
    /// </summary>
    /// <param name="pipelineEverStarted">
    /// True once <c>PipelineHost.Started</c> has fired at least once this session (a real,
    /// non-null <c>SessionController</c>/<c>IHotkeySource</c> pair exists) — independent of
    /// whether that controller has since faulted.
    /// </param>
    /// <param name="controllerCurrentlyFaulted">
    /// True if the real, shared <c>SessionController.State</c> is currently <c>Faulted</c>.
    /// Only meaningful when <paramref name="pipelineEverStarted"/> is true.
    /// </param>
    /// <param name="pipelineFailureReason">
    /// The reason <c>PipelineHost.Failed</c> most recently fired with, or null if the
    /// pipeline has never failed to start this session (it may simply not have finished
    /// starting yet, or never have been attempted).
    /// </param>
    /// <param name="hookFaultDetail">
    /// The most specific detail available about why the hook faulted (from the real
    /// <c>IHotkeySource.Faulted</c> event's <c>Reason</c>/<c>Exception</c>), or null if no
    /// such event has been observed (e.g. the controller started already-Faulted, with the
    /// cause only in the application log).
    /// </param>
    public static (CheckStatus Status, string Detail) EvaluateHookCheck(
        bool pipelineEverStarted,
        bool controllerCurrentlyFaulted,
        string? pipelineFailureReason,
        string? hookFaultDetail)
    {
        if (!pipelineEverStarted)
        {
            return pipelineFailureReason is null
                ? (CheckStatus.NotTested,
                    "Not tested — the real dictation pipeline has not started yet this session.")
                : (CheckStatus.NotTested,
                    "Not tested — the real dictation pipeline is not running this session. "
                    + $"Reason it failed to start: {pipelineFailureReason}");
        }

        return controllerCurrentlyFaulted
            ? (CheckStatus.Red,
                "The hotkey/hook has faulted: "
                + (hookFaultDetail ?? "the session transitioned to Faulted (see the application log for the exact cause)."))
            : (CheckStatus.Green,
                "The real, shared hotkey source is running and healthy (SessionController.State is not Faulted).");
    }

    /// <summary>Which of the three candidate locations (config override, repo-local dev
    /// <c>models/</c> dir, standard per-OS location) the "model files present &amp; hashed"
    /// check should report on — or that none of them had a complete set of required
    /// files.</summary>
    public enum ModelResolutionOutcome
    {
        /// <summary><c>asr.modelDir</c> is set but missing one or more required files — a
        /// real configuration error, reported distinctly from "not found anywhere" since the
        /// user explicitly pointed at this path.</summary>
        ConfigOverrideMissingFiles,

        /// <summary>The configured override is complete.</summary>
        ConfigOverrideFound,

        /// <summary>No override configured; the repo-local dev-convenience <c>models/</c>
        /// walk-up found a complete model.</summary>
        DevDirFound,

        /// <summary>No override, no repo-local dev dir; the standard per-OS location has a
        /// complete model.</summary>
        StandardDirFound,

        /// <summary>Nothing found anywhere — the real pipeline would attempt a fresh
        /// download on its next start.</summary>
        NotFoundAnywhere,
    }

    public readonly record struct ModelResolutionResult(
        ModelResolutionOutcome Outcome, string? Dir, IReadOnlyList<string>? MissingFiles);

    /// <summary>
    /// Mirrors <c>Soneto.Composition.DaemonComposition.BuildAndStartSessionControllerAsync</c>'s
    /// own model-resolution order (config override, then the repo-local dev <c>models/</c>
    /// walk-up, then the standard per-OS location) EXACTLY, but never downloads anything —
    /// this is a read-only diagnostic check, not a real pipeline startup. Every filesystem
    /// touch is injected as a delegate so this stays a pure function of its inputs for
    /// testing; the real caller (<see cref="PermissionsDoctorViewModel"/>) passes the real
    /// <c>Soneto.Core.Asr.ModelManager</c>/<c>Soneto.Composition.DaemonComposition</c> static
    /// methods.
    /// </summary>
    public static ModelResolutionResult ResolveModelDirForCheck(
        string? configModelDirOverride,
        Func<string, bool> areRequiredFilesPresent,
        Func<string, IReadOnlyList<string>> missingFiles,
        Func<string?> findDevModelDirWalkingUp,
        string standardModelDir)
    {
        if (!string.IsNullOrWhiteSpace(configModelDirOverride))
        {
            return areRequiredFilesPresent(configModelDirOverride)
                ? new ModelResolutionResult(ModelResolutionOutcome.ConfigOverrideFound, configModelDirOverride, null)
                : new ModelResolutionResult(
                    ModelResolutionOutcome.ConfigOverrideMissingFiles, configModelDirOverride,
                    missingFiles(configModelDirOverride));
        }

        var devDir = findDevModelDirWalkingUp();
        if (devDir is not null && areRequiredFilesPresent(devDir))
            return new ModelResolutionResult(ModelResolutionOutcome.DevDirFound, devDir, null);

        if (areRequiredFilesPresent(standardModelDir))
            return new ModelResolutionResult(ModelResolutionOutcome.StandardDirFound, standardModelDir, null);

        return new ModelResolutionResult(ModelResolutionOutcome.NotFoundAnywhere, null, null);
    }
}
