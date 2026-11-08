using System.IO;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Real <see cref="DeploymentApplier"/> concurrency, not just lock-object semantics. Both appliers
/// run the complete journal → backup → verify → manifest path over one synthetic game root.
/// </summary>
public sealed class DeploymentConcurrencyTests
{
    private const string GameId = "testgame";

    [Fact]
    public async Task TwoAppliersOnTheSameTargetLeaveOneCompleteFileAndTwoCommittedJournals()
    {
        using var host = TestHost.Create();
        var gameRoot = host.CreateGameRoot();
        host.PathSafety.ApproveGameRoot(GameId, gameRoot, Path.Combine(gameRoot, "Mods"));

        var stagedA = await StageAsync(host, "A", "AAAA");
        var stagedB = await StageAsync(host, "B", "BBBB");

        var planA = BuildPlan(host, gameRoot, stagedA);
        var planB = BuildPlan(host, gameRoot, stagedB);
        var target = planA.Operations.OfType<PlaceFileOperation>().Single().TargetPath;
        Assert.Equal(target, planB.Operations.OfType<PlaceFileOperation>().Single().TargetPath);

        var gate = new InstallationGate(crossProcess: false);
        var applierA = new DeploymentApplier(host.PathSafety, host.Journals, host.Paths, host.Library, gate);
        var applierB = new DeploymentApplier(host.PathSafety, host.Journals, host.Paths, host.Library, gate);
        var definition = new TestGameAdapter().Definition;

        var results = await Task.WhenAll(
            applierA.ApplyAsync(planA, definition, [Entry(stagedA, host)], host.Settings.Deployment, host.Logger),
            applierB.ApplyAsync(planB, definition, [Entry(stagedB, host)], host.Settings.Deployment, host.Logger));

        Assert.All(results, result => Assert.True(result.Succeeded, result.Error));
        var content = await File.ReadAllTextAsync(target);
        Assert.True(content is "AAAA" or "BBBB", $"Partial or unknown content: '{content}'.");

        Assert.Empty(host.Journals.FindInterrupted());
        Assert.All(host.Journals.Inspect(), inspection => Assert.False(inspection.Interrupted));

        var manifestA = host.Library.GetManifest(GameId, stagedA.Metadata.Id);
        var manifestB = host.Library.GetManifest(GameId, stagedB.Metadata.Id);
        Assert.NotNull(manifestA);
        Assert.NotNull(manifestB);

        // The second applier must record the first as the displaced owner; otherwise uninstalling it
        // would restore the wrong file. Which one is second depends on scheduling.
        var displaced = manifestA.Files.Single().DisplacedModId == stagedB.Metadata.Id
            || manifestB.Files.Single().DisplacedModId == stagedA.Metadata.Id;
        Assert.True(displaced, "Neither manifest recorded the other mod as the displaced owner.");
    }

    [Fact]
    public async Task TwoDifferentGamesDeployAtTheSameTime()
    {
        using var first = TestHost.Create();
        using var second = TestHost.Create();
        var firstRoot = first.CreateGameRoot();
        var secondRoot = second.CreateGameRoot();
        first.PathSafety.ApproveGameRoot(GameId, firstRoot, Path.Combine(firstRoot, "Mods"));
        second.PathSafety.ApproveGameRoot(GameId, secondRoot, Path.Combine(secondRoot, "Mods"));

        var firstStage = await StageAsync(first, "A", "AAAA");
        var secondStage = await StageAsync(second, "B", "BBBB");
        var definition = new TestGameAdapter().Definition;

        var results = await Task.WhenAll(
            first.Applier.ApplyAsync(
                BuildPlan(first, firstRoot, firstStage), definition, [Entry(firstStage, first)],
                first.Settings.Deployment, first.Logger),
            second.Applier.ApplyAsync(
                BuildPlan(second, secondRoot, secondStage), definition, [Entry(secondStage, second)],
                second.Settings.Deployment, second.Logger));

        Assert.All(results, result => Assert.True(result.Succeeded, result.Error));
        Assert.True(File.Exists(Path.Combine(firstRoot, "Mods", "plugins", "shared.dll")));
        Assert.True(File.Exists(Path.Combine(secondRoot, "Mods", "plugins", "shared.dll")));
    }

    [Fact]
    public async Task TwoAppliersApplyingTheSameModEndWithOneConsistentManifest()
    {
        using var host = TestHost.Create();
        var gameRoot = host.CreateGameRoot();
        host.PathSafety.ApproveGameRoot(GameId, gameRoot, Path.Combine(gameRoot, "Mods"));

        var staged = await StageAsync(host, "A", "AAAA");
        var plan = BuildPlan(host, gameRoot, staged);
        var gate = new InstallationGate(crossProcess: false);
        var applierA = new DeploymentApplier(host.PathSafety, host.Journals, host.Paths, host.Library, gate);
        var applierB = new DeploymentApplier(host.PathSafety, host.Journals, host.Paths, host.Library, gate);
        var definition = new TestGameAdapter().Definition;
        var entry = Entry(staged, host);

        var results = await Task.WhenAll(
            applierA.ApplyAsync(plan, definition, [entry], host.Settings.Deployment, host.Logger),
            applierB.ApplyAsync(plan, definition, [entry], host.Settings.Deployment, host.Logger));

        Assert.All(results, result => Assert.True(result.Succeeded, result.Error));
        var target = Path.Combine(gameRoot, "Mods", "plugins", "shared.dll");
        Assert.Equal("AAAA", await File.ReadAllTextAsync(target));
        var manifest = host.Library.GetManifest(GameId, staged.Metadata.Id);
        Assert.NotNull(manifest);
        Assert.Single(manifest.Files);
        Assert.Empty(host.Journals.FindInterrupted());
    }

