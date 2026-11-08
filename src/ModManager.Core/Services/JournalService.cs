using System.Text.Json;
using System.Text.Json.Serialization;
using ModManager.Core.Safety;

namespace ModManager.Core.Services;

/// <summary>
/// Durable transaction journal with startup recovery.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — Modrix <c>deploy/journal.rs</c> plus <c>deploy/apply.rs</c>. Modrix keeps two
/// control files: a <c>journal</c> recording the pre-state of every target it is about to touch, and
/// a <c>commit</c> marker holding the new manifest rows. The direction of recovery is then decided
/// purely by which files exist — there is no state machine to get out of sync:
/// </para>
/// <list type="bullet">
/// <item>journal present, commit absent → the irreversible point was <b>not</b> reached → roll back
/// the disk and leave the manifest alone.</item>
/// <item>journal present, commit present → the irreversible point <b>was</b> reached → roll forward,
/// i.e. finish the bookkeeping only.</item>
/// </list>
/// <para>
/// <b>A deliberate improvement over Modrix.</b> Modrix clears the two files in the order
/// <i>commit first, then journal</i>. A crash between those two deletions therefore leaves
/// "journal present, commit absent" — which recovery reads as "roll back" — even though the manifest
/// has already been committed. That is a genuine inconsistency window (the DB says deployed, the disk
/// says reverted). Rather than copy it, this implementation:
/// </para>
/// <list type="number">
/// <item>deletes the journal first, so a crash mid-clear leaves only a commit marker, and</item>
/// <item>additionally <b>rolls forward on a lone commit marker</b>, so the commit is honoured
/// whichever file survives.</item>
/// </list>
/// <para>
/// The net effect is that once the irreversible point is passed, recovery can only ever move
/// forward. That is the property worth having.
/// </para>
/// <para>
/// <b>Flush discipline.</b> The journal is written and flushed <i>before</i> the first mutation and
/// is rewritten after every recorded step. It is not an append-only file: like Modrix's, it is a
/// whole-document image rewritten atomically, which keeps recovery simple and bounded by the entry
/// count. Atomicity comes from write-to-sibling-temp-then-rename, so a half-written journal is never
/// observable — a crash leaves either the old or the new image, never a corrupt one.
/// </para>
/// </remarks>
public sealed class JournalService : IJournalService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppPaths _paths;
    private readonly IAppLogger? _logger;
    /// <summary>
    /// Serialises journal mutations and flushes. A plain <see cref="object"/> because
    /// <c>System.Threading.Lock</c> requires .NET 9 and this project targets <c>net8.0</c>.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>Creates the journal service.</summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="logger">Optional logger.</param>
    public JournalService(AppPaths paths, IAppLogger? logger = null)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <inheritdoc />
    public DeploymentJournal Open(string gameId, string profileId, string planHash, IReadOnlyList<string> modIds)
    {
        var transactionId = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..32];
        var journal = new DeploymentJournal
        {
            TransactionId = transactionId,
            GameId = gameId,
            ProfileId = profileId,
            PlanHash = planHash,
            State = JournalState.Open,
        };

        journal.ModIds.AddRange(modIds);
        Append(journal, JournalStepKind.Begin, null, $"Deploy for {gameId}/{profileId}");
        return journal;
    }

    /// <inheritdoc />
    public void Append(DeploymentJournal journal, JournalStepKind kind, string? path, string? detail, bool succeeded = true)
    {
        ArgumentNullException.ThrowIfNull(journal);

        lock (_gate)
        {
            journal.Entries.Add(new JournalEntry(
                journal.Entries.Count + 1,
                DateTimeOffset.UtcNow,
                kind,
                path,
                detail,
                succeeded));

            Flush(journal);
        }
    }

    /// <inheritdoc />
    public void RecordUndo(DeploymentJournal journal, UndoAction action)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(action);

        lock (_gate)
        {
            journal.UndoActions.Add(action);
        }
    }

    /// <inheritdoc />
    public void Transition(DeploymentJournal journal, JournalState state, string? error = null)
    {
        ArgumentNullException.ThrowIfNull(journal);

        lock (_gate)
        {
            journal.State = state;
            journal.Error = error;
            if (state is JournalState.Committed or JournalState.RolledBack or JournalState.Failed)
            {
                journal.FinishedAt = DateTimeOffset.UtcNow;
            }

            Flush(journal);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DeploymentJournal> FindInterrupted()
    {
        var interrupted = new List<DeploymentJournal>();

        foreach (var journal in All())
        {
            if (journal.NeedsRecovery)
            {
                interrupted.Add(journal);
            }
        }

        return interrupted;
    }

    /// <inheritdoc />
    public IReadOnlyList<DeploymentJournal> All()
    {
        var journals = new List<DeploymentJournal>();

        if (!Directory.Exists(_paths.Journals))
        {
            return journals;
        }

        foreach (var file in Directory.EnumerateFiles(_paths.Journals, "*.json"))
        {
            var journal = TryRead(file);
            if (journal is not null)
            {
                journals.Add(journal);
            }
        }

        journals.Sort((left, right) => right.StartedAt.CompareTo(left.StartedAt));
        return journals;
    }

    /// <summary>
    /// Reads every journal and reports whether it is complete, interrupted, or already committed.
    /// This never mutates the journal or the game folder; it is the inspection step before recovery.
    /// </summary>
    public IReadOnlyList<JournalInspection> Inspect()
    {
        var markers = LoadCommitMarkers()
            .Select(marker => marker.TransactionId)
            .ToHashSet(StringComparer.Ordinal);

        return All()
            .Select(journal => new JournalInspection(
                journal.TransactionId,
                journal.GameId,
                journal.State,
                markers.Contains(journal.TransactionId),
                journal.NeedsRecovery,
                journal.UndoActions.Count,
                journal.Error))
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately not <c>async</c>. Every step is a short synchronous file operation (read a marker,
    /// copy a backup back, delete a journal), so there is nothing to await — an <c>async</c> keyword here
    /// would only wrap the work in a state machine and invite the reader to assume it yields. The
    /// <see cref="Task{TResult}"/> return type is kept because callers await it from the startup path.
    /// </remarks>
    public Task<JournalRecoveryReport> RecoverAsync(
        IAppLogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var recovered = new List<string>();
        var failed = new List<string>();
        var deferred = new List<string>();
        var messages = new List<string>();
        var commitMarkers = LoadCommitMarkers();

        foreach (var journal in FindInterrupted())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var commit = commitMarkers.FirstOrDefault(marker =>
                string.Equals(marker.TransactionId, journal.TransactionId, StringComparison.Ordinal));

            try
            {
                if (commit is not null)
                {
                    // The irreversible point was passed, so the only correct direction is forward.
                    logger.Warn(
                        "Journal",
                        $"事务 {journal.TransactionId} 已到达提交点但未完成，正在前滚。",
                        $"Manifest 行数：{commit.Manifest.Files.Count}");

                    var manifest = BuildManifestFromCommit(journal, commit);
                    WriteRecoveredManifest(manifest);

                    Clear(journal.TransactionId);
                    recovered.Add(journal.TransactionId);
                    messages.Add(
                        $"Transaction {journal.TransactionId} was completed (its changes were already applied).");
                    continue;
                }

                logger.Warn(
                    "Journal",
                    $"事务 {journal.TransactionId} 在提交点之前被中断，正在回滚。",
                    $"{journal.UndoActions.Count} 项回滚动作");

                var rollback = RollbackDetailed(journal, logger);
                if (rollback.Failures.Count == 0 && rollback.Deferred.Count == 0)
                {
                    Transition(journal, JournalState.RolledBack);
                    Clear(journal.TransactionId);
                    recovered.Add(journal.TransactionId);
                    messages.Add(
                        $"Transaction {journal.TransactionId} was rolled back; the game folder is back as it was.");
                }
                else
                {
                    var details = rollback.Failures.Concat(rollback.Deferred).ToList();
                    if (rollback.Deferred.Count > 0)
                    {
                        deferred.Add(journal.TransactionId);
                    }

                    if (rollback.Failures.Count > 0)
                    {
                        failed.Add(journal.TransactionId);
                    }

                    Transition(journal, JournalState.Failed, string.Join("; ", details));
                    messages.Add(
                        $"Transaction {journal.TransactionId} needs attention and was left on disk: "
                        + string.Join("; ", details));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Transition(journal, JournalState.Failed, ex.Message);
                failed.Add(journal.TransactionId);
                messages.Add($"Transaction {journal.TransactionId} failed to recover: {ex.Message}");
            }
        }

        // A leftover commit marker with no journal also means the irreversible point was passed.
        foreach (var commit in commitMarkers)
        {
            if (recovered.Contains(commit.TransactionId) || failed.Contains(commit.TransactionId))
            {
                continue;
            }

            // If a journal exists for it we already handled it above; a lone marker means the journal
            // was deleted but the marker was not, so finish the bookkeeping.
            var leftovers = FindInterrupted()
                .Any(j => string.Equals(j.TransactionId, commit.TransactionId, StringComparison.Ordinal));
            if (leftovers)
            {
                continue;
            }

            logger.Warn("Journal", $"发现没有事务日志的提交标记，正在前滚：{commit.TransactionId}");
            ClearCommit(commit.TransactionId);
            recovered.Add(commit.TransactionId);
            messages.Add($"A completed transaction ({commit.TransactionId}) was finalised.");
        }

        return Task.FromResult(new JournalRecoveryReport(recovered, failed, messages)
        {
            Deferred = deferred,
        });
    }

    /// <inheritdoc />
    public int PruneCommitted(int keep)
    {
        var committed = All()
            .Where(j => j.State == JournalState.Committed)
            .OrderByDescending(j => j.FinishedAt ?? j.StartedAt)
            .ToList();

        var removed = 0;
        for (var index = Math.Max(0, keep); index < committed.Count; index++)
        {
            if (Clear(committed[index].TransactionId))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Writes the commit marker. This is the irreversible point of the whole transaction: once it is
    /// on disk, recovery may only roll forward.
    /// </summary>
    /// <param name="journal">Journal that is committing.</param>
    /// <param name="manifest">Manifest the commit will publish.</param>
    public void WriteCommit(DeploymentJournal journal, ModManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(manifest);

        var commit = new CommitMarker
        {
            TransactionId = journal.TransactionId,
            GameId = journal.GameId,
            ProfileId = journal.ProfileId,
            ModId = manifest.ModId,
            Manifest = manifest,
        };

        var payload = JsonSerializer.SerializeToUtf8Bytes(commit, SerializerOptions);
        FileSystemOps.WriteAtomicAsync(CommitPath(journal.TransactionId), payload).GetAwaiter().GetResult();
        Append(journal, JournalStepKind.ManifestCommitted, null, $"Commit marker written for mod {manifest.ModId}");
    }

    /// <summary>
    /// Replays every undo action in reverse order. Every step is idempotent, so this can itself be
    /// interrupted and re-run — which is what makes recovery reliable rather than a second chance to
    /// lose data.
    /// </summary>
    /// <param name="journal">Journal to roll back.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>The list of actions that could not be undone.</returns>
    public List<string> Rollback(DeploymentJournal journal, IAppLogger logger) =>
        RollbackDetailed(journal, logger).Failures.ToList();

    /// <summary>
    /// Replays every undo action in reverse order and separates hard failures from actions that were
    /// deliberately skipped because the file no longer belongs to this transaction.
    /// </summary>
    /// <param name="journal">Journal to roll back.</param>
    /// <param name="logger">Logger.</param>
    public JournalRollbackResult RollbackDetailed(DeploymentJournal journal, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(logger);

        var failures = new List<string>();
        var deferred = new List<string>();
        var blockedDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Transition(journal, JournalState.Applying, journal.Error);
        Append(journal, JournalStepKind.RollbackBegin, null, $"{journal.UndoActions.Count} action(s) to reverse");

        // Reverse order: the last thing done must be the first thing undone, or a restore could be
        // overwritten by the removal that logically preceded it.
        for (var index = journal.UndoActions.Count - 1; index >= 0; index--)
        {
            var action = journal.UndoActions[index];
            try
            {
                switch (action)
                {
                    case RemovePlacedFileUndo remove:
                        // If the restore of the file this placement replaced failed, deleting the
                        // placement would leave neither the mod file nor the original on disk.
                        if (blockedDeletes.Contains(remove.Path))
                        {
                            logger.Warn(
                                "Journal",
                                $"回滚时暂不删除 “{remove.Path}”：它的原文件恢复失败，保留现有文件以便人工处理。");
                            deferred.Add($"kept {remove.Path} after a failed restore");
                            break;
                        }

                        // Only remove the file if it is still byte-identical to what we placed. If the
                        // user edited it in the meantime, deleting it would destroy their work — the
                        // "no silent clobber" invariant.
                        if (FileSystemOps.ExistsNoFollow(remove.Path)
                            && !HashMatches(remove.Path, remove.DeployedSha256))
                        {
                            logger.Warn(
                                "Journal",
                                $"回滚时不删除 “{remove.Path}”：它在部署后被改动过。");
                            deferred.Add($"kept changed file {remove.Path}");
                            break;
                        }

                        FileSystemOps.RemoveIfPresent(remove.Path);
                        break;

                    case RestoreOverwrittenFileUndo restore:
                        if (!FileSystemOps.ExistsNoFollow(restore.BackupPath))
                        {
                            failures.Add($"backup missing for {restore.Path} ({restore.BackupPath})");
                            blockedDeletes.Add(restore.Path);
                            break;
                        }

                        if (!HashMatches(restore.BackupPath, restore.OriginalSha256))
                        {
                            failures.Add($"backup hash mismatch for {restore.Path} ({restore.BackupPath})");
                            blockedDeletes.Add(restore.Path);
                            break;
                        }

                        // The target may be the file we placed, the original bytes (already restored),
                        // a user edit, or an unrelated external file. Only restore when we can prove it
                        // is still the file this transaction wrote; otherwise keep it and defer.
                        if (FileSystemOps.ExistsNoFollow(restore.Path))
                        {
                            if (HashMatches(restore.Path, restore.OriginalSha256))
                            {
                                break;
                            }

                            var placed = journal.UndoActions
                                .OfType<RemovePlacedFileUndo>()
                                .FirstOrDefault(candidate =>
                                    string.Equals(candidate.Path, restore.Path, StringComparison.OrdinalIgnoreCase));

                            if (placed is null || !HashMatches(restore.Path, placed.DeployedSha256))
                            {
                                logger.Warn(
                                    "Journal",
                                    $"回滚时不覆盖 “{restore.Path}”：它已不再是本次事务写入的内容。");
                                deferred.Add($"kept unknown file {restore.Path}");
                                break;
                            }
                        }

                        FileSystemOps.CopyAsync(restore.BackupPath, restore.Path).GetAwaiter().GetResult();
                        if (!HashMatches(restore.Path, restore.OriginalSha256))
                        {
                            failures.Add($"restored bytes do not match the backup hash for {restore.Path}");
                        }

                        break;

                    case RestoreDeletedFileUndo deleted:
                        if (!File.Exists(FileSystemOps.ToExtendedPath(deleted.SourcePath)))
                        {
                            failures.Add($"staged source missing for {deleted.Path} ({deleted.SourcePath})");
                            break;
                        }

                        // A file that already exists is not ours to replace: it may be a user file or
                        // something another program created after the deletion.
                        if (FileSystemOps.ExistsNoFollow(deleted.Path))
                        {
                            logger.Warn(
                                "Journal",
                                $"回滚时不覆盖 “{deleted.Path}”：目标位置已有不属于本次事务的文件。");
                            deferred.Add($"kept existing file {deleted.Path}");
                            break;
                        }

                        FileSystemOps.CopyAsync(deleted.SourcePath, deleted.Path).GetAwaiter().GetResult();
                        break;

                    case RemoveDirectoryUndo directory:
                        // Non-recursive by construction: only a directory this transaction created and
                        // that is now empty is removed.
                        if (Directory.Exists(FileSystemOps.ToExtendedPath(directory.Path)))
                        {
                            try
                            {
                                Directory.Delete(FileSystemOps.ToExtendedPath(directory.Path), recursive: false);
                            }
                            catch (IOException)
                            {
                                // Not empty, so something else needs it. Correct to leave it.
                            }
                        }

                        break;

                    default:
                        failures.Add($"unknown undo action {action.GetType().Name}");
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{action.Describe()}: {ex.Message}");
            }
        }

        Append(
            journal,
            JournalStepKind.RollbackComplete,
            null,
            $"{failures.Count} failure(s), {deferred.Count} deferred");
        return new JournalRollbackResult(failures, deferred);
    }

    /// <summary>Deletes a journal and its commit marker.</summary>
    /// <param name="transactionId">Transaction to clear.</param>
    /// <remarks>
    /// Order matters and is the opposite of Modrix's: the journal goes first, so an interruption
    /// leaves at most a lone commit marker, which recovery honours by rolling forward.
    /// </remarks>
    public bool Clear(string transactionId)
    {
        var journalRemoved = RemoveFileQuietly(_paths.JournalFile(transactionId));
        var commitRemoved = ClearCommit(transactionId);
        return journalRemoved || commitRemoved;
    }

    /// <summary>Deletes a commit marker.</summary>
    /// <param name="transactionId">Transaction whose marker to delete.</param>
    private bool ClearCommit(string transactionId) => RemoveFileQuietly(CommitPath(transactionId));

    /// <summary>Path of a transaction's commit marker.</summary>
    /// <param name="transactionId">Transaction id.</param>
    private string CommitPath(string transactionId) => Path.Combine(_paths.Journals, transactionId + ".commit.json");

    /// <summary>Reads every commit marker on disk.</summary>
    private List<CommitMarker> LoadCommitMarkers()
    {
        var markers = new List<CommitMarker>();

        if (!Directory.Exists(_paths.Journals))
        {
            return markers;
        }

        foreach (var file in Directory.EnumerateFiles(_paths.Journals, "*.commit.json"))
        {
            try
            {
                var marker = JsonSerializer.Deserialize<CommitMarker>(File.ReadAllBytes(file), SerializerOptions);
                if (marker is not null)
                {
                    markers.Add(marker);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger?.Warn("Journal", $"已忽略无法读取的提交标记：{file}", ex.Message);
            }
        }

        return markers;
    }

    /// <summary>Rebuilds a manifest from a commit marker, for the roll-forward path.</summary>
    /// <param name="journal">Journal that committed.</param>
    /// <param name="commit">Commit marker.</param>
    private static ModManifest BuildManifestFromCommit(DeploymentJournal journal, CommitMarker commit) =>
        commit.Manifest with { ProfileId = journal.ProfileId };

    /// <summary>Writes a recovered manifest into the manifest directory.</summary>
    /// <param name="manifest">Manifest to write.</param>
    private void WriteRecoveredManifest(ModManifest manifest)
    {
        var path = _paths.ManifestFile(manifest.GameId, manifest.ModId);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);
        FileSystemOps.WriteAtomicAsync(path, payload).GetAwaiter().GetResult();
    }

    /// <summary>Serialises a journal to disk atomically.</summary>
    /// <param name="journal">Journal to persist.</param>
    private void Flush(DeploymentJournal journal)
    {
        try
        {
            Directory.CreateDirectory(_paths.Journals);
            var payload = JsonSerializer.SerializeToUtf8Bytes(journal, SerializerOptions);
            FileSystemOps.WriteAtomicAsync(_paths.JournalFile(journal.TransactionId), payload).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A journal we cannot write is a serious condition: crash safety depends on it. Surface
            // it loudly rather than continuing silently.
            _logger?.Error(
                "Journal",
                $"无法写入事务日志 {journal.TransactionId}，该事务无法保证崩溃恢复。",
                ex.Message);
        }
    }

    /// <summary>Reads one journal file, tolerating corruption.</summary>
    /// <param name="file">File to read.</param>
    private DeploymentJournal? TryRead(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<DeploymentJournal>(File.ReadAllBytes(file), SerializerOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger?.Warn("Journal", $"已忽略无法读取的事务日志：{file}", ex.Message);
            return null;
        }
    }

    /// <summary>True when a file's current hash equals the expected hash.</summary>
    /// <param name="path">File to hash.</param>
    /// <param name="expected">Expected lowercase hex hash.</param>
    private static bool HashMatches(string path, string expected)
    {
        try
        {
            return string.Equals(FileSystemOps.HashFile(path), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Deletes a file, treating absence as success.</summary>
    /// <param name="path">File to delete.</param>
    private static bool RemoveFileQuietly(string path)
    {
        try
        {
            if (!File.Exists(FileSystemOps.ToExtendedPath(path)))
            {
                return false;
            }

            File.Delete(FileSystemOps.ToExtendedPath(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The new manifest rows a committing transaction intends to publish.
    /// </summary>
    private sealed record CommitMarker
    {
        /// <summary>Transaction the marker belongs to.</summary>
        public required string TransactionId { get; init; }

        /// <summary>Game being deployed.</summary>
        public required string GameId { get; init; }

        /// <summary>Profile being deployed.</summary>
        public required string ProfileId { get; init; }

        /// <summary>Mod whose manifest is being published.</summary>
        public required string ModId { get; init; }

        /// <summary>The manifest itself, so a roll-forward needs nothing else.</summary>
        public required ModManifest Manifest { get; init; }
    }
}
