using System.Diagnostics;
using System.IO;
using ModManager.Core;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Recovery from a genuinely exited child process (exit code 42, no finally blocks, no graceful
/// rollback). The child writes a journal and a partial filesystem state, then calls
/// <see cref="Environment.Exit(int)"/>; the parent opens the same app-data tree and runs recovery.
/// This is a real process exit, though the child constructs the crash state explicitly rather than
/// killing the full applier mid-operation.
/// </summary>
public sealed class CrashRecoveryProcessTests
{
    [Fact]
    public async Task PartialPlacementLeftByADeadProcessIsRolledBack()
    {
        using var tree = new TemporaryTree();
        await RunCrashAsync(tree, "crash-partial");

        var target = Path.Combine(tree.Root, "game", "Mods", "plugins", "partial.dll");
        Assert.True(File.Exists(target));

        using var host = TestHost.Create(tree);
        var report = await host.Journals.RecoverAsync(host.Logger);

        Assert.NotEmpty(report.Recovered);
        Assert.False(File.Exists(target), "Recovery must remove the half-placed file.");
        Assert.Empty(host.Journals.FindInterrupted());
    }

    [Fact]
    public async Task DeletedFileLeftByADeadProcessIsRestoredFromStaging()
    {
        using var tree = new TemporaryTree();
        await RunCrashAsync(tree, "crash-delete");

        var target = Path.Combine(tree.Root, "game", "Mods", "plugins", "deleted.dll");
        Assert.False(File.Exists(target));

        using var host = TestHost.Create(tree);
        var report = await host.Journals.RecoverAsync(host.Logger);

        Assert.NotEmpty(report.Recovered);
        Assert.True(File.Exists(target));
        Assert.Equal("SOURCE", await File.ReadAllTextAsync(target));
        Assert.Empty(host.Journals.FindInterrupted());
    }

    [Fact]
    public async Task OverwrittenFileWithoutPlacementProofIsDeferredNotSilentlyReplaced()
    {
        using var tree = new TemporaryTree();
        await RunCrashAsync(tree, "crash-overwrite");

        var target = Path.Combine(tree.Root, "game", "Mods", "plugins", "overwritten.dll");
        Assert.Equal("MODDED", await File.ReadAllTextAsync(target));

        using var host = TestHost.Create(tree);
        var report = await host.Journals.RecoverAsync(host.Logger);

        Assert.NotEmpty(report.Deferred);
        Assert.Equal("MODDED", await File.ReadAllTextAsync(target));
        Assert.Single(host.Journals.FindInterrupted());
    }

    [Theory]
    [InlineData("JournalOpened")]
    [InlineData("FirstFilePlaced")]
    [InlineData("AllFilesPlaced")]
    [InlineData("BeforeCommit")]
    public async Task RealApplierKilledAtCheckpointRecoversToThePreTransactionState(string checkpoint)
    {
        using var tree = new TemporaryTree();
        await RunApplierCrashAsync(tree, checkpoint);

        var plugin = Path.Combine(tree.Root, "game", "BepInEx", "plugins", "Example.dll");
        var config = Path.Combine(tree.Root, "game", "BepInEx", "config", "Example.json");

        using var host = TestHost.Create(tree);
        var report = await host.Journals.RecoverAsync(host.Logger);

        Assert.NotEmpty(report.Recovered);
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(plugin));
        Assert.False(File.Exists(config), "A partially placed target must not survive rollback.");
        Assert.Empty(host.Journals.FindInterrupted());
    }

    private static async Task RunApplierCrashAsync(TemporaryTree tree, string checkpoint)
    {
        var probeDll = Path.Combine(AppContext.BaseDirectory, "ModManager.LockProbe.dll");
        Assert.True(File.Exists(probeDll), $"The applier probe was not copied to '{probeDll}'.");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(probeDll);
        startInfo.ArgumentList.Add("apply");
        startInfo.ArgumentList.Add(tree.Dir("appdata"));
        startInfo.ArgumentList.Add(tree.Dir("game"));
        startInfo.ArgumentList.Add(checkpoint);

        using var child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the applier probe.");

        var line = await child.StandardOutput.ReadLineAsync()
            .WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal($"CHECKPOINT:{checkpoint}", line);

        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task RunCrashAsync(TemporaryTree tree, string mode)
    {
        var probeDll = Path.Combine(AppContext.BaseDirectory, "ModManager.LockProbe.dll");
        Assert.True(File.Exists(probeDll), $"The crash probe was not copied to '{probeDll}'.");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(probeDll);
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(tree.Dir("appdata"));
        startInfo.ArgumentList.Add(tree.Dir("game"));

        using var child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the crash probe.");

        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(42, child.ExitCode);
    }
}
