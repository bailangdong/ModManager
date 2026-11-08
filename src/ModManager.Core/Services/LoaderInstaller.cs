using ModManager.Core.Safety;

namespace ModManager.Core.Services;

/// <summary>
/// Installs BepInEx/MelonLoader distributions from their official zip. The archive is extracted to
/// an app-owned staging directory first, then only an allow-listed set of loader paths is copied
/// into the game root. Every replaced file is backed up before the copy; a failure restores them.
/// </summary>
public sealed class LoaderInstaller : ILoaderInstaller
{
    private static readonly string[] AllowedDirectories = ["bepinex/", "melonloader/"];

    private static readonly HashSet<string> AllowedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "doorstop_config.ini",
        ".doorstop_version",
        "winhttp.dll",
        "version.dll",
        "dobby.dll",
        "changelog.txt",
        "readme.txt",
        "readme.md",
    };

    private readonly AppPaths _paths;
    private readonly IArchiveReader _archives;

    /// <summary>Creates the installer.</summary>
    public LoaderInstaller(AppPaths paths, IArchiveReader archives)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _archives = archives ?? throw new ArgumentNullException(nameof(archives));
    }

    /// <inheritdoc />
    public LoaderStatus Check(IGameAdapter adapter, string installPath) => adapter.CheckLoader(installPath);

    /// <inheritdoc />
    public async Task<OperationResult<LoaderInstallResult>> InstallFromArchiveAsync(
        IGameAdapter adapter,
        string installPath,
        string archivePath,
        IAppLogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            return OperationResult<LoaderInstallResult>.Fail("The game directory does not exist.");
        }

        if (!File.Exists(archivePath))
        {
            return OperationResult<LoaderInstallResult>.Fail("The loader archive does not exist.");
        }

        var kind = adapter.Definition.Loader?.Kind ?? ModLoaderKind.Unknown;
        if (kind is not (ModLoaderKind.BepInEx or ModLoaderKind.MelonLoader))
        {
            return OperationResult<LoaderInstallResult>.Fail(
                "Automatic loader installation is only implemented for BepInEx and MelonLoader.");
        }

        ArchiveIndex index;
        try
        {
            index = await _archives.IndexAsync(archivePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return OperationResult<LoaderInstallResult>.Fail($"The loader archive could not be read: {ex.Message}");
        }

        var selected = index.Entries
            .Where(entry => !entry.IsDirectory)
            .Select(entry => (Entry: entry, Relative: NormalizeLoaderPath(entry.Path)))
            .Where(pair => pair.Relative is not null)
            .Select(pair => (pair.Entry, Relative: pair.Relative!))
            .ToList();

        if (selected.Count == 0)
        {
            return OperationResult<LoaderInstallResult>.Fail(
                "This archive does not look like a BepInEx/MelonLoader distribution.");
        }

        var selectedPaths = selected.Select(pair => pair.Entry.Path).ToList();

        var staging = Path.Combine(_paths.Mods, "_loader", Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(
            _paths.Backups,
            AppPaths.Sanitize(adapter.GameId),
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-loader-{kind.ToString().ToLowerInvariant()}");

        var created = new List<string>();
        var backups = new List<(string Original, string Backup)>();
        var warnings = new List<string>();

        try
        {
            Directory.CreateDirectory(staging);
            await _archives.ExtractAsync(
                    archivePath,
                    staging,
                    selectedPaths,
                    progress: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            foreach (var (entry, relative) in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var target = Path.GetFullPath(Path.Combine(installPath, relative));
                if (!IsInside(installPath, target))
                {
                    return OperationResult<LoaderInstallResult>.Fail(
                        $"The loader archive contains an unsafe path: {entry.Path}");
                }

                var staged = Path.Combine(staging, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(staged))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (File.Exists(target))
                {
                    Directory.CreateDirectory(backup);
                    var saved = Path.Combine(backup, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    await FileSystemOps.CopyAsync(target, saved, cancellationToken).ConfigureAwait(false);
                    backups.Add((target, saved));
                }
                else
                {
                    created.Add(target);
                }

                await FileSystemOps.CopyAsync(staged, target, cancellationToken).ConfigureAwait(false);
            }

            var status = adapter.CheckLoader(installPath);
            if (!status.Installed)
            {
                warnings.Add(status.Message);
            }

            logger.Info(
                "Loader",
                $"已安装 {kind} 运行环境（{selected.Count} 个文件）。",
                $"备份目录：{(backups.Count > 0 ? backup : "无需备份")}");

            return OperationResult<LoaderInstallResult>.Ok(
                new LoaderInstallResult(status.Installed, kind, selected.Count, backups.Count > 0 ? backup : null, warnings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.Error("Loader", $"安装 {kind} 失败，正在恢复。", ex.Message);

            foreach (var (original, saved) in backups)
            {
                try
                {
                    await FileSystemOps.CopyAsync(saved, original, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"无法恢复 {original}：{restoreError.Message}");
                }
            }

            foreach (var file in created)
            {
                FileSystemOps.RemoveIfPresent(file);
            }

            return OperationResult<LoaderInstallResult>.Fail($"Loader installation failed: {ex.Message}");
        }
        finally
        {
            FileSystemOps.RemoveDirectoryTree(staging);
        }
    }

    private static string? NormalizeLoaderPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        if (IsAllowed(normalized))
        {
            return normalized;
        }

        // Official packs are sometimes wrapped in a single top-level folder (e.g.
        // "BepInEx_win_x64_5.4.23.2/BepInEx/core/..."). Strip exactly one wrapper, never more,
        // so an arbitrary path cannot be promoted into the game root by accident.
        var slash = normalized.IndexOf('/');
        if (slash > 0)
        {
            var inner = normalized[(slash + 1)..];
            if (IsAllowed(inner))
            {
                return inner;
            }
        }

        return null;
    }

    private static bool IsAllowed(string normalized)
    {
        if (AllowedRootFiles.Contains(normalized))
        {
            return true;
        }

        return AllowedDirectories.Any(directory =>
            normalized.StartsWith(directory, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInside(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
