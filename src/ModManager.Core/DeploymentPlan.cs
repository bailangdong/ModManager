namespace ModManager.Core;

/// <summary>
/// Root of the staging area for one mod: <c>ModId/Version/</c> under the managed mods directory.
/// </summary>
/// <remarks>
/// The path is normalised to forward slashes and is always relative to the staging root, so a
/// manifest stays portable and can never encode an absolute path from another machine.
/// </remarks>
public sealed record ModRoot(string ModId, string Version)
{
    /// <summary>Relative staging path, e.g. <c>coolmod/1.2.0</c>.</summary>
    public string RelativePath => $"{ModId}/{Version}";
}

/// <summary>How an archive was compressed. Determines which extractor can read it.</summary>
public enum ArchiveFormat
{
    Unknown = 0,
    Zip = 1,
    SevenZip = 2,
    Rar = 3,
    Tar = 4,
    Gzip = 5,
    Fomod = 6,
}

/// <summary>
/// One entry inside an archive, captured during analysis without extracting anything.
/// </summary>
/// <param name="Path">Archive-relative path, forward slashes, case preserved.</param>
/// <param name="IsDirectory">True for directory entries (kept so empty dirs can be planned).</param>
/// <param name="Size">Uncompressed size in bytes; 0 for directories or when unknown.</param>
public sealed record ArchiveEntry(string Path, bool IsDirectory, long Size)
{
    /// <summary>Lower-cased path, used for every case-insensitive comparison in the analyzer.</summary>
    public string LowerPath => Path.ToLowerInvariant();
}

/// <summary>
/// A FileOperation is a single primitive scheduled by the planner.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — Modrix's applier executes a list of operations, each undoable, rather than
/// a single "install" call. Two-phase planning (plan then apply) is what makes installation
/// previewable: the UI renders the exact operation list before anything touches disk.
/// </remarks>
public abstract record FileOperation
{
    /// <summary>Absolute path the operation acts on.</summary>
    public required string TargetPath { get; init; }

    /// <summary>Human-readable one-line summary for the installation preview.</summary>
    public abstract string Describe();
}

/// <summary>Place a staged file into the game folder, preferring <see cref="Method"/>.</summary>
public sealed record PlaceFileOperation : FileOperation
{
    /// <summary>Absolute path of the staged source file.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Preferred link strategy; the applier downgrades on failure.</summary>
    public DeploymentMethod Method { get; init; } = DeploymentMethod.Hardlink;

    /// <summary>Stable identity of the providing mod, recorded in the deployed manifest.</summary>
    public required string ModId { get; init; }

    /// <summary>Target path relative to the deployment root, forward slashes.</summary>
    public required string RelativeTarget { get; init; }

    /// <inheritdoc />
    public override string Describe() => $"Deploy {RelativeTarget} [{Method}]";
}

/// <summary>Create a directory that the game expects to exist.</summary>
public sealed record CreateDirectoryOperation : FileOperation
{
    /// <summary>True when the planner created it only to hold deployed files and it may be removed later.</summary>
    public bool RemovableWhenEmpty { get; init; } = true;

    /// <inheritdoc />
    public override string Describe() => $"Create {TargetPath}";
}

/// <summary>Delete a file that we previously deployed (undeploy / disable).</summary>
public sealed record DeleteFileOperation : FileOperation
{
    /// <summary>Hash recorded at deploy time. Used to prove the file is still ours before deleting.</summary>
    public string? ExpectedHash { get; init; }

    /// <summary>Mod that owns the file, for the manifest bookkeeping.</summary>
    public required string ModId { get; init; }

    /// <summary>Target path relative to the deployment root, forward slashes.</summary>
    public required string RelativeTarget { get; init; }

    /// <summary>Backup of the original this mod displaced, if any; deleting restores it.</summary>
    public string? BackupPath { get; init; }

    /// <summary>Hash of the displaced original, used to verify the restore.</summary>
    public string? OriginalSha256 { get; init; }

    /// <inheritdoc />
    public override string Describe() => $"Remove {RelativeTarget}";
}

/// <summary>Put a backed-up original file back where it came from.</summary>
public sealed record RestoreBackupOperation : FileOperation
{
    /// <summary>Absolute path of the backup copy under <c>Backups/</c>.</summary>
    public required string BackupPath { get; init; }

    /// <summary>Mod whose uninstall triggered the restore.</summary>
    public required string ModId { get; init; }

    /// <inheritdoc />
    public override string Describe() => $"Restore original {TargetPath}";
}

/// <summary>
/// A target path that more than one enabled mod wants to write.
/// </summary>
/// <param name="RelativeTarget">Contested path, relative to the deployment root.</param>
/// <param name="WinnerModId">Mod that supplies the file after resolution.</param>
/// <param name="OverriddenModIds">Mods that lose, in load-order sequence.</param>
/// <param name="Resolution">Rule that produced the winner.</param>
/// <param name="IsBlocking">True when the conflict is unresolvable and deployment must stop.</param>
/// <param name="Reason">Why the winner won, shown verbatim in the UI.</param>
public sealed record FileConflict(
    string RelativeTarget,
    string WinnerModId,
    IReadOnlyList<string> OverriddenModIds,
    ConflictResolutionKind Resolution,
    bool IsBlocking,
    string Reason)
{
    /// <summary>Absolute canonical target, so two relative-looking paths cannot be mistaken for each other.</summary>
    public string? AbsoluteTarget { get; init; }

    /// <summary>Every mod that claimed this path, in load order, with its source and resolution evidence.</summary>
    public IReadOnlyList<FileConflictProvider> Providers { get; init; } = [];
}

