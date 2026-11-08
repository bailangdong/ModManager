namespace ModManager.Core;

/// <summary>The mods a plan is built from: metadata plus its manifest, if deployed.</summary>
/// <param name="Metadata">Library record.</param>
/// <param name="Manifest">Deployment record, or null when never deployed.</param>
/// <param name="StagingRoot">Absolute directory holding the mod's extracted files.</param>
public sealed record ModSetEntry(ModMetadata Metadata, ModManifest? Manifest, string StagingRoot);

/// <summary>
/// Builds a deployment plan without touching the filesystem.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — Modrix's <c>deploy/plan.rs</c>, described in its architecture docs as
/// <b>"pure, no I/O"</b>. Modrix's stated reason: <i>"Deployment is split in two so the risky half
/// is a pure function and the destructive half is transactional."</i>
/// </para>
/// <para>
/// That split is the single most valuable idea we borrow, because it buys four things at once:
/// </para>
/// <list type="number">
/// <item><b>Truthful preview.</b> The Installation Preview is not a simulation of the install; it
/// <i>is</i> the plan, rendered. There is no second code path that can disagree with reality.</item>
/// <item><b>Determinism.</b> Same inputs, same plan, always — which makes the plan hash a valid
/// idempotence key ("a redeploy with no changes is a no-op").</item>
/// <item><b>Testability.</b> Modrix's five invariants (reversibility, idempotence, no silent
/// clobber, crash safety, determinism) can be asserted against a pure function without mocking a
/// filesystem.</item>
/// <item><b>Previewability of refusals.</b> A path <see cref="PathSafetyService"/> would reject is
/// surfaced as an <see cref="PathSafetyVerdict.Unsafe"/> entry before anything is written, instead
/// of as an exception halfway through.</item>
/// </list>
/// <para>
/// <b>Contract:</b> implementations must not read or write the game folder. Reading the staging
/// area to enumerate files is the only permitted I/O, and the result must depend only on those
/// contents plus the inputs.
/// </para>
/// </remarks>
public interface IDeploymentPlanner
{
    /// <summary>
    /// Resolves the virtual file tree for a profile and produces the operations needed to make the
    /// game folder match it exactly.
    /// </summary>
    /// <param name="definition">Game being deployed.</param>
    /// <param name="installPath">Absolute game install directory.</param>
    /// <param name="profile">Profile providing the enabled set, order and overrides.</param>
    /// <param name="mods">Every mod candidate, with manifests for the currently deployed state.</param>
    /// <param name="conflictOverrides">Per-path decisions gathered from the repository.</param>
    DeploymentPlan BuildPlan(
        GameDefinition definition,
        string installPath,
        Profile profile,
        IReadOnlyList<ModSetEntry> mods,
        IReadOnlyList<ConflictResolution> conflictOverrides);

    /// <summary>
    /// Builds the plan for a single not-yet-installed mod, for the Installation Preview.
    /// </summary>
    /// <param name="definition">Game being installed into.</param>
    /// <param name="installPath">Absolute game install directory.</param>
    /// <param name="mod">Mod being previewed.</param>
    /// <param name="existingMods">Mods already enabled, so conflicts can be reported.</param>
    DeploymentPlan BuildPreviewPlan(
        GameDefinition definition,
        string installPath,
        ModMetadata mod,
        IReadOnlyList<ModSetEntry> existingMods);
}

/// <summary>
/// Executes a plan transactionally, or reverses a previous one.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — Modrix's <c>deploy/apply.rs</c>, which "carries the plan out. It places each
/// file by hardlink, falling back to symlink, then copy. It backs up any pre-existing game file
/// before overwriting it, writes a journal before touching anything so a crash is recoverable, and
/// commits the manifest with a temp-file-plus-atomic-rename."
/// <para>
/// <b>Ordering contract</b> (this ordering is the crash-safety guarantee, so it is fixed):
/// </para>
/// <list type="number">
/// <item>open and flush the journal;</item>
/// <item>per file: verify the target, back up if occupied, append the undo action, then place;</item>
/// <item>verify what was written;</item>
/// <item>commit the manifest atomically;</item>
/// <item>mark the journal committed.</item>
/// </list>
/// </remarks>
public interface IDeploymentApplier
{
    /// <summary>Applies a plan, reporting progress and honouring cancellation.</summary>
    /// <param name="plan">Plan produced by <see cref="IDeploymentPlanner"/>.</param>
    /// <param name="definition">Game definition, for method preference and capabilities.</param>
    /// <param name="mods">Mods in the plan, needed to write manifests.</param>
    /// <param name="settings">Deployment options.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="progress">Progress sink, may be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="checkpoints">Test-only deployment checkpoints; production passes null.</param>
    Task<DeployResult> ApplyAsync(
        DeploymentPlan plan,
        GameDefinition definition,
        IReadOnlyList<ModSetEntry> mods,
        DeploymentOptions settings,
        IAppLogger logger,
        IProgress<DeployProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Services.IDeploymentCheckpointSink? checkpoints = null);

