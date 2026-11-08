using System.IO;
using System.Security.Cryptography;
using System.Text;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

public sealed class DeferredTransactionServiceTests
{
    [Fact]
    public async Task QueryAndRetryRecoveryRestoreTheOriginalAndClearTheJournal()
    {
        using var host = TestHost.Create();
        var (journal, target, _) = CreateDeferred(host, targetContent: "MODDED", backupExists: true);
        var service = new DeferredTransactionService(host.Journals, host.Paths, host.Logger);

        var info = Assert.Single(service.Query());
        Assert.Equal(journal.TransactionId, info.TransactionId);
        Assert.Equal(DeferredDiagnosis.NeedsConfirmation, info.Diagnosis);
        Assert.Contains(info.Files, file => file.TargetPath == target && file.TargetExists);

        var result = await service.RetryRecoveryAsync(journal.TransactionId, "test");

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(target));
        Assert.Empty(service.Query());
    }

    [Fact]
    public async Task MissingBackupIsDiagnosedAndRefused()
    {
        using var host = TestHost.Create();
        var (_, target, backup) = CreateDeferred(host, targetContent: "MODDED", backupExists: false);
        var service = new DeferredTransactionService(host.Journals, host.Paths, host.Logger);

        var info = Assert.Single(service.Query());
        Assert.Equal(DeferredDiagnosis.BackupMissing, info.Diagnosis);
        Assert.False(File.Exists(backup));

        var result = await service.RestoreBackupAsync(info.TransactionId, target, "test");
        Assert.False(result.Succeeded);
        Assert.Contains("备份", result.Message);
    }

    [Fact]
    public async Task RestoreBackupRefusesToOverwriteAUserModifiedTarget()
    {
        using var host = TestHost.Create();
        var (journal, target, _) = CreateDeferred(host, targetContent: "USER-EDIT", backupExists: true);
        var service = new DeferredTransactionService(host.Journals, host.Paths, host.Logger);

        var result = await service.RestoreBackupAsync(journal.TransactionId, target, "test");

        Assert.False(result.Succeeded);
        Assert.Equal("USER-EDIT", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task MarkResolvedIsAuditedAndRemovesTheTransactionFromTheQuery()
    {
        using var host = TestHost.Create();
        var (journal, _, _) = CreateDeferred(host, targetContent: "USER-EDIT", backupExists: true);
        var service = new DeferredTransactionService(host.Journals, host.Paths, host.Logger);

        var result = await service.MarkResolvedAsync(journal.TransactionId, "已人工核对", "tester");

        Assert.True(result.Succeeded, result.Message);
        Assert.Empty(service.Query());
        var audit = Path.Combine(host.Paths.Data, "DeferredAudit", "audit.jsonl");
        Assert.True(File.Exists(audit));
        Assert.Contains("MarkResolved", await File.ReadAllTextAsync(audit));
    }

    [Fact]
    public async Task WrongTransactionIdIsRejected()
    {
        using var host = TestHost.Create();
        var service = new DeferredTransactionService(host.Journals, host.Paths, host.Logger);

        var result = await service.MarkResolvedAsync("does-not-exist", "x", "tester");

        Assert.False(result.Succeeded);
    }

    private static (DeploymentJournal Journal, string Target, string Backup) CreateDeferred(
        TestHost host,
        string targetContent,
        bool backupExists)
    {
        var target = Path.Combine(host.Tree.Root, "game", "Mods", "plugins", "Example.dll");
        var backup = Path.Combine(host.Tree.Root, "appdata", "Backups", "testgame", "tx", "Example.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, "ORIGINAL");
        File.WriteAllText(target, targetContent);
        if (!backupExists)
        {
            File.Delete(backup);
        }

        var journal = host.Journals.Open("testgame", "default", "hash", ["Example"]);
        host.Journals.RecordUndo(journal, new RestoreOverwrittenFileUndo(target, backup, Hash("ORIGINAL")));
        host.Journals.RecordUndo(journal, new RemovePlacedFileUndo(target, Hash("MODDED"), "Example"));
        host.Journals.Transition(journal, JournalState.Failed, "simulated deferred state");
        return (journal, target, backup);
    }

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
