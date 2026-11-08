using System.IO;
using ModManager.Core;
using ModManager.Core.Safety;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Transaction rollback and crash recovery.
/// </summary>
/// <remarks>
/// This is where "an interrupted deploy is recovered on next launch" is either true or false. The journal is
/// written before the first mutation, and rollback replays the undo log in reverse — so the properties worth
/// proving are: everything we did is taken back, the user's own edits are <i>not</i> taken back, and a
/// transaction that passed its commit point moves forward instead of back.
/// </remarks>
public sealed class JournalRecoveryTests
{
    /// <summary>Rollback removes a placed file whose bytes are still exactly what we deployed.</summary>
    [Fact]
    public void RollbackRemovesAFileThatStillMatchesWhatWeDeployed()
    {
        using var tree = new TemporaryTree();
        var journals = NewJournals(tree, out var logger);
        var placed = tree.WriteFile("game/Mods/plugins/mod.dll", "our bytes");
        var journal = journals.Open("testgame", "default", "plan-hash", ["mod-1"]);

        journals.RecordUndo(journal, new RemovePlacedFileUndo(placed, FileSystemOps.HashFile(placed), "mod-1"));
        var failures = journals.Rollback(journal, logger);

        Assert.Empty(failures);
        Assert.False(File.Exists(placed));
    }

    /// <summary>
    /// The "no silent clobber" invariant: if the user edited a deployed file, rollback must leave it alone.
    /// </summary>
    [Fact]
    public void RollbackKeepsAFileTheUserEditedSinceDeployment()
    {
        using var tree = new TemporaryTree();
        var journals = NewJournals(tree, out var logger);
        var placed = tree.WriteFile("game/Mods/plugins/mod.dll", "our bytes");
        var deployedHash = FileSystemOps.HashFile(placed);
        tree.WriteFile("game/Mods/plugins/mod.dll", "the user's important edit");

        var journal = journals.Open("testgame", "default", "plan-hash", ["mod-1"]);
        journals.RecordUndo(journal, new RemovePlacedFileUndo(placed, deployedHash, "mod-1"));
        var failures = journals.Rollback(journal, logger);

        // Skipping is deliberate, not a failure: the transaction is still considered rolled back cleanly.
        Assert.Empty(failures);
        Assert.True(File.Exists(placed));
        Assert.Equal("the user's important edit", File.ReadAllText(placed));
    }

    /// <summary>An overwritten game file is put back byte-for-byte from its backup.</summary>
    [Fact]
    public void RollbackRestoresAnOverwrittenFileFromItsBackup()
    {
        using var tree = new TemporaryTree();
        var journals = NewJournals(tree, out var logger);
        var backup = tree.WriteFile("appdata/Backups/original.dll", "original bytes");
        var target = tree.WriteFile("game/Mods/original.dll", "our replacement");
        var journal = journals.Open("testgame", "default", "plan-hash", ["mod-1"]);

        // Real transactions record the placement too; that is the proof that the bytes currently on
        // disk are ours and may be replaced by the backup during rollback.
        journals.RecordUndo(journal, new RestoreOverwrittenFileUndo(target, backup, FileSystemOps.HashFile(backup)));
        journals.RecordUndo(journal, new RemovePlacedFileUndo(target, FileSystemOps.HashFile(target), "mod-1"));
        var failures = journals.Rollback(journal, logger);

        Assert.Empty(failures);
        Assert.Equal("original bytes", File.ReadAllText(target));
    }

    /// <summary>
    /// A target edited after deployment must not be replaced by its backup during rollback.
    /// </summary>
    [Fact]
    public void RollbackKeepsAnOverwrittenFileTheUserEditedAfterDeployment()
    {
        using var tree = new TemporaryTree();
        var journals = NewJournals(tree, out var logger);
        var backup = tree.WriteFile("appdata/Backups/original.dll", "original bytes");
        var target = tree.WriteFile("game/Mods/original.dll", "our replacement");
        var deployed = FileSystemOps.HashFile(target);
        tree.WriteFile("game/Mods/original.dll", "the user's edit");

        var journal = journals.Open("testgame", "default", "plan-hash", ["mod-1"]);
        journals.RecordUndo(journal, new RestoreOverwrittenFileUndo(target, backup, FileSystemOps.HashFile(backup)));
        journals.RecordUndo(journal, new RemovePlacedFileUndo(target, deployed, "mod-1"));

        var result = journals.RollbackDetailed(journal, logger);

        Assert.Empty(result.Failures);
        Assert.NotEmpty(result.Deferred);
        Assert.Equal("the user's edit", File.ReadAllText(target));
    }

