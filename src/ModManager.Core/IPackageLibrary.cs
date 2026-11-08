namespace ModManager.Core;

/// <summary>
/// The local package repository: archives the user has acquired, independent of any game folder
/// and independent of the platform that produced them.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam between the acquisition center and the installation engine. Acquisition only
/// ever writes <see cref="ModPackage"/> records and archive files here; installation only ever
/// reads them. No platform API type (<c>NexusModFile</c>, Thunderstore JSON, etc.) crosses this
/// boundary, and no game path is stored in a package.
/// </para>
/// <para>
/// Implementations must be idempotent: importing the same bytes twice returns the existing
/// package, and committing the same source identity twice does not create a second record.
/// </para>
/// </remarks>
public interface IPackageLibrary
{
    /// <summary>All packages, newest first.</summary>
    IReadOnlyList<ModPackage> All();

    /// <summary>Packages for one game key, newest first.</summary>
    IReadOnlyList<ModPackage> ForGame(string gameId);

    /// <summary>One package by id, or null.</summary>
    ModPackage? Get(string packageId);

    /// <summary>Find a package by archive SHA-256, case-insensitive.</summary>
    ModPackage? FindByHash(string sha256, string? gameId = null, string? platform = null);

    /// <summary>Find a package by exact source identity, or null.</summary>
    ModPackage? FindBySource(PackageSourceRef source);

    /// <summary>Import a user-picked archive into the library.</summary>
    /// <param name="archivePath">Existing archive outside the library.</param>
    /// <param name="gameId">Game key this archive is intended for.</param>
    /// <param name="displayName">Optional display name; falls back to the file name.</param>
    /// <param name="version">Optional version string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationResult<PackageImportResult>> ImportLocalAsync(
        string archivePath,
        string gameId,
        string? displayName = null,
        string? version = null,
        CancellationToken cancellationToken = default);

    /// <summary>Commit a completed download into the library.</summary>
    Task<OperationResult<ModPackage>> CommitAsync(
        PackageCommitRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Re-hash the archive and update its state.</summary>
    Task<OperationResult<ModPackage>> VerifyAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    /// <summary>Remove a package and its archive. Never touches a game folder.</summary>
    Task<OperationResult<bool>> RemoveAsync(
        string packageId,
        CancellationToken cancellationToken = default);

    /// <summary>Library counters for the UI.</summary>
    PackageLibrarySummary GetSummary();
}
