using ModManager.Core.Safety;

namespace ModManager.Core.Services;

/// <summary>
/// Executes a deployment plan transactionally.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — Modrix <c>deploy/apply.rs</c>. The ordering below is not a style choice; it is
/// the crash-safety argument, so it is fixed and documented at each step:
/// </para>
/// <list type="number">
/// <item><b>Probe.</b> For every path the plan will touch, work out what is there now and record the
/// undo action. No mutation yet.</item>
/// <item><b>Journal.</b> Open the transaction and flush it. From this instant an interrupted run is
/// recoverable, because the journal on disk describes what was about to happen.</item>
/// <item><b>Mutate.</b> Removals first (deletions are hash-checked), then placements. Each step is
/// recorded <i>before</i> it is performed.</item>
/// <item><b>Verify.</b> Re-hash what was written. A partial write that still returned success is
/// caught here rather than discovered by the game.</item>
/// <item><b>Commit.</b> Write the commit marker — the irreversible point — then publish the manifest.
/// </item>
/// <item><b>Clear.</b> Remove the journal, then the marker.</item>
/// </list>
/// <para>
/// <b>Link strategy.</b> Placement tries the preferred method first and downgrades on failure, per
/// file. Modrix does the same silently and only records the outcome, which is the right call: a
/// cross-volume hardlink failure or a Windows machine without Developer Mode is a normal condition,
/// not an error, and the fallback (copy) always works. The method actually used is recorded in the
/// manifest so the UI can explain why a mod consumed disk space.
/// </para>
/// <para>
/// <b>No silent clobber.</b> A file is only ever removed when its hash still matches what we
/// deployed. If the user edited it, we keep it and say so. That is Modrix's third invariant and the
/// user's spec §13.
/// </para>
/// </remarks>
public sealed class DeploymentApplier : IDeploymentApplier
{
    private readonly IPathSafetyService _pathSafety;
    private readonly JournalService _journals;
    private readonly AppPaths _paths;
    private readonly IModRepository? _modRepository;
    private readonly IInstallationGate _gate;

    /// <summary>Creates the applier.</summary>
    /// <param name="pathSafety">The gate every mutation passes through.</param>
    /// <param name="journals">Transaction journal.</param>
    /// <param name="paths">Application paths, for the backup store.</param>
    /// <param name="modRepository">Optional manifest sink; when absent, manifests are written to disk only.</param>
    /// <param name="gate">Per-game deployment lock; a default in-process gate is created when omitted.</param>
    public DeploymentApplier(
        IPathSafetyService pathSafety,
        JournalService journals,
        AppPaths paths,
        IModRepository? modRepository = null,
        IInstallationGate? gate = null)
    {
        _pathSafety = pathSafety;
        _journals = journals;
        _paths = paths;
        _modRepository = modRepository;
        _gate = gate ?? new InstallationGate();
    }

    /// <inheritdoc />
    public async Task<DeployResult> ApplyAsync(
        DeploymentPlan plan,
        GameDefinition definition,
        IReadOnlyList<ModSetEntry> mods,
        DeploymentOptions settings,
        IAppLogger logger,
        IProgress<DeployProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        using var lease = await _gate.AcquireAsync(
            string.IsNullOrWhiteSpace(plan.GameRoot) ? plan.DeploymentRoot : plan.GameRoot,
            "apply",
            cancellationToken).ConfigureAwait(false);

        var warnings = new List<string>();
        var journal = _journals.Open(
            plan.GameId,
            plan.ProfileId,
            plan.PlanHash,
            plan.EnabledModsInOrder);

        var methods = ResolveMethodOrder(definition, settings);
        var deployed = new List<DeployedFile>();
        var createdDirectories = new List<string>();
        var backupCount = 0;

        var writes = plan.Operations.OfType<PlaceFileOperation>().ToList();
        var deletes = plan.Operations.OfType<DeleteFileOperation>().ToList();
        var directories = plan.Operations.OfType<CreateDirectoryOperation>().ToList();
        var total = writes.Count + deletes.Count;
        var completed = 0;

        logger.Info(
            "Deploy",
            $"正在为 {definition.Name} 应用部署方案：写入 {writes.Count} 个文件、移除 {deletes.Count} 个文件。",
            $"目标目录：{plan.DeploymentRoot}");

        try
        {
            _journals.Transition(journal, JournalState.Applying);

            // --- step 1: probe + back up, before anything is mutated ---------------------------
            // Backups are taken here, while the game folder is still pristine. Taking them during the
            // mutation loop would risk backing up a file we had already half-written.
            var backups = new Dictionary<string, (string BackupPath, string OriginalHash)>(StringComparer.OrdinalIgnoreCase);
            var displacedMods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var write in writes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!FileSystemOps.ExistsNoFollow(write.TargetPath))
                {
                    _journals.RecordUndo(journal, new RemovePlacedFileUndo(write.TargetPath, string.Empty, write.ModId));
                    continue;
                }

                // A file that is already one of OURS (same mod's row for the same target, same size) is
                // not a displaced original: backing our own bytes up as "the original" would make a
                // later uninstall restore the mod's own content and leave the file behind. The
                // recorded original, if there ever was one, is carried over unchanged instead.
                var previousRow = FindRecordedRow(mods, write.RelativeTarget, write.ModId)
                    ?? FindRecordedRowFromRepository(plan.GameId, write.RelativeTarget, write.ModId);

                if (previousRow is not null && FileMatchesRowSize(write.TargetPath, previousRow))
                {
                    backups[write.TargetPath] = (previousRow.BackupPath ?? string.Empty, previousRow.OriginalSha256 ?? string.Empty);
                    displacedMods[write.TargetPath] = previousRow.DisplacedModId ?? string.Empty;
                    _journals.RecordUndo(journal, new RestoreDeletedFileUndo(write.TargetPath, write.SourcePath));
                    continue;
                }

                // The bytes underneath may belong to another mod rather than to the game. That case is
                // recorded as a mod displacement instead of a file backup: the loser's own staging copy
                // is the authoritative source for restoring it, and nothing pretends to be the game's
                // original.
                var displacedModId = FindRowOwner(mods, write.RelativeTarget, write.ModId)
                    ?? FindRowOwnerFromRepository(plan.GameId, write.RelativeTarget, write.ModId);
                if (displacedModId is not null)
                {
                    backups[write.TargetPath] = (string.Empty, string.Empty);
                    displacedMods[write.TargetPath] = displacedModId;
                    _journals.RecordUndo(journal, new RestoreDeletedFileUndo(write.TargetPath, write.SourcePath));
                    _journals.Append(journal, JournalStepKind.FileBackedUp, write.TargetPath, $"displaces mod {displacedModId}");
                    continue;
                }

                if (settings.BackupBeforeOverwrite)
                {
                    var (backupPath, originalHash) = await BackupAsync(
                        plan.GameId,
                        write.ModId,
                        write.TargetPath,
                        plan.DeploymentRoot,
                        cancellationToken).ConfigureAwait(false);

                    if (backupPath is null)
                    {
                        warnings.Add($"Could not back up '{write.RelativeTarget}'; it will not be overwritten.");
                        _journals.Append(journal, JournalStepKind.Error, write.TargetPath, "backup failed", succeeded: false);
                        continue;
                    }

                    backups[write.TargetPath] = (backupPath, originalHash);
                    _journals.RecordUndo(journal, new RestoreOverwrittenFileUndo(write.TargetPath, backupPath, originalHash));
                    _journals.Append(journal, JournalStepKind.FileBackedUp, write.TargetPath, backupPath);
                    backupCount++;
                }
                else
                {
                    // Explicitly unsafe configuration: the original is discarded. Recorded so the
                    // manifest can show that this deployment is not reversible.
                    warnings.Add($"Backups are disabled; '{write.RelativeTarget}' was overwritten without preserving the original.");
                    _journals.Append(journal, JournalStepKind.FileBackedUp, write.TargetPath, "backup disabled by settings");
                }
            }