    /// <summary>
    /// A file that appeared at a deleted path after the crash is not ours to overwrite.
    /// </summary>
    [Fact]
    public void RollbackKeepsAnUnknownFileAtADeletedPath()
    {
        using var tree = new TemporaryTree();
        var journals = NewJournals(tree, out var logger);
        var staged = tree.WriteFile("appdata/Mods/staged/original.dll", "staged bytes");
        var target = tree.WriteFile("game/Mods/original.dll", "external file");

        var journal = journals.Open("testgame", "default", "plan-hash", ["mod-1"]);
        journals.RecordUndo(journal, new RestoreDeletedFileUndo(target, staged));

        var result = journals.RollbackDetailed(journal, logger);

        Assert.Empty(result.Failures);
        Assert.NotEmpty(result.Deferred);
        Assert.Equal("external file", File.ReadAllText(target));
    }

    /// <summary>A deleted game file is recreated from staging.</summary>
    [Fact]
    public void RollbackRecreatesADeletedFileFromStaging()
    {
        using var tree = new TemporaryTree();
        var journals = NewJournals(tree, out var logger);
        var staged = tree.WriteFile("appdata/Mods/staged/original.dll", "staged bytes");
        var target = tree.WriteFile("game/Mods/original.dll", "will be deleted");
        File.Delete(target);
        var journal = journals.Open("testgame", "default", "plan-hash", ["mod-1"]);

        journals.RecordUndo(journal, new RestoreDeletedFileUndo(target, staged));
        var failures = journals.Rollback(journal, logger);

        Assert.Empty(failures);
        Assert.True(File.Exists(target));
        Assert.Equal("staged bytes", File.ReadAllText(target));
    }

    /// <summary>The undo log is replayed in reverse, so the last thing done is the first thing undone.</summary>
    [Fact]
    public void RollbackReplaysUndoActionsInReverseOrder()
    {
        using var tree = new TemporaryTree();
        var journals = NewJournals(tree, out var logger);
        var target = tree.WriteFile("game/Mods/data.bin", "ours");
        var backup = tree.WriteFile("appdata/Backups/data.bin", "original");

        var journal = journals.Open("testgame", "default", "plan-hash", ["mod-1"]);

        // Recorded in the order they happened: first the overwrite, then the placement of the new bytes.
        journals.RecordUndo(journal, new RestoreOverwrittenFileUndo(target, backup, FileSystemOps.HashFile(backup)));
        journals.RecordUndo(journal, new RemovePlacedFileUndo(target, FileSystemOps.HashFile(target), "mod-1"));

        var failures = journals.Rollback(journal, logger);

        // Stripping the placement first lets the restore land last, which is the only order that leaves the
        // original bytes on disk. Reversed, the restore would be deleted by the placement's own undo.
        Assert.Empty(failures);
        Assert.Equal("original", File.ReadAllText(target));
    }

    /// <summary>A journal left mid-flight is found, rolled back, and cleared on the next launch.</summary>
    [Fact]
    public async Task RecoveryRollsBackAnInterruptedTransaction()
    {
        using var tree = new TemporaryTree();
        var journals = NewJournals(tree, out var logger);
        var placed = tree.WriteFile("game/Mods/plugins/mod.dll", "half-installed");
        var journal = journals.Open("testgame", "default", "plan-hash", ["mod-1"]);

        journals.RecordUndo(journal, new RemovePlacedFileUndo(placed, FileSystemOps.HashFile(placed), "mod-1"));
        journals.Transition(journal, JournalState.Applying);

        Assert.Single(journals.FindInterrupted());

        var report = await journals.RecoverAsync(logger);

        Assert.Contains(journal.TransactionId, report.Recovered);
        Assert.Empty(report.Failed);
        Assert.False(File.Exists(placed));
        Assert.Empty(journals.FindInterrupted());
    }

