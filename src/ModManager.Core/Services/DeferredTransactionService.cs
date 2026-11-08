using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ModManager.Core.Services;

/// <summary>
/// Read-only inspection plus three explicit, audited actions over journals left in a deferred state.
/// It never deletes a journal or a backup, and it refuses to touch a target it cannot prove belongs
/// to the interrupted transaction.
/// </summary>
public sealed class DeferredTransactionService : IDeferredTransactionService
{
    private readonly JournalService _journals;
    private readonly AppPaths _paths;
    private readonly IAppLogger _logger;
    private readonly object _auditGate = new();

    public DeferredTransactionService(JournalService journals, AppPaths paths, IAppLogger logger)
    {
        _journals = journals;
        _paths = paths;
        _logger = logger;
    }

    public IReadOnlyList<DeferredTransactionInfo> Query() =>
        _journals.All()
            .Where(journal => journal.NeedsRecovery)
            .OrderBy(journal => journal.StartedAt)
            .Select(BuildInfo)
            .ToList();

    public DeferredTransactionInfo? Get(string transactionId)
    {
        var journal = Find(transactionId);
        return journal is null ? null : BuildInfo(journal);
    }

    public async Task<DeferredActionResult> RetryRecoveryAsync(
        string transactionId,
        string actor,
        CancellationToken ct = default)
    {
        var journal = Find(transactionId);
        if (journal is null)
        {
            return new DeferredActionResult(false, "找不到该事务。");
        }

        var result = await _journals.RecoverTransactionAsync(transactionId, _logger, ct).ConfigureAwait(false);
        var message = result.Recovered
            ? "恢复成功，事务已回滚并清理。"
            : "恢复未完成：" + string.Join("；", result.Details);
        Audit(transactionId, "RetryRecovery", actor, message);
        return new DeferredActionResult(result.Recovered, message);
    }

