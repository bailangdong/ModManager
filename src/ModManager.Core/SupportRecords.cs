namespace ModManager.Core;

/// <summary>Severity of a log line.</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
}

/// <summary>One structured log line.</summary>
/// <param name="Timestamp">When it happened.</param>
/// <param name="Level">Severity.</param>
/// <param name="Category">Subsystem, e.g. <c>Deploy</c>, <c>Nexus</c>, <c>Safety</c>.</param>
/// <param name="Message">Message text.</param>
/// <param name="Detail">Optional multi-line detail, such as a file list or a stack trace.</param>
/// <param name="Context">Optional key/value context (game id, transaction id).</param>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? Detail = null,
    IReadOnlyDictionary<string, string>? Context = null);

/// <summary>
/// A game the user has registered, i.e. a definition bound to a concrete install directory.
/// </summary>
/// <remarks>
/// The definition says <i>what</i> a game is; this says <i>where</i> the one on this machine
/// lives. Keeping them separate is what lets one definition serve any number of installs and lets
/// detection results exist before the user confirms them.
/// </remarks>
public sealed record GameInstance
{
    /// <summary>Definition id.</summary>
    public required string GameId { get; init; }

    /// <summary>Display name, copied for list rendering.</summary>
    public required string Name { get; init; }

    /// <summary>Absolute install directory. Must pass <see cref="PathSafetyService"/> validation before use.</summary>
    public required string InstallPath { get; init; }

    /// <summary>Absolute mod root, derived from the definition and validated.</summary>
    public required string ModRootPath { get; init; }

    /// <summary>How the install was detected.</summary>
    public InstallProbeKind DetectedBy { get; init; } = InstallProbeKind.PathHint;

    /// <summary>True when the user added it by hand rather than via detection.</summary>
    public bool AddedManually { get; init; }

    /// <summary>When it was registered.</summary>
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Last time we successfully verified the install still exists.</summary>
    public DateTimeOffset? LastVerifiedAt { get; init; }

    /// <summary>Active profile id for this game.</summary>
    public string? ActiveProfileId { get; init; }

    /// <summary>True when the game's mod loader is present.</summary>
    public bool LoaderInstalled { get; init; }

    /// <summary>Detected loader version, when readable.</summary>
    public string? LoaderVersion { get; init; }

    /// <summary>User-configured launch executable (absolute, or relative to <see cref="InstallPath"/>).</summary>
    public string? LaunchExecutable { get; init; }

    /// <summary>User-configured launch arguments. Empty means none.</summary>
    public string? LaunchArguments { get; init; }

    /// <summary>User-configured working directory for launching. Empty means the install directory.</summary>
    public string? LaunchWorkingDirectory { get; init; }

    /// <summary>
    /// Steam application id, when the game was added from a Steam detection.
    /// </summary>
    /// <remarks>
    /// Recorded so a later re-scan can recognise the same game, report it as supported if a dedicated
    /// definition has since been added, and never create a second record for it.
    /// </remarks>
    public int? SteamAppId { get; init; }

    /// <summary>Support level at the time the game was added.</summary>
    public GameSupportKind SupportKind { get; init; } = GameSupportKind.Supported;

    /// <summary>
    /// Dedicated definition id that now covers this install, when one exists.
    /// </summary>
    /// <remarks>
    /// Set when a re-scan finds that a game added in generic mode has since gained official support. The
    /// record keeps its own id — and therefore its mods, manifests and chosen mod root — while the interface
    /// can show that a dedicated definition is available.
    /// </remarks>
    public string? SupportedDefinitionId { get; init; }

    /// <summary>Last time a scan saw this install on disk.</summary>
    public DateTimeOffset? LastDetectedAt { get; init; }

    /// <summary>True when the install directory was missing the last time it was checked.</summary>
    public bool InstallMissing { get; init; }
}

/// <summary>
/// The result of running detection for one game definition. Not yet registered.
/// </summary>
/// <param name="Definition">Definition that was probed.</param>
/// <param name="Found">True when a plausible install was located.</param>
/// <param name="InstallPath">Located directory.</param>
/// <param name="Method">Which probe succeeded.</param>
/// <param name="Evidence">Why we believe it (files found, registry key, appmanifest).</param>
/// <param name="Confidence">0..1 confidence.</param>
public sealed record GameDetectionResult(
    GameDefinition Definition,
    bool Found,
    string? InstallPath,
    InstallProbeKind Method,
    IReadOnlyList<string> Evidence,
    double Confidence);

/// <summary>One game's row on the Dashboard.</summary>
public sealed record GameSummary
{
    /// <summary>Instance this summarises.</summary>
    public required GameInstance Instance { get; init; }

