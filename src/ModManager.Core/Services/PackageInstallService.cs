using System.Security.Cryptography;
using System.Text;

namespace ModManager.Core.Services;

/// <summary>
/// Default bridge from <see cref="IPackageLibrary"/> to <see cref="IModInstallService"/>.
/// </summary>
public sealed class PackageInstallService : IPackageInstallService
{
    private readonly IPackageLibrary _library;
    private readonly IModRepository _mods;
    private readonly IModInstallService _installer;
    private readonly IGameAdapterRegistry _games;
    private readonly IArchiveReader _archives;
    private readonly IInstallMethodDetector _methods;
    private readonly ILoaderInstaller _loaders;
    private readonly IAppLogger _logger;

    /// <summary>Creates the bridge.</summary>
    public PackageInstallService(
        IPackageLibrary library,
        IModRepository mods,
        IModInstallService installer,
        IGameAdapterRegistry games,
        IArchiveReader archives,
        IInstallMethodDetector methods,
        ILoaderInstaller loaders,
        IAppLogger logger)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _mods = mods ?? throw new ArgumentNullException(nameof(mods));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _archives = archives ?? throw new ArgumentNullException(nameof(archives));
        _methods = methods ?? throw new ArgumentNullException(nameof(methods));
        _loaders = loaders ?? throw new ArgumentNullException(nameof(loaders));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<OperationResult<InstallMethodAnalysis>> AnalyzeAsync(
        string packageId,
        string gameId,
        CancellationToken cancellationToken = default)
    {
        var package = _library.Get(packageId);
        if (package is null)
        {
            return OperationResult<InstallMethodAnalysis>.Fail("The package is not in the local library.");
        }

        if (!package.IsArchivePresent)
        {
            return OperationResult<InstallMethodAnalysis>.Fail("The package archive is missing.");
        }

        var adapter = _games.Get(gameId);
        if (adapter is null)
        {
            return OperationResult<InstallMethodAnalysis>.Fail($"No game adapter is registered for '{gameId}'.");
        }

        try
        {
            var index = await _archives.IndexAsync(package.ArchivePath, cancellationToken).ConfigureAwait(false);
            return OperationResult<InstallMethodAnalysis>.Ok(_methods.Analyze(adapter, index));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return OperationResult<InstallMethodAnalysis>.Fail($"The archive could not be read: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public Task<OperationResult<PackageDependencyReport>> InspectDependenciesAsync(
        string packageId,
        string gameId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var package = _library.Get(packageId);
        if (package is null)
        {
            return Task.FromResult(OperationResult<PackageDependencyReport>.Fail(
                "The package is not in the local library."));
        }

        var installed = _mods.ForGame(gameId);
        var inLibrary = _library.ForGame(gameId);
        var rows = new List<PackageDependencyStatus>(package.Dependencies.Count);

        foreach (var raw in package.Dependencies)
        {
            var declaration = raw?.Trim();
            if (string.IsNullOrWhiteSpace(declaration))
            {
                continue;
            }

            var requirement = VersionOrder.Parse(declaration);
            var kind = ClassifyDependency(declaration, requirement);
            var installedMatch = installed.FirstOrDefault(mod => Matches(declaration, mod.Name));
            if (installedMatch is not null)
            {
                var satisfied = requirement.IsResolvable
                    ? requirement.IsSatisfiedBy(installedMatch.Version)
                    : (bool?)null;
                rows.Add(new PackageDependencyStatus(
                    declaration,
                    satisfied == false ? PackageDependencyState.Mismatch : PackageDependencyState.Installed,
                    MatchedModId: installedMatch.Id,
                    MatchedVersion: installedMatch.Version)
                {
                    Kind = kind,
                    VersionSatisfied = satisfied,
                });
                continue;
            }

            var libraryMatch = inLibrary.FirstOrDefault(candidate =>
                !string.Equals(candidate.PackageId, packageId, StringComparison.Ordinal)
                && Matches(declaration, candidate.Name));

            if (libraryMatch is null)
            {
                rows.Add(new PackageDependencyStatus(declaration, PackageDependencyState.Missing)
                {
                    Kind = kind,
                });
                continue;
            }

            var librarySatisfied = requirement.IsResolvable
                ? requirement.IsSatisfiedBy(libraryMatch.Version)
                : (bool?)null;
            rows.Add(new PackageDependencyStatus(
                declaration,
                librarySatisfied == false ? PackageDependencyState.Mismatch : PackageDependencyState.InLibrary,
                MatchedPackageId: libraryMatch.PackageId,
                MatchedVersion: libraryMatch.Version)
            {
                Kind = kind,
                VersionSatisfied = librarySatisfied,
            });
        }

        return Task.FromResult(OperationResult<PackageDependencyReport>.Ok(
            new PackageDependencyReport(packageId, rows)));
    }

    /// <inheritdoc />
    public Task<OperationResult<FomodPlan>> ReadFomodPlanAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var package = _library.Get(packageId);
        if (package is null)
        {
            return Task.FromResult(OperationResult<FomodPlan>.Fail(
                "The package is not in the local library."));
        }

        if (!package.IsArchivePresent)
        {
            return Task.FromResult(OperationResult<FomodPlan>.Fail("The package archive is missing."));
        }

        return FomodPlanReader.ReadAsync(package.ArchivePath, _archives, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<OperationResult<FomodSelectionResult>> PreviewFomodAsync(
        string packageId,
        IReadOnlyList<FomodSelection> selections,
        CancellationToken cancellationToken = default)
    {
        var plan = await ReadFomodPlanAsync(packageId, cancellationToken).ConfigureAwait(false);
        if (!plan.Succeeded)
        {
            return OperationResult<FomodSelectionResult>.Fail(plan.Error ?? "无法读取 FOMOD 计划。");
        }

        var resolved = FomodPlanSelector.Resolve(plan.Value!, selections);
        return resolved.Errors.Count == 0
            ? OperationResult<FomodSelectionResult>.Ok(resolved)
            : OperationResult<FomodSelectionResult>.Fail(string.Join("；", resolved.Errors));
    }

    /// <inheritdoc />
    public async Task<OperationResult<StagedMod>> StageFomodAsync(
        string packageId,
        string gameId,
        IReadOnlyList<FomodSelection> selections,
        string stagingRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stagingRoot))
        {
            return OperationResult<StagedMod>.Fail("暂存根目录为空。");
        }

        var package = _library.Get(packageId);
        if (package is null)
        {
            return OperationResult<StagedMod>.Fail("The package is not in the local library.");
        }

        if (package.State != PackageState.Ready || !package.IsArchivePresent)
        {
            return OperationResult<StagedMod>.Fail(
                $"The package archive is {package.State.ToString().ToLowerInvariant()}; verify it first.");
        }

        var adapter = _games.Get(gameId);
        if (adapter is null)
        {
            return OperationResult<StagedMod>.Fail($"No game adapter is registered for '{gameId}'.");
        }

        var plan = await ReadFomodPlanAsync(packageId, cancellationToken).ConfigureAwait(false);
        if (!plan.Succeeded || plan.Value is null)
        {
            return OperationResult<StagedMod>.Fail(plan.Error ?? "无法读取 FOMOD 计划。");
        }

        var resolved = FomodPlanSelector.Resolve(plan.Value, selections);
        if (resolved.Errors.Count > 0)
        {
            return OperationResult<StagedMod>.Fail(string.Join("；", resolved.Errors));
        }

        var modId = "fomod-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(packageId))).ToLowerInvariant()[..16];

        string rootFull;
        string contentRoot;
        try
        {
            rootFull = Path.GetFullPath(stagingRoot);
            contentRoot = Path.GetFullPath(Path.Combine(rootFull, modId, "content"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return OperationResult<StagedMod>.Fail($"暂存路径无效：{ex.Message}");
        }

        if (!contentRoot.StartsWith(
                rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<StagedMod>.Fail("暂存路径越界，已拒绝。");
        }

        try
        {
            if (Directory.Exists(contentRoot))
            {
                Directory.Delete(contentRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult<StagedMod>.Fail($"无法清理旧暂存目录：{ex.Message}");
        }

        var staged = await FomodStager.StageAsync(
            package.ArchivePath,
            plan.Value,
            selections,
            contentRoot,
            _archives,
            cancellationToken).ConfigureAwait(false);

        if (!staged.Succeeded || staged.Value is null)
        {
            return OperationResult<StagedMod>.Fail(staged.Error ?? "FOMOD 暂存失败。");
        }

        var result = staged.Value;
        long totalSize = 0;
        foreach (var relative in result.WrittenFiles)
        {
            var full = Path.Combine(contentRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                totalSize += new FileInfo(full).Length;
            }
        }

        var metadata = new ModMetadata
        {
            Id = modId,
            Name = package.Name,
            Author = package.Author,
            Version = package.Version,
            GameId = gameId,
            Source = MapPlatform(package.Source.Platform),
            PackageId = package.PackageId,
            Platform = package.Source.Platform,
            SourceKey = package.Source.ModKey,
            SourceFileKey = package.Source.FileKey,
            InstallMethod = nameof(InstallMethodKind.Fomod),
            ArchivePath = package.ArchivePath,
            ArchiveHash = package.ArchiveSha256,
            NexusModId = ParseNexusId(package, isFile: false),
            NexusFileId = ParseNexusId(package, isFile: true),
            SourceUrl = package.Source.PageUrl,
            Summary = package.Description,
            StagingPath = contentRoot,
            AnalyzedRootPath = string.Empty,
            AnalyzedRootConfidence = 1.0,
            RootConfirmedByUser = true,
            Dependencies = package.Dependencies,
            InstalledAt = DateTimeOffset.UtcNow,
            State = ModState.Staged,
            ArchiveFormat = package.ArchiveFormat,
            StagedSizeBytes = totalSize,
            StagedFileCount = result.WrittenFiles.Count,
        };

        _mods.Upsert(metadata);

        var analysis = new RootAnalysis
        {
            SelectedRoot = string.Empty,
            Confidence = 1.0,
            Candidates = [],
            Signals = [],
            Format = package.ArchiveFormat,
            TotalSize = totalSize,
            FileCount = result.WrittenFiles.Count,
            IsFomod = true,
        };

        return OperationResult<StagedMod>.Ok(
            new StagedMod(metadata, analysis, contentRoot, string.Empty));
    }

    private static int? ParseNexusId(ModPackage package, bool isFile)
    {
        if (!string.Equals(package.Source.Platform, PackagePlatforms.NexusMods, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var raw = isFile ? package.Source.FileKey : package.Source.ModKey;
        return int.TryParse(raw, out var value) && value > 0 ? value : null;
    }

    private static ModSource MapPlatform(string platform) => platform.ToLowerInvariant() switch
    {
        PackagePlatforms.NexusMods => ModSource.NexusMods,
        PackagePlatforms.Thunderstore => ModSource.Thunderstore,
        PackagePlatforms.GitHub => ModSource.GitHub,
        PackagePlatforms.CurseForge => ModSource.CurseForge,
        PackagePlatforms.Local => ModSource.LocalFile,
        _ => ModSource.Other,
    };

    /// <summary>
    /// Matches a raw dependency declaration against a display name. Thunderstore uses
    /// <c>Name-1.2.3</c>, other sources use a bare name; both must resolve without a network call.
    /// </summary>
    /// <summary>
    /// Classifies how strongly a dependency was declared. This is deliberately a display/blocking
    /// classification, not a resolver: unknown declarations are never guessed into "required".
    /// </summary>
    private static PackageDependencyKind ClassifyDependency(
        string declaration,
        VersionRequirement requirement)
    {
        if (!requirement.IsResolvable)
        {
            return PackageDependencyKind.Unknown;
        }

        if (requirement.Optional)
        {
            return PackageDependencyKind.Optional;
        }

        return declaration.Contains("recommended", StringComparison.OrdinalIgnoreCase)
            ? PackageDependencyKind.Recommended
            : PackageDependencyKind.Required;
    }

    private static bool Matches(string declaration, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (string.Equals(declaration, name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return declaration.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase)
            || declaration.StartsWith(name + "@", StringComparison.OrdinalIgnoreCase)
            || declaration.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<OperationResult<StagedMod>> StageAsync(
        string packageId,
        string gameId,
        bool acceptLowConfidence = false,
        string? rootOverride = null,
        IProgress<ExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var package = _library.Get(packageId);
        if (package is null)
        {
            return OperationResult<StagedMod>.Fail("The package is not in the local library.");
        }

        if (package.State != PackageState.Ready || !package.IsArchivePresent)
        {
            return OperationResult<StagedMod>.Fail(
                $"The package archive is {package.State.ToString().ToLowerInvariant()}; "
                + "verify or re-acquire it before installing.");
        }

        var adapter = _games.Get(gameId);
        if (adapter is null)
        {
            return OperationResult<StagedMod>.Fail($"No game adapter is registered for '{gameId}'.");
        }

        // A loader distribution and a wizard-driven installer are not ordinary mods. Refuse them
        // here rather than extracting a BepInEx/MelonLoader payload into the mod staging area or
        // silently applying one branch of a FOMOD. The dedicated paths are LoaderInstaller and a
        // future wizard; both must be explicit user actions.
        var method = await AnalyzeAsync(packageId, gameId, cancellationToken).ConfigureAwait(false);
        if (method.Succeeded && method.Value is { } analysis)
        {
            if (!analysis.CanAutoStage)
            {
                return OperationResult<StagedMod>.Fail(
                    analysis.AutoStageBlockReason ?? "该安装包不能通过普通自动安装入口处理。");
            }

            // A low-confidence classification is not an automatic decision. The caller may still
            // choose to stage it for a manual preview (acceptLowConfidence), but the default path
            // must never guess a target root for an unfamiliar archive.
            if (analysis.NeedsConfirmation && !acceptLowConfidence)
            {
                return OperationResult<StagedMod>.Fail(
                    $"安装方式识别置信度仅为 {analysis.Confidence:P0}，需要人工确认目标目录后再暂存。");
            }
        }

        var request = new InstallRequest
        {
            ArchivePath = package.ArchivePath,
            GameId = gameId,
            DisplayName = package.Name,
            Version = package.Version,
            SourceUrl = package.Source.PageUrl,
            ExpectedSha256 = package.ArchiveSha256,
            PackageId = package.PackageId,
            Platform = package.Source.Platform,
            SourceKey = package.Source.ModKey,
            SourceFileKey = package.Source.FileKey,
            InstallMethod = package.InstallMethodHint,
            AcceptLowConfidence = acceptLowConfidence,
            RootOverride = rootOverride,
        };

        return await _installer
            .StageAsync(request, adapter, _logger, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<OperationResult<StagedMod>> ImportAndStageAsync(
        string archivePath,
        string gameId,
        string? displayName = null,
        string? version = null,
        bool acceptLowConfidence = false,
        CancellationToken cancellationToken = default)
    {
        var imported = await _library
            .ImportLocalAsync(archivePath, gameId, displayName, version, cancellationToken)
            .ConfigureAwait(false);

        if (!imported.Succeeded || imported.Value is null)
        {
            return OperationResult<StagedMod>.Fail(imported.Error ?? "Import failed.");
        }

        return await StageAsync(
            imported.Value.Package.PackageId,
            gameId,
            acceptLowConfidence,
            rootOverride: null,
            progress: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<OperationResult<InstallPreview>> PreviewAsync(
        StagedMod staged,
        string installPath,
        IReadOnlyList<ModSetEntry> existingMods)
    {
        var adapter = _games.Get(staged.Metadata.GameId);
        return adapter is null
            ? Task.FromResult(OperationResult<InstallPreview>.Fail(
                $"No game adapter is registered for '{staged.Metadata.GameId}'."))
            : _installer.PreviewAsync(staged, adapter, installPath, existingMods);
    }

    /// <inheritdoc />
    public Task<DeployResult> InstallAsync(
        StagedMod staged,
        string installPath,
        IProgress<DeployProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var adapter = _games.Get(staged.Metadata.GameId);
        if (adapter is null)
        {
            return Task.FromResult(new DeployResult(
                false,
                null,
                0,
                0,
                string.Empty,
                [],
                $"No game adapter is registered for '{staged.Metadata.GameId}'."));
        }

        return _installer.InstallAsync(staged, adapter, installPath, _logger, progress, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<OperationResult<LoaderInstallResult>> InstallLoaderAsync(
        string packageId,
        string gameId,
        string installPath,
        CancellationToken cancellationToken = default)
    {
        var package = _library.Get(packageId);
        if (package is null)
        {
            return OperationResult<LoaderInstallResult>.Fail("The package is not in the local library.");
        }

        if (package.State != PackageState.Ready || !package.IsArchivePresent)
        {
            return OperationResult<LoaderInstallResult>.Fail(
                $"The package archive is {package.State.ToString().ToLowerInvariant()}; verify it first.");
        }

        var adapter = _games.Get(gameId);
        if (adapter is null)
        {
            return OperationResult<LoaderInstallResult>.Fail($"No game adapter is registered for '{gameId}'.");
        }

        var method = await AnalyzeAsync(packageId, gameId, cancellationToken).ConfigureAwait(false);
        var loaderAnalysis = method.Value;
        if (!method.Succeeded || loaderAnalysis is null)
        {
            return OperationResult<LoaderInstallResult>.Fail(
                method.Error ?? "The loader archive could not be classified.");
        }

        if (loaderAnalysis.Kind != InstallMethodKind.LoaderDistribution)
        {
            return OperationResult<LoaderInstallResult>.Fail(
                "This package is not a BepInEx/MelonLoader distribution; install it as a normal mod.");
        }

        return await _loaders
            .InstallFromArchiveAsync(adapter, installPath, package.ArchivePath, _logger, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<DeployResult> UninstallAsync(
        string gameId,
        string modId,
        IGameAdapter adapter,
        string installPath,
        bool deleteModifiedFiles,
        IAppLogger logger,
        CancellationToken cancellationToken = default)
        => _installer.UninstallAsync(
            gameId,
            modId,
            adapter,
            installPath,
            deleteModifiedFiles,
            logger,
            cancellationToken);
}
