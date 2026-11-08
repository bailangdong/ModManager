using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace ModManager.Core;

/// <summary>One append-only line in a transaction journal.</summary>
/// <param name="Sequence">Monotonic step number within the transaction.</param>
/// <param name="Timestamp">When the step happened.</param>
/// <param name="Kind">What kind of step it was.</param>
/// <param name="Path">Path involved, when the step touched one.</param>
/// <param name="Detail">Extra context (method used, hash, backup location).</param>
/// <param name="Succeeded">False when the step failed; the journal then carries an undo requirement.</param>
public sealed record JournalEntry(
    int Sequence,
    DateTimeOffset Timestamp,
    JournalStepKind Kind,
    string? Path,
    string? Detail,
    bool Succeeded);

/// <summary>
/// A completed, reversible unit of work: everything we did, and how to take it back.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — Modrix <c>deploy/journal.rs</c>. The rule that matters is <b>ordering</b>:
/// the journal is created and flushed <i>before</i> the first filesystem mutation, and each step
/// is appended <i>before</i> its action becomes irreversible. That is the whole crash-safety
/// story — a journal in <see cref="JournalState.Applying"/> means "something was in flight when
/// we died", which is precisely the information needed to recover deterministically. Modrix
/// states the invariant as: "The journal is written before any file moves, so an interrupted
/// deploy is recovered on next launch."
/// </para>
/// <para>
/// The undo log is the reverse: replaying <see cref="UndoActions"/> in reverse order restores
/// the pre-deploy state, which is how "rollback must not leave a half-installed mod" is
/// satisfied.
/// </para>
/// </remarks>
public sealed record DeploymentJournal
{
    /// <summary>Unique transaction id; also the journal file name.</summary>
    public required string TransactionId { get; init; }

    /// <summary>Game being deployed into.</summary>
    public required string GameId { get; init; }

    /// <summary>Profile being deployed.</summary>
    public required string ProfileId { get; init; }

    /// <summary>Plan fingerprint, so recovery can tell whether it is recovering the current intent.</summary>
    public required string PlanHash { get; init; }

    /// <summary>Current lifecycle state.</summary>
    public JournalState State { get; set; } = JournalState.Open;

    /// <summary>When the transaction opened.</summary>
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the transaction reached a terminal state.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Append-only step log.</summary>
    public List<JournalEntry> Entries { get; init; } = [];

    /// <summary>
    /// Actions that reverse this transaction, in the order they were produced.
    /// <b>Replay in reverse order.</b>
    /// </summary>
    public List<UndoAction> UndoActions { get; init; } = [];

    /// <summary>Mods this transaction installed, for manifest cleanup on rollback.</summary>
    public List<string> ModIds { get; init; } = [];

    /// <summary>Last error recorded, if the transaction failed.</summary>
    public string? Error { get; set; }

    /// <summary>True when the transaction ended without needing recovery.</summary>
    public bool IsTerminal => State is JournalState.Committed or JournalState.RolledBack or JournalState.Resolved;

    /// <summary>True when the next launch must repair this transaction.</summary>
    public bool NeedsRecovery => !IsTerminal;
}

/// <summary>
/// A single reversible filesystem action recorded before it is performed.
/// </summary>
/// <remarks>
/// Each variant records the minimum needed to undo itself. Note that "undo" is not always
/// "delete": a file we overwrote must be restored from its backup, and a file we deleted must be
/// recreated from staging. Encoding that per-variant is what makes rollback correct instead of
/// approximately correct.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(RemovePlacedFileUndo), "removePlacedFile")]
[JsonDerivedType(typeof(RestoreOverwrittenFileUndo), "restoreOverwrittenFile")]
[JsonDerivedType(typeof(RestoreDeletedFileUndo), "restoreDeletedFile")]
[JsonDerivedType(typeof(RemoveDirectoryUndo), "removeDirectory")]
public abstract record UndoAction
{
    /// <summary>Absolute path the action affects.</summary>
    public required string Path { get; init; }

