namespace ModManager.Core;

/// <summary>Result of installing a loader distribution into a game folder.</summary>
/// <param name="Installed">True when the loader markers are present afterwards.</param>
/// <param name="LoaderKind">Loader that was installed.</param>
/// <param name="FilesCopied">Files written into the game folder.</param>
/// <param name="BackupDirectory">Where overwritten originals were preserved, if any.</param>
/// <param name="Warnings">Non-fatal remarks.</param>
public sealed record LoaderInstallResult(
    bool Installed,
    ModLoaderKind LoaderKind,
    int FilesCopied,
    string? BackupDirectory,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Installs a mod-loader distribution (BepInEx/MelonLoader) from a trusted archive.
/// </summary>
/// <remarks>
/// This is intentionally not part of the normal mod install pipeline: a loader distribution writes
/// into the game root and can replace runtime DLLs. The caller must show the source, the files it
/// will touch and obtain explicit confirmation before calling <see cref="InstallFromArchiveAsync"/>.
/// No arbitrary executable is ever launched.
/// </remarks>
public interface ILoaderInstaller
{
    /// <summary>Checks whether the game's declared loader is present.</summary>
    LoaderStatus Check(IGameAdapter adapter, string installPath);

    /// <summary>
    /// Installs a loader zip into the game root, backing up every file it replaces. Only known
    /// loader paths are accepted; anything else is refused.
    /// </summary>
    Task<OperationResult<LoaderInstallResult>> InstallFromArchiveAsync(
        IGameAdapter adapter,
        string installPath,
        string archivePath,
        IAppLogger logger,
        CancellationToken cancellationToken = default);
}
