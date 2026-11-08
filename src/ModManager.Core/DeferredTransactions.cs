using ModManager.Core.Services;

namespace ModManager.Core;

/// <summary>Why a deferred transaction cannot be repaired without a decision.</summary>
public enum DeferredDiagnosis
{
    AutoRecoverable,
    NeedsConfirmation,
    BackupMissing,
    TargetModified,
    StateMismatch,
    Unsafe,
}

/// <summary>One file touched by a deferred transaction.</summary>
public sealed record DeferredFileState(
    string TargetPath,
    string Operation,
    bool TargetExists,
    bool TargetModified,
    string? BackupPath,
    bool BackupExists,
    bool BackupValid,
    string? SourcePath);

/// <summary>A journal left for human review, with enough detail to decide safely.</summary>
public sealed record DeferredTransactionInfo(
    string TransactionId,
    string GameId,
    IReadOnlyList<string> ModIds,
    JournalState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<DeferredFileState> Files,
    IReadOnlyList<string> CompletedOperations,
    IReadOnlyList<string> PendingOperations,
    IReadOnlyList<string> AvailableBackups,
    string? FailureReason,
    DeferredDiagnosis Diagnosis,
    string RecommendedAction);

/// <summary>Result of a manual deferred-transaction action.</summary>
public sealed record DeferredActionResult(bool Succeeded, string Message);

/// <summary>Query, diagnose, and safely resolve deferred transactions.</summary>
public interface IDeferredTransactionService
{
    IReadOnlyList<DeferredTransactionInfo> Query();
    DeferredTransactionInfo? Get(string transactionId);
    Task<DeferredActionResult> RetryRecoveryAsync(string transactionId, string actor, CancellationToken ct = default);
    Task<DeferredActionResult> RestoreBackupAsync(string transactionId, string targetPath, string actor, CancellationToken ct = default);
    Task<DeferredActionResult> MarkResolvedAsync(string transactionId, string note, string actor, CancellationToken ct = default);
}