    public Task<DeferredActionResult> RestoreBackupAsync(
        string transactionId,
        string targetPath,
        string actor,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var journal = Find(transactionId);
        if (journal is null)
        {
            return Task.FromResult(new DeferredActionResult(false, "找不到该事务。"));
        }

        var restore = journal.UndoActions
            .OfType<RestoreOverwrittenFileUndo>()
            .FirstOrDefault(action => string.Equals(action.Path, targetPath, StringComparison.OrdinalIgnoreCase));
        if (restore is null)
        {
            return Task.FromResult(new DeferredActionResult(false, "该事务没有针对此路径的备份恢复动作。"));
        }

        if (!File.Exists(restore.BackupPath))
        {
            return Task.FromResult(new DeferredActionResult(false, "备份文件不存在，拒绝恢复。"));
        }

        if (!HashMatches(restore.BackupPath, restore.OriginalSha256))
        {
            return Task.FromResult(new DeferredActionResult(false, "备份哈希与记录不一致，拒绝恢复。"));
        }

        if (File.Exists(restore.Path))
        {
            var deployedHash = journal.UndoActions
                .OfType<RemovePlacedFileUndo>()
                .FirstOrDefault(action => string.Equals(action.Path, restore.Path, StringComparison.OrdinalIgnoreCase))
                ?.DeployedSha256;

            var currentIsOurs = !string.IsNullOrEmpty(deployedHash) && HashMatches(restore.Path, deployedHash);
            var currentIsOriginal = HashMatches(restore.Path, restore.OriginalSha256);
            if (!currentIsOurs && !currentIsOriginal)
            {
                return Task.FromResult(new DeferredActionResult(
                    false,
                    "目标文件在事务中断后被修改，拒绝用备份覆盖。"));
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(restore.Path)!);
        File.Copy(restore.BackupPath, restore.Path, overwrite: true);
        var message = $"已从备份恢复：{restore.Path}";
        Audit(transactionId, "RestoreBackup", actor, message);
        return Task.FromResult(new DeferredActionResult(true, message));
    }

    public Task<DeferredActionResult> MarkResolvedAsync(
        string transactionId,
        string note,
        string actor,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var journal = Find(transactionId);
        if (journal is null)
        {
            return Task.FromResult(new DeferredActionResult(false, "找不到该事务。"));
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            return Task.FromResult(new DeferredActionResult(false, "必须填写人工确认说明。"));
        }

        _journals.Transition(journal, JournalState.Resolved, note.Trim());
        var message = $"已标记为人工解决：{note.Trim()}";
        Audit(transactionId, "MarkResolved", actor, message);
        return Task.FromResult(new DeferredActionResult(true, message));
    }

    private DeploymentJournal? Find(string transactionId) =>
        _journals.All().FirstOrDefault(journal =>
            string.Equals(journal.TransactionId, transactionId, StringComparison.Ordinal));

    private static DeferredTransactionInfo BuildInfo(DeploymentJournal journal)
    {
        var files = new List<DeferredFileState>();
        foreach (var undo in journal.UndoActions)
        {
            if (undo is RestoreOverwrittenFileUndo restore)
            {
                var deployedHash = journal.UndoActions
                    .OfType<RemovePlacedFileUndo>()
                    .FirstOrDefault(action => string.Equals(action.Path, restore.Path, StringComparison.OrdinalIgnoreCase))
                    ?.DeployedSha256;
                var exists = File.Exists(restore.Path);
                var isOriginal = exists && HashMatches(restore.Path, restore.OriginalSha256);
                var isOurPlacement = exists
                    && !string.IsNullOrWhiteSpace(deployedHash)
                    && HashMatches(restore.Path, deployedHash);
                files.Add(new DeferredFileState(
                    restore.Path,
                    "restoreOverwritten",
                    exists,
                    exists && !isOriginal && !isOurPlacement,
                    restore.BackupPath,
                    File.Exists(restore.BackupPath),
                    File.Exists(restore.BackupPath) && HashMatches(restore.BackupPath, restore.OriginalSha256),
                    null));
            }
            else if (undo is RestoreDeletedFileUndo deleted)
            {
                files.Add(new DeferredFileState(
                    deleted.Path,
                    "restoreDeleted",
                    File.Exists(deleted.Path),
                    false,
                    null,
                    false,
                    false,
                    deleted.SourcePath));
            }
            else
            {
                files.Add(new DeferredFileState(
                    undo.Path,
                    undo.GetType().Name,
                    File.Exists(undo.Path),
                    false,
                    null,
                    false,
                    false,
                    null));
            }
        }

        var diagnosis = Diagnose(journal, files);
        return new DeferredTransactionInfo(
            journal.TransactionId,
            journal.GameId,
            journal.ModIds.ToList(),
            journal.State,
            journal.StartedAt,
            journal.FinishedAt,
            files,
            journal.Entries.Where(entry => entry.Succeeded).Select(entry => entry.Kind.ToString()).ToList(),
            journal.Entries.Where(entry => !entry.Succeeded).Select(entry => entry.Kind.ToString()).ToList(),
            files.Where(file => file.BackupExists).Select(file => file.BackupPath!).ToList(),
            journal.Error,
            diagnosis,
            diagnosis switch
            {
                DeferredDiagnosis.AutoRecoverable => "可尝试 RetryRecovery。",
                DeferredDiagnosis.BackupMissing => "备份缺失或损坏，需人工确认后 MarkResolved。",
                DeferredDiagnosis.TargetModified => "目标已被外部修改，禁止自动覆盖，需人工确认。",
                DeferredDiagnosis.StateMismatch => "Journal 状态与磁盘不一致，需人工检查。",
                _ => "无法安全判断，保留现状并人工处理。",
            });
    }

    private static DeferredDiagnosis Diagnose(DeploymentJournal journal, IReadOnlyList<DeferredFileState> files)
    {
        if (journal.UndoActions.Count == 0)
        {
            return DeferredDiagnosis.Unsafe;
        }

        if (files.Any(file => file.Operation == "restoreOverwritten" && !file.BackupValid))
        {
            return DeferredDiagnosis.BackupMissing;
        }

        if (files.Any(file => file.TargetModified && file.Operation == "restoreOverwritten"))
        {
            return DeferredDiagnosis.TargetModified;
        }

        return journal.State == JournalState.Failed
            ? DeferredDiagnosis.NeedsConfirmation
            : DeferredDiagnosis.AutoRecoverable;
    }

    private void Audit(string transactionId, string action, string actor, string result)
    {
        lock (_auditGate)
        {
            var directory = Path.Combine(_paths.Data, "DeferredAudit");
            Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.UtcNow,
                transactionId,
                action,
                actor,
                result,
            });
            File.AppendAllText(Path.Combine(directory, "audit.jsonl"), line + Environment.NewLine, Encoding.UTF8);
        }
    }

    private static bool HashMatches(string path, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected) || !File.Exists(path))
        {
            return false;
        }

        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
