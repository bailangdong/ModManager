namespace ModManager.Core;

/// <summary>
/// Bridges the package library to the installation pipeline. This is the only place that turns a
/// <see cref="ModPackage"/> into an <see cref="InstallRequest"/>.
/// </summary>
/// <remarks>
/// Keeping the bridge separate from both the library and <see cref="IModInstallService"/> is what
/// lets the acquisition center stop at "package is in the library": no code on the download path
/// touches a game folder, and no installer code needs to know which platform produced the bytes.
/// </remarks>
public interface IPackageInstallService
{
    /// <summary>Classifies a package's install method without extracting it.</summary>
    Task<OperationResult<InstallMethodAnalysis>> AnalyzeAsync(
        string packageId,
        string gameId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports whether a package's declared dependencies are already installed or present in the
    /// library. Never downloads anything.
    /// </summary>
    Task<OperationResult<PackageDependencyReport>> InspectDependenciesAsync(
        string packageId,
        string gameId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a FOMOD plan from a library package. This only describes steps, groups and files;
    /// it never applies an option or writes to the game directory.
    /// </summary>
    Task<OperationResult<FomodPlan>> ReadFomodPlanAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a user's FOMOD selections against the plan and returns the files that would be
    /// staged. It is pure with respect to the file system: nothing is extracted or written.
    /// </summary>
    Task<OperationResult<FomodSelectionResult>> PreviewFomodAsync(
        string packageId,
        IReadOnlyList<FomodSelection> selections,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a user's FOMOD selections into a staging directory and registers the result as a
    /// <see cref="StagedMod"/> so the normal deployment preview/install pipeline can consume it.
    /// Writes only below <paramref name="stagingRoot"/>, never into the game directory.
    /// </summary>
    Task<OperationResult<StagedMod>> StageFomodAsync(
        string packageId,
        string gameId,
        IReadOnlyList<FomodSelection> selections,
        string stagingRoot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stages a library package for one game. Reads the archive in place and never writes to the
    /// game directory.
    /// </summary>
    Task<OperationResult<StagedMod>> StageAsync(
        string packageId,
        string gameId,
        bool acceptLowConfidence = false,
        string? rootOverride = null,
        IProgress<ExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Imports a user-picked archive into the library, then stages it.</summary>
    Task<OperationResult<StagedMod>> ImportAndStageAsync(
        string archivePath,
        string gameId,
        string? displayName = null,
        string? version = null,
        bool acceptLowConfidence = false,
        CancellationToken cancellationToken = default);

    /// <summary>Builds the installation preview for a staged package.</summary>
    Task<OperationResult<InstallPreview>> PreviewAsync(
        StagedMod staged,
        string installPath,
        IReadOnlyList<ModSetEntry> existingMods);

    /// <summary>Deploys a staged package.</summary>
    Task<DeployResult> InstallAsync(
        StagedMod staged,
        string installPath,
        IProgress<DeployProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs a loader distribution that is already in the library. The caller must confirm the
    /// source and the files it will touch first; this method never runs an executable.
    /// </summary>
    Task<OperationResult<LoaderInstallResult>> InstallLoaderAsync(
        string packageId,
        string gameId,
        string installPath,
        CancellationToken cancellationToken = default);

    /// <summary>Uninstalls one installed instance.</summary>
    Task<DeployResult> UninstallAsync(
        string gameId,
        string modId,
        IGameAdapter adapter,
        string installPath,
        bool deleteModifiedFiles,
        IAppLogger logger,
        CancellationToken cancellationToken = default);
}
