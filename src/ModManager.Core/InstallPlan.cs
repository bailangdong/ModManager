namespace ModManager.Core;

/// <summary>
/// Why a particular archive root was selected, with the evidence that produced it.
/// </summary>
/// <remarks>
/// The user's spec §6 is explicit: <b>"不能只根据目录名字猜"</b> — directory names alone are not
/// enough. So the analyzer never returns a bare decision; it returns a decision plus the list of
/// signals that produced it, which lets the UI justify the choice and lets a test assert on the
/// reasoning rather than just the outcome.
/// </remarks>
public enum RootDetectionSignal
{
    /// <summary>Nothing to go on; fall back to user confirmation.</summary>
    None = 0,

    /// <summary>Top-level directory matched the game's declared content directories (strongest positive).</summary>
    ContentDirectoryMatch = 1,

    /// <summary>The archive has exactly one top-level directory, so it is very likely packaging.</summary>
    SingleTopLevelDirectory = 2,

    /// <summary>Candidate contains files whose extensions the game declares as mod content.</summary>
    ModFileExtensionMatch = 3,

    /// <summary>Candidate matches a known mod-loader layout (BepInEx/plugins, Data/, Mods/).</summary>
    KnownLoaderLayout = 4,

    /// <summary>Candidate contains a recognised mod metadata file (manifest.json, modinfo.json, *.esp).</summary>
    ModMetadataPresent = 5,

    /// <summary>A readme/description inside the candidate names it as the mod root.</summary>
    ReadmeHint = 6,

    /// <summary>Candidate carries an already-known Mod Root depth from the game definition's strip prefixes.</summary>
    StripPrefixMatch = 7,

    /// <summary>Top-level entries that are obvious packaging (docs, screenshots, fomod) were skipped.</summary>
    PackagingEntriesIgnored = 8,

    /// <summary>Candidate rejected because it is a known-noise directory.</summary>
    RejectedNoiseDirectory = 9,

    /// <summary>Candidate rejected because it holds no recognised mod content at all.</summary>
    RejectedNoContent = 10,
}

/// <summary>One scored candidate for the install root, kept for explanation and debugging.</summary>
/// <param name="RelativePath">Forward-slash archive-relative path; empty string means the archive root.</param>
/// <param name="Score">Accumulated score; higher wins.</param>
/// <param name="Signals">Signals that contributed, with their individual weights.</param>
/// <param name="FileCount">Files under this candidate.</param>
/// <param name="TotalSize">Uncompressed bytes under this candidate.</param>
public sealed record ModRootCandidate(
    string RelativePath,
    double Score,
    IReadOnlyList<RootSignalHit> Signals,
    int FileCount,
    long TotalSize);

/// <summary>A signal that fired, with the reason text shown in the preview.</summary>
/// <param name="Signal">Which signal.</param>
/// <param name="Weight">Signed contribution to the score.</param>
/// <param name="Reason">Why it fired, in language a user can read.</param>
public sealed record RootSignalHit(RootDetectionSignal Signal, double Weight, string Reason);

/// <summary>
/// The analyzer's conclusion about an archive.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — Modrix's staging/hoisting rule ("staging, normalized, ready") combined with
/// the user's spec §6, which asks for a preview whenever confidence is insufficient. We make
/// "confidence" a first-class number and a threshold decision, so the UI's rule is mechanical:
/// below <see cref="RootAnalysis.Threshold"/> show the preview and ask.
/// </remarks>
public sealed record RootAnalysis
{
    /// <summary>The chosen root, forward-slash relative to the archive root. Empty = archive root.</summary>
    public required string SelectedRoot { get; init; }

    /// <summary>Confidence 0..1 for <see cref="SelectedRoot"/>.</summary>
    public required double Confidence { get; init; }

    /// <summary>Every candidate considered, best first. Shown in the Advanced preview.</summary>
    public required IReadOnlyList<ModRootCandidate> Candidates { get; init; }

    /// <summary>Signals behind the winning candidate.</summary>
    public required IReadOnlyList<RootSignalHit> Signals { get; init; }

    /// <summary>Detected archive format.</summary>
    public ArchiveFormat Format { get; init; } = ArchiveFormat.Zip;

    /// <summary>Total uncompressed bytes in the archive.</summary>
    public long TotalSize { get; init; }

    /// <summary>Total file entries.</summary>
    public int FileCount { get; init; }

    /// <summary>Relative paths that will be skipped (readmes, images, fomod metadata) and why.</summary>
    public IReadOnlyList<RootSignalHit> SkippedEntries { get; init; } = [];

    /// <summary>True when the layout looks like a FOMOD and needs the wizard rather than a plain copy.</summary>
    public bool IsFomod { get; init; }

    /// <summary>Detected mod-loader kind, when the layout is loader-shaped.</summary>
    public ModLoaderKind DetectedLoader { get; init; } = ModLoaderKind.Unknown;

    /// <summary>Readme files found, used as weak evidence and surfaced in the preview.</summary>
    public IReadOnlyList<string> ReadmePaths { get; init; } = [];

    /// <summary>Free-form remarks.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// Confidence below which the UI must show an installation preview instead of installing
    /// silently. Chosen so that a single strong signal (declared content directory) clears the
    /// bar, but "one wrapper folder and nothing else" does not.
    /// </summary>
    public const double Threshold = 0.7d;

    /// <summary>True when a human should confirm before we install.</summary>
    public bool NeedsUserConfirmation => Confidence < Threshold;

    /// <summary>Files that will actually be deployed, given the selected root.</summary>
    public int DeployableFileCount { get; init; }
}

/// <summary>The analyzer's view of an archive before anything is written.</summary>
/// <param name="ArchivePath">Absolute archive path.</param>
/// <param name="Entries">Entries read from the archive's central directory.</param>
public sealed record ArchiveIndex(string ArchivePath, IReadOnlyList<ArchiveEntry> Entries);

