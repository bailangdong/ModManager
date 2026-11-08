using ModManager.Core.Safety;

namespace ModManager.Core.Services;

/// <summary>
/// The end-to-end mod install pipeline: archive, analyze, stage, preview, deploy.
/// </summary>
/// <remarks>
/// <para>
/// The user's spec §5 fixes this pipeline and forbids the shortcut in the strongest terms:
/// <i>"不要：ExtractAll(gameDirectory) 这是不可接受的"</i>. The stages are therefore separate methods
/// rather than steps inside one call, so no caller can collapse them and no future edit can
/// accidentally reintroduce the shortcut:
/// </para>
/// <code>
/// Download → Archive → Analyze → Staging → Deployment → Game
/// </code>
/// <para>
/// The nine steps the spec lists for installation map onto those stages as follows: download and hashing
/// happen before this class; analysing, finding the mod root, judging the mod type and generating the
/// install plan are <see cref="StageAsync"/> plus <see cref="PreviewAsync"/>; the dependency and conflict
/// checks are part of the preview; backups, deploy and verify are the applier's job; and the database and
/// manifest writes close it out.
/// </para>
/// <para>
/// <b>Why staging is non-negotiable.</b> A mod archive cannot be trusted to describe its own layout, so
/// analysis has to happen before anything reaches the game folder, and it has to be reversible. Staging
/// gives both: extraction goes into the application's own directory where a mistake costs nothing, and
/// the deploy engine is the only thing that ever writes to the game.
/// </para>
/// </remarks>
public sealed class ModInstallService : IModInstallService
{
    private readonly AppPaths _paths;
    private readonly IArchiveReader _archives;
    private readonly IModAnalyzer _analyzer;
    private readonly IDeploymentPlanner _planner;
    private readonly IDeploymentApplier _applier;
    private readonly IVerifyService _verify;
    private readonly IDependencyService _dependencies;
    private readonly IModRepository _mods;
    private readonly IBackupRepository _backups;
    private readonly IConflictRepository _conflicts;
    private readonly IPathSafetyService _pathSafety;
    private readonly SettingsCarrier _settings;

    /// <summary>Creates the install service.</summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="archives">Archive reader.</param>
    /// <param name="analyzer">Root analyzer.</param>
    /// <param name="planner">Deployment planner.</param>
    /// <param name="applier">Deployment applier.</param>
    /// <param name="verify">Verification service.</param>
    /// <param name="dependencies">Dependency service.</param>
    /// <param name="mods">Mod repository.</param>
    /// <param name="backups">Backup repository.</param>
    /// <param name="conflicts">Conflict repository.</param>
    /// <param name="pathSafety">Path safety gate.</param>
    /// <param name="settings">Live settings carrier, shared with the UI.</param>
    public ModInstallService(
        AppPaths paths,
        IArchiveReader archives,
        IModAnalyzer analyzer,
        IDeploymentPlanner planner,
        IDeploymentApplier applier,
        IVerifyService verify,
        IDependencyService dependencies,
        IModRepository mods,
        IBackupRepository backups,
        IConflictRepository conflicts,
        IPathSafetyService pathSafety,
        SettingsCarrier settings)
    {
        _paths = paths;
        _archives = archives;
        _analyzer = analyzer;
        _planner = planner;
        _applier = applier;
        _verify = verify;
        _dependencies = dependencies;
        _mods = mods;
        _backups = backups;
        _conflicts = conflicts;
        _pathSafety = pathSafety;
        _settings = settings;
    }

