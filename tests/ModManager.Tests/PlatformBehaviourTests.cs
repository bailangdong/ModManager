using System.IO;
using ModManager.Core;
using ModManager.Core.Persistence;
using ModManager.Core.Services;
using ModManager.App.ViewModels;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// End-to-end behaviour of the platform features against the real services: installing into a folder that
/// does not exist yet, updating an installed Nexus mod through the normal pipeline, and the download model
/// the online page depends on.
/// </summary>
public sealed class PlatformBehaviourTests
{
    private static StagedMod Confirm(StagedMod staged)
    {
        staged.Metadata.RootConfirmedByUser = true;
        return staged;
    }

    /// <summary>
    /// A missing mod-root directory is created when the adapter can determine it, instead of failing.
    /// </summary>
    /// <remarks>
    /// This is the "目标目录不存在" case: the mod root is derived from the adapter, so creating it is safe
    /// and is what lets a first install work on a game the user has never modded. The planner records what
    /// it created, which is the evidence the assertion checks.
    /// </remarks>
    [Fact]
    public async Task MissingModRootDirectoryIsCreatedWhenItCanBeDetermined()
    {
        using var tree = new TemporaryTree();
        var host = TestHost.Create(tree);
        using var _ = host;

        // The install root exists; the mod root ("Mods") deliberately does not.
        var gameRoot = host.CreateGameRoot();
        host.PathSafety.ApproveGameRoot("testgame", gameRoot, Path.Combine(gameRoot, "Mods"));
        Assert.False(Directory.Exists(Path.Combine(gameRoot, "Mods")));

        var staged = await host.Install.StageAsync(
            new InstallRequest
            {
                ArchivePath = TestArchives.CreateZip(tree, "mod.zip", ("Mods/plugins/mod.dll", "bytes")),
                GameId = "testgame",
                AcceptLowConfidence = true,
            },
            new TestGameAdapter(),
            host.Logger);

        Assert.True(staged.Succeeded, staged.Error);

        var deployed = await host.Install.InstallAsync(Confirm(staged.Value!), new TestGameAdapter(), gameRoot, host.Logger);

        Assert.True(deployed.Succeeded, deployed.Error);
        Assert.True(Directory.Exists(Path.Combine(gameRoot, "Mods")));
        Assert.True(File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")));
        Assert.Contains(deployed.Manifest!.CreatedDirectories, dir =>
            dir.Replace('\\', '/').Contains("/Mods", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A mod installed from Nexus keeps its Nexus id, so the library can find it without guessing.
    /// </summary>
    [Fact]
    public async Task InstalledNexusModKeepsItsIdAndCanBeFound()
    {
        using var tree = new TemporaryTree();
        var host = TestHost.Create(tree);
        using var _ = host;

        var gameRoot = host.CreateGameRoot();
        host.PathSafety.ApproveGameRoot("testgame", gameRoot, Path.Combine(gameRoot, "Mods"));

        var staged = await host.Install.StageAsync(
            new InstallRequest
            {
                ArchivePath = TestArchives.CreateZip(tree, "online.zip", ("Mods/plugins/online.dll", "bytes")),
                GameId = "testgame",
                AcceptLowConfidence = true,
                NexusModId = 4242,
                NexusFileId = 11,
                SourceUrl = "https://www.nexusmods.com/testgame/mods/4242",
            },
            new TestGameAdapter(),
            host.Logger);

        Assert.True(staged.Succeeded, staged.Error);
        Assert.True((await host.Install.InstallAsync(Confirm(staged.Value!), new TestGameAdapter(), gameRoot, host.Logger)).Succeeded);

        var installed = host.Library.ModsForGame("testgame");
        var linked = Assert.Single(installed);
        Assert.Equal(4242, linked.NexusModId);
        Assert.Equal(11, linked.NexusFileId);
        Assert.Equal(ModSource.NexusMods, linked.Source);

        var linker = new NexusLibraryLinker(new FakeNexusProvider());
        Assert.Equal(linked.Id, linker.FindInstalled(installed, 4242)?.Id);
        Assert.Null(linker.FindInstalled(installed, 9999));
    }

    /// <summary>
    /// Updating through the online page's flow removes the old version and installs the new one, leaving a
    /// single manifest and the new file content on disk.
    /// </summary>
    [Fact]
    public async Task UpdateReplacesTheInstalledVersionThroughTheNormalPipeline()
    {
        using var tree = new TemporaryTree();
        var host = TestHost.Create(tree);
        using var _ = host;

        var gameRoot = host.CreateGameRoot();
        host.PathSafety.ApproveGameRoot("testgame", gameRoot, Path.Combine(gameRoot, "Mods"));
        var target = Path.Combine(gameRoot, "Mods", "data", "shared.txt");

        var v1 = await StageNexusAsync(host, tree, "v1.zip", "version one");
        Assert.True((await host.Install.InstallAsync(Confirm(v1), new TestGameAdapter(), gameRoot, host.Logger)).Succeeded);
        Assert.Equal("version one", File.ReadAllText(target));
        // The provider reports a newer version, which is what makes the page offer "更新".
        var summary = new NexusModSummary
        {
            ModId = 4242,
            Name = "Online Mod",
            Version = "2.0",
            ModManagerDownloadAllowed = true,
        };

        var plan = OnlineModActionPlan.Evaluate(summary, host.Library.ModsForGame("testgame")[0], authenticated: true);
        Assert.Equal(OnlineModLocalState.UpdateAvailable, plan.LocalState);
        Assert.True(plan.CanInstall);

        // The flow the page performs: uninstall the old mod, then install the new archive.
        Assert.True((await host.Install
            .UninstallAsync("testgame", v1.Metadata.Id, new TestGameAdapter(), gameRoot, false, host.Logger)).Succeeded);

        var v2 = await StageNexusAsync(host, tree, "v2.zip", "version two");
        Assert.True((await host.Install.InstallAsync(Confirm(v2), new TestGameAdapter(), gameRoot, host.Logger)).Succeeded);

        Assert.Equal("version two", File.ReadAllText(target));
        Assert.Single(host.Library.ModsForGame("testgame"));
        Assert.Equal(v2.Metadata.Id, host.Library.ModsForGame("testgame")[0].Id);
    }

    /// <summary>Uninstalling through the online page removes only that mod's files.</summary>
    [Fact]
    public async Task UninstallRemovesOnlyTheSelectedMod()
    {
        using var tree = new TemporaryTree();
        var host = TestHost.Create(tree);
        using var _ = host;

        var gameRoot = host.CreateGameRoot();
        host.PathSafety.ApproveGameRoot("testgame", gameRoot, Path.Combine(gameRoot, "Mods"));

        var first = await host.Install.StageAsync(
            new InstallRequest
            {
                ArchivePath = TestArchives.CreateZip(tree, "one.zip", ("Mods/data/one.txt", "one")),
                GameId = "testgame",
                AcceptLowConfidence = true,
                NexusModId = 1,
            },
            new TestGameAdapter(),
            host.Logger);

        var second = await host.Install.StageAsync(
            new InstallRequest
            {
                ArchivePath = TestArchives.CreateZip(tree, "two.zip", ("Mods/data/two.txt", "two")),
                GameId = "testgame",
                AcceptLowConfidence = true,
                NexusModId = 2,
            },
            new TestGameAdapter(),
            host.Logger);

        Assert.True((await host.Install.InstallAsync(Confirm(first.Value!), new TestGameAdapter(), gameRoot, host.Logger)).Succeeded);
        Assert.True((await host.Install.InstallAsync(Confirm(second.Value!), new TestGameAdapter(), gameRoot, host.Logger)).Succeeded);

        var removed = await host.Install.UninstallAsync(
            "testgame",
            first.Value!.Metadata.Id,
            new TestGameAdapter(),
            gameRoot,
            false,
            host.Logger);

        Assert.True(removed.Succeeded, removed.Error);
        Assert.False(File.Exists(Path.Combine(gameRoot, "Mods", "data", "one.txt")));
        Assert.True(File.Exists(Path.Combine(gameRoot, "Mods", "data", "two.txt")));
        Assert.Single(host.Library.ModsForGame("testgame"));
    }

    /// <summary>The download model reports resumability, progress and ETA as the page expects.</summary>
    [Fact]
    public void DownloadRecordReportsResumabilityAndProgress()
    {
        var partial = new DownloadRecord
        {
            Id = "d1",
            FileName = "mod.zip",
            Url = "https://example.invalid/mod.zip",
            TargetPath = Path.Combine("artifacts", "test-mods", "mod.zip"),
            PartialPath = Path.Combine("artifacts", "test-mods", "mod.zip.part"),
            State = DownloadState.Failed,
            TotalBytes = 1000,
            BytesDownloaded = 250,
            BytesPerSecond = 250,
            Segments = [new ByteRange(0, 249)],
        };

        Assert.True(partial.CanResume);
        Assert.Equal(0.25d, partial.Fraction!.Value, 3);
        Assert.Equal(3d, partial.Eta!.Value.TotalSeconds, 1);

        var completed = partial with { State = DownloadState.Completed };
        Assert.False(completed.CanResume);
        Assert.False(completed.IsActive);
    }

    /// <summary>Settings survive a restart, including the Nexus section.</summary>
    [Fact]
    public void NexusSettingsSurviveARestart()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));
        paths.EnsureCreated();

        var first = new LibraryRepository(new JsonDocumentStore(paths), paths);
        var settings = first.Load();
        var saved = settings with
        {
            Nexus = settings.Nexus with
            {
                AuthMode = NexusAuthMode.ApiKey,
                HasApiKey = true,
                UserName = "tester",
                IsPremium = true,
            },
            Ui = settings.Ui with { StartupPageName = "在线 Mod" },
        };

        first.Save(saved);

        var second = new LibraryRepository(new JsonDocumentStore(paths), paths);
        var reloaded = second.Load();

        Assert.Equal(NexusAuthMode.ApiKey, reloaded.Nexus.AuthMode);
        Assert.True(reloaded.Nexus.HasApiKey);
        Assert.Equal("tester", reloaded.Nexus.UserName);
        Assert.True(reloaded.Nexus.IsPremium);
        Assert.Equal("在线 Mod", reloaded.Ui.StartupPageName);
    }

    /// <summary>Three mods claiming one path produce one conflict naming every provider.</summary>
    [Fact]
    public async Task ThreeProvidersAreAllListedInTheConflict()
    {
        using var tree = new TemporaryTree();
        var host = TestHost.Create(tree);
        using var _ = host;

        var gameRoot = host.CreateGameRoot();
        host.PathSafety.ApproveGameRoot("testgame", gameRoot, Path.Combine(gameRoot, "Mods"));

        foreach (var name in new[] { "a", "b", "c" })
        {
            var staged = await host.Install.StageAsync(
                new InstallRequest
                {
                    ArchivePath = TestArchives.CreateZip(tree, $"{name}.zip", ("Mods/shared.dll", name)),
                    GameId = "testgame",
                    AcceptLowConfidence = true,
                },
                new TestGameAdapter(),
                host.Logger);

            Assert.True((await host.Install.InstallAsync(Confirm(staged.Value!), new TestGameAdapter(), gameRoot, host.Logger)).Succeeded);
        }

        var mods = host.Library.ModsForGame("testgame");
        var modSet = mods
            .Select(mod => new ModSetEntry(mod, host.Library.GetManifest("testgame", mod.Id), mod.StagingPath))
            .ToList();

        var conflict = host.Conflicts.Analyze("testgame", modSet, []).Single();

        Assert.Equal("shared.dll", conflict.RelativeTarget);
        Assert.Equal(2, conflict.OverriddenModIds.Count);
        Assert.All(mods, mod => Assert.True(
            conflict.WinnerModId == mod.Id || conflict.OverriddenModIds.Contains(mod.Id),
            $"Mod {mod.Id} 未出现在冲突的提供者列表中"));
        Assert.Equal("c", File.ReadAllText(Path.Combine(gameRoot, "Mods", "shared.dll")));
    }

    /// <summary>Stages an archive as a Nexus mod.</summary>
    /// <param name="host">Test host.</param>
    /// <param name="tree">Temporary tree.</param>
    /// <param name="name">Archive name.</param>
    /// <param name="content">File content.</param>
    /// <param name="version">Version the source published.</param>
    private static async Task<StagedMod> StageNexusAsync(
        TestHost host,
        TemporaryTree tree,
        string name,
        string content,
        string version = "1.0")
    {
        var staged = await host.Install.StageAsync(
            new InstallRequest
            {
                ArchivePath = TestArchives.CreateZip(tree, name, ("Mods/data/shared.txt", content)),
                GameId = "testgame",
                AcceptLowConfidence = true,
                NexusModId = 4242,
                Version = version,
            },
            new TestGameAdapter(),
            host.Logger);

        Assert.True(staged.Succeeded, staged.Error);
        return staged.Value!;
    }
}