    /// <summary>Definition providing capabilities.</summary>
    public required GameDefinition Definition { get; init; }

    /// <summary>Mods staged for this game.</summary>
    public int InstalledModCount { get; init; }

    /// <summary>Mods currently enabled in the active profile.</summary>
    public int EnabledModCount { get; init; }

    /// <summary>Mods with a newer upstream version.</summary>
    public int UpdateCount { get; init; }

    /// <summary>Contested paths in the active profile.</summary>
    public int ConflictCount { get; init; }

    /// <summary>Unsatisfied requirements, including a missing loader.</summary>
    public int MissingDependencyCount { get; init; }

    /// <summary>True when the deployed state no longer matches the manifests.</summary>
    public bool NeedsRedeploy { get; init; }

    /// <summary>Last successful deploy.</summary>
    public DateTimeOffset? LastDeployedAt { get; init; }
}

/// <summary>Everything the Dashboard shows, assembled in one pass.</summary>
public sealed record DashboardSnapshot
{
    /// <summary>Per-game rows.</summary>
    public required IReadOnlyList<GameSummary> Games { get; init; }

    /// <summary>Total staged mods across all games.</summary>
    public required int TotalMods { get; init; }

    /// <summary>Total available updates.</summary>
    public required int TotalUpdates { get; init; }

    /// <summary>Total unresolved conflicts.</summary>
    public required int TotalConflicts { get; init; }

    /// <summary>Total missing dependencies.</summary>
    public required int TotalMissingDependencies { get; init; }

    /// <summary>Downloads currently active.</summary>
    public required int ActiveDownloads { get; init; }

    /// <summary>Downloads that failed and can be retried.</summary>
    public required int FailedDownloads { get; init; }

    /// <summary>Interrupted transactions found at startup, if any.</summary>
    public required JournalRecoveryReport Recovery { get; init; }

    /// <summary>Free space on the drive holding the staging area, in bytes.</summary>
    public long? FreeStagingSpace { get; init; }
}

/// <summary>A health problem with a suggested fix.</summary>
/// <param name="Severity">How bad it is.</param>
/// <param name="Title">Short summary.</param>
/// <param name="Detail">Explanation.</param>
/// <param name="FixHint">What the user should do.</param>
/// <param name="RelatedModId">Mod at fault, when there is one.</param>
/// <param name="IsBlocking">True when deployment must refuse until it is resolved.</param>
public sealed record HealthIssue(
    IssueSeverity Severity,
    string Title,
    string Detail,
    string? FixHint,
    string? RelatedModId,
    bool IsBlocking);

/// <summary>Content found in the game folder that this manager did not deploy.</summary>
/// <param name="GameId">Game scanned.</param>
/// <param name="RelativePath">Path relative to the mod root.</param>
/// <param name="IsDirectory">True for a folder-shaped mod.</param>
/// <param name="Label">Scan rule that matched.</param>
/// <param name="SizeBytes">Size on disk, when cheaply available.</param>
/// <param name="IsBaseGameFile">True when it matches the definition's vanilla file list.</param>
public sealed record ExternalContent(
    string GameId,
    string RelativePath,
    bool IsDirectory,
    string Label,
    long SizeBytes,
    bool IsBaseGameFile);

/// <summary>Counts returned by a verify pass over a game's deployed files.</summary>
/// <param name="GameId">Game verified.</param>
/// <param name="Total">Files checked.</param>
/// <param name="Intact">Files matching their manifest hash.</param>
/// <param name="Modified">Files changed since deploy.</param>
/// <param name="Missing">Files that vanished.</param>
/// <param name="Unreadable">Files that could not be read.</param>
/// <param name="Details">Per-file results, for the detail pane.</param>
public sealed record VerifyReport(
    string GameId,
    int Total,
    int Intact,
    int Modified,
    int Missing,
    int Unreadable,
    IReadOnlyList<FileVerification> Details)
{
    /// <summary>True when the deployed tree matches the manifests exactly.</summary>
    public bool IsClean => Modified == 0 && Missing == 0 && Unreadable == 0;
}

/// <summary>Generic result wrapper so UI/CLI layers can report failures without exceptions.</summary>
/// <typeparam name="T">Payload type.</typeparam>
/// <param name="Succeeded">True on success.</param>
/// <param name="Value">Payload on success.</param>
/// <param name="Error">Message on failure.</param>
public sealed record OperationResult<T>(bool Succeeded, T? Value, string? Error)
{
    /// <summary>Creates a success.</summary>
    public static OperationResult<T> Ok(T value) => new(true, value, null);

    /// <summary>Creates a failure.</summary>
    public static OperationResult<T> Fail(string error) => new(false, default, error);
}