    /// <inheritdoc />
    public async Task<OperationResult<StagedMod>> StageAsync(
        InstallRequest request,
        IGameAdapter adapter,
        IAppLogger logger,
        IProgress<ExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(logger);

        if (!File.Exists(FileSystemOps.ToExtendedPath(request.ArchivePath)))
        {
            return OperationResult<StagedMod>.Fail($"The archive was not found: {request.ArchivePath}");
        }

        // The archive hash is the duplicate-install guard: staging the same archive twice under two ids
        // would give the user two mods fighting over the same files.
        var archiveHash = await FileSystemOps.HashFileAsync(request.ArchivePath, cancellationToken).ConfigureAwait(false);

        if (request.ExpectedSha256 is { Length: > 0 } expected
            && !string.Equals(expected, archiveHash, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<StagedMod>.Fail(
                $"The archive does not match its expected SHA-256 (expected {expected}, got {archiveHash}). "
                + "The download may be corrupt; delete it and download again.");
        }

        var existing = _mods.ForGame(adapter.GameId).FirstOrDefault(mod =>
            string.Equals(mod.ArchiveHash, archiveHash, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return OperationResult<StagedMod>.Fail(
                $"This exact archive is already installed as '{existing.Name}'. "
                + "Remove that mod first if you want to reinstall it.");
        }

        var format = _archives.DetectFormat(request.ArchivePath);
        if (!_archives.CanRead(format))
        {
            return OperationResult<StagedMod>.Fail(
                $"Archives in the {format} format are not supported. Extract "
                + $"'{Path.GetFileName(request.ArchivePath)}' manually, then add the resulting folder.");
        }

        // --- analyse before extracting ----------------------------------------------------------
        ArchiveIndex index;
        try
        {
            index = await _archives.IndexAsync(request.ArchivePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            return OperationResult<StagedMod>.Fail($"The archive could not be read: {ex.Message}");
        }

        var analysis = _analyzer.Analyze(adapter, index);

        // The adapter gets the final say, because it knows things the generic scorer cannot.
        if (adapter.RefineRootAnalysis(analysis.Candidates, index.Entries) is { } refined)
        {
            analysis = refined;
        }

        // A user-chosen root overrides the analyzer entirely. This is the "let the user confirm" escape
        // hatch the spec requires, and it is recorded so the UI can show that the choice was manual.
        var rootConfirmed = false;
        if (request.RootOverride is not null)
        {
            analysis = analysis with { SelectedRoot = request.RootOverride.Trim('/'), Confidence = 1d };
            rootConfirmed = true;
        }
        else if (analysis.NeedsUserConfirmation && !request.AcceptLowConfidence)
        {
            return OperationResult<StagedMod>.Fail(
                $"The install layout could not be determined with confidence "
                + $"({analysis.Confidence:0.00} is below {RootAnalysis.Threshold:0.00}). "
                + "Review the installation preview and choose the correct mod root.");
        }

        // --- stage ------------------------------------------------------------------------------
        var modId = CreateModId(request, adapter);
        var stagingRoot = Path.Combine(_paths.Mods, modId, "content");

        var appCheck = _pathSafety.ValidateAppPath(stagingRoot);
        if (!appCheck.Allowed)
        {
            return OperationResult<StagedMod>.Fail($"The staging directory was refused: {appCheck.Reason}");
        }

        // A user-supplied root override is still untrusted input: it must be a plain relative path,
        // or the staging layout below would be allowed to walk outside the staging root.
        if (analysis.SelectedRoot.Length > 0
            && !ArchiveReader.IsSafeEntryName(analysis.SelectedRoot.TrimEnd('/'), out var rootProblem))
        {
            return OperationResult<StagedMod>.Fail($"The chosen mod root is not a safe relative path: {rootProblem}");
        }

        // Built before extraction: everything outside the chosen root is simply not extracted, so noise
        // like readmes never reaches the staging area and cannot be deployed by accident.
        var entriesToExtract = SelectEntriesUnderRoot(index, analysis.SelectedRoot);

        if (entriesToExtract.Count == 0)
        {
            return OperationResult<StagedMod>.Fail(
                "No files were found under the selected mod root. The archive may be empty or the root "
                + "may be wrong.");
        }

        ExtractionResult extraction;
        try
        {
            extraction = await _archives.ExtractAsync(
                request.ArchivePath,
                stagingRoot,
                entriesToExtract,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return OperationResult<StagedMod>.Fail($"Extraction failed: {ex.Message}");
        }

        if (extraction.Rejected.Any(rejection => rejection.IsSecurityRejection))
        {
            // A rejected path is not a warning: it means the archive tried to write outside its own
            // staging directory. The staging area is removed and the install refused.
            var details = string.Join(
                "; ",
                extraction.Rejected
                    .Where(rejection => rejection.IsSecurityRejection)
                    .Take(5)
                    .Select(rejection => $"{rejection.EntryPath}: {rejection.Reason}"));

            logger.Error("Install", $"“{Path.GetFileName(request.ArchivePath)}” 已被拒绝：其中包含不安全的路径。", details);
            FileSystemOps.RemoveDirectoryTree(Path.Combine(_paths.Mods, modId));

            return OperationResult<StagedMod>.Fail(
                "The archive was refused because it contains file paths that try to escape the mod folder: " + details);
        }

        // --- adapter mapping: strip the wrapper and apply the adapter's deploy mapping ----------
        // The generic pipeline stage: Archive → Analyze → Adapter → staging layout → InstallPlan.
        // The analyzer picks the root; the adapter maps staged paths to their final relative
        // location. Both happen inside the app-owned staging area, never in the game folder.
        if (!NormalizeStagedTree(adapter, stagingRoot, analysis.SelectedRoot, logger))
        {
            FileSystemOps.RemoveDirectoryTree(Path.Combine(_paths.Mods, modId));
            return OperationResult<StagedMod>.Fail(
                "The staged files could not be laid out safely; the install was cancelled.");
        }

        var metadata = new ModMetadata
        {
            Id = modId,
            Name = request.DisplayName ?? DeriveDisplayName(request.ArchivePath, analysis.SelectedRoot),
            GameId = adapter.GameId,
            // Provenance is recorded from what the caller knew at install time. A Nexus id here is what
            // lets the library link a local mod to its online entry without matching on names.
            Source = MapSource(request),
            PackageId = request.PackageId,
            Platform = request.Platform,
            SourceKey = request.SourceKey,
            SourceFileKey = request.SourceFileKey,
            InstallMethod = request.InstallMethod,
            NexusModId = request.NexusModId,
            NexusFileId = request.NexusFileId,
            SourceUrl = request.SourceUrl,
            Version = request.Version,
            ArchivePath = request.ArchivePath,
            ArchiveHash = archiveHash,
            StagingPath = stagingRoot,
            AnalyzedRootPath = analysis.SelectedRoot,
            AnalyzedRootConfidence = analysis.Confidence,
            RootConfirmedByUser = rootConfirmed,
            ArchiveFormat = format,
            StagedFileCount = extraction.ExtractedPaths.Count,
            StagedSizeBytes = extraction.BytesWritten,
        };

        _mods.Upsert(metadata);

        logger.Info(
            "Install",
            $"已暂存 “{metadata.Name}”（{extraction.ExtractedPaths.Count} 个文件，根目录 “{analysis.SelectedRoot}”）。",
            $"置信度 {analysis.Confidence:0.00}，Mod 标识 {modId}");

        return OperationResult<StagedMod>.Ok(new StagedMod(metadata, analysis, stagingRoot, analysis.SelectedRoot));
    }

    /// <inheritdoc />
    public async Task<OperationResult<InstallPreview>> PreviewAsync(
        StagedMod staged,
        IGameAdapter adapter,
        string installPath,
        IReadOnlyList<ModSetEntry> existingMods)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(existingMods);

        var deploymentRoot = DeploymentPlanner.ResolveDeploymentRoot(adapter.Definition, installPath);

        // The preview plan is built by the same planner that performs the install. That is the whole
        // benefit of a pure planner: the preview is not an estimate of the install, it IS the plan,
        // rendered. There is no second code path that can disagree with what happens next.
        var plan = _planner.BuildPreviewPlan(adapter.Definition, installPath, staged.Metadata, existingMods);

        var entries = new List<PreviewEntry>();
        var rejections = new List<string>();

        foreach (var file in plan.Files)
        {
            var target = DeploymentPlanner.SafeCombine(deploymentRoot, file.RelativeTarget);

            if (file.Safety == PathSafetyVerdict.Unsafe)
            {
                rejections.Add($"{file.RelativeTarget}: {file.Note}");
            }

            var verdict = file.Safety;
            if (verdict != PathSafetyVerdict.Unsafe
                && plan.Conflicts.Any(conflict =>
                    string.Equals(conflict.RelativeTarget, file.RelativeTarget, StringComparison.OrdinalIgnoreCase)))
            {
                verdict = PathSafetyVerdict.Conflict;
            }

            entries.Add(new PreviewEntry(file.SourcePath ?? file.RelativeTarget, target, verdict, file.Note));
        }

        var missing = _dependencies.FindMissing(
            adapter,
            installPath,
            [.. existingMods.Select(mod => mod.Metadata), staged.Metadata]);

        var blockReasons = BuildBlockReasons(staged, missing, rejections);
        var preview = new InstallPreview
        {
            ModId = staged.Metadata.Id,
            ModName = staged.Metadata.Name,
            GameId = adapter.GameId,
            DeploymentRoot = deploymentRoot,
            DetectedRoot = staged.RootRelativePath,
            Confidence = staged.Analysis.Confidence,
            Signals = staged.Analysis.Signals,
            Entries = entries,
            Conflicts = plan.Conflicts,
            MissingDependencies = missing,
            Rejections = rejections,
            BlockReasons = blockReasons,
            TotalBytes = staged.Metadata.StagedSizeBytes,
        };

        return await Task.FromResult(OperationResult<InstallPreview>.Ok(preview)).ConfigureAwait(false);
    }

    private static List<string> BuildBlockReasons(
        StagedMod staged,
        IReadOnlyList<MissingDependency> missing,
        IReadOnlyList<string> rejections)
    {
        var reasons = new List<string>();

        if (staged.Analysis.NeedsUserConfirmation && !staged.Metadata.RootConfirmedByUser)
        {
            reasons.Add(
                $"安装根目录置信度只有 {staged.Analysis.Confidence:0.00}，低于 {RootAnalysis.Threshold:0.00}；"
                + "请先在预览中确认或重新选择根目录。");
        }

        foreach (var dependency in missing)
        {
            var requirement = VersionOrder.Parse(dependency.Requirement);
            if (dependency.IsLoader || !requirement.Optional)
            {
                reasons.Add($"缺少必需依赖：{dependency.Requirement}。");
            }
        }

        if (rejections.Count > 0)
        {
            reasons.Add("安装计划包含不安全路径，已阻止部署。");
        }

        return reasons;
    }

    /// <inheritdoc />
    public async Task<DeployResult> InstallAsync(
        StagedMod staged,
        IGameAdapter adapter,
        string installPath,
        IAppLogger logger,
        IProgress<DeployProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(logger);

        if (staged.Analysis.NeedsUserConfirmation && !staged.Metadata.RootConfirmedByUser)
        {
            var reason =
                $"安装根目录置信度只有 {staged.Analysis.Confidence:0.00}，低于 {RootAnalysis.Threshold:0.00}；"
                + "必须先由用户确认或重新选择根目录，不能自动安装。";
            logger.Warn("Install", $"已阻止 “{staged.Metadata.Name}” 的自动安装：{reason}");
            return new DeployResult(false, null, 0, 0, "none", [reason], reason);
        }

        var missingBeforeInstall = _dependencies.FindMissing(
            adapter,
            installPath,
            [.. _mods.ForGame(adapter.GameId).Where(mod => mod.Id != staged.Metadata.Id), staged.Metadata]);
        var blockingDependencies = missingBeforeInstall
            .Where(dependency => dependency.IsLoader || !VersionOrder.Parse(dependency.Requirement).Optional)
            .ToList();
        if (blockingDependencies.Count > 0)
        {
            var reason = "缺少必需依赖：" + string.Join("、", blockingDependencies.Select(d => d.Requirement));
            logger.Warn("Install", $"已阻止 “{staged.Metadata.Name}” 的安装：{reason}");
            return new DeployResult(false, null, 0, 0, "none", [reason], reason);
        }

        var profile = EnsureProfile(adapter.GameId, staged.Metadata.Id);
        var mods = BuildModSet(adapter, installPath, profile, [staged]);

        // A single-mod plan, but built through the shared planner so the same conflict resolution and the
        // same safety classification apply as in a full deployment. The planner scopes removals to mods
        // present in its mod set, so planning one mod install can only ever remove that mod's own files.
        var plan = _planner.BuildPlan(
            adapter.Definition,
            installPath,
            profile,
            mods,
            _conflicts.ForGame(adapter.GameId));

        var unsafeFiles = plan.Files.Where(file => file.Safety == PathSafetyVerdict.Unsafe).ToList();
        if (unsafeFiles.Count > 0)
        {
            var reason = "安装计划包含不安全路径：" + string.Join("、", unsafeFiles.Select(file => file.RelativeTarget));
            logger.Warn("Install", $"已阻止 “{staged.Metadata.Name}” 的部署：{reason}");
            return new DeployResult(false, null, 0, 0, "none", [reason], reason);
        }

        if (plan.IsEmpty && plan.Conflicts.Count == 0)
        {
            logger.Warn("Install", $"“{staged.Metadata.Name}” 没有生成可部署的文件。");
        }

        var settings = _settings.Deployment;
        var result = await _applier
            .ApplyAsync(plan, adapter.Definition, mods, settings, logger, progress, cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            // The manifest the applier published covers the whole plan; rewrite it as this mod's own
            // manifest so uninstall can find exactly this mod's files.
            var previous = _mods.GetManifest(adapter.GameId, staged.Metadata.Id);
            var written = result.Manifest!.Files
                .Where(file => string.Equals(file.ModId, staged.Metadata.Id, StringComparison.Ordinal))
                .ToList();

            // A reinstall whose plan turned out to be a no-op writes nothing, so the rows already on
            // record must be carried over — otherwise the mod would appear to own no files at all and
            // the next uninstall could not clean up after it. Rows are only carried when the file is
            // still on disk, so a stale claim never survives.
            var merged = new List<DeployedFile>(written);
            if (previous is not null)
            {
                foreach (var file in previous.Files)
                {
                    if (merged.Any(existing =>
                            string.Equals(existing.RelativeTarget, file.RelativeTarget, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var absolute = DeploymentPlanner.SafeCombine(result.Manifest.DeploymentRoot, file.RelativeTarget);
                    if (FileSystemOps.ExistsNoFollow(absolute))
                    {
                        merged.Add(file);
                    }
                }
            }

            var manifest = result.Manifest with
            {
                ModId = staged.Metadata.Id,
                Name = staged.Metadata.Name,
                Version = staged.Metadata.Version,
                Source = staged.Metadata.Source,
                PackageId = staged.Metadata.PackageId,
                Files = merged,
                Dependencies = staged.Metadata.Dependencies,
            };

            _mods.SaveManifest(manifest);
            _mods.Upsert(staged.Metadata with { State = ModState.Deployed });

            // Register every backup so a later restore can find it without re-reading the manifest.
            foreach (var file in manifest.Files.Where(file => file.BackupPath is not null))
            {
                _backups.Add(new BackupEntry
                {
                    GameId = adapter.GameId,
                    OriginalPath = DeploymentPlanner.SafeCombine(manifest.DeploymentRoot, file.RelativeTarget),
                    RelativePath = file.RelativeTarget,
                    BackupPath = file.BackupPath!,
                    OriginalSha256 = file.OriginalSha256 ?? string.Empty,
                    ModId = staged.Metadata.Id,
                });
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<DeployResult> UninstallAsync(
        string gameId,
        string modId,
        IGameAdapter adapter,
        string installPath,
        bool deleteModifiedFiles,
        IAppLogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(logger);

        _ = installPath;

        var manifest = _mods.GetManifest(gameId, modId);
        if (manifest is null)
        {
            // Nothing was ever deployed, so this is a staging-only cleanup rather than an error.
            var purged = PurgeStaging(modId);
            logger.Info("Install", $"Mod {modId} 没有 Manifest，只移除了它的暂存文件。");

            return new DeployResult(
                purged.Succeeded,
                null,
                0,
                0,
                "none",
                [purged.Error ?? "No deployment was recorded for this mod."],
                purged.Succeeded ? null : purged.Error);
        }

        // The user-modification protection rule, applied before anything is deleted. Files whose hash no
        // longer matches what we deployed are reported for confirmation and, absent that confirmation,
        // left strictly alone.
        var verdicts = _verify.PlanUndeploy(manifest);
        var protectedFiles = verdicts.Where(verdict => verdict.RequiresUserConfirmation).ToList();

        var effectiveManifest = manifest;

        if (protectedFiles.Count > 0 && !deleteModifiedFiles)
        {
            var kept = protectedFiles.Select(file => file.RelativeTarget).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in protectedFiles)
            {
                logger.Warn(
                    "Install",
                    $"保留 “{file.RelativeTarget}”：它在安装后被修改过，已视为你手动调整的内容。");
            }

            // Only the files we can still prove are ours are handed to the applier for removal; the rest
            // are dropped from the manifest and left on disk.
            effectiveManifest = manifest with
            {
                Files = manifest.Files
                    .Where(file => !kept.Contains(file.RelativeTarget))
                    .ToList(),
            };
        }

        var settings = _settings.Deployment;
        DeployResult result = effectiveManifest.Files.Count > 0
            ? await _applier
                .UndeployAsync(effectiveManifest, adapter.Definition, settings, logger, null, cancellationToken)
                .ConfigureAwait(false)
            : new DeployResult(true, effectiveManifest, 0, 0, "none", [], null);

        if (!result.Succeeded)
        {
            return result;
        }

        _mods.RemoveManifest(gameId, modId);

        var purge = PurgeStaging(modId);
        if (!purge.Succeeded)
        {
            logger.Warn("Install", $"未能完全删除 Mod {modId} 的暂存文件：{purge.Error}");
        }

        _mods.Remove(modId);

        var warnings = result.Warnings.ToList();
        if (protectedFiles.Count > 0)
        {
            warnings.Add(
                $"{protectedFiles.Count} file(s) were changed by hand after installation and were kept: "
                + string.Join(", ", protectedFiles.Take(10).Select(file => file.RelativeTarget)));
        }

        return result with { Warnings = warnings };
    }

    /// <inheritdoc />
    public OperationResult<bool> PurgeStaging(string modId)
    {
        var mod = _mods.Get(modId);

        // Prefer the recorded staging path; fall back to the conventional location so a mod whose record
        // was lost can still be cleaned up.
        var root = mod?.StagingPath is { Length: > 0 } staged
            ? Path.GetDirectoryName(staged) ?? Path.Combine(_paths.Mods, modId)
            : Path.Combine(_paths.Mods, modId);

        var check = _pathSafety.ValidateAppPath(root);
        if (!check.Allowed)
        {
            return OperationResult<bool>.Fail($"Refusing to delete the staging directory: {check.Reason}");
        }

        // A staging directory is always one level below the managed mods folder, which is asserted here
        // so a malformed record can never point the delete at the mods folder itself.
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(check.CanonicalPath!)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.Mods)),
                StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<bool>.Fail("Refusing to delete the whole staging root.");
        }

        return FileSystemOps.RemoveDirectoryTree(check.CanonicalPath!)
            ? OperationResult<bool>.Ok(true)
            : OperationResult<bool>.Fail($"Could not remove '{check.CanonicalPath}'.");
    }

    /// <summary>Builds the mod set for a game: every mod with its manifest, plus any extra staged mods.</summary>
    /// <param name="adapter">Game adapter.</param>
    /// <param name="installPath">Install directory.</param>
    /// <param name="profile">Profile supplying order and disabled set.</param>
    /// <param name="extra">Mods not yet in the repository.</param>
    public IReadOnlyList<ModSetEntry> BuildModSet(
        IGameAdapter adapter,
        string installPath,
        Profile profile,
        IReadOnlyList<StagedMod> extra)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(extra);

        var entries = new List<ModSetEntry>();

        foreach (var mod in _mods.ForGame(adapter.GameId))
        {
            entries.Add(new ModSetEntry(mod, _mods.GetManifest(adapter.GameId, mod.Id), mod.StagingPath));
        }

        foreach (var staged in extra)
        {
            entries.RemoveAll(entry => string.Equals(entry.Metadata.Id, staged.Metadata.Id, StringComparison.Ordinal));
            entries.Add(new ModSetEntry(staged.Metadata, _mods.GetManifest(adapter.GameId, staged.Metadata.Id), staged.StagingRoot));
        }

        _ = installPath;
        return entries;
    }

    /// <summary>Returns the active profile for a game, creating a default one when none exists.</summary>
    /// <param name="gameId">Game id.</param>
    /// <param name="initialModId">Mod to enable in a newly created profile.</param>
    public Profile EnsureProfile(string gameId, string? initialModId = null)
    {
        var existing = _mods.ProfilesForGame(gameId).FirstOrDefault(profile => profile.IsActive);
        if (existing is not null)
        {
            return initialModId is null || existing.ModOrder.Contains(initialModId, StringComparer.Ordinal)
                ? existing
                : SaveProfileOrder(existing, initialModId);
        }

        var profile = new Profile
        {
            Id = $"{gameId}-default",
            Name = "Default",
            GameId = gameId,
            Description = "The profile created automatically for this game.",
            IsActive = true,
            ModOrder = initialModId is null ? [] : [initialModId],
        };

        _mods.SaveProfile(profile);
        return profile;
    }

    /// <summary>Appends a mod to a profile's order and persists it.</summary>
    /// <param name="profile">Profile to update.</param>
    /// <param name="modId">Mod to append.</param>
    private Profile SaveProfileOrder(Profile profile, string modId)
    {
        var updated = profile with { ModOrder = [.. profile.ModOrder, modId] };
        _mods.SaveProfile(updated);
        return updated;
    }

    /// <summary>
    /// Lays out the extracted staging tree in its final relative shape: strips the analyzer-chosen
    /// wrapper directory, then applies the adapter's deploy mapping to every file.
    /// </summary>
    /// <param name="adapter">Game adapter whose mapping decides the final layout.</param>
    /// <param name="stagingRoot">Absolute staging content root.</param>
    /// <param name="selectedRoot">Analyzer-chosen root, archive-relative; empty means the archive root.</param>
    /// <param name="logger">Logger.</param>
    /// <remarks>
    /// This is where the adapter hook enters the pipeline: <see cref="IGameAdapter.MapDeployPath"/>
    /// is the one game-specific degree of freedom the layout has, and it is applied here, inside the
    /// app-owned staging area, so the game folder is still only ever touched by the deploy engine.
    /// Every destination is re-validated against the staging root before a move happens.
    /// </remarks>
    private static bool NormalizeStagedTree(
        IGameAdapter adapter,
        string stagingRoot,
        string selectedRoot,
        IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        var prefix = string.IsNullOrWhiteSpace(selectedRoot)
            ? stagingRoot
            : Path.Combine(stagingRoot, selectedRoot.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(FileSystemOps.ToExtendedPath(prefix)))
        {
            return false;
        }

        var moves = new List<(string From, string To)>();

        foreach (var file in FileSystemOps.EnumerateFilesSafe(prefix))
        {
            var relative = Path.GetRelativePath(prefix, file).Replace('\\', '/');

            string mapped;
            try
            {
                mapped = adapter.MapDeployPath(new ArchiveEntry(relative, IsDirectory: false, Size: 0)) ?? relative;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                logger.Warn("Install", $"适配器映射 “{relative}” 失败，将按原路径部署。", ex.Message);
                mapped = relative;
            }

            if (string.Equals(prefix, stagingRoot, StringComparison.OrdinalIgnoreCase)
                && string.Equals(mapped, relative, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ArchiveReader.TryResolveSafeDestination(stagingRoot, mapped, out var destination, out var reason))
            {
                logger.Error("Install", $"拒绝把暂存文件 “{relative}” 放到 “{mapped}”：{reason}");
                return false;
            }

            moves.Add((file, destination));
        }

        foreach (var (from, to) in moves)
        {
            var directory = Path.GetDirectoryName(to);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(FileSystemOps.ToExtendedPath(directory));
            }

            File.Move(FileSystemOps.ToExtendedPath(from), FileSystemOps.ToExtendedPath(to), overwrite: true);
        }

        // Remove the now-empty wrapper tree, but never anything at or above the staging root. The
        // removal only happens once the prefix contains no files at all — if anything was left behind,
        // it stays and is reported rather than silently deleted.
        if (!string.Equals(prefix, stagingRoot, StringComparison.OrdinalIgnoreCase))
        {
            var leftoverFiles = FileSystemOps.EnumerateFilesSafe(prefix).ToList();
            if (leftoverFiles.Count == 0)
            {
                FileSystemOps.RemoveDirectoryTree(prefix);
            }
            else
            {
                logger.Warn(
                    "Install",
                    $"外层目录 “{selectedRoot}” 在整理后仍包含 {leftoverFiles.Count} 个文件，已保留。");
            }
        }

        return true;
    }

    /// <summary>Selects the archive entries that sit under the chosen root and are deployable.</summary>
    /// <param name="index">Archive index.</param>
    /// <param name="root">Chosen root, forward slash, empty for the archive root.</param>
    /// <remarks>
    /// Filtering here rather than after extraction is what keeps the staging area meaningful: readmes,
    /// screenshots and FOMOD metadata that live outside the mod root never enter it, so the deploy set
    /// and the staging set are the same thing.
    /// </remarks>
    private static List<string> SelectEntriesUnderRoot(ArchiveIndex index, string root)
    {
        var selected = new List<string>();

        foreach (var entry in index.Entries)
        {
            var path = entry.Path.Replace('\\', '/');

            if (root.Length > 0)
            {
                if (!path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                path = path.Length > root.Length + 1 ? path[(root.Length + 1)..] : string.Empty;
            }

            if (path.Length == 0)
            {
                continue;
            }

            // Mirror the planner's exclusions so the preview's counts match what will deploy.
            var excluded = path
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment.StartsWith('.') || segment.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase));

            if (excluded)
            {
                continue;
            }

            selected.Add(entry.Path);
        }

        return selected;
    }

    /// <summary>Maps the platform-neutral request to the coarse source enum used by the UI.</summary>
    private static ModSource MapSource(InstallRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Platform))
        {
            return request.Platform.ToLowerInvariant() switch
            {
                PackagePlatforms.NexusMods => ModSource.NexusMods,
                PackagePlatforms.Thunderstore => ModSource.Thunderstore,
                PackagePlatforms.GitHub => ModSource.GitHub,
                PackagePlatforms.CurseForge => ModSource.CurseForge,
                PackagePlatforms.Local => ModSource.LocalFile,
                _ => ModSource.Other,
            };
        }

        return request.NexusModId is not null ? ModSource.NexusMods : ModSource.LocalFile;
    }

    /// <summary>
    /// Derives a stable, unique mod id from the archive and the chosen root.
    /// </summary>
    /// <param name="request">Install request.</param>
    /// <param name="adapter">Game adapter.</param>
    /// <remarks>
    /// The id must be filename-safe because it becomes a directory name, and unique because two archives
    /// can legitimately share a name. A short hash of the archive path supplies the uniqueness without
    /// making the id unreadable.
    /// </remarks>
    private static string CreateModId(InstallRequest request, IGameAdapter adapter)
    {
        var stem = Path.GetFileNameWithoutExtension(request.ArchivePath);
        var slug = new string(stem
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? char.ToLowerInvariant(ch) : '-')
            .ToArray())
            .Trim('-');

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        if (slug.Length == 0)
        {
            slug = "mod";
        }

        if (slug.Length > 64)
        {
            slug = slug[..64].TrimEnd('-');
        }

        var hash = FileSystemOps.HashText(adapter.GameId + "|" + request.ArchivePath)[..8];
        return $"{slug}-{hash}";
    }