    [Fact]
    public async Task ApplyAndUndeployOnTheSameTargetNeverDeleteTheSurvivingMod()
    {
        using var host = TestHost.Create();
        var gameRoot = host.CreateGameRoot();
        host.PathSafety.ApproveGameRoot(GameId, gameRoot, Path.Combine(gameRoot, "Mods"));

        var stagedA = await StageAsync(host, "A", "AAAA");
        var stagedB = await StageAsync(host, "B", "BBBB");
        var definition = new TestGameAdapter().Definition;
        var gate = new InstallationGate(crossProcess: false);
        var applier = new DeploymentApplier(host.PathSafety, host.Journals, host.Paths, host.Library, gate);

        var installA = await applier.ApplyAsync(
            BuildPlan(host, gameRoot, stagedA), definition, [Entry(stagedA, host)],
            host.Settings.Deployment, host.Logger);
        Assert.True(installA.Succeeded, installA.Error);
        Assert.NotNull(installA.Manifest);

        var installB = applier.ApplyAsync(
            BuildPlan(host, gameRoot, stagedB), definition, [Entry(stagedB, host)],
            host.Settings.Deployment, host.Logger);
        var uninstallA = applier.UndeployAsync(
            installA.Manifest!, definition, host.Settings.Deployment, host.Logger);

        var results = await Task.WhenAll(installB, uninstallA);
        Assert.True(results[0].Succeeded, results[0].Error);
        Assert.True(results[1].Succeeded, results[1].Error);

        var target = Path.Combine(gameRoot, "Mods", "plugins", "shared.dll");
        Assert.Equal("BBBB", await File.ReadAllTextAsync(target));
        Assert.Empty(host.Journals.FindInterrupted());
        Assert.NotNull(host.Library.GetManifest(GameId, stagedB.Metadata.Id));
    }

    [Fact]
    public async Task BatchUninstallRemovesEachModAndKeepsAnUnrelatedFile()
    {
        using var host = TestHost.Create();
        var gameRoot = host.CreateGameRoot();
        host.PathSafety.ApproveGameRoot(GameId, gameRoot, Path.Combine(gameRoot, "Mods"));

        var unrelated = Path.Combine(gameRoot, "Mods", "plugins", "keep-me.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        await File.WriteAllTextAsync(unrelated, "user file");

        var stagedA = await StageAsync(host, "A", "AAAA", "a.dll");
        var stagedB = await StageAsync(host, "B", "BBBB", "b.dll");
        var adapter = new TestGameAdapter();

        Assert.True((await host.Install.InstallAsync(stagedA, adapter, gameRoot, host.Logger)).Succeeded);
        Assert.True((await host.Install.InstallAsync(stagedB, adapter, gameRoot, host.Logger)).Succeeded);

        var uninstallA = await host.Install.UninstallAsync(
            GameId, stagedA.Metadata.Id, adapter, gameRoot, deleteModifiedFiles: false, host.Logger);
        var uninstallB = await host.Install.UninstallAsync(
            GameId, stagedB.Metadata.Id, adapter, gameRoot, deleteModifiedFiles: false, host.Logger);

        Assert.True(uninstallA.Succeeded, uninstallA.Error);
        Assert.True(uninstallB.Succeeded, uninstallB.Error);
        Assert.False(File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "a.dll")));
        Assert.False(File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "b.dll")));
        Assert.Equal("user file", await File.ReadAllTextAsync(unrelated));

        // A second uninstall has no manifest to act on. It must not fall back to deleting whatever
        // happens to be at the old path.
        var repeated = await host.Install.UninstallAsync(
            GameId, stagedA.Metadata.Id, adapter, gameRoot, deleteModifiedFiles: false, host.Logger);
        Assert.True(repeated.Succeeded, repeated.Error);
        Assert.Equal("user file", await File.ReadAllTextAsync(unrelated));
    }

    private static async Task<StagedMod> StageAsync(
        TestHost host,
        string name,
        string content,
        string fileName = "shared.dll")
    {
        var archive = TestArchives.CreateZip(
            host.Tree,
            $"{name}.zip",
            ($"{name}/Mods/plugins/{fileName}", content));

        var request = new InstallRequest
        {
            ArchivePath = archive,
            GameId = GameId,
            AcceptLowConfidence = true,
        };

        var result = await host.Install.StageAsync(request, new TestGameAdapter(), host.Logger);
        Assert.True(result.Succeeded, result.Error);
        result.Value!.Metadata.RootConfirmedByUser = true;
        return result.Value!;
    }

    private static ModSetEntry Entry(StagedMod staged, TestHost host) =>
        new(staged.Metadata, host.Library.GetManifest(GameId, staged.Metadata.Id), staged.StagingRoot);

    private static DeploymentPlan BuildPlan(TestHost host, string gameRoot, StagedMod staged)
    {
        var profile = new Profile
        {
            Id = "concurrency",
            Name = "Concurrency",
            GameId = GameId,
            ModOrder = [staged.Metadata.Id],
        };

        return host.Planner.BuildPlan(
            new TestGameAdapter().Definition,
            gameRoot,
            profile,
            [Entry(staged, host)],
            []);
    }
}
