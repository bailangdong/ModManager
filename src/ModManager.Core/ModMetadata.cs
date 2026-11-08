namespace ModManager.Core;

/// <summary>
/// An installed mod: what it is, where it came from, and what it currently provides.
/// </summary>
/// <remarks>
/// This is the library-side record, independent of any game folder. Everything that describes
/// the <i>mod</i> lives here; everything that describes <i>what we did to the game folder</i>
/// lives in <see cref="ModManifest"/>. Keeping those apart is what lets us undeploy and
/// redeploy without losing provenance, and lets a mod be staged but never deployed.
/// </remarks>
public sealed record ModMetadata
{
    /// <summary>Stable id, derived from the archive name plus a short hash to stay unique.</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Author as declared in Nexus metadata or an archive manifest.</summary>
    public string? Author { get; init; }

    /// <summary>Version string as declared by the source.</summary>
    public string? Version { get; init; }

    /// <summary>Game this mod was installed for.</summary>
    public required string GameId { get; init; }

    /// <summary>Where it came from.</summary>
    public ModSource Source { get; init; } = ModSource.LocalFile;

    /// <summary>Library package this instance was staged from, when acquired through the package library.</summary>
    public string? PackageId { get; init; }

    /// <summary>Platform key of the package source, e.g. <c>nexusmods</c>, <c>thunderstore</c>, <c>local</c>.</summary>
    public string? Platform { get; init; }

    /// <summary>Platform-native mod key, when the source supplied one.</summary>
    public string? SourceKey { get; init; }

    /// <summary>Platform-native file/version key, when the source supplied one.</summary>
    public string? SourceFileKey { get; init; }

    /// <summary>Detected or declared install method, e.g. <c>BepInEx</c>, <c>Fomod</c>, <c>Smapi</c>.</summary>
    public string? InstallMethod { get; init; }

    /// <summary>Original archive absolute path (kept for reinstall and forensics).</summary>
    public string? ArchivePath { get; init; }

    /// <summary>SHA-256 of the original archive. The duplicate-install guard.</summary>
    public string? ArchiveHash { get; init; }

    /// <summary>SHA-256 of the original archive. Alias kept for the manifest contract.</summary>
    public string? Sha256 => ArchiveHash;

    /// <summary>Nexus mod id when sourced from Nexus.</summary>
    public int? NexusModId { get; init; }

    /// <summary>Nexus file id when sourced from Nexus.</summary>
    public int? NexusFileId { get; init; }

    /// <summary>Nexus category, shown on the Mods page.</summary>
    public string? Category { get; init; }

    /// <summary>Page URL, for "open in browser".</summary>
    public string? SourceUrl { get; init; }

    /// <summary>Relative URL/path of the cached cover image, if any.</summary>
    public string? CoverImagePath { get; init; }

    /// <summary>Short description from the source.</summary>
    public string? Summary { get; init; }

    /// <summary>Absolute staging root holding this mod's extracted files.</summary>
    public required string StagingPath { get; init; }

    /// <summary>Archive-relative path that was chosen as the install root.</summary>
    public string? AnalyzedRootPath { get; init; }

    /// <summary>Confidence of the automatic root decision, 0..1.</summary>
    public double AnalyzedRootConfidence { get; init; }

    /// <summary>True when a human confirmed or corrected the analysis.</summary>
    public bool RootConfirmedByUser { get; set; }

    /// <summary>Mod ids this mod requires. Empty when the game has no dependency metadata.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>Free-text requirements from the source page (not machine-resolvable).</summary>
    public IReadOnlyList<string> Requirements { get; init; } = [];

    /// <summary>Mods this one is known to conflict with, as declared upstream.</summary>
    public IReadOnlyList<string> Incompatibilities { get; init; } = [];

    /// <summary>When it was staged.</summary>
    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the archive was last seen unchanged, for update checks.</summary>
    public DateTimeOffset? SourceUpdatedAt { get; init; }

    /// <summary>Newest version known upstream, when it differs from <see cref="Version"/>.</summary>
    public string? AvailableVersion { get; init; }

    /// <summary>Current state as shown in the UI.</summary>
    public ModState State { get; init; } = ModState.Staged;

    /// <summary>True when the archive format needed a non-zip extractor.</summary>
    public ArchiveFormat ArchiveFormat { get; init; } = ArchiveFormat.Zip;

    /// <summary>Total uncompressed size of the staged files, for the preview.</summary>
    public long StagedSizeBytes { get; init; }

    /// <summary>File count in the staging area, for the preview.</summary>
    public int StagedFileCount { get; init; }
}