    /// <summary>Derives a display name from the archive name or the chosen root.</summary>
    /// <param name="archivePath">Archive path.</param>
    /// <param name="selectedRoot">Chosen root, which is often the real mod name.</param>
    private static string DeriveDisplayName(string archivePath, string selectedRoot)
    {
        var stem = Path.GetFileNameWithoutExtension(archivePath);

        // A single wrapper directory is usually the mod's real name, so it reads better than the archive
        // file name, which is often a version-stamped download name.
        if (selectedRoot.Length > 0 && !selectedRoot.Contains('/', StringComparison.Ordinal))
        {
            return selectedRoot;
        }

        return stem.Length > 0 ? stem : "Unnamed mod";
    }
}

/// <summary>
/// Carries live settings to services that need them, without every service taking a dependency on the
/// settings store.
/// </summary>
/// <remarks>
/// A mutable carrier rather than a read-only getter so the UI can hot-reload settings without rebuilding
/// the object graph. One instance is shared by every service that reads deployment options, so a change in
/// Settings takes effect on the next operation rather than at the next restart.
/// </remarks>
public sealed class SettingsCarrier
{
    /// <summary>Current settings.</summary>
    public AppSettings Settings { get; set; } = new();

    /// <summary>Convenience accessor for the deployment options.</summary>
    public DeploymentOptions Deployment => Settings.Deployment;
}
