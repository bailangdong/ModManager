using System.IO;
using ModManager.Core;
using ModManager.Core.Persistence;
using ModManager.Core.Safety;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Restart simulation: the full pipeline's state — mods, manifests, profiles, enable/disable state —
/// must survive a brand-new object graph over the same files, which is the app's real persistence
/// story rather than a memory-state story.
/// </summary>
public sealed class RestartPersistenceTests
{
    private const string GameId = "testgame";

    private static async Task<StagedMod> StageAsync(TestHost host, string archivePath)
    {
        var request = new InstallRequest
        {
            ArchivePath = archivePath,
            GameId = GameId,
            AcceptLowConfidence = true,
        };

        var result = await host.Install.StageAsync(request, new TestGameAdapter(), host.Logger);
        Assert.True(result.Succeeded, result.Error ?? "staging failed");
        result.Value!.Metadata.RootConfirmedByUser = true;
        return result.Value!;
    }

    private static async Task<DeployResult> RedeployAsync(TestHost host, string gameRoot, Profile profile)
    {
        var adapter = new TestGameAdapter();
        var modSet = new List<ModSetEntry>();

        foreach (var mod in host.Library.ModsForGame(GameId))
        {
            modSet.Add(new ModSetEntry(mod, host.Library.GetManifest(GameId, mod.Id), mod.StagingPath));
        }

        var plan = host.Planner.BuildPlan(
            adapter.Definition,
            gameRoot,
            profile,
            modSet,
            host.Library.ConflictRulesForGame(GameId));

        return await host.Applier.ApplyAsync(plan, adapter.Definition, modSet, host.Settings.Deployment, host.Logger);
    }

    /// <summary>
    /// An installed, then disabled mod stays in that exact state across two restarts: the files stay
    /// removed while disabled, and the library, manifest, and profile all survive.
    /// </summary>
    [Fact]
    public async Task InstalledAndDisabledStateSurvivesRestarts()
    {
        using var tree = new TemporaryTree();
        var gameRoot = Path.Combine(tree.Root, "game");
        Directory.CreateDirectory(Path.Combine(gameRoot, "Mods"));

        var archive = TestArchives.CreateZip(tree, "mod.zip", ("Mods/plugins/mod.dll", "dll-bytes"));

        // --- first session: install ------------------------------------------------
        using (var host = TestHost.Create(tree))
        {
            host.PathSafety.ApproveGameRoot(GameId, gameRoot, Path.Combine(gameRoot, "Mods"));
            var staged = await StageAsync(host, archive);
            var install = await host.Install.InstallAsync(staged, new TestGameAdapter(), gameRoot, host.Logger);
            Assert.True(install.Succeeded, install.Error);
            Assert.True(File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")));
        }

        // --- restart 1: everything is still there ----------------------------------
        using (var host = TestHost.Create(tree))
        {
            host.PathSafety.ApproveGameRoot(GameId, gameRoot, Path.Combine(gameRoot, "Mods"));

            var mod = host.Library.ModsForGame(GameId).Single();
            Assert.Equal(ModState.Deployed, mod.State);
            Assert.Equal("dll-bytes", File.ReadAllText(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")));

            var manifest = host.Library.GetManifest(GameId, mod.Id);
            Assert.NotNull(manifest);
            Assert.Single(manifest!.Files);

            var profile = host.Library.GetProfile($"{GameId}-default");
            Assert.NotNull(profile);
            Assert.Contains(mod.Id, profile!.ModOrder);

            // Disable for real.
            var disabled = profile with { DisabledModIds = [mod.Id] };
            host.Library.SaveProfile(disabled);
            var result = await RedeployAsync(host, gameRoot, disabled);
            Assert.True(result.Succeeded, result.Error);
            Assert.False(File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")));
        }

        // --- restart 2: the disable survived, and enabling restores the file --------
        using (var host = TestHost.Create(tree))
        {
            host.PathSafety.ApproveGameRoot(GameId, gameRoot, Path.Combine(gameRoot, "Mods"));

            var mod = host.Library.ModsForGame(GameId).Single();
            var profile = host.Library.GetProfile($"{GameId}-default");
            Assert.NotNull(profile);
            Assert.Contains(mod.Id, profile!.DisabledModIds);
            Assert.False(File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")));

            var enabled = profile with { DisabledModIds = [] };
            host.Library.SaveProfile(enabled);
            var result = await RedeployAsync(host, gameRoot, enabled);
            Assert.True(result.Succeeded, result.Error);
            Assert.True(File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")));
        }
    }

    /// <summary>Uninstalling in one session leaves nothing for the next session to find.</summary>
    [Fact]
    public async Task UninstallStateSurvivesRestart()
    {
        using var tree = new TemporaryTree();
        var gameRoot = Path.Combine(tree.Root, "game");
        Directory.CreateDirectory(Path.Combine(gameRoot, "Mods"));

        var archive = TestArchives.CreateZip(tree, "mod.zip", ("Mods/plugins/mod.dll", "dll-bytes"));

        using (var host = TestHost.Create(tree))
        {
            host.PathSafety.ApproveGameRoot(GameId, gameRoot, Path.Combine(gameRoot, "Mods"));
            var staged = await StageAsync(host, archive);
            Assert.True((await host.Install.InstallAsync(staged, new TestGameAdapter(), gameRoot, host.Logger)).Succeeded);

            var uninstall = await host.Install.UninstallAsync(
                GameId,
                staged.Metadata.Id,
                new TestGameAdapter(),
                gameRoot,
                deleteModifiedFiles: false,
                host.Logger);
            Assert.True(uninstall.Succeeded, uninstall.Error);
            Assert.False(File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")));
        }

        using (var host = TestHost.Create(tree))
        {
            Assert.Empty(host.Library.ModsForGame(GameId));
            Assert.False(File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")));
        }
    }

    /// <summary>Every metadata field the import writes survives a restart byte for byte.</summary>
    [Fact]
    public void ModMetadataSurvivesRestart()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));
        paths.EnsureCreated();

        var metadata = new ModMetadata
        {
            Id = "mod-1",
            Name = "Example Mod",
            Author = "Some Author",
            Version = "1.2.3",
            GameId = GameId,
            Source = ModSource.LocalFile,
            ArchivePath = @"C:\downloads\example.zip",
            ArchiveHash = "aabbccddeeff00112233445566778899",
            StagingPath = tree.Dir("staging"),
            StagedFileCount = 24,
            StagedSizeBytes = 18_400_000,
            InstalledAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        };

        var first = new LibraryRepository(new JsonDocumentStore(paths), paths);
        first.Upsert(metadata);

        var second = new LibraryRepository(new JsonDocumentStore(paths), paths);
        var loaded = second.Mod("mod-1");

        Assert.NotNull(loaded);
        Assert.Equal("Example Mod", loaded!.Name);
        Assert.Equal("Some Author", loaded.Author);
        Assert.Equal("1.2.3", loaded.Version);
        Assert.Equal("aabbccddeeff00112233445566778899", loaded.ArchiveHash);
        Assert.Equal(24, loaded.StagedFileCount);
        Assert.Equal(18_400_000, loaded.StagedSizeBytes);
        Assert.Equal(metadata.InstalledAt, loaded.InstalledAt);
    }
}
