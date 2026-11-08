using System.IO;
using ModManager.Core;
using ModManager.Core.Persistence;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Library persistence, dependency checking and conflict detection — the bookkeeping the generic
/// pipeline leans on, each proven against a fresh temporary tree.
/// </summary>
public sealed class LibraryDependencyConflictTests
{
    private const string GameId = "testgame";

    private static ModMetadata NewMod(string id, string name, string stagingPath, IReadOnlyList<string>? dependencies = null) => new()
    {
        Id = id,
        Name = name,
        GameId = GameId,
        StagingPath = stagingPath,
        Dependencies = dependencies ?? [],
    };

    // ---------------------------------------------------------------- Library

    /// <summary>Add, query, update and delete a library record, and prove it survives a restart.</summary>
    [Fact]
    public void LibraryRoundTripsAcrossRepositoryInstances()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));
        paths.EnsureCreated();
        var staging = tree.Dir("staging");

        var first = new LibraryRepository(new JsonDocumentStore(paths), paths);
        first.Upsert(NewMod("mod-1", "First Name", staging, ["mod-2"]));

        // "Restart": a brand-new repository over the same directory must see the same data.
        var second = new LibraryRepository(new JsonDocumentStore(paths), paths);
        var loaded = second.ModsForGame(GameId).Single();
        Assert.Equal("First Name", loaded.Name);
        Assert.Equal(new[] { "mod-2" }, loaded.Dependencies);

        // Update.
        second.Upsert(NewMod("mod-1", "Updated Name", staging));
        Assert.Equal("Updated Name", second.Mod("mod-1")!.Name);

        // Delete.
        IModRepository repository = second;
        Assert.True(repository.Remove("mod-1"));
        Assert.Null(second.Mod("mod-1"));
        Assert.Empty(second.ModsForGame(GameId));
    }

    /// <summary>Manifests and profiles persist alongside the mod records.</summary>
    [Fact]
    public void ManifestsAndProfilesPersist()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));
        paths.EnsureCreated();

        var repository = new LibraryRepository(new JsonDocumentStore(paths), paths);
        repository.SaveManifest(new ModManifest
        {
            ModId = "mod-1",
            Name = "Test Mod",
            GameId = GameId,
            ProfileId = "default",
            DeploymentRoot = "Mods",
            Files =
            [
                new DeployedFile
                {
                    RelativeTarget = "plugins/a.dll",
                    Sha256 = "abc",
                    ModId = "mod-1",
                },
            ],
        });

        repository.SaveProfile(new Profile
        {
            Id = "default",
            Name = "Default",
            GameId = GameId,
            IsActive = true,
            ModOrder = ["mod-1"],
        });

        var reloaded = new LibraryRepository(new JsonDocumentStore(paths), paths);
        var manifest = reloaded.GetManifest(GameId, "mod-1");
        Assert.NotNull(manifest);
        Assert.Single(manifest!.Files);

        var profile = reloaded.GetProfile("default");
        Assert.NotNull(profile);
        Assert.Equal(new[] { "mod-1" }, profile!.ModOrder);

        Assert.True(reloaded.RemoveManifest(GameId, "mod-1"));
        Assert.Null(reloaded.GetManifest(GameId, "mod-1"));
    }

    // ---------------------------------------------------------------- Dependencies

    /// <summary>A mod requiring another mod reports the missing dependency.</summary>
    [Fact]
    public void FindMissingReportsARequiredModThatIsNotInstalled()
    {
        var adapter = new TestGameAdapter();
        var dependencies = new DependencyService();
        var mod = NewMod("mod-a", "Mod A", "staging", ["mod-b"]);

        var missing = dependencies.FindMissing(adapter, "unused", [mod]);

        var report = missing.Single();
        Assert.Equal("mod-a", report.ModId);
        Assert.Equal("mod-b", report.Requirement);
        Assert.False(report.IsLoader);
    }

    /// <summary>When the required mod is installed (by id or by name), nothing is reported.</summary>
    [Fact]
    public void FindMissingReportsNothingWhenTheDependencyIsSatisfied()
    {
        var adapter = new TestGameAdapter();
        var dependencies = new DependencyService();
        var mods = new[]
        {
            NewMod("mod-a", "Mod A", "staging", ["mod-b"]),
            NewMod("mod-b", "Mod B", "staging"),
        };

        Assert.Empty(dependencies.FindMissing(adapter, "unused", mods));
    }

    // ---------------------------------------------------------------- Conflicts

    private static ModSetEntry Entry(string id, string name, string stagingRoot) =>
        new(NewMod(id, name, stagingRoot), null, stagingRoot);

    /// <summary>Two mods providing the same file produce one conflict naming both, with the later mod winning.</summary>
    [Fact]
    public void ConflictIsDetectedWhenTwoModsProvideTheSameFile()
    {
        using var tree = new TemporaryTree();
        var a = Entry("mod-a", "Mod A", tree.Dir("staging-a"));
        var b = Entry("mod-b", "Mod B", tree.Dir("staging-b"));
        tree.WriteFile("staging-a/data/shared.json", "{\"from\":\"a\"}");
        tree.WriteFile("staging-b/data/shared.json", "{\"from\":\"b\"}");

        var conflicts = new ConflictService().Analyze(GameId, [a, b], []);

        var conflict = conflicts.Single();
        Assert.Equal("data/shared.json", conflict.RelativeTarget);
        Assert.Equal("mod-b", conflict.WinnerModId);
        Assert.Contains("mod-a", conflict.OverriddenModIds);

        var explanation = new ConflictService().Explain(GameId, "data/shared.json", [a, b]);
        Assert.Equal("mod-b", explanation.WinnerModId);
        Assert.Equal(2, explanation.Steps.Count);
    }

    /// <summary>Disjoint file sets produce no conflicts at all.</summary>
    [Fact]
    public void NoConflictIsReportedForDisjointFiles()
    {
        using var tree = new TemporaryTree();
        var a = Entry("mod-a", "Mod A", tree.Dir("staging-a"));
        var b = Entry("mod-b", "Mod B", tree.Dir("staging-b"));
        tree.WriteFile("staging-a/data/a.json", "{}");
        tree.WriteFile("staging-b/data/b.json", "{}");

        Assert.Empty(new ConflictService().Analyze(GameId, [a, b], []));
    }

    /// <summary>An explicit per-path rule pins the winner regardless of order.</summary>
    [Fact]
    public void ConflictRulePinsTheWinner()
    {
        using var tree = new TemporaryTree();
        var a = Entry("mod-a", "Mod A", tree.Dir("staging-a"));
        var b = Entry("mod-b", "Mod B", tree.Dir("staging-b"));
        tree.WriteFile("staging-a/data/shared.json", "{}");
        tree.WriteFile("staging-b/data/shared.json", "{}");

        var rule = new ConflictResolution("data/shared.json", ConflictResolutionKind.PreferMod, "mod-a");
        var conflicts = new ConflictService().Analyze(GameId, [a, b], [rule]);

        var conflict = conflicts.Single();
        Assert.Equal("mod-a", conflict.WinnerModId);
        Assert.Equal(ConflictResolutionKind.PreferMod, conflict.Resolution);
    }
}