/// <summary>
/// One line of an installation preview: where a file comes from and where it lands.
/// </summary>
/// <param name="SourceRelative">Archive-relative source path, shown to the user verbatim.</param>
/// <param name="TargetPath">Absolute destination path.</param>
/// <param name="Safety">Safe / Overwrite / Conflict / Unsafe badge.</param>
/// <param name="Note">Why that badge, e.g. "replaces vanilla archive, will be backed up".</param>
public sealed record PreviewEntry(
    string SourceRelative,
    string TargetPath,
    PathSafetyVerdict Safety,
    string Note);

/// <summary>
/// Everything the Installation Preview dialog needs, assembled without touching the game folder.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — the user's spec §16 ("Source → Target, with ✔ Safe / ⚠ Conflict / ❌ Unsafe").
/// Because our planner is pure, the preview is not an approximation of the install: it <i>is</i>
/// the install plan, rendered. That is the payoff for keeping the planner free of I/O.
/// </remarks>
public sealed record InstallPreview
{
    /// <summary>Mod being installed.</summary>
    public required string ModId { get; init; }

    /// <summary>Display name.</summary>
    public required string ModName { get; init; }

    /// <summary>Game being installed into.</summary>
    public required string GameId { get; init; }

    /// <summary>Absolute deployment root.</summary>
    public required string DeploymentRoot { get; init; }

    /// <summary>Root the analyzer picked, archive-relative.</summary>
    public required string DetectedRoot { get; init; }

    /// <summary>Confidence in that root.</summary>
    public required double Confidence { get; init; }

    /// <summary>The reasoning, rendered in the "Why?" section.</summary>
    public required IReadOnlyList<RootSignalHit> Signals { get; init; }

    /// <summary>Per-file source→target list.</summary>
    public required IReadOnlyList<PreviewEntry> Entries { get; init; }

    /// <summary>Conflicts this install would create.</summary>
    public required IReadOnlyList<FileConflict> Conflicts { get; init; }

    /// <summary>Missing dependencies detected for this mod.</summary>
    public required IReadOnlyList<MissingDependency> MissingDependencies { get; init; }

    /// <summary>Rejected paths, with reasons. Empty is the happy case.</summary>
    public required IReadOnlyList<string> Rejections { get; init; }

    /// <summary>Total bytes to deploy.</summary>
    public long TotalBytes { get; init; }

    /// <summary>True when at least one path was classified Unsafe; installing must be blocked.</summary>
    public bool HasUnsafePaths => Entries.Any(e => e.Safety == PathSafetyVerdict.Unsafe) || Rejections.Count > 0;

    /// <summary>True when the user must confirm because the root guess is weak.</summary>
    public bool NeedsConfirmation => Confidence < RootAnalysis.Threshold;

    /// <summary>
    /// Reasons this preview must not be turned into a deployment without user action. Empty means
    /// the preview is installable as-is. Low-confidence roots, unsafe paths and blocking conflicts
    /// are all reported here instead of being left for the UI to infer.
    /// </summary>
    public IReadOnlyList<string> BlockReasons { get; init; } = [];

    /// <summary>True when the preview can be installed after the user's confirmation.</summary>
    public bool CanInstall => BlockReasons.Count == 0 && !HasUnsafePaths;
}

/// <summary>Request to install a mod, produced by the UI or the CLI.</summary>
public sealed record InstallRequest
{
    /// <summary>Archive to install.</summary>
    public required string ArchivePath { get; init; }

    /// <summary>Game to install into.</summary>
    public required string GameId { get; init; }

    /// <summary>Optional name override.</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Root override chosen by the user in the preview. When set, the analyzer's guess is ignored
    /// — this is the "let the user confirm or correct" escape hatch the spec requires.
    /// </summary>
    public string? RootOverride { get; init; }

    /// <summary>Continue even when confidence is low and no override was supplied.</summary>
    public bool AcceptLowConfidence { get; init; }

    /// <summary>Source archive hash, when already known (downloads).</summary>
    public string? ExpectedSha256 { get; init; }

    /// <summary>Library package this archive came from, when it was acquired through the library.</summary>
    public string? PackageId { get; init; }

    /// <summary>Platform key of the source, e.g. <c>nexusmods</c>, <c>thunderstore</c>, <c>local</c>.</summary>
    public string? Platform { get; init; }

    /// <summary>Platform-native mod key.</summary>
    public string? SourceKey { get; init; }

    /// <summary>Platform-native file/version key.</summary>
    public string? SourceFileKey { get; init; }

    /// <summary>Detected install method, passed through so the metadata keeps the analysis result.</summary>
    public string? InstallMethod { get; init; }

    /// <summary>
    /// Nexus mod id, when the archive came from Nexus.
    /// </summary>
    /// <remarks>
    /// Recorded at install time so the library can associate the installed mod with its online entry using
    /// a stable identifier. Nothing later has to guess from a name — which is what makes "check for
    /// updates" trustworthy instead of approximate.
    /// </remarks>
    public int? NexusModId { get; init; }

    /// <summary>Nexus file id the archive was downloaded from.</summary>
    public int? NexusFileId { get; init; }

    /// <summary>
    /// Version the source published for this archive, when it stated one.
    /// </summary>
    /// <remarks>
    /// Recorded so an update check has a baseline to compare against. Without it the library could only say
    /// "installed, version unknown", and the online page could never offer an update for a mod it installed
    /// itself.
    /// </remarks>
    public string? Version { get; init; }

    /// <summary>Page URL of the source, kept for provenance and for the "open page" action.</summary>
    public string? SourceUrl { get; init; }
}