/// <summary>
/// One file this manager deployed into a game folder.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — Modrix's deploy manifest. Every deployed file is recorded with enough
/// information to reverse its own deployment and nothing more. This is the record that makes
/// uninstalling Mod A unable to touch Mod B's files, and makes "the user edited this file after
/// deploy" detectable (<see cref="Sha256"/> vs. the file's current hash).
/// </remarks>
public sealed record DeployedFile
{
    /// <summary>Forward-slash path relative to the game's deployment root.</summary>
    public required string RelativeTarget { get; init; }

    /// <summary>Absolute staged source path, when the file was placed rather than inherited.</summary>
    public string? SourcePath { get; init; }

    /// <summary>SHA-256 of the bytes we deployed. The "is this still our file" test.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Size in bytes at deploy time.</summary>
    public long Size { get; init; }

    /// <summary>Last write time at deploy time, an extra tamper signal.</summary>
    public DateTimeOffset? LastWriteTimeUtc { get; init; }

    /// <summary>How it was actually materialised (may differ from the preferred method after downgrade).</summary>
    public DeploymentMethod Method { get; init; } = DeploymentMethod.Hardlink;

    /// <summary>Mod that owns this path.</summary>
    public required string ModId { get; init; }

    /// <summary>True when this file replaced a pre-existing game file.</summary>
    public bool ReplacedExistingFile { get; init; }

    /// <summary>Absolute path of the backup holding the overwritten original, if any.</summary>
    public string? BackupPath { get; init; }

    /// <summary>Hash of the original file we overwrote. Used to prove the restore is the right content.</summary>
    public string? OriginalSha256 { get; init; }

    /// <summary>
    /// The mod whose file this placement displaced, when the bytes underneath belonged to another mod
    /// rather than to the game.
    /// </summary>
    /// <remarks>
    /// This is what makes "uninstall B restores A" exact instead of approximate: the displaced mod's
    /// file is reproduced from <i>its own</i> staging copy and verified against <i>its own</i> recorded
    /// hash — never from a byte copy that could later be mistaken for the game's original.
    /// </remarks>
    public string? DisplacedModId { get; init; }
}

/// <summary>
/// The complete record of what one mod deployed.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — the user's specification §9, matching Modrix's manifest commit. The
/// manifest is the authority for undeploy: <b>uninstall walks this list only</b>, so a file
/// this mod never deployed can never be removed by uninstalling it.
/// </remarks>
public sealed record ModManifest
{
    /// <summary>Mod the manifest belongs to.</summary>
    public required string ModId { get; init; }

    /// <summary>Display name, duplicated so a manifest is readable standalone.</summary>
    public required string Name { get; init; }

    /// <summary>Version at deploy time.</summary>
    public string? Version { get; init; }

    /// <summary>Where it came from.</summary>
    public ModSource Source { get; init; }

    /// <summary>
    /// Library package that produced this deployment, when it came through
    /// <c>PackageInstallService</c>. Null for legacy installs performed before the package library
    /// existed. This is the link uninstall uses to explain which archive owns the files.
    /// </summary>
    public string? PackageId { get; init; }

    /// <summary>Game deployed into.</summary>
    public required string GameId { get; init; }

    /// <summary>Profile active at deploy time. A manifest belongs to exactly one profile.</summary>
    public required string ProfileId { get; init; }

    /// <summary>Absolute deployment root the relative paths are anchored to.</summary>
    public required string DeploymentRoot { get; init; }

    /// <summary>When it was deployed.</summary>
    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Last successful verify.</summary>
    public DateTimeOffset? VerifiedAt { get; init; }

    /// <summary>Every deployed file. The only source of truth for undeploy.</summary>
    public IReadOnlyList<DeployedFile> Files { get; init; } = [];

    /// <summary>Mod ids this mod declared it needs.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>Conflicts recorded at deploy time, so the UI can explain a past decision.</summary>
    public IReadOnlyList<string> Conflicts { get; init; } = [];

    /// <summary>Plain-language description of the method used, e.g. "hardlink → symlink fallback".</summary>
    public string DeploymentMethod { get; init; } = string.Empty;

    /// <summary>Ordered deployment method preference actually used.</summary>
    public IReadOnlyList<DeploymentMethod> MethodPreference { get; init; } = [];

    /// <summary>Fingerprint of the plan this manifest came from; a re-plan with the same hash is a no-op.</summary>
    public string? PlanHash { get; init; }

    /// <summary>Directories created solely for this mod, so undeploy can remove them when empty.</summary>
    public IReadOnlyList<string> CreatedDirectories { get; init; } = [];
}

/// <summary>
/// A named set of enabled mods, priorities and load order — MO2's central abstraction.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — MO2 profiles + Vortex profiles. The user's spec §14 requires
/// Vanilla / Test / Big-Pack style profiles that each store enabled mods, load order, priority,
/// settings and deployment state, and that switching profiles redeploys automatically.
/// <para>
/// We store the mod set and order as data and redeploy on switch rather than snapshotting game
/// files, because redeploying from manifests is reversible (Modrix invariant #1) where
/// snapshotting is not.
/// </para>
/// </remarks>
public sealed record Profile
{
    /// <summary>Stable id.</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Game the profile belongs to.</summary>
    public required string GameId { get; init; }

