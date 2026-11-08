namespace ModManager.Core;

/// <summary>
/// How a file is materialised into the game folder.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — Modrix (<c>crates/modrix-core/src/deploy/apply.rs</c>) and Vortex both
/// prefer non-destructive deployment: a hardlink costs no extra disk space and makes
/// "undeploy" a pure unlink rather than a content restore. Modrix's applier tries
/// <c>hardlink -> symlink -> copy</c> and downgrades per file when the filesystem refuses
/// (different volume, FAT32, permissions). We model that ordered preference explicitly as
/// <see cref="DeploymentMethod"/> list on the game definition instead of hardcoding it, because
/// the right answer is game- and volume-dependent: Skyrim's <c>Data/</c> is fine with links,
/// while some launchers refuse to read symlinked archives and must be copied.
/// </remarks>
public enum DeploymentMethod
{
    /// <summary>Real file, shared content with the staging copy. Preferred: zero extra space.</summary>
    Hardlink = 0,

    /// <summary>Reparse point to the staged file. Used when hardlinks are unavailable.</summary>
    Symlink = 1,

    /// <summary>Directory reparse point. Only meaningful for whole-directory mods.</summary>
    Junction = 2,

    /// <summary>Byte-for-byte copy. Always works; costs disk and makes undeploy destructive.</summary>
    Copy = 3,

    /// <summary>
    /// Reserved for a future MO2-style virtual filesystem (USVFS) that never writes to the
    /// game folder at all. Declared now so the capability surface is honest about it, but the
    /// applier deliberately throws rather than pretending to support it.
    /// </summary>
    Virtual = 4,
}

/// <summary>Outcome of verifying a deployed file against its manifest hash.</summary>
public enum FileVerifyStatus
{
    /// <summary>Missing from disk.</summary>
    Missing = 0,

    /// <summary>Present and byte-identical to what we deployed.</summary>
    Intact = 1,

    /// <summary>Present but the content differs from what we deployed (user or another tool edited it).</summary>
    Modified = 2,

    /// <summary>Present, but not a link/copy we own (someone replaced it wholesale).</summary>
    Foreign = 3,

    /// <summary>Could not be read (locked, permission denied).</summary>
    Unreadable = 4,
}

/// <summary>Lifecycle of a transaction journal.</summary>
/// <remarks>
/// DESIGN REFERENCE — Modrix <c>deploy/journal.rs</c>. The journal is written BEFORE any
/// filesystem mutation ("crash-safety" invariant) and moves forward-only. A journal found in
/// <see cref="Applying"/> on the next launch is by definition interrupted, which is what makes
/// recovery deterministic instead of guesswork.
/// </remarks>
public enum JournalState
{
    /// <summary>Opened but no filesystem change attempted yet.</summary>
    Open = 0,

    /// <summary>Step records are being appended and undo actions executed.</summary>
    Applying = 1,

    /// <summary>Every planned file is on disk and verified.</summary>
    Applied = 2,

    /// <summary>Manifest committed. This transaction must never be rolled back.</summary>
    Committed = 3,

    /// <summary>Interrupted/reversed; undo actions replayed and verified.</summary>
    RolledBack = 4,

    /// <summary>Reached a state we could not repair automatically. Requires user attention.</summary>
    Failed = 5,
}

/// <summary>Kind of journal entry, used for forensics and for choosing the recovery action.</summary>
public enum JournalStepKind
{
    Begin = 0,
    DirectoryCreated = 1,
    FileBackedUp = 2,
    FilePlaced = 3,
    FileDeleted = 4,
    FileRestored = 5,
    ManifestCommitted = 6,
    VerifyPassed = 7,
    VerifyFailed = 8,
    RollbackBegin = 9,
    RollbackComplete = 10,
    Error = 11,
}

/// <summary>Severity of a recorded problem surfaced to the UI.</summary>
public enum IssueSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>Which side of a conflict the user wants to win.</summary>
public enum ConflictResolutionKind
{
    /// <summary>No decision yet; the planner uses load order.</summary>
    Undecided = 0,

    /// <summary>A specific mod always wins this path.</summary>
    PreferMod = 1,

    /// <summary>A specific mod never provides this path.</summary>
    ExcludeMod = 2,
}

/// <summary>State of a download in the queue.</summary>
public enum DownloadState
{
    Queued = 0,
    Resolving = 1,
    Downloading = 2,
    Paused = 3,
    Verifying = 4,
    Completed = 5,
    Failed = 6,
    Cancelled = 7,
}

/// <summary>Installation state of a mod as presented to the user.</summary>
public enum ModState
{
    /// <summary>Files are in the staging area, but nothing has been deployed.</summary>
    Staged = 0,

    /// <summary>Enabled; the planner includes it and the applier deployed it.</summary>
    Deployed = 1,

    /// <summary>Known but excluded from the plan; its deployed files were removed.</summary>
    Disabled = 2,

    /// <summary>A newer upstream version exists.</summary>
    UpdateAvailable = 3,

    /// <summary>Deployed but something no longer matches (mod file changed, conflict, missing loader).</summary>
    NeedsAttention = 4,
}

/// <summary>Source a mod came from. Drives provenance display and update checks.</summary>
public enum ModSource
{
    /// <summary>Imported from a local archive the user picked.</summary>
    LocalFile = 0,

    /// <summary>Downloaded from Nexus Mods through the official API.</summary>
    NexusMods = 1,

    /// <summary>Discovered already present in the game folder (hand-installed).</summary>
    External = 2,

    /// <summary>Downloaded from Thunderstore through its public API.</summary>
    Thunderstore = 3,

    /// <summary>Downloaded from a GitHub release.</summary>
    GitHub = 4,

    /// <summary>Downloaded from CurseForge.</summary>
    CurseForge = 5,

    /// <summary>Any other acquisition source.</summary>
    Other = 6,
}
