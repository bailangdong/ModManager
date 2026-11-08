namespace ModManager.Core;

/// <summary>
/// Reads and extracts mod archives.
/// </summary>
/// <remarks>
/// <para>
/// <b>Security contract — this is the highest-risk parser in the application.</b> An archive is
/// fully attacker-controlled input, so every entry path is treated as hostile:
/// </para>
/// <list type="bullet">
/// <item>Absolute paths (<c>C:\Windows\...</c>, <c>/etc/...</c>) are rejected, not sanitised.</item>
/// <item>Parent traversal (<c>../../..</c>) is rejected after normalisation, not merely trimmed —
/// trimming turns <c>....//</c> into <c>../</c> and is a well-known bypass.</item>
/// <item>Drive-relative paths (<c>C:foo</c>) and UNC paths (<c>\\server\share</c>) are rejected.</item>
/// <item>Reparse-point entries are rejected, so an archive cannot plant a symlink that later
/// writes escape the extraction root.</item>
/// <item>Extraction is confined to a caller-supplied root, and the final resolved path is
/// re-checked against that root after resolution — the check is on the real path, not the string.</item>
/// <item>Entry count, per-entry size and total expanded size are bounded, so a zip bomb fails
/// loudly instead of filling the disk.</item>
/// </list>
/// <para>
/// Modrix takes the same stance ("no recursion over untrusted input (archives, FOMOD XML)"), and
/// the reason is identical: this is the place where a mod manager can compromise a machine.
/// </para>
/// </remarks>
public interface IArchiveReader
{
    /// <summary>Formats this reader can handle.</summary>
    IReadOnlyList<ArchiveFormat> SupportedFormats { get; }

    /// <summary>Detects the format from a file's signature and extension.</summary>
    /// <param name="archivePath">Archive to sniff.</param>
    ArchiveFormat DetectFormat(string archivePath);

    /// <summary>True when the format can be read without an external helper tool.</summary>
    /// <param name="format">Format to test.</param>
    bool CanRead(ArchiveFormat format);

    /// <summary>
    /// Enumerates entries without extracting. Cheap: reads the central directory only.
    /// </summary>
    /// <param name="archivePath">Archive to index.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ArchiveIndex> IndexAsync(string archivePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts selected entries into <paramref name="destinationRoot"/>. Implementations must apply
    /// every rejection rule in the security contract and must report skipped entries rather than
    /// failing the whole extraction for one bad path.
    /// </summary>
    /// <param name="archivePath">Archive to extract.</param>
    /// <param name="destinationRoot">Directory that must contain every extracted file.</param>
    /// <param name="entriesToExtract">Entries to write, by archive-relative path.</param>
    /// <param name="progress">Progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ExtractionResult> ExtractAsync(
        string archivePath,
        string destinationRoot,
        IReadOnlyCollection<string> entriesToExtract,
        IProgress<ExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Limits enforced while reading an archive.</summary>
public sealed record ArchiveLimits
{
    /// <summary>Maximum number of entries read from one archive.</summary>
    public int MaxEntries { get; init; } = 200_000;

    /// <summary>Maximum uncompressed size of a single entry.</summary>
    public long MaxEntryBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    /// <summary>Maximum total uncompressed size, the zip-bomb guard.</summary>
    public long MaxTotalBytes { get; init; } = 64L * 1024 * 1024 * 1024;

    /// <summary>Maximum compression ratio before an entry is treated as a bomb.</summary>
    public double MaxCompressionRatio { get; init; } = 1000d;

    /// <summary>Maximum path length of an extracted file.</summary>
    public int MaxPathLength { get; init; } = 32_000;

