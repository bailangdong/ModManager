using System.IO;
using ModManager.Core;
using ModManager.Core.Persistence;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Load order and the real-world edge cases the v1.0 checklist names: reordering must change who
/// wins a contested file, and the engine must handle corrupt archives, duplicate content and
/// same-name mods without ever damaging the wrong files.
/// </summary>
public sealed class LoadOrderAndEdgeCaseTests
{
    private const string GameId = "testgame";

    private static async Task<StagedMod> StageAsync(TestHost host, string archivePath)
    {
        var result = await host.Install.StageAsync(
            new InstallRequest { ArchivePath = archivePath, GameId = GameId, AcceptLowConfidence = true },
            new TestGameAdapter(),
            host.Logger);

        Assert.True(result.Succeeded, result.Error ?? "staging failed");
        result.Value!.Metadata.RootConfirmedByUser = true;
        return result.Value!;
    }

    private static List<ModSetEntry> ModSet(TestHost host) =>
        host.Library.ModsForGame(GameId)
            .Select(mod => new ModSetEntry(mod, host.Library.GetManifest(GameId, mod.Id), mod.StagingPath))
            .ToList();

    /// <summary>Reordering the profile changes which mod supplies the contested file — on disk.</summary>
    [Fact]
    public async Task ReorderingChangesTheConflictWinnerOnDisk()
    {
        using var tree = new TemporaryTree();
        var host = TestHost.Create(tree);
        using var _ = host;

        var gameRoot = host.CreateGameRoot();
        host.PathSafety.ApproveGameRoot(GameId, gameRoot, Path.Combine(gameRoot, "Mods"));

        var first = await StageAsync(host, TestArchives.CreateZip(tree, "aaa.zip", ("Mods/data/shared.txt", "from-A")));
        var second = await StageAsync(host, TestArchives.CreateZip(tree, "bbb.zip", ("Mods/data/shared.txt", "from-B")));

        Assert.True((await host.Install.InstallAsync(first, new TestGameAdapter(), gameRoot, host.Logger)).Succeeded);
        Assert.True((await host.Install.InstallAsync(second, new TestGameAdapter(), gameRoot, host.Logger)).Succeeded);

        var target = Path.Combine(gameRoot, "Mods", "data", "shared.txt");
        Assert.Equal("from-B", File.ReadAllText(target));

        // Reorder: the first mod now loads last, so it must win the contested file.
        var profile = host.Library.GetProfile($"{GameId}-default")!;
        var reordered = profile with { ModOrder = [second.Metadata.Id, first.Metadata.Id] };
        host.Library.SaveProfile(reordered);

        var plan = host.Planner.BuildPlan(
            new TestGameAdapter().Definition,
            gameRoot,
            reordered,
            ModSet(host),
            []);

        // The conflict service resolves by list order, so the set must be ordered by the profile —
        // exactly as the app's Conflicts page does — or the reported winner would not be the deployed one.
        var orderedSet = reordered.ModOrder
            .Select(id => ModSet(host).First(entry => entry.Metadata.Id == id))
            .ToList();

        var conflicts = host.Conflicts.Analyze(GameId, orderedSet, []);
        Assert.Equal(first.Metadata.Id, conflicts.Single().WinnerModId);

        var result = await host.Applier.ApplyAsync(
            plan,
            new TestGameAdapter().Definition,
            ModSet(host),
            host.Settings.Deployment,
            host.Logger);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("from-A", File.ReadAllText(target));

        // Switch back: the second mod wins again, and the file must still exist afterwards — a
        // superseded file being removed must never be skipped over by an "already deployed" check.
        var restored = profile with { ModOrder = [first.Metadata.Id, second.Metadata.Id] };
        host.Library.SaveProfile(restored);

        var backPlan = host.Planner.BuildPlan(
            new TestGameAdapter().Definition,
            gameRoot,
            restored,
            ModSet(host),
            []);

        var backResult = await host.Applier.ApplyAsync(
            backPlan,
            new TestGameAdapter().Definition,
            ModSet(host),
            host.Settings.Deployment,
            host.Logger);

        Assert.True(backResult.Succeeded, backResult.Error);
        Assert.True(File.Exists(target), "The contested file must exist after switching the winner back.");
        Assert.Equal("from-B", File.ReadAllText(target));
    }