    /// <summary>
    /// Reverses one mod's deployment, restoring every file it overwrote.
    /// </summary>
    /// <param name="manifest">Manifest describing what to undo.</param>
    /// <param name="definition">Game definition.</param>
    /// <param name="settings">Deployment options.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="progress">Progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DeployResult> UndeployAsync(
        ModManifest manifest,
        GameDefinition definition,
        DeploymentOptions settings,
        IAppLogger logger,
        IProgress<DeployProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Durable transaction journal, including startup recovery.</summary>
public interface IJournalService
{
    /// <summary>Opens a new transaction and flushes its first record before any mutation.</summary>
    /// <param name="gameId">Game being modified.</param>
    /// <param name="profileId">Profile being deployed.</param>
    /// <param name="planHash">Fingerprint of the plan.</param>
    /// <param name="modIds">Mods the transaction concerns.</param>
    DeploymentJournal Open(string gameId, string profileId, string planHash, IReadOnlyList<string> modIds);

    /// <summary>Appends a step. Flushed to disk before the caller performs the action.</summary>
    /// <param name="journal">Journal to append to.</param>
    /// <param name="kind">Step kind.</param>
    /// <param name="path">Path involved.</param>
    /// <param name="detail">Extra context.</param>
    /// <param name="succeeded">Whether the step worked.</param>
    void Append(DeploymentJournal journal, JournalStepKind kind, string? path, string? detail, bool succeeded = true);

    /// <summary>Records an undo action. Must be called before performing the action it undoes.</summary>
    /// <param name="journal">Journal to update.</param>
    /// <param name="action">Reversal action.</param>
    void RecordUndo(DeploymentJournal journal, UndoAction action);

    /// <summary>Moves the journal to a new state and flushes it.</summary>
    /// <param name="journal">Journal to update.</param>
    /// <param name="state">New state.</param>
    /// <param name="error">Optional error text.</param>
    void Transition(DeploymentJournal journal, JournalState state, string? error = null);

    /// <summary>Journals left in a non-terminal state by a previous run.</summary>
    IReadOnlyList<DeploymentJournal> FindInterrupted();

    /// <summary>
    /// Reads journals and reports completed, interrupted and committed transactions without changing
    /// anything on disk. Use this before deciding whether recovery needs user attention.
    /// </summary>
    IReadOnlyList<JournalInspection> Inspect();

    /// <summary>
    /// Repairs every interrupted transaction: replays undo actions in reverse, verifies the result,
    /// and reports what happened. Called once at startup, before the UI enables anything.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<JournalRecoveryReport> RecoverAsync(IAppLogger logger, CancellationToken cancellationToken = default);

    /// <summary>All journals, newest first, for the Logs page's Transaction view.</summary>
    IReadOnlyList<DeploymentJournal> All();

    /// <summary>Removes committed journals beyond the retention setting.</summary>
    /// <param name="keep">How many of the newest to keep.</param>
    int PruneCommitted(int keep);
}

/// <summary>Verifies a deployed tree against its manifests.</summary>
public interface IVerifyService
{
    /// <summary>Checks every deployed file for a game.</summary>
    /// <param name="gameId">Game to verify.</param>
    /// <param name="manifests">Manifests to check against.</param>
    /// <param name="installPath">Absolute install directory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<VerifyReport> VerifyGameAsync(
        string gameId,
        IReadOnlyList<ModManifest> manifests,
        string installPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Decides what may be removed for one mod, applying the user-modification protection rule.
    /// </summary>
    /// <param name="manifest">Manifest being uninstalled.</param>
    /// <returns>Per-file verdicts; entries with <see cref="FileVerification.RequiresUserConfirmation"/>
    /// must be confirmed by the user before deletion.</returns>
    IReadOnlyList<FileVerification> PlanUndeploy(ModManifest manifest);
}