    /// <summary>Default limits.</summary>
    public static ArchiveLimits Default { get; } = new();
}

/// <summary>Progress of an extraction.</summary>
/// <param name="TotalEntries">Entries to write.</param>
/// <param name="CompletedEntries">Entries written so far.</param>
/// <param name="CurrentEntry">Entry being written.</param>
/// <param name="BytesWritten">Bytes written so far.</param>
public sealed record ExtractionProgress(int TotalEntries, int CompletedEntries, string? CurrentEntry, long BytesWritten);

/// <summary>Outcome of an extraction.</summary>
/// <param name="ExtractedPaths">Archive-relative paths actually written.</param>
/// <param name="Rejected">Entries refused, with the reason. Empty is the normal case.</param>
/// <param name="BytesWritten">Total bytes written.</param>
public sealed record ExtractionResult(
    IReadOnlyList<string> ExtractedPaths,
    IReadOnlyList<RejectedEntry> Rejected,
    long BytesWritten)
{
    /// <summary>True when nothing was refused.</summary>
    public bool IsClean => Rejected.Count == 0;
}

/// <summary>An entry that was refused, with the reason kept for the log and the preview.</summary>
/// <param name="EntryPath">Offending archive path, quoted verbatim so the user can see it.</param>
/// <param name="Reason">Why it was refused.</param>
/// <param name="IsSecurityRejection">True when the reason was a traversal/escape attempt rather than a size limit.</param>
public sealed record RejectedEntry(string EntryPath, string Reason, bool IsSecurityRejection);

/// <summary>
/// Analyses an archive and decides where its content really begins.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — the user's spec §6, informed by Modrix's staging normalisation and by
/// Modrix's explicit warning that directory names alone are not sufficient: "A lone
/// <c>BepInEx/</c> (or legacy <c>QMods/</c>) tree at an archive root is content, not a packaging
/// wrapper - hoisting it would strip the container and deploy the plugin where the loader never
/// finds it."
/// <para>
/// So the analyzer scores candidates using several independent signals (declared content
/// directories, mod file extensions, known loader layouts, mod metadata files, readme hints,
/// redundant-prefix matches) and returns a confidence. When confidence is below
/// <see cref="RootAnalysis.Threshold"/> the UI must show the preview and let the user choose — the
/// spec's "如果置信度不足：显示安装预览，让用户确认".
/// </para>
/// </remarks>
public interface IModAnalyzer
{
    /// <summary>Analyzes an indexed archive for a game.</summary>
    /// <param name="adapter">Game the mod is for; supplies content directories and loader layout.</param>
    /// <param name="index">Archive index.</param>
    /// <param name="limits">Limits to apply.</param>
    RootAnalysis Analyze(IGameAdapter adapter, ArchiveIndex index, ArchiveLimits? limits = null);

    /// <summary>
    /// Analyzes an already-extracted directory. Used for re-analysis after a user picks a different
    /// root, and for scanning external content.
    /// </summary>
    /// <param name="adapter">Game the content is for.</param>
    /// <param name="directory">Absolute directory to analyse.</param>
    RootAnalysis AnalyzeDirectory(IGameAdapter adapter, string directory);
}

/// <summary>
/// The end-to-end mod installation pipeline.
/// </summary>
/// <remarks>
/// The user's spec §5 fixes the shape of this pipeline and forbids the shortcut:
/// <i>"不要 ExtractAll(gameDirectory) — 这是不可接受的。"</i> The interface therefore exposes the
/// stages separately so that no caller can collapse them:
/// <c>Download → Archive → Analyze → Staging → Deployment → Game</c>.
/// </remarks>
public interface IModInstallService
{
    /// <summary>
    /// Stages an archive: indexes it, analyses the root, extracts it into the non-destructive
    /// staging area, and records metadata. <b>Writes nothing into the game folder.</b>
    /// </summary>
    /// <param name="request">Install request.</param>
    /// <param name="adapter">Game adapter.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="progress">Progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationResult<StagedMod>> StageAsync(
        InstallRequest request,
        IGameAdapter adapter,
        IAppLogger logger,
        IProgress<ExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds the Installation Preview for a staged mod: exactly what would be deployed where, with
    /// a safety verdict per path. Performs no writes.
    /// </summary>
    /// <param name="staged">Staged mod.</param>
    /// <param name="adapter">Game adapter.</param>
    /// <param name="installPath">Absolute install directory.</param>
    /// <param name="existingMods">Currently enabled mods, for conflict reporting.</param>
    Task<OperationResult<InstallPreview>> PreviewAsync(
        StagedMod staged,
        IGameAdapter adapter,
        string installPath,
        IReadOnlyList<ModSetEntry> existingMods);