    /// <summary>A three-way conflict names every shadowed mod and the single winner.</summary>
    [Fact]
    public async Task ThreeWayConflictReportsEveryShadowedMod()
    {
        using var tree = new TemporaryTree();
        var host = TestHost.Create(tree);
        using var _ = host;

        var gameRoot = host.CreateGameRoot();
        host.PathSafety.ApproveGameRoot(GameId, gameRoot, Path.Combine(gameRoot, "Mods"));

        var a = await StageAsync(host, TestArchives.CreateZip(tree, "a.zip", ("Mods/shared.dll", "A")));
        var b = await StageAsync(host, TestArchives.CreateZip(tree, "b.zip", ("Mods/shared.dll", "B")));
        var c = await StageAsync(host, TestArchives.CreateZip(tree, "c.zip", ("Mods/shared.dll", "C")));

        foreach (var staged in new[] { a, b, c })
        {
            Assert.True((await host.Install.InstallAsync(staged, new TestGameAdapter(), gameRoot, host.Logger)).Succeeded);
        }

        var conflict = host.Conflicts.Analyze(GameId, ModSet(host), []).Single();

        Assert.Equal(c.Metadata.Id, conflict.WinnerModId);
        Assert.Equal(2, conflict.OverriddenModIds.Count);
        Assert.Contains(a.Metadata.Id, conflict.OverriddenModIds);
        Assert.Contains(b.Metadata.Id, conflict.OverriddenModIds);
        Assert.Equal("C", File.ReadAllText(Path.Combine(gameRoot, "Mods", "shared.dll")));
    }

    /// <summary>The same archive cannot be installed twice: the content hash is the duplicate guard.</summary>
    [Fact]
    public async Task DuplicateArchiveIsRejectedByContentHash()
    {
        using var tree = new TemporaryTree();
        var host = TestHost.Create(tree);
        using var _ = host;

        var archive = TestArchives.CreateZip(tree, "mod.zip", ("Mods/plugins/mod.dll", "bytes"));
        await StageAsync(host, archive);

        var second = await host.Install.StageAsync(
            new InstallRequest { ArchivePath = archive, GameId = GameId, AcceptLowConfidence = true },
            new TestGameAdapter(),
            host.Logger);

        Assert.False(second.Succeeded);
        Assert.Contains("already installed", second.Error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Two archives with the same name but different content are two distinct mods.</summary>
    [Fact]
    public async Task SameNameDifferentContentCreatesASeparateMod()
    {
        using var tree = new TemporaryTree();
        var host = TestHost.Create(tree);
        using var _ = host;

        var first = await StageAsync(host, TestArchives.CreateZip(tree, "v1.zip", ("SameMod/Mods/a.dll", "version-one")));
        var second = await StageAsync(host, TestArchives.CreateZip(tree, "v2.zip", ("SameMod/Mods/a.dll", "version-two")));

        Assert.NotEqual(first.Metadata.Id, second.Metadata.Id);
        Assert.Equal(2, host.Library.ModsForGame(GameId).Count);
    }

    /// <summary>A 7z file whose header is corrupt fails with a clear message instead of a crash.</summary>
    [Fact]
    public async Task CorruptSevenZipFailsWithAClearError()
    {
        using var tree = new TemporaryTree();
        var file = tree.WriteBytes("broken.7z", [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04, 0x11, 0x22, 0x33]);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => new ArchiveReader().IndexAsync(file));
        Assert.Contains("broken.7z", exception.Message);
    }

    /// <summary>Reinstalling a mod is journaled, idempotent, and leaves exactly one manifest row per file.</summary>
    [Fact]
    public async Task ReinstallKeepsTheManifestConsistent()
    {
        using var tree = new TemporaryTree();
        var host = TestHost.Create(tree);
        using var _ = host;

        var gameRoot = host.CreateGameRoot();
        host.PathSafety.ApproveGameRoot(GameId, gameRoot, Path.Combine(gameRoot, "Mods"));

        var staged = await StageAsync(host, TestArchives.CreateZip(tree, "mod.zip", ("Mods/plugins/mod.dll", "bytes")));
        Assert.True((await host.Install.InstallAsync(staged, new TestGameAdapter(), gameRoot, host.Logger)).Succeeded);

        var again = await host.Install.InstallAsync(staged, new TestGameAdapter(), gameRoot, host.Logger);
        Assert.True(again.Succeeded, again.Error);

        var manifest = host.Library.GetManifest(GameId, staged.Metadata.Id);
        Assert.NotNull(manifest);
        Assert.Single(manifest!.Files);
        Assert.True(File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")));
        Assert.Empty(host.Journals.FindInterrupted());
    }

    /// <summary>A profile's load order survives a restart.</summary>
    [Fact]
    public void LoadOrderPersistsAcrossRestart()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));
        paths.EnsureCreated();

        var first = new LibraryRepository(new JsonDocumentStore(paths), paths);
        first.SaveProfile(new Profile
        {
            Id = "default",
            Name = "Default",
            GameId = GameId,
            IsActive = true,
            ModOrder = ["mod-b", "mod-a"],
        });

        var second = new LibraryRepository(new JsonDocumentStore(paths), paths);
        Assert.Equal(new[] { "mod-b", "mod-a" }, second.GetProfile("default")!.ModOrder);
    }
}