/// <summary>One claimant of a contested file, kept so the UI can explain where each version came from.</summary>
/// <param name="ModId">Claiming mod.</param>
/// <param name="SourcePath">Absolute staged source.</param>
/// <param name="BaseRoot">Root that produced the target.</param>
/// <param name="Evidence">Why this target was chosen.</param>
/// <param name="IsWinner">True for the version that currently wins.</param>
public sealed record FileConflictProvider(
    string ModId,
    string SourcePath,
    string BaseRoot,
    string Evidence,
    bool IsWinner);

/// <summary>
/// One file the planner intends to touch, with the evidence needed to justify it.
/// </summary>
/// <param name="RelativeTarget">Forward-slash path relative to the deployment root.</param>
/// <param name="SourcePath">Absolute staged source path, or null for pure deletions.</param>
/// <param name="ModId">Owning mod.</param>
/// <param name="Safety">Risk classification shown as a badge in the installation preview.</param>
/// <param name="Note">Why this classification was chosen.</param>
public sealed record PlannedFile(
    string RelativeTarget,
    string? SourcePath,
    string ModId,
    PathSafetyVerdict Safety,
    string Note);

/// <summary>
/// A complete, reviewable deployment plan.
/// </summary>
/// <remarks>
/// The planner that produces this performs <b>no filesystem mutation at all</b>. Everything the
/// applier needs is decided here, which is what gives us determinism (same inputs -> same plan)
/// and a truthful installation preview.
/// </remarks>
public sealed record DeploymentPlan
{
    /// <summary>Game the plan belongs to.</summary>
    public required string GameId { get; init; }

    /// <summary>Profile the plan was built for.</summary>
    public required string ProfileId { get; init; }

    /// <summary>Absolute, canonical deployment root (the game's Mod Root).</summary>
    public required string DeploymentRoot { get; init; }

    /// <summary>
    /// Game install root used for the deployment lock. Optional for older/constructed plans; when
    /// absent the applier falls back to <see cref="DeploymentRoot"/>.
    /// </summary>
    public string? GameRoot { get; init; }

    /// <summary>Every file the plan will place, in deterministic order.</summary>
    public required IReadOnlyList<PlannedFile> Files { get; init; }

    /// <summary>Ordered primitive operations handed to the applier.</summary>
    public required IReadOnlyList<FileOperation> Operations { get; init; }

    /// <summary>Conflicts detected while resolving the virtual tree.</summary>
    public required IReadOnlyList<FileConflict> Conflicts { get; init; }

    /// <summary>Non-fatal remarks (skipped files, downgraded methods, missing optionals).</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Enabled mod ids in the exact order used to resolve winners.</summary>
    public required IReadOnlyList<string> EnabledModsInOrder { get; init; }

    /// <summary>Deterministic fingerprint of the inputs, used for idempotence checks.</summary>
    public required string PlanHash { get; init; }

    /// <summary>True when nothing needs to change, i.e. a redeploy is a no-op.</summary>
    public bool IsEmpty => Operations.Count == 0;
}

/// <summary>Risk classification attached to every planned path by <c>PathSafetyService</c>.</summary>
public enum PathSafetyVerdict
{
    /// <summary>Inside the approved root, new file, no existing content at risk.</summary>
    Safe = 0,

    /// <summary>Inside the approved root but it overwrites an existing game file (will be backed up).</summary>
    Overwrite = 1,

    /// <summary>Contested by another mod; resolvable but needs a decision.</summary>
    Conflict = 2,

    /// <summary>Rejected. The operation must not be executed at all.</summary>
    Unsafe = 3,
}

/// <summary>Result of a safety check, including the reason so the UI can explain a refusal.</summary>
/// <param name="Allowed">False means the caller must abandon the operation.</param>
/// <param name="CanonicalPath">Fully resolved path (symlinks/junctions expanded) when resolvable.</param>
/// <param name="Reason">Explanation, quoted verbatim in the UI and the log.</param>
/// <param name="Verdict">Risk classification for the installation preview.</param>
public sealed record PathSafetyResult(bool Allowed, string? CanonicalPath, string Reason, PathSafetyVerdict Verdict)
{
    /// <summary>Creates a refusal. Never throws, so callers can always report a reason.</summary>
    public static PathSafetyResult Reject(string reason)
        => new(false, null, reason, PathSafetyVerdict.Unsafe);

    /// <summary>Creates an approval.</summary>
    public static PathSafetyResult Allow(string canonical, PathSafetyVerdict verdict, string reason)
        => new(true, canonical, reason, verdict);
}