    /// <summary>Deploys a staged mod into the game folder.</summary>
    /// <param name="staged">Staged mod.</param>
    /// <param name="adapter">Game adapter.</param>
    /// <param name="installPath">Absolute install directory.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="progress">Progress sink.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DeployResult> InstallAsync(
        StagedMod staged,
        IGameAdapter adapter,
        string installPath,
        IAppLogger logger,
        IProgress<DeployProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uninstalls a mod: reverses its deployment, honouring the user-modification protection rule,
    /// then removes its staging directory.
    /// </summary>
    /// <param name="gameId">Game.</param>
    /// <param name="modId">Mod.</param>
    /// <param name="adapter">Game adapter.</param>
    /// <param name="installPath">Absolute install directory.</param>
    /// <param name="deleteModifiedFiles">True only when the user explicitly confirmed deleting files that changed.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DeployResult> UninstallAsync(
        string gameId,
        string modId,
        IGameAdapter adapter,
        string installPath,
        bool deleteModifiedFiles,
        IAppLogger logger,
        CancellationToken cancellationToken = default);

    /// <summary>Removes staging files for a mod without touching the game folder.</summary>
    /// <param name="modId">Mod whose staging area to delete.</param>
    OperationResult<bool> PurgeStaging(string modId);
}

/// <summary>A mod that has been analysed and extracted, but not deployed.</summary>
/// <param name="Metadata">Library record.</param>
/// <param name="Analysis">Root analysis result.</param>
/// <param name="StagingRoot">Absolute staging directory holding the extracted files.</param>
/// <param name="RootRelativePath">Chosen install root relative to the archive root.</param>
public sealed record StagedMod(
    ModMetadata Metadata,
    RootAnalysis Analysis,
    string StagingRoot,
    string RootRelativePath);

/// <summary>Detects and explains file-level conflicts between mods.</summary>
public interface IConflictService
{
    /// <summary>
    /// Computes the full conflict set for a profile, including which mod wins each contested path
    /// and why.
    /// </summary>
    /// <param name="gameId">Game.</param>
    /// <param name="mods">Mods in the profile, in resolution order.</param>
    /// <param name="overrides">Recorded user decisions.</param>
    IReadOnlyList<FileConflict> Analyze(string gameId, IReadOnlyList<ModSetEntry> mods, IReadOnlyList<ConflictResolution> overrides);

    /// <summary>
    /// Explains one contested path in detail, for the conflict detail pane
    /// (<c>Mod A → a.pak → Mod B overrides</c>).
    /// </summary>
    /// <param name="gameId">Game.</param>
    /// <param name="relativeTarget">Contested path.</param>
    /// <param name="mods">Mods in scope.</param>
    ConflictExplanation Explain(string gameId, string relativeTarget, IReadOnlyList<ModSetEntry> mods);
}

/// <summary>Step-by-step account of one contested path.</summary>
/// <param name="RelativeTarget">Contested path.</param>
/// <param name="Steps">Providers in resolution order, earliest first, with the winner flagged.</param>
/// <param name="WinnerModId">Mod whose bytes are on disk.</param>
/// <param name="Explanation">Sentence suitable for the UI.</param>
public sealed record ConflictExplanation(
    string RelativeTarget,
    IReadOnlyList<ConflictStep> Steps,
    string WinnerModId,
    string Explanation);

/// <summary>One mod's claim on a contested path.</summary>
/// <param name="ModId">Claimant.</param>
/// <param name="ModName">Display name.</param>
/// <param name="SourcePath">Staged source path.</param>
/// <param name="Size">File size.</param>
/// <param name="Wins">True for the mod that actually provides the bytes.</param>
/// <param name="IsDisabled">True when the claimant is disabled in the profile.</param>
public sealed record ConflictStep(string ModId, string ModName, string SourcePath, long Size, bool Wins, bool IsDisabled);