    /// <summary>Optional description.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Mod ids in resolution order, lowest priority first. Later entries win file conflicts.
    /// </summary>
    /// <remarks>
    /// Modrix's planner resolves "later mod wins" over the enabled mods in load order. Making the
    /// order explicit and stored (rather than implied by install time) is what makes the planner a
    /// pure function and therefore deterministic.
    /// </remarks>
    public IReadOnlyList<string> ModOrder { get; init; } = [];

    /// <summary>Mod ids explicitly disabled in this profile.</summary>
    public IReadOnlyList<string> DisabledModIds { get; init; } = [];

    /// <summary>Plugin order for games with a load order; empty otherwise.</summary>
    public IReadOnlyList<string> PluginOrder { get; init; } = [];

    /// <summary>Per-path conflict overrides, keyed by relative target.</summary>
    public IReadOnlyDictionary<string, ConflictResolution> ConflictOverrides { get; init; } =
        new Dictionary<string, ConflictResolution>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True for the profile in use.</summary>
    public bool IsActive { get; init; }

    /// <summary>When created, for stable listing.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Last time this profile was deployed.</summary>
    public DateTimeOffset? LastDeployedAt { get; init; }

    /// <summary>Version of the game folder state recorded at last deploy, for drift detection.</summary>
    public string? LastPlanHash { get; init; }
}

/// <summary>A user decision about one contested path.</summary>
/// <param name="RelativeTarget">Contested path, or <c>*</c> for a mod-wide rule.</param>
/// <param name="Kind">Which mod, and how.</param>
/// <param name="ModId">The mod the rule names.</param>
public sealed record ConflictResolution(string RelativeTarget, ConflictResolutionKind Kind, string ModId)
{
    /// <summary>True when this rule applies to the given path.</summary>
    public bool AppliesTo(string relativeTarget) =>
        RelativeTarget == "*" || string.Equals(RelativeTarget, relativeTarget, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A file we overwrote, preserved so the original can be put back.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — the user's spec §18 and Modrix's "back up anything it overwrites". Layout
/// is <c>Backups/&lt;gameId&gt;/&lt;timestamp&gt;/&lt;relative path&gt;</c> so a whole backup set
/// can be removed as one directory and a human can find files by their real names.
/// </remarks>
public sealed record BackupEntry
{
    /// <summary>Game the backup belongs to.</summary>
    public required string GameId { get; init; }

    /// <summary>Absolute path the file was taken from.</summary>
    public required string OriginalPath { get; init; }

    /// <summary>Path relative to the deployment root, mirroring the backup layout.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Absolute path of the copy inside the backup tree.</summary>
    public required string BackupPath { get; init; }

    /// <summary>SHA-256 of the original bytes.</summary>
    public required string OriginalSha256 { get; init; }

    /// <summary>Original size.</summary>
    public long Size { get; init; }

    /// <summary>Mod whose deployment caused the backup.</summary>
    public required string ModId { get; init; }

    /// <summary>When the backup was taken.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True once the backup has been consumed by a successful restore.</summary>
    public bool Restored { get; init; }
}

/// <summary>Outcome of verifying one deployed file, used by the protection prompt.</summary>
/// <param name="RelativeTarget">Path relative to the deployment root.</param>
/// <param name="Status">What we found.</param>
/// <param name="ExpectedHash">Hash recorded at deploy time.</param>
/// <param name="ActualHash">Hash found now, when readable.</param>
/// <param name="OwnerModId">Mod the manifest credits with this file.</param>
public sealed record FileVerification(
    string RelativeTarget,
    FileVerifyStatus Status,
    string? ExpectedHash,
    string? ActualHash,
    string OwnerModId)
{
    /// <summary>
    /// True when the file must not be deleted without asking. This is the "user modified a.dll"
    /// case from the spec: current hash differs from the mod hash, so we default to keeping it.
    /// </summary>
    public bool RequiresUserConfirmation => Status is FileVerifyStatus.Modified or FileVerifyStatus.Foreign;
}

/// <summary>
/// A dependency the game or a mod needs that is not satisfied.
/// </summary>
/// <param name="ModId">Mod that declared the need, or <c>__game__</c> for a game-level need.</param>
/// <param name="Requirement">Human-readable requirement.</param>
/// <param name="IsLoader">True when the missing piece is a mod loader (a hard blocker).</param>
/// <param name="ActionUrl">Where to get it.</param>
public sealed record MissingDependency(string ModId, string Requirement, bool IsLoader, string? ActionUrl);