    /// <summary>Human-readable description for the recovery log.</summary>
    public abstract string Describe();
}

/// <summary>Undo a placement by removing the file, but only if it is still byte-identical to ours.</summary>
/// <remarks>
/// Note on shape: <see cref="UndoAction.Path"/> is a <c>required</c> member of the base record, so these
/// variants declare an explicit constructor that assigns it rather than using positional parameters.
/// A positional parameter named <c>Path</c> would neither bind to the inherited member nor be readable
/// (CS8907), and being unread it would also leave the base member unset at every construction site.
/// The constructor is marked <see cref="SetsRequiredMembersAttribute"/> because it does set the inherited
/// required member, which is what lets call sites keep using plain positional construction. The parameter
/// list is unchanged, so no call site had to be rewritten.
/// </remarks>
public sealed record RemovePlacedFileUndo : UndoAction
{
    /// <summary>Creates the undo action.</summary>
    /// <param name="path">Absolute placed path.</param>
    /// <param name="deployedSha256">Hash we wrote; a mismatch means the user changed it and we must not delete.</param>
    /// <param name="modId">Owning mod.</param>
    [SetsRequiredMembers]
    public RemovePlacedFileUndo(string path, string deployedSha256, string modId)
    {
        Path = path;
        DeployedSha256 = deployedSha256;
        ModId = modId;
    }

    /// <summary>Hash we wrote; a mismatch means the user changed it and we must not delete.</summary>
    public string DeployedSha256 { get; init; }

    /// <summary>Owning mod.</summary>
    public string ModId { get; init; }

    /// <inheritdoc />
    public override string Describe() => $"remove placed file {Path}";
}

/// <summary>Undo a backup+overwrite by putting the original bytes back.</summary>
public sealed record RestoreOverwrittenFileUndo : UndoAction
{
    /// <summary>Creates the undo action.</summary>
    /// <param name="path">Absolute path that was overwritten.</param>
    /// <param name="backupPath">Where the original bytes were preserved.</param>
    /// <param name="originalSha256">Hash of the original, to verify the restore.</param>
    [SetsRequiredMembers]
    public RestoreOverwrittenFileUndo(string path, string backupPath, string originalSha256)
    {
        Path = path;
        BackupPath = backupPath;
        OriginalSha256 = originalSha256;
    }

    /// <summary>Where the original bytes were preserved.</summary>
    public string BackupPath { get; init; }

    /// <summary>Hash of the original, to verify the restore.</summary>
    public string OriginalSha256 { get; init; }

    /// <inheritdoc />
    public override string Describe() => $"restore {Path} from {BackupPath}";
}

/// <summary>Undo a file deletion by copying it back from staging.</summary>
public sealed record RestoreDeletedFileUndo : UndoAction
{
    /// <summary>Creates the undo action.</summary>
    /// <param name="path">Absolute path that was deleted.</param>
    /// <param name="sourcePath">Staged copy that can recreate it.</param>
    [SetsRequiredMembers]
    public RestoreDeletedFileUndo(string path, string sourcePath)
    {
        Path = path;
        SourcePath = sourcePath;
    }

    /// <summary>Staged copy that can recreate it.</summary>
    public string SourcePath { get; init; }

    /// <inheritdoc />
    public override string Describe() => $"restore deleted file {Path} from {SourcePath}";
}

/// <summary>Undo a directory creation. Removed only when it ended up empty.</summary>
public sealed record RemoveDirectoryUndo : UndoAction
{
    /// <summary>Creates the undo action.</summary>
    /// <param name="path">Absolute directory path.</param>
    [SetsRequiredMembers]
    public RemoveDirectoryUndo(string path) => Path = path;

    /// <inheritdoc />
    public override string Describe() => $"remove directory {Path} if empty";
}