    /// <summary>
    /// A transaction that passed its commit point is rolled <i>forward</i>: reverting it would contradict a
    /// manifest that is already published.
    /// </summary>
    [Fact]
    public async Task RecoveryRollsForwardPastTheCommitPoint()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));
        paths.EnsureCreated();
        var logger = new AppLogger();
        var journals = new JournalService(paths, logger);
        var journal = journals.Open("testgame", "default", "plan-hash", ["mod-1"]);

        journals.WriteCommit(journal, NewManifest());
        journals.Transition(journal, JournalState.Applying);

        var report = await journals.RecoverAsync(logger);

        Assert.Contains(journal.TransactionId, report.Recovered);
        Assert.Empty(report.Failed);

        var manifestPath = paths.ManifestFile("testgame", "mod-1");
        Assert.True(File.Exists(manifestPath), $"Expected a recovered manifest at {manifestPath}.");
    }

    /// <summary>A committed transaction is finished business and must not appear as needing recovery.</summary>
    [Fact]
    public void CommittedTransactionsAreNotReportedAsInterrupted()
    {
        using var tree = new TemporaryTree();
        var journals = NewJournals(tree, out _);
        var journal = journals.Open("testgame", "default", "plan-hash", ["mod-1"]);

        journals.Transition(journal, JournalState.Committed);

        Assert.Empty(journals.FindInterrupted());
    }

    /// <summary>
    /// Recovery must leave a user-edited overwrite target alone and ask for review, not silently
    /// restore the old backup over it.
    /// </summary>
    [Fact]
    public async Task RecoveryDefersWhenTheOverwrittenFileWasEditedAfterDeployment()
    {
        using var tree = new TemporaryTree();
        var journals = NewJournals(tree, out var logger);
        var backup = tree.WriteFile("appdata/Backups/original.dll", "original bytes");
        var target = tree.WriteFile("game/Mods/original.dll", "our replacement");
        var deployed = FileSystemOps.HashFile(target);
        tree.WriteFile("game/Mods/original.dll", "the user's edit");

        var journal = journals.Open("testgame", "default", "plan-hash", ["mod-1"]);
        journals.RecordUndo(journal, new RestoreOverwrittenFileUndo(target, backup, FileSystemOps.HashFile(backup)));
        journals.RecordUndo(journal, new RemovePlacedFileUndo(target, deployed, "mod-1"));
        journals.Transition(journal, JournalState.Applying);

        var report = await journals.RecoverAsync(logger);

        Assert.Contains(journal.TransactionId, report.Deferred);
        Assert.Empty(report.Failed);
        Assert.Equal("the user's edit", File.ReadAllText(target));
        Assert.Single(journals.FindInterrupted());
    }

    /// <summary>A corrupt backup must be reported before it is copied over a deployed file.</summary>
    [Fact]
    public void RollbackRefusesACorruptBackup()
    {
        using var tree = new TemporaryTree();
        var journals = NewJournals(tree, out var logger);
        var backup = tree.WriteFile("appdata/Backups/original.dll", "original bytes");
        var target = tree.WriteFile("game/Mods/original.dll", "our replacement");
        var journal = journals.Open("testgame", "default", "plan-hash", ["mod-1"]);

        journals.RecordUndo(journal, new RemovePlacedFileUndo(target, FileSystemOps.HashFile(target), "mod-1"));
        journals.RecordUndo(journal, new RestoreOverwrittenFileUndo(target, backup, FileSystemOps.HashFile(backup)));
        tree.WriteFile("appdata/Backups/original.dll", "corrupted backup");

        var result = journals.RollbackDetailed(journal, logger);

        Assert.Contains(result.Failures, failure => failure.Contains("hash mismatch", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("our replacement", File.ReadAllText(target));
    }

    /// <summary>A missing backup is a hard failure, but the deployed file is kept for manual repair.</summary>
    [Fact]
    public void RollbackReportsAMissingBackupAndKeepsTheDeployedFile()
    {
        using var tree = new TemporaryTree();
        var journals = NewJournals(tree, out var logger);
        var target = tree.WriteFile("game/Mods/original.dll", "our replacement");
        var missingBackup = tree.Dir("appdata/Backups") + "/does-not-exist.dll";
        var journal = journals.Open("testgame", "default", "plan-hash", ["mod-1"]);

        journals.RecordUndo(journal, new RemovePlacedFileUndo(target, FileSystemOps.HashFile(target), "mod-1"));
        journals.RecordUndo(journal, new RestoreOverwrittenFileUndo(target, missingBackup, "original-hash"));

        var result = journals.RollbackDetailed(journal, logger);

        Assert.Contains(result.Failures, failure => failure.Contains("backup missing", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("our replacement", File.ReadAllText(target));
    }

    /// <summary>Inspection is read-only and exposes both interrupted and committed transactions.</summary>
    [Fact]
    public void InspectDistinguishesInterruptedAndCommittedTransactions()
    {
        using var tree = new TemporaryTree();
        var journals = NewJournals(tree, out _);
        var interrupted = journals.Open("testgame", "default", "plan-1", ["mod-1"]);
        journals.Transition(interrupted, JournalState.Applying);

        var committed = journals.Open("testgame", "default", "plan-2", ["mod-2"]);
        journals.WriteCommit(committed, NewManifest());
        journals.Transition(committed, JournalState.Applying);

        var snapshot = journals.Inspect();

        var interruptedRow = Assert.Single(snapshot, row => row.TransactionId == interrupted.TransactionId);
        Assert.True(interruptedRow.Interrupted);
        Assert.False(interruptedRow.HasCommitMarker);

        var committedRow = Assert.Single(snapshot, row => row.TransactionId == committed.TransactionId);
        Assert.True(committedRow.HasCommitMarker);
        Assert.Equal(JournalState.Applying, committedRow.State);
    }

    private static JournalService NewJournals(TemporaryTree tree, out AppLogger logger)
    {
        var paths = new AppPaths(tree.Dir("appdata"));
        paths.EnsureCreated();
        logger = new AppLogger();
        return new JournalService(paths, logger);
    }

    private static ModManifest NewManifest() => new()
    {
        ModId = "mod-1",
        Name = "Test Mod",
        GameId = "testgame",
        ProfileId = "default",
        DeploymentRoot = "Mods",
    };
}