            foreach (var delete in deletes)
            {
                // The undo depends on what the removal does: a file we displaced gets its original
                // restored, so the undo re-restores the original; a plain placement is recreated from
                // staging on rollback.
                if (delete.BackupPath is { Length: > 0 } backupPath)
                {
                    _journals.RecordUndo(journal, new RestoreOverwrittenFileUndo(
                        delete.TargetPath,
                        backupPath,
                        delete.OriginalSha256 ?? string.Empty));
                }
                else
                {
                    _journals.RecordUndo(journal, new RestoreDeletedFileUndo(
                        delete.TargetPath,
                        FindStagedSource(delete, mods) ?? string.Empty));
                }
            }

            // --- step 2: mutate ---------------------------------------------------------------
            foreach (var delete in deletes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The game id — not the mod id — keys the approved-root lookup in the safety gate:
                // a removal must be validated against the game whose folder the file lives in.
                var outcome = RemoveDeployedFile(delete, plan.GameId, logger, warnings);
                completed++;
                _journals.Append(
                    journal,
                    JournalStepKind.FileDeleted,
                    delete.TargetPath,
                    outcome,
                    succeeded: !outcome.Contains("kept", StringComparison.OrdinalIgnoreCase));

                progress?.Report(new DeployProgress(total, completed, delete.RelativeTarget, null, backupCount));
            }

            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_pathSafety.TryCreateDirectory(plan.GameId, directory.TargetPath, out var reason))
                {
                    createdDirectories.Add(directory.TargetPath);
                    _journals.RecordUndo(journal, new RemoveDirectoryUndo(directory.TargetPath));
                    _journals.Append(journal, JournalStepKind.DirectoryCreated, directory.TargetPath, null);
                }
                else
                {
                    warnings.Add($"Could not create '{directory.TargetPath}': {reason}");
                    _journals.Append(journal, JournalStepKind.Error, directory.TargetPath, reason, succeeded: false);
                }
            }

            foreach (var write in writes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var check = _pathSafety.ValidateWrite(plan.GameId, write.TargetPath);
                if (!check.Allowed)
                {
                    warnings.Add($"Refused to write '{write.RelativeTarget}': {check.Reason}");
                    _journals.Append(journal, JournalStepKind.Error, write.TargetPath, check.Reason, succeeded: false);
                    completed++;
                    continue;
                }

                var (success, method, hash, size) = await PlaceFileAsync(
                    write,
                    methods,
                    cancellationToken).ConfigureAwait(false);

                if (!success)
                {
                    throw new IOException($"Failed to place '{write.RelativeTarget}' by any permitted method.");
                }

                backups.TryGetValue(write.TargetPath, out var backup);

                // The undo action recorded during the probe assumed nothing was there (empty hash).
                // Now that we know the real hash, replace it so a rollback can prove the file is ours.
                ReplaceProbeUndo(journal, write.TargetPath, hash, write.ModId);

                var hasBackup = !string.IsNullOrEmpty(backup.BackupPath);
                var hasOriginalHash = !string.IsNullOrEmpty(backup.OriginalHash);

                deployed.Add(new DeployedFile
                {
                    RelativeTarget = write.RelativeTarget,
                    SourcePath = write.SourcePath,
                    Sha256 = hash,
                    Size = size,
                    LastWriteTimeUtc = TryGetLastWrite(write.TargetPath),
                    Method = method,
                    ModId = write.ModId,
                    ReplacedExistingFile = hasBackup || displacedMods.ContainsKey(write.TargetPath),
                    BackupPath = hasBackup ? backup.BackupPath : null,
                    OriginalSha256 = hasOriginalHash ? backup.OriginalHash : null,
                    DisplacedModId = displacedMods.TryGetValue(write.TargetPath, out var displaced) && displaced.Length > 0
                        ? displaced
                        : null,
                });

                _journals.Append(journal, JournalStepKind.FilePlaced, write.TargetPath, $"{method}");

                completed++;
                progress?.Report(new DeployProgress(total, completed, write.RelativeTarget, method, backupCount));
            }

            // --- step 3: verify ---------------------------------------------------------------
            if (settings.VerifyAfterDeploy)
            {
                // Verification hashes the real file at its absolute path, which is why the plan's
                // operations (not just the manifest rows) are consulted here.
                var absolutePaths = writes.ToDictionary(
                    w => w.RelativeTarget,
                    w => w.TargetPath,
                    StringComparer.OrdinalIgnoreCase);

                var mismatches = VerifyDeployedAt(deployed, absolutePaths);
                if (mismatches.Count > 0)
                {
                    foreach (var mismatch in mismatches)
                    {
                        _journals.Append(journal, JournalStepKind.VerifyFailed, mismatch, "hash mismatch after write", succeeded: false);
                    }

                    throw new IOException(
                        $"{mismatches.Count} file(s) did not verify after deployment: {string.Join(", ", mismatches.Take(5))}");
                }

                _journals.Append(journal, JournalStepKind.VerifyPassed, null, $"{deployed.Count} file(s) verified");
            }

            // --- step 4: commit ---------------------------------------------------------------
            var manifest = BuildManifest(plan, definition, methods, deployed, createdDirectories, warnings);

            _journals.Transition(journal, JournalState.Applied);
            _journals.WriteCommit(journal, manifest);

            // Only per-mod manifests are published. The combined manifest is the journal's commit
            // record and the caller's summary, but writing it into the manifest store would be a
            // regression: it is keyed by whichever mod happens to own the first deployed path, so it
            // would overwrite that mod's complete record with just this plan's rows.
            ReconcileModManifests(plan, definition, methods, deployed, createdDirectories, warnings, mods);

            _journals.Transition(journal, JournalState.Committed);
            _journals.Clear(journal.TransactionId);

            logger.Info(
                "Deploy",
                $"已为 {definition.Name} 部署 {deployed.Count} 个文件；备份原文件 {backupCount} 个。",
                $"事务 {journal.TransactionId}");

            return new DeployResult(true, manifest, deployed.Count, backupCount, journal.TransactionId, warnings, null);
        }
        catch (OperationCanceledException)
        {
            _journals.Transition(journal, JournalState.Failed, "cancelled");
            await TryRollbackAsync(journal, settings, logger).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.Error("Deploy", $"部署失败：{ex.Message}", $"事务 {journal.TransactionId}");
            _journals.Transition(journal, JournalState.Failed, ex.Message);

            if (settings.RollbackOnFailure)
            {
                await TryRollbackAsync(journal, settings, logger).ConfigureAwait(false);
            }

            return new DeployResult(false, null, deployed.Count, backupCount, journal.TransactionId, warnings, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<DeployResult> UndeployAsync(
        ModManifest manifest,
        GameDefinition definition,
        DeploymentOptions settings,
        IAppLogger logger,
        IProgress<DeployProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        using var lease = await _gate.AcquireAsync(
            string.IsNullOrWhiteSpace(manifest.GameRoot) ? manifest.DeploymentRoot : manifest.GameRoot,
            "undeploy",
            cancellationToken).ConfigureAwait(false);

        var warnings = new List<string>();
        var journal = _journals.Open(manifest.GameId, manifest.ProfileId, "undeploy:" + manifest.ModId, [manifest.ModId]);
        var total = manifest.Files.Count;
        var completed = 0;
        var removed = 0;

        logger.Info("Deploy", $"正在卸载 “{manifest.Name}”（{total} 个文件）。");

        try
        {
            _journals.Transition(journal, JournalState.Applying);

            // Reverse order, so files placed last are removed first. That keeps a directory from
            // being emptied before a file inside it is removed.
            for (var index = manifest.Files.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var file = manifest.Files[index];
                var absolute = DeploymentPlanner.SafeCombine(manifest.DeploymentRoot, file.RelativeTarget);

                var check = _pathSafety.ValidateDelete(manifest.GameId, absolute, isDirectory: false);
                if (!check.Allowed)
                {
                    warnings.Add($"Refused to remove '{file.RelativeTarget}': {check.Reason}");
                    completed++;
                    continue;
                }

                var canonical = check.CanonicalPath!;
                var keptModified = false;

                // If we recorded a backup but it is gone, deleting the deployed file would leave the
                // game with a hole and no way to put the original back. Keep the file, report it, and
                // let the user repair the backup instead.
                if (file.BackupPath is { Length: > 0 } missingBackup
                    && !File.Exists(FileSystemOps.ToExtendedPath(missingBackup)))
                {
                    warnings.Add(
                        $"Kept '{file.RelativeTarget}': its backup is missing, so the deployed file was not removed.");
                    _journals.Append(journal, JournalStepKind.FileDeleted, canonical, "kept: backup missing", succeeded: false);
                    completed++;
                    progress?.Report(new DeployProgress(total, completed, file.RelativeTarget, null, 0));
                    continue;
                }

                if (FileSystemOps.ExistsNoFollow(canonical))
                {
                    // "No silent clobber": a file that changed since we deployed it belongs to the
                    // user now. We keep it, report it, and let the UI ask.
                    if (HashMatches(canonical, file.Sha256))
                    {
                        _journals.RecordUndo(journal, new RestoreDeletedFileUndo(canonical, file.SourcePath ?? string.Empty));
                        FileSystemOps.RemoveIfPresent(canonical);
                        _journals.Append(journal, JournalStepKind.FileDeleted, canonical, null);
                        removed++;
                    }
                    else
                    {
                        keptModified = true;
                        warnings.Add(
                            $"Kept '{file.RelativeTarget}': its contents changed since it was installed, so it was not deleted or overwritten.");
                        _journals.Append(journal, JournalStepKind.FileDeleted, canonical, "kept: modified after deploy", succeeded: false);
                    }
                }

                // A user-edited file must not be replaced by a backup or by another mod's staged copy.
                // Restoring underneath it would silently destroy the edit we just promised to keep.
                if (keptModified)
                {
                    completed++;
                    progress?.Report(new DeployProgress(total, completed, file.RelativeTarget, null, 0));
                    continue;
                }

                // Put the original back if we displaced one.
                if (file.BackupPath is not null && File.Exists(FileSystemOps.ToExtendedPath(file.BackupPath)))
                {
                    await FileSystemOps.CopyAsync(file.BackupPath, canonical, cancellationToken).ConfigureAwait(false);
                    _journals.Append(journal, JournalStepKind.FileRestored, canonical, file.BackupPath);
                }
                else if (file.DisplacedModId is { Length: > 0 } displacedModId)
                {
                    // The bytes here belonged to another mod. Reproduce that mod's file from ITS staged
                    // source and verify it against ITS recorded hash — the exact "uninstall B restores A"
                    // requirement, with no byte copy that could later be mistaken for the game's original.
                    var restored = await TryRestoreDisplacedModFileAsync(
                        manifest.GameId,
                        displacedModId,
                        file.RelativeTarget,
                        canonical,
                        logger,
                        cancellationToken).ConfigureAwait(false);

                    if (restored)
                    {
                        _journals.Append(journal, JournalStepKind.FileRestored, canonical, $"restored mod {displacedModId}");
                    }
                    else
                    {
                        FileSystemOps.RemoveEmptyAncestors(canonical, manifest.DeploymentRoot);
                    }
                }
                else if (file.BackupPath is null)
                {
                    FileSystemOps.RemoveEmptyAncestors(canonical, manifest.DeploymentRoot);
                }

                completed++;
                progress?.Report(new DeployProgress(total, completed, file.RelativeTarget, null, 0));
            }

            _journals.Transition(journal, JournalState.Committed);
            _journals.Clear(journal.TransactionId);

            logger.Info("Deploy", $"已卸载 “{manifest.Name}”，移除 {removed} 个文件。");
            return new DeployResult(true, manifest, removed, 0, journal.TransactionId, warnings, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error("Deploy", $"卸载 “{manifest.Name}” 失败：{ex.Message}");
            _journals.Transition(journal, JournalState.Failed, ex.Message);
            await TryRollbackAsync(journal, settings, logger).ConfigureAwait(false);
            return new DeployResult(false, null, removed, 0, journal.TransactionId, warnings, ex.Message);
        }
    }

    /// <summary>
    /// Places one file, trying each permitted method in turn.
    /// </summary>
    /// <param name="write">Placement to perform.</param>
    /// <param name="methods">Permitted methods in preference order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success, the method actually used, the deployed hash and the size.</returns>
    private static async Task<(bool Success, DeploymentMethod Method, string Hash, long Size)> PlaceFileAsync(
        PlaceFileOperation write,
        IReadOnlyList<DeploymentMethod> methods,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(write.TargetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(FileSystemOps.ToExtendedPath(directory));
        }

        // Hash the staged source, not the placed file: the source is authoritative and reading it once
        // here avoids a second full read after placement.
        var hash = await FileSystemOps.HashFileAsync(write.SourcePath, cancellationToken).ConfigureAwait(false);
        long size;
        try
        {
            size = new FileInfo(FileSystemOps.ToExtendedPath(write.SourcePath)).Length;
        }
        catch (IOException)
        {
            size = 0;
        }

        foreach (var method in methods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var placed = method switch
            {
                // A sibling temp file keeps every attempt on the same volume, so the final rename is
                // atomic and the destination is never observed half-written.
                DeploymentMethod.Hardlink => TryVia(
                    () => FileSystemOps.TryCreateHardLink(write.SourcePath, FileSystemOps.SiblingTempPath(write.TargetPath)),
                    write.TargetPath),

                DeploymentMethod.Symlink => TryVia(
                    () => FileSystemOps.TryCreateSymlink(write.SourcePath, FileSystemOps.SiblingTempPath(write.TargetPath)),
                    write.TargetPath),

                DeploymentMethod.Junction => false,

                DeploymentMethod.Copy => await TryCopyViaAsync(write, cancellationToken).ConfigureAwait(false),

                DeploymentMethod.Virtual => false,

                _ => false,
            };

            if (placed)
            {
                return (true, method, hash, size);
            }
        }

        return (false, DeploymentMethod.Copy, hash, size);
    }

    /// <summary>Runs a link attempt then commits the temp file, if the link succeeded.</summary>
    /// <param name="attempt">Link attempt writing to the sibling temp path.</param>
    /// <param name="targetPath">Final destination.</param>
    private static bool TryVia(Func<bool> attempt, string targetPath)
    {
        if (!attempt())
        {
            return false;
        }

        FileSystemOps.CommitRename(FileSystemOps.SiblingTempPath(targetPath), targetPath);
        return true;
    }

    /// <summary>Copies via a sibling temp file, then commits it.</summary>
    /// <param name="write">Placement to perform.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task<bool> TryCopyViaAsync(PlaceFileOperation write, CancellationToken cancellationToken)
    {
        var temp = FileSystemOps.SiblingTempPath(write.TargetPath);
        try
        {
            await FileSystemOps.CopyAsync(write.SourcePath, temp, cancellationToken).ConfigureAwait(false);
            FileSystemOps.CommitRename(temp, write.TargetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileSystemOps.RemoveIfPresent(temp);
            return false;
        }
    }

    /// <summary>Removes one file a previous manifest claimed, honouring the hash check.</summary>
    /// <param name="delete">Deletion to perform.</param>
    /// <param name="gameId">Game whose folder the deletion targets.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="warnings">Warnings sink.</param>
    /// <remarks>
    /// When the deployment displaced an original game file, the removal restores that original from
    /// its backup instead of leaving a hole — a disabled mod must stop affecting the game, which
    /// includes giving back the file it was overriding.
    /// </remarks>
    private string RemoveDeployedFile(DeleteFileOperation delete, string gameId, IAppLogger logger, List<string> warnings)
    {
        var exists = FileSystemOps.ExistsNoFollow(delete.TargetPath);

        if (exists && delete.ExpectedHash is { Length: > 0 } expected && !HashMatches(delete.TargetPath, expected))
        {
            var message = $"Kept '{delete.RelativeTarget}': it changed since deployment, so it was not removed.";
            warnings.Add(message);
            logger.Warn("Deploy", message);

            // "kept" makes the caller record this step as a non-success, which is what a forensic
            // reader of the journal should see.
            return "kept: modified";
        }

        if (exists)
        {
            if (!_pathSafety.TryDeleteFile(gameId, delete.TargetPath, out var reason))
            {
                warnings.Add($"Refused to remove '{delete.RelativeTarget}': {reason}");
                return "kept: " + reason;
            }
        }

        // Give the displaced original back, when there is one.
        if (delete.BackupPath is { Length: > 0 } backupPath
            && File.Exists(FileSystemOps.ToExtendedPath(backupPath)))
        {
            try
            {
                FileSystemOps.CopyAsync(backupPath, delete.TargetPath).GetAwaiter().GetResult();
                logger.Info("Deploy", $"已从备份还原 “{delete.RelativeTarget}” 的原文件。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not restore the original of '{delete.RelativeTarget}' from its backup: {ex.Message}");
                return "removed, restore failed";
            }

            return "removed, original restored";
        }

        if (!exists)
        {
            return "already absent";
        }

        FileSystemOps.RemoveEmptyAncestors(delete.TargetPath, Path.GetDirectoryName(delete.TargetPath) ?? delete.TargetPath);
        return "removed";
    }

    /// <summary>Takes a content-addressed backup of a file we are about to overwrite.</summary>
    /// <param name="gameId">Game id.</param>
    /// <param name="modId">Mod causing the overwrite.</param>
    /// <param name="targetPath">File being overwritten.</param>
    /// <param name="deploymentRoot">Deployment root, for the relative path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Layout is <c>Backups/&lt;gameId&gt;/&lt;modId&gt;/&lt;relative path&gt;</c> rather than Modrix's
    /// content-addressed store. Two reasons for diverging: a human can find a file by its real name
    /// when something goes wrong, and deleting one mod's backups is a single directory removal. The
    /// cost is no cross-mod deduplication, which for game files (mostly small, mostly unique) is not
    /// worth the loss of legibility.
    /// </remarks>
    private async Task<(string? BackupPath, string OriginalHash)> BackupAsync(
        string gameId,
        string modId,
        string targetPath,
        string deploymentRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            var relative = Path.GetRelativePath(deploymentRoot, targetPath);
            var backupPath = Path.Combine(_paths.Backups, gameId, AppPaths.Sanitize(modId), relative);

            var directory = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(FileSystemOps.ToExtendedPath(directory));
            }

            // Copy rather than hardlink: the original must keep its own bytes if the mod file is later
            // edited in place, otherwise editing the deployed file would silently change the backup.
            await FileSystemOps.CopyAsync(targetPath, backupPath, cancellationToken).ConfigureAwait(false);
            var hash = await FileSystemOps.HashFileAsync(backupPath, cancellationToken).ConfigureAwait(false);
            return (backupPath, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, string.Empty);
        }
    }

    /// <summary>Re-hashes files at their real paths and reports mismatches.</summary>
    /// <param name="deployed">Deployed files.</param>
    /// <param name="absolutePaths">Absolute path per relative target.</param>
    private static List<string> VerifyDeployedAt(
        IReadOnlyList<DeployedFile> deployed,
        IReadOnlyDictionary<string, string> absolutePaths)
    {
        var mismatches = new List<string>();

        foreach (var file in deployed)
        {
            if (!absolutePaths.TryGetValue(file.RelativeTarget, out var absolute))
            {
                continue;
            }

            if (!FileSystemOps.ExistsNoFollow(absolute))
            {
                mismatches.Add($"{file.RelativeTarget} (missing)");
                continue;
            }

            if (!HashMatches(absolute, file.Sha256))
            {
                mismatches.Add($"{file.RelativeTarget} (content differs)");
            }
        }

        return mismatches;
    }

    /// <summary>Replaces the placeholder undo action recorded during the probe with a hash-bearing one.</summary>
    /// <param name="journal">Journal to update.</param>
    /// <param name="targetPath">Placed path.</param>
    /// <param name="hash">Hash now on disk.</param>
    /// <param name="modId">Owning mod.</param>
    private static void ReplaceProbeUndo(DeploymentJournal journal, string targetPath, string hash, string modId)
    {
        for (var index = 0; index < journal.UndoActions.Count; index++)
        {
            if (journal.UndoActions[index] is RemovePlacedFileUndo existing
                && string.Equals(existing.Path, targetPath, StringComparison.OrdinalIgnoreCase)
                && existing.DeployedSha256.Length == 0)
            {
                journal.UndoActions[index] = existing with { DeployedSha256 = hash, ModId = modId };
                return;
            }
        }

        journal.UndoActions.Add(new RemovePlacedFileUndo(targetPath, hash, modId));
    }

    /// <summary>Builds the manifest a successful apply publishes.</summary>
    /// <param name="plan">Plan that was applied.</param>
    /// <param name="definition">Game definition.</param>
    /// <param name="methods">Method preference used.</param>
    /// <param name="deployed">Files deployed.</param>
    /// <param name="createdDirectories">Directories created.</param>
    /// <param name="warnings">Warnings collected.</param>
    private static ModManifest BuildManifest(
        DeploymentPlan plan,
        GameDefinition definition,
        IReadOnlyList<DeploymentMethod> methods,
        IReadOnlyList<DeployedFile> deployed,
        IReadOnlyList<string> createdDirectories,
        IReadOnlyList<string> warnings)
    {
        var used = deployed
            .GroupBy(f => f.Method)
            .Select(group => $"{group.Key} x{group.Count()}")
            .ToList();

        // Conflicts are recorded so a later investigation can see what the state was at deploy time
        // rather than guessing from the current mods.
        var conflicts = plan.Conflicts
            .Select(c => $"{c.RelativeTarget}：{c.WinnerModId} 覆盖 {string.Join("、", c.OverriddenModIds)}")
            .ToList();

        // A plan can deploy for several mods at once, so the manifest is keyed by the mod that owns
        // the first deployed path. A plan with nothing to place is a pure removal pass and gets no
        // manifest of its own.
        var ownerModId = deployed.Count > 0 ? deployed[0].ModId : plan.ProfileId;

        return new ModManifest
        {
            ModId = ownerModId,
            Name = definition.Name,
            GameId = plan.GameId,
            ProfileId = plan.ProfileId,
            DeploymentRoot = plan.DeploymentRoot,
            GameRoot = plan.GameRoot,
            Files = deployed,
            Conflicts = conflicts,
            DeploymentMethod = used.Count > 0 ? string.Join(", ", used) : "none",
            MethodPreference = methods,
            PlanHash = plan.PlanHash,
            CreatedDirectories = createdDirectories,
        };
    }

    /// <summary>
    /// Rewrites each participating mod's manifest so it describes exactly the files that mod has on
    /// disk after this apply, and removes the manifest of a mod that no longer has any.
    /// </summary>
    /// <param name="plan">Plan that was applied.</param>
    /// <param name="definition">Game definition.</param>
    /// <param name="methods">Method preference used.</param>
    /// <param name="deployed">Files deployed by this apply.</param>
    /// <param name="createdDirectories">Directories created by this apply.</param>
    /// <param name="warnings">Warnings collected.</param>
    /// <param name="mods">Mods that participated in the plan.</param>
    /// <remarks>
    /// Without this step a disable would leave the mod's old manifest claiming files that were just
    /// removed, and a later enable would then see "already deployed" and skip placing them. The
    /// manifest is the only source of truth for undeploy, so it must always match the disk.
    /// </remarks>
    private void ReconcileModManifests(
        DeploymentPlan plan,
        GameDefinition definition,
        IReadOnlyList<DeploymentMethod> methods,
        IReadOnlyList<DeployedFile> deployed,
        IReadOnlyList<string> createdDirectories,
        IReadOnlyList<string> warnings,
        IReadOnlyList<ModSetEntry> mods)
    {
        foreach (var entry in mods)
        {
            var modId = entry.Metadata.Id;
            var own = deployed
                .Where(file => string.Equals(file.ModId, modId, StringComparison.Ordinal))
                .ToList();

            var existing = _modRepository?.GetManifest(plan.GameId, modId);

            // The manifest is "this plan's placements" PLUS every row already on record whose file is
            // still on disk. Existence is the truth here: a row whose file was really removed is
            // dropped, while a row whose file still exists is kept — even when another mod currently
            // supplies the bytes — so the loser's claim survives the winner's uninstall.
            var rows = new List<DeployedFile>(own);
            if (existing is not null)
            {
                foreach (var file in existing.Files)
                {
                    if (rows.Any(row => string.Equals(row.RelativeTarget, file.RelativeTarget, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var absolute = DeploymentPlanner.SafeCombine(plan.DeploymentRoot, file.RelativeTarget);
                    if (FileSystemOps.ExistsNoFollow(absolute))
                    {
                        rows.Add(file);
                    }
                }
            }

            if (rows.Count > 0)
            {
                var manifest = BuildManifest(plan, definition, methods, rows, createdDirectories, warnings);
                SaveManifest(manifest with { Name = entry.Metadata.Name, Version = entry.Metadata.Version });
                continue;
            }

            // Nothing left on record for this mod. Its manifest is removed when this plan removed its
            // files and the mod is no longer enabled (a disable); a superseded-but-enabled mod keeps
            // its claims, and a mod this plan never touched keeps its manifest untouched.
            var stillEnabled = plan.EnabledModsInOrder.Contains(modId, StringComparer.Ordinal);
            var removedByThisPlan = plan.Files.Any(file =>
                string.Equals(file.ModId, modId, StringComparison.Ordinal) && file.SourcePath is null);

            if (removedByThisPlan && !stillEnabled)
            {
                RemoveManifestFor(plan.GameId, modId);
            }
        }
    }

    /// <summary>Removes a mod's manifest record and file, when one exists.</summary>
    /// <param name="gameId">Game.</param>
    /// <param name="modId">Mod.</param>
    private void RemoveManifestFor(string gameId, string modId)
    {
        _modRepository?.RemoveManifest(gameId, modId);

        var path = _paths.ManifestFile(gameId, modId);
        try
        {
            FileSystemOps.RemoveIfPresent(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A manifest file we cannot delete is a consistency problem, not a crash: the in-memory
            // record is already gone, and the next successful apply will overwrite the file.
        }
    }

    /// <summary>
    /// Manifests are produced per mod by the install service; the applier also writes a combined one
    /// keyed by profile so a verify pass can find everything at once.
    /// </summary>
    /// <param name="manifest">Manifest to save.</param>
    private void SaveManifest(ModManifest manifest)
    {
        _modRepository?.SaveManifest(manifest);

        var path = _paths.ManifestFile(manifest.GameId, manifest.ModId);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            Serialization.ManifestJson.Options);

        FileSystemOps.WriteAtomicAsync(path, payload).GetAwaiter().GetResult();
    }

    /// <summary>Finds the row one mod recorded for a target path.</summary>
    /// <param name="mods">Mods with manifests.</param>
    /// <param name="relativeTarget">Target path relative to the deployment root.</param>
    /// <param name="modId">Mod whose own row is wanted.</param>
    /// <remarks>
    /// Deliberately scoped to the mod that is placing the file: another mod's row describes <i>its</i>
    /// displaced original, and inheriting it would make an uninstall restore the wrong bytes.
    /// </remarks>
    private static DeployedFile? FindRecordedRow(IReadOnlyList<ModSetEntry> mods, string relativeTarget, string modId)
    {
        foreach (var mod in mods)
        {
            if (!string.Equals(mod.Metadata.Id, modId, StringComparison.Ordinal)
                || mod.Manifest is not { } manifest)
            {
                continue;
            }

            foreach (var file in manifest.Files)
            {
                if (string.Equals(file.RelativeTarget, relativeTarget, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }

        return null;
    }

    /// <summary>Finds one mod's recorded row from the repository, when the plan's mod set lacks it.</summary>
    /// <param name="gameId">Game.</param>
    /// <param name="relativeTarget">Target path.</param>
    /// <param name="modId">Mod whose row is wanted.</param>
    /// <remarks>
    /// The plan's mod set is not guaranteed to carry every manifest (a reinstall passes the mod being
    /// reinstalled without one), and the backup decision must not depend on that detail — otherwise the
    /// mod's own file would be re-captured as "the original".
    /// </remarks>
    private DeployedFile? FindRecordedRowFromRepository(string gameId, string relativeTarget, string modId) =>
        _modRepository?
            .GetManifest(gameId, modId)?
            .Files
            .FirstOrDefault(file => string.Equals(file.RelativeTarget, relativeTarget, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds another mod's claim on a target from the repository.</summary>
    /// <param name="gameId">Game.</param>
    /// <param name="relativeTarget">Target path.</param>
    /// <param name="excludingModId">Mod doing the placing.</param>
    private string? FindRowOwnerFromRepository(string gameId, string relativeTarget, string excludingModId)
    {
        if (_modRepository is null)
        {
            return null;
        }

        foreach (var manifest in _modRepository.ManifestsForGame(gameId))
        {
            if (string.Equals(manifest.ModId, excludingModId, StringComparison.Ordinal))
            {
                continue;
            }

            if (manifest.Files.Any(file => string.Equals(file.RelativeTarget, relativeTarget, StringComparison.OrdinalIgnoreCase)))
            {
                return manifest.ModId;
            }
        }

        return null;
    }

    /// <summary>Finds the mod, other than the one placing the file, that claims a target path.</summary>
    /// <param name="mods">Mods with manifests.</param>
    /// <param name="relativeTarget">Target path relative to the deployment root.</param>
    /// <param name="excludingModId">Mod doing the placing.</param>
    private static string? FindRowOwner(IReadOnlyList<ModSetEntry> mods, string relativeTarget, string excludingModId)
    {
        // Walk from the top of the load order down. The first manifest that owns this path is the
        // effective owner: C displaced B, even though A also has a (now shadowed) record for it.
        for (var i = mods.Count - 1; i >= 0; i--)
        {
            var mod = mods[i];
            if (string.Equals(mod.Metadata.Id, excludingModId, StringComparison.Ordinal)
                || mod.Manifest is not { } manifest)
            {
                continue;
            }

            foreach (var file in manifest.Files)
            {
                if (string.Equals(file.RelativeTarget, relativeTarget, StringComparison.OrdinalIgnoreCase))
                {
                    return mod.Metadata.Id;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Reproduces a displaced mod's file at a path, from that mod's own staged source.
    /// </summary>
    /// <param name="gameId">Game.</param>
    /// <param name="displacedModId">Mod that used to own the path.</param>
    /// <param name="relativeTarget">Target path.</param>
    /// <param name="canonicalTarget">Absolute destination.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the other mod's file was restored.</returns>
    /// <remarks>
    /// Only possible while that mod is still installed: its staging area holds the exact bytes and its
    /// manifest holds the hash to verify them. When it is gone, nothing is restored — a path no mod
    /// owns must stay empty rather than inherit a stray copy.
    /// </remarks>
    private async Task<bool> TryRestoreDisplacedModFileAsync(
        string gameId,
        string displacedModId,
        string relativeTarget,
        string canonicalTarget,
        IAppLogger logger,
        CancellationToken cancellationToken)
    {
        var manifest = _modRepository?.GetManifest(gameId, displacedModId);
        var row = manifest?.Files.FirstOrDefault(file =>
            string.Equals(file.RelativeTarget, relativeTarget, StringComparison.OrdinalIgnoreCase));

        if (row?.SourcePath is not { Length: > 0 } sourcePath
            || !File.Exists(FileSystemOps.ToExtendedPath(sourcePath)))
        {
            return false;
        }

        try
        {
            await FileSystemOps.CopyAsync(sourcePath, canonicalTarget, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(FileSystemOps.HashFile(canonicalTarget), row.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.Warn("Deploy", $"已为 Mod {displacedModId} 还原 “{relativeTarget}”，但哈希不一致。");
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warn("Deploy", $"无法为 Mod {displacedModId} 还原 “{relativeTarget}”：{ex.Message}");
            return false;
        }
    }

    /// <summary>True when the file's size matches the recorded row — the cheap "still our file" test.</summary>
    /// <param name="path">Absolute path.</param>
    /// <param name="row">Recorded row.</param>
    private static bool FileMatchesRowSize(string path, DeployedFile row)
    {
        if (row.Size <= 0)
        {
            return true;
        }

        try
        {
            return new FileInfo(FileSystemOps.ToExtendedPath(path)).Length == row.Size;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Finds the staged source for a file being removed, for the undo action.</summary>
    /// <param name="delete">Deletion being performed.</param>
    /// <param name="mods">Mods available.</param>
    private static string? FindStagedSource(DeleteFileOperation delete, IReadOnlyList<ModSetEntry> mods)
    {
        foreach (var mod in mods)
        {
            if (!string.Equals(mod.Metadata.Id, delete.ModId, StringComparison.Ordinal))
            {
                continue;
            }

            var candidate = DeploymentPlanner.SafeCombine(mod.StagingRoot, delete.RelativeTarget);
            if (File.Exists(FileSystemOps.ToExtendedPath(candidate)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Rolls back a failed transaction, logging rather than throwing.</summary>
    /// <param name="journal">Journal to roll back.</param>
    /// <param name="settings">Deployment options.</param>
    /// <param name="logger">Logger.</param>
    private async Task TryRollbackAsync(DeploymentJournal journal, DeploymentOptions settings, IAppLogger logger)
    {
        if (!settings.RollbackOnFailure)
        {
            return;
        }

        var rollback = _journals.RollbackDetailed(journal, logger);
        if (rollback.Failures.Count == 0 && rollback.Deferred.Count == 0)
        {
            logger.Info("Deploy", $"已回滚事务 {journal.TransactionId}。");
            _journals.Transition(journal, JournalState.RolledBack);
            _journals.Clear(journal.TransactionId);
        }
        else
        {
            // Leaving the journal in place is deliberate: it is the only record of what is half-done,
            // and startup recovery will try again or ask the user to decide.
            logger.Error(
                "Deploy",
                $"事务 {journal.TransactionId} 的回滚未完成，下次启动时会再次尝试恢复或要求人工处理。",
                string.Join("; ", rollback.Failures.Concat(rollback.Deferred)));
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>Resolves the method order to try: the user's allow-list, filtered to the game's own.</summary>
    /// <param name="definition">Game definition.</param>
    /// <param name="settings">Deployment options.</param>
    /// <remarks>
    /// The intersection is deliberate, and one-directional: the game may narrow what is tried, and
    /// the user may narrow it further, but neither can widen the other. A user who distrusts
    /// hardlinks must be able to say so, and a game that cannot read symlinked archives must not have
    /// them forced on it.
    /// </remarks>
    private static IReadOnlyList<DeploymentMethod> ResolveMethodOrder(
        GameDefinition definition,
        DeploymentOptions settings)
    {
        var allowed = settings.AllowedMethods.Count > 0
            ? settings.AllowedMethods
            : definition.DeploymentPreference;

        var order = definition.DeploymentPreference
            .Where(method => allowed.Contains(method))
            .ToList();

        // Always keep Copy as the last resort: without it a deployment can simply fail, and a plain
        // copy is valid for every game that can be modded at all.
        if (!order.Contains(DeploymentMethod.Copy))
        {
            order.Add(DeploymentMethod.Copy);
        }

        return order;
    }

    /// <summary>True when a file's current hash matches the expected hash.</summary>
    /// <param name="path">File to hash.</param>
    /// <param name="expected">Expected lowercase hex hash.</param>
    private static bool HashMatches(string path, string expected)
    {
        if (string.IsNullOrEmpty(expected))
        {
            // No recorded hash means we cannot prove ownership, so we must assume it is not ours.
            return false;
        }

        try
        {
            return string.Equals(FileSystemOps.HashFile(path), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Reads a file's last write time, tolerating failure.</summary>
    /// <param name="path">File to stat.</param>
    private static DateTimeOffset? TryGetLastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(FileSystemOps.ToExtendedPath(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