/// <summary>
/// Outcome of replaying a journal's undo log.
/// </summary>
/// <param name="Failures">Actions that could not be completed and need attention.</param>
/// <param name="Deferred">Actions deliberately skipped because the file no longer belongs to this transaction.</param>
public sealed record JournalRollbackResult(
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Deferred);

/// <summary>
/// A read-only snapshot of one journal, used to reconcile a previous run with what is on disk.
/// </summary>
/// <param name="TransactionId">Transaction id.</param>
/// <param name="GameId">Game the transaction targets.</param>
/// <param name="State">Last recorded state.</param>
/// <param name="HasCommitMarker">Whether the irreversible commit marker is present.</param>
/// <param name="Interrupted">Whether this transaction still needs recovery or review.</param>
/// <param name="UndoActionCount">Number of reversal steps recorded.</param>
/// <param name="Error">Last recorded error, if any.</param>
public sealed record JournalInspection(
    string TransactionId,
    string GameId,
    JournalState State,
    bool HasCommitMarker,
    bool Interrupted,
    int UndoActionCount,
    string? Error);

/// <summary>Outcome of re-running recovery for one transaction.</summary>
/// <param name="Recovered">True when the journal reached RolledBack and was cleared.</param>
/// <param name="Deferred">True when at least one file was left for human review.</param>
/// <param name="Details">Failure/deferred details.</param>
public sealed record JournalTransactionRecoveryResult(
    bool Recovered,
    bool Deferred,
    IReadOnlyList<string> Details);

/// <summary>
/// What happened when we inspected journals left behind by a previous run.
/// </summary>
/// <param name="Recovered">Transactions rolled back successfully.</param>
/// <param name="Failed">Transactions we could not repair; the user must intervene.</param>
/// <param name="Messages">Human-readable account, written to the log and shown once at startup.</param>
public sealed record JournalRecoveryReport(
    IReadOnlyList<string> Recovered,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Messages)
{
    /// <summary>An empty report, used when there was nothing to do.</summary>
    public static JournalRecoveryReport Empty { get; } = new([], [], []);

    /// <summary>
    /// Transactions left on disk because a file could not be proven to belong to the transaction.
    /// These are deliberately not repaired automatically; the user must inspect them.
    /// </summary>
    public IReadOnlyList<string> Deferred { get; init; } = [];

    /// <summary>True when at least one transaction needed attention.</summary>
    public bool HadWork => Recovered.Count > 0 || Failed.Count > 0 || Deferred.Count > 0;
}

/// <summary>Progress of a deployment, reported incrementally so the UI can stay responsive.</summary>
public sealed record DeployProgress(
    int TotalFiles,
    int CompletedFiles,
    string? CurrentFile,
    DeploymentMethod? CurrentMethod,
    int BackupCount)
{
    /// <summary>Completion fraction 0..1, safe when there is nothing to do.</summary>
    public double Fraction => TotalFiles <= 0 ? 1d : Math.Clamp((double)CompletedFiles / TotalFiles, 0d, 1d);

    /// <summary>An initial zero-progress value.</summary>
    public static DeployProgress Start(int total) => new(total, 0, null, null, 0);
}

/// <summary>Outcome of an apply or undeploy.</summary>
/// <param name="Succeeded">True when the transaction committed.</param>
/// <param name="Manifest">Manifest written on success.</param>
/// <param name="DeployedFileCount">Files placed.</param>
/// <param name="BackupCount">Originals preserved.</param>
/// <param name="TransactionId">Journal id, for the log and for support.</param>
/// <param name="Warnings">Non-fatal remarks.</param>
/// <param name="Error">Fatal error when <paramref name="Succeeded"/> is false.</param>
public sealed record DeployResult(
    bool Succeeded,
    ModManifest? Manifest,
    int DeployedFileCount,
    int BackupCount,
    string TransactionId,
    IReadOnlyList<string> Warnings,
    string? Error);
