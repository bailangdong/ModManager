using System.IO;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// A mod may legitimately ship a plugin, a config file and a patcher side by side. BepInEx reads
/// those from three different sibling trees, so a single-root plan would silently put the config
/// where the loader never looks. These tests pin the declared-rule path, not a name heuristic.
/// </summary>
public sealed class MultiTargetPlanningTests
{
    [Fact]
    public async Task BepInExPackagePlansPluginsConfigAndPatchersIntoSeparateTrees()
    {
        using var host = TestHost.Create();
        var gameRoot = host.CreateGameRoot();
        Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
        Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "config"));
        Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "patchers"));
        host.PathSafety.ApproveGameRoot(
            "bepinex-test",
            gameRoot,
            Path.Combine(gameRoot, "BepInEx"));

        var staging = host.Tree.Dir("staging-multitarget");
        Directory.CreateDirectory(Path.Combine(staging, "plugins"));
        Directory.CreateDirectory(Path.Combine(staging, "config"));
        Directory.CreateDirectory(Path.Combine(staging, "patchers"));
        File.WriteAllText(Path.Combine(staging, "plugins", "Example.dll"), "plugin");
        File.WriteAllText(Path.Combine(staging, "config", "Example.json"), "{}");
        File.WriteAllText(Path.Combine(staging, "patchers", "Example.Patcher.dll"), "patcher");
        File.WriteAllText(Path.Combine(staging, "README.md"), "docs");

        var mod = new ModMetadata
        {
            Id = "Example",
            Name = "Example",
            GameId = "bepinex-test",
            StagingPath = staging,
        };
        var profile = new Profile
        {
            Id = "default",
            Name = "Default",
            GameId = "bepinex-test",
            ModOrder = ["Example"],
        };
        var definition = new GameDefinition
        {
            Id = "bepinex-test",
            Name = "BepInEx Test",
            ModRoot = "BepInEx",
            InstallTargets =
            [
                new("BepInEx/plugins", "BepInEx/plugins"),
                new("BepInEx/config", "BepInEx/config"),
                new("BepInEx/patchers", "BepInEx/patchers"),
                new("plugins", "BepInEx/plugins"),
                new("config", "BepInEx/config"),
                new("patchers", "BepInEx/patchers"),
            ],
        };

        var modSet = new[] { new ModSetEntry(mod, null, staging) };
        var plan = host.Planner.BuildPlan(
            definition,
            gameRoot,
            profile,
            modSet,
            []);

        Assert.Equal(Path.Combine(gameRoot, "BepInEx"), plan.DeploymentRoot);
        Assert.Contains(plan.Files, file => file.RelativeTarget == "plugins/Example.dll");
        Assert.Contains(plan.Files, file => file.RelativeTarget == "config/Example.json");
        Assert.Contains(plan.Files, file => file.RelativeTarget == "patchers/Example.Patcher.dll");
        Assert.DoesNotContain(plan.Files, file => file.RelativeTarget.Contains("README", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(plan.Operations.OfType<PlaceFileOperation>(),
            op => op.TargetPath == Path.Combine(gameRoot, "BepInEx", "plugins", "Example.dll"));
        Assert.Contains(plan.Operations.OfType<PlaceFileOperation>(),
            op => op.TargetPath == Path.Combine(gameRoot, "BepInEx", "config", "Example.json"));
        Assert.Contains(plan.Operations.OfType<PlaceFileOperation>(),
            op => op.TargetPath == Path.Combine(gameRoot, "BepInEx", "patchers", "Example.Patcher.dll"));

        var applied = await host.Applier.ApplyAsync(plan, definition, modSet, host.Settings.Deployment, host.Logger);
        Assert.True(applied.Succeeded, applied.Error);
        Assert.True(File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "Example.dll")));
        Assert.True(File.Exists(Path.Combine(gameRoot, "BepInEx", "config", "Example.json")));
        Assert.True(File.Exists(Path.Combine(gameRoot, "BepInEx", "patchers", "Example.Patcher.dll")));
        Assert.False(File.Exists(Path.Combine(gameRoot, "BepInEx", "README.md")));

        var undone = await host.Applier.UndeployAsync(
            applied.Manifest!,
            definition,
            host.Settings.Deployment,
            host.Logger);
        Assert.True(undone.Succeeded, undone.Error);
        Assert.False(File.Exists(Path.Combine(gameRoot, "BepInEx", "plugins", "Example.dll")));
        Assert.False(File.Exists(Path.Combine(gameRoot, "BepInEx", "config", "Example.json")));
        Assert.False(File.Exists(Path.Combine(gameRoot, "BepInEx", "patchers", "Example.Patcher.dll")));
    }

    [Fact]
    public void TwoModsClaimingTheSameAbsolutePathReportBothProviders()
    {
        using var host = TestHost.Create();
        var gameRoot = host.CreateGameRoot();
        host.PathSafety.ApproveGameRoot("conflict-test", gameRoot, Path.Combine(gameRoot, "Mods"));

        var stagingA = host.Tree.Dir("conflict-a");
        var stagingB = host.Tree.Dir("conflict-b");
        Directory.CreateDirectory(Path.Combine(stagingA, "plugins"));
        Directory.CreateDirectory(Path.Combine(stagingB, "plugins"));
        File.WriteAllText(Path.Combine(stagingA, "plugins", "same.dll"), "A");
        File.WriteAllText(Path.Combine(stagingB, "plugins", "same.dll"), "B");

        var modA = new ModMetadata { Id = "A", Name = "A", GameId = "conflict-test", StagingPath = stagingA };
        var modB = new ModMetadata { Id = "B", Name = "B", GameId = "conflict-test", StagingPath = stagingB };
        var profile = new Profile
        {
            Id = "default",
            Name = "Default",
            GameId = "conflict-test",
            ModOrder = ["A", "B"],
        };
        var definition = new GameDefinition { Id = "conflict-test", Name = "Conflict Test", ModRoot = "Mods" };

        var plan = host.Planner.BuildPlan(
            definition,
            gameRoot,
            profile,
            [new ModSetEntry(modA, null, stagingA), new ModSetEntry(modB, null, stagingB)],
            []);

        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal("plugins/same.dll", conflict.RelativeTarget);
        Assert.Equal(Path.Combine(gameRoot, "Mods", "plugins", "same.dll"), conflict.AbsoluteTarget);
        Assert.Equal("B", conflict.WinnerModId);
        Assert.Equal(2, conflict.Providers.Count);
        Assert.Contains(conflict.Providers, provider => provider.ModId == "A" && !provider.IsWinner);
        Assert.Contains(conflict.Providers, provider => provider.ModId == "B" && provider.IsWinner);
        Assert.All(conflict.Providers, provider => Assert.EndsWith("same.dll", provider.SourcePath, StringComparison.OrdinalIgnoreCase));
    }
}
