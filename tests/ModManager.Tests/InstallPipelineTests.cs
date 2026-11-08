using System.IO;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// The generic install pipeline end to end: archive → analyze → adapter → plan → safety → deploy,
/// exercised through <see cref="ModInstallService"/> against a synthetic game root.
/// </summary>
/// <remarks>
/// Everything runs against a fake <c>game/</c> tree inside the temporary directory — never a real
/// game folder. The fake adapter (<see cref="TestGameAdapter"/>) proves the generic flow without any
/// game-specific code in the engine.
/// </remarks>
public sealed class InstallPipelineTests
{
    private const string GameId = "testgame";

    /// <summary>A fresh host and a fake game root, with the root approved like the app does at startup.</summary>
    private static (TestHost Host, string GameRoot) NewEnvironment()
    {
        var host = TestHost.Create();
        var gameRoot = host.CreateGameRoot();
        host.PathSafety.ApproveGameRoot(GameId, gameRoot, Path.Combine(gameRoot, "Mods"));
        return (host, gameRoot);
    }

    private static async Task<StagedMod> StageAsync(TestHost host, string archivePath)
    {
        var adapter = new TestGameAdapter();
        var request = new InstallRequest
        {
            ArchivePath = archivePath,
            GameId = GameId,
            AcceptLowConfidence = true,
        };

        var result = await host.Install.StageAsync(request, adapter, host.Logger);
        Assert.True(result.Succeeded, result.Error ?? "staging failed");
        result.Value!.Metadata.RootConfirmedByUser = true;
        return result.Value!;
    }

    private static async Task<DeployResult> InstallAsync(TestHost host, string gameRoot, StagedMod staged)
    {
        var adapter = new TestGameAdapter();
        return await host.Install.InstallAsync(staged, adapter, gameRoot, host.Logger);
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

        return await host.Applier.ApplyAsync(
            plan,
            adapter.Definition,
            modSet,
            host.Settings.Deployment,
            host.Logger);
    }

    /// <summary>Staging analyses and extracts into the app's own area, leaving the game folder untouched.</summary>
    [Fact]
    public async Task StageAnalysesAndExtractsWithoutTouchingTheGameFolder()
    {
        var (host, gameRoot) = NewEnvironment();
        using var _ = host;

        var archive = TestArchives.CreateZip(
            host.Tree,
            "CoolMod.zip",
            ("CoolMod/Mods/plugins/mod.dll", "dll-bytes"),
            ("CoolMod/README.txt", "readme"));

        var staged = await StageAsync(host, archive);

        // Wrapper stripped, adapter mapping applied: the staged layout is the final deploy layout.
        var stagedFile = Path.Combine(staged.StagingRoot, "plugins", "mod.dll");
        Assert.True(File.Exists(stagedFile), $"Expected the mapped staged file at {stagedFile}.");
        Assert.False(Directory.Exists(Path.Combine(staged.StagingRoot, "CoolMod")), "The wrapper directory must be stripped.");
        Assert.False(Directory.Exists(Path.Combine(gameRoot, "Mods")), "The game folder must be untouched by staging.");
        Assert.NotNull(host.Library.Mod(staged.Metadata.Id));
    }

    /// <summary>A wrapped archive installs to the right place, and the library + manifest persist.</summary>
    [Fact]
    public async Task InstallPlacesFilesAndPersistsLibraryState()
    {
        var (host, gameRoot) = NewEnvironment();
        using var _ = host;

        var archive = TestArchives.CreateZip(
            host.Tree,
            "BetterMod.zip",
            ("BetterMod/Mods/plugins/mod.dll", "dll-bytes"),
            ("BetterMod/Mods/config.json", "{\"a\":1}"));

        var staged = await StageAsync(host, archive);
        var preview = await host.Install.PreviewAsync(staged, new TestGameAdapter(), gameRoot, []);

        Assert.True(preview.Succeeded, preview.Error);
        Assert.DoesNotContain(preview.Value!.Entries, e => e.Safety == PathSafetyVerdict.Unsafe);

        var result = await InstallAsync(host, gameRoot, staged);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("dll-bytes", File.ReadAllText(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")));
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(gameRoot, "Mods", "config.json")));

        var mod = host.Library.Mod(staged.Metadata.Id);
        Assert.NotNull(mod);
        Assert.Equal(ModState.Deployed, mod!.State);

        var manifest = host.Library.GetManifest(GameId, staged.Metadata.Id);
        Assert.NotNull(manifest);
        Assert.Equal(2, manifest!.Files.Count);
    }

    /// <summary>A second mod shipping the same file conflicts, and the later mod wins on disk.</summary>
    [Fact]
    public async Task PreviewReportsConflictAndTheLaterModWins()
    {
        var (host, gameRoot) = NewEnvironment();
        using var _ = host;

        var firstArchive = TestArchives.CreateZip(host.Tree, "mod-a.zip", ("Mods/data/shared.json", "\"from-a\""));
        var secondArchive = TestArchives.CreateZip(host.Tree, "mod-b.zip", ("Mods/data/shared.json", "\"from-b\""));

        var first = await StageAsync(host, firstArchive);
        var firstInstall = await InstallAsync(host, gameRoot, first);
        Assert.True(firstInstall.Succeeded, firstInstall.Error);

        var second = await StageAsync(host, secondArchive);

        var existing = new List<ModSetEntry>
        {
            new(first.Metadata, host.Library.GetManifest(GameId, first.Metadata.Id), first.StagingRoot),
        };

        var preview = await host.Install.PreviewAsync(second, new TestGameAdapter(), gameRoot, existing);

        Assert.True(preview.Succeeded, preview.Error);
        var conflict = preview.Value!.Conflicts.Single(c => c.RelativeTarget == "data/shared.json");
        Assert.Equal(second.Metadata.Id, conflict.WinnerModId);
        Assert.Contains(first.Metadata.Id, conflict.OverriddenModIds);

        var secondInstall = await InstallAsync(host, gameRoot, second);
        Assert.True(secondInstall.Succeeded, secondInstall.Error);
        Assert.Equal("\"from-b\"", File.ReadAllText(Path.Combine(gameRoot, "Mods", "data", "shared.json")));

        var manifest = host.Library.GetManifest(GameId, second.Metadata.Id);
        Assert.NotNull(manifest);
        Assert.Contains(manifest!.Conflicts, c => c.Contains("data/shared.json", StringComparison.OrdinalIgnoreCase));

        // Installing B must never damage A's bookkeeping: A's manifest survives untouched.
        Assert.NotNull(host.Library.GetManifest(GameId, first.Metadata.Id));
    }

    /// <summary>Overwriting a vanilla file backs it up, and uninstalling puts the original back.</summary>
    [Fact]
    public async Task InstallingOverAVanillaFileBacksItUpAndUninstallRestoresIt()
    {
        var (host, gameRoot) = NewEnvironment();
        using var _ = host;

        var vanilla = Path.Combine(gameRoot, "Mods", "vanilla.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(vanilla)!);
        File.WriteAllText(vanilla, "original game bytes");

        var archive = TestArchives.CreateZip(host.Tree, "overwriter.zip", ("Mods/vanilla.txt", "mod replacement"));
        var staged = await StageAsync(host, archive);

        var result = await InstallAsync(host, gameRoot, staged);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("mod replacement", File.ReadAllText(vanilla));
        Assert.Equal(1, result.BackupCount);

        var backups = host.Library.BackupsForGame(GameId);
        Assert.Single(backups);

        var uninstall = await host.Install.UninstallAsync(
            GameId,
            staged.Metadata.Id,
            new TestGameAdapter(),
            gameRoot,
            deleteModifiedFiles: false,
            host.Logger);

        Assert.True(uninstall.Succeeded, uninstall.Error);
        Assert.True(
            string.Equals("original game bytes", File.ReadAllText(vanilla), StringComparison.Ordinal),
            "The vanilla file must be restored from its backup.");
        Assert.Null(host.Library.Mod(staged.Metadata.Id));
    }

    /// <summary>Disabling really removes the deployed files; enabling deploys them again.</summary>
    [Fact]
    public async Task DisableRemovesDeployedFilesAndEnableRestoresThem()
    {
        var (host, gameRoot) = NewEnvironment();
        using var _ = host;

        var archive = TestArchives.CreateZip(host.Tree, "toggle.zip", ("Mods/plugins/mod.dll", "dll-bytes"));
        var staged = await StageAsync(host, archive);
        var install = await InstallAsync(host, gameRoot, staged);
        Assert.True(install.Succeeded, install.Error);

        var deployed = Path.Combine(gameRoot, "Mods", "plugins", "mod.dll");
        Assert.True(File.Exists(deployed));

        var profile = host.Library.GetProfile($"{GameId}-default");
        Assert.NotNull(profile);

        var disabledProfile = profile! with { DisabledModIds = [staged.Metadata.Id] };
        host.Library.SaveProfile(disabledProfile);

        var disabledResult = await RedeployAsync(host, gameRoot, disabledProfile);
        Assert.True(disabledResult.Succeeded, disabledResult.Error);
        Assert.False(File.Exists(deployed), "Disabling must remove the deployed files, not just flip a flag.");

        var enabledProfile = disabledProfile with { DisabledModIds = [] };
        host.Library.SaveProfile(enabledProfile);

        var enabledResult = await RedeployAsync(host, gameRoot, enabledProfile);
        Assert.True(enabledResult.Succeeded, enabledResult.Error);
        Assert.True(File.Exists(deployed), "Enabling must deploy the files again.");
    }

    /// <summary>Uninstalling keeps a file the user edited after installation.</summary>
    [Fact]
    public async Task UninstallKeepsFilesTheUserModified()
    {
        var (host, gameRoot) = NewEnvironment();
        using var _ = host;

        var archive = TestArchives.CreateZip(
            host.Tree,
            "twins.zip",
            ("Mods/plugins/a.dll", "a-bytes"),
            ("Mods/plugins/b.dll", "b-bytes"));

        var staged = await StageAsync(host, archive);
        var install = await InstallAsync(host, gameRoot, staged);
        Assert.True(install.Succeeded, install.Error);

        var aFile = Path.Combine(gameRoot, "Mods", "plugins", "a.dll");
        var bFile = Path.Combine(gameRoot, "Mods", "plugins", "b.dll");

        File.WriteAllText(aFile, "the user's own edit");

        var result = await host.Install.UninstallAsync(
            GameId,
            staged.Metadata.Id,
            new TestGameAdapter(),
            gameRoot,
            deleteModifiedFiles: false,
            host.Logger);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(aFile), "The user-modified file must be kept.");
        Assert.Equal("the user's own edit", File.ReadAllText(aFile));
        Assert.False(File.Exists(bFile), "Untouched files must be removed.");
        Assert.Contains(result.Warnings, w => w.Contains("kept", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Uninstalling must not restore the vanilla backup over a file the user edited after installation.
    /// The edit wins over both the mod and the game's original bytes.
    /// </summary>
    [Fact]
    public async Task UninstallKeepsUserEditsInsteadOfRestoringTheVanillaBackup()
    {
        var (host, gameRoot) = NewEnvironment();
        using var _ = host;

        var vanilla = Path.Combine(gameRoot, "Mods", "vanilla.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(vanilla)!);
        File.WriteAllText(vanilla, "original game bytes");

        var archive = TestArchives.CreateZip(host.Tree, "overwriter.zip", ("Mods/vanilla.txt", "mod replacement"));
        var staged = await StageAsync(host, archive);

        var install = await InstallAsync(host, gameRoot, staged);
        Assert.True(install.Succeeded, install.Error);
        Assert.Equal(1, install.BackupCount);

        // The user edits the deployed file. Uninstall promises to keep that edit.
        File.WriteAllText(vanilla, "user edit after install");

        var result = await host.Install.UninstallAsync(
            GameId,
            staged.Metadata.Id,
            new TestGameAdapter(),
            gameRoot,
            deleteModifiedFiles: false,
            host.Logger);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("user edit after install", File.ReadAllText(vanilla));
        Assert.Contains(result.Warnings, w => w.Contains("kept", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Uninstalling only ever removes the files the mod itself deployed.</summary>
    [Fact]
    public async Task UninstallOnlyRemovesFilesTheModDeployed()
    {
        var (host, gameRoot) = NewEnvironment();
        using var _ = host;

        var unrelated = Path.Combine(gameRoot, "Mods", "unrelated.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(unrelated)!);
        File.WriteAllText(unrelated, "not ours");

        var archive = TestArchives.CreateZip(host.Tree, "ours.zip", ("Mods/plugins/ours.dll", "ours-bytes"));
        var staged = await StageAsync(host, archive);
        Assert.True((await InstallAsync(host, gameRoot, staged)).Succeeded);

        var result = await host.Install.UninstallAsync(
            GameId,
            staged.Metadata.Id,
            new TestGameAdapter(),
            gameRoot,
            deleteModifiedFiles: false,
            host.Logger);

        Assert.True(result.Succeeded, result.Error);
        Assert.False(File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "ours.dll")));
        Assert.True(File.Exists(unrelated), "A file the mod never deployed must never be removed.");
    }

    /// <summary>A mid-install failure rolls the transaction back to the pre-install state.</summary>
    [Fact]
    public async Task InstallRollsBackWhenAPlannedFileCannotBePlaced()
    {
        var (host, gameRoot) = NewEnvironment();
        using var _ = host;

        // A file squatting on a directory name makes the second placement fail after the first succeeded.
        var blocker = Path.Combine(gameRoot, "Mods", "z-blocked");
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        File.WriteAllText(blocker, "I am a file, not a directory");

        var archive = TestArchives.CreateZip(
            host.Tree,
            "half.zip",
            ("Mods/plugins/a-ok.txt", "placed first"),
            ("Mods/z-blocked/sub.txt", "cannot be placed"));

        var staged = await StageAsync(host, archive);
        var result = await InstallAsync(host, gameRoot, staged);

        Assert.False(result.Succeeded, "The install must fail.");
        Assert.False(
            File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "a-ok.txt")),
            "The file placed before the failure must be rolled back.");
        Assert.Empty(host.Journals.FindInterrupted());
        Assert.False(Directory.Exists(Path.Combine(gameRoot, "Mods", "z-blocked", "sub.txt")));
    }

    /// <summary>Disabling gives the game back the file the mod was overriding.</summary>
    [Fact]
    public async Task DisableRestoresTheOriginalFileTheModOverwrote()
    {
        var (host, gameRoot) = NewEnvironment();
        using var _ = host;

        var vanilla = Path.Combine(gameRoot, "Mods", "vanilla.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(vanilla)!);
        File.WriteAllText(vanilla, "original game bytes");

        var archive = TestArchives.CreateZip(host.Tree, "overwriter.zip", ("Mods/vanilla.txt", "mod replacement"));
        var staged = await StageAsync(host, archive);
        var install = await InstallAsync(host, gameRoot, staged);
        Assert.True(install.Succeeded, install.Error);
        Assert.Equal("mod replacement", File.ReadAllText(vanilla));

        var profile = host.Library.GetProfile($"{GameId}-default")!;
        var disabled = profile with { DisabledModIds = [staged.Metadata.Id] };
        host.Library.SaveProfile(disabled);

        var disabledResult = await RedeployAsync(host, gameRoot, disabled);
        Assert.True(disabledResult.Succeeded, disabledResult.Error);
        Assert.True(
            string.Equals("original game bytes", File.ReadAllText(vanilla), StringComparison.Ordinal),
            "Disabling must restore the file the mod was overriding.");

        var enabled = disabled with { DisabledModIds = [] };
        host.Library.SaveProfile(enabled);
        var enabledResult = await RedeployAsync(host, gameRoot, enabled);
        Assert.True(enabledResult.Succeeded, enabledResult.Error);
        Assert.True(
            string.Equals("mod replacement", File.ReadAllText(vanilla), StringComparison.Ordinal),
            "Enabling must deploy the mod's file again.");

        var uninstall = await host.Install.UninstallAsync(
            GameId,
            staged.Metadata.Id,
            new TestGameAdapter(),
            gameRoot,
            deleteModifiedFiles: false,
            host.Logger);
        Assert.True(uninstall.Succeeded, uninstall.Error);
        Assert.True(
            string.Equals("original game bytes", File.ReadAllText(vanilla), StringComparison.Ordinal),
            "Uninstalling must leave the original in place.");
    }

    /// <summary>The whole pipeline works identically for a 7z archive.</summary>
    [Fact]
    public async Task SevenZipArchiveInstallsThroughTheSamePipeline()
    {
        var (host, gameRoot) = NewEnvironment();
        using var _ = host;

        var archive = TestArchives.FixturePath("sample.7z");

        var staged = await StageAsync(host, archive);
        var result = await InstallAsync(host, gameRoot, staged);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("dll-bytes", File.ReadAllText(Path.Combine(gameRoot, "Mods", "plugins", "mod.dll")));
    }

    [Fact]
    public async Task LowConfidenceRootWithoutUserConfirmationIsBlocked()
    {
        var (host, gameRoot) = NewEnvironment();
        using var _ = host;

        var archive = TestArchives.CreateZip(
            host.Tree,
            "ambiguous.zip",
            ("Mods/plugins/ambiguous.dll", "bytes"));

        var staged = await host.Install.StageAsync(
            new InstallRequest { ArchivePath = archive, GameId = GameId, AcceptLowConfidence = true },
            new TestGameAdapter(),
            host.Logger);
        Assert.True(staged.Succeeded, staged.Error);

        var result = await host.Install.InstallAsync(
            staged.Value!,
            new TestGameAdapter(),
            gameRoot,
            host.Logger);

        Assert.False(result.Succeeded, "A low-confidence root must not install without explicit confirmation.");
        Assert.Contains("置信度", result.Error);
        Assert.False(File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "ambiguous.dll")));
    }

    [Fact]
    public async Task MissingRequiredDependencyBlocksInstall()
    {
        var (host, gameRoot) = NewEnvironment();
        using var _ = host;

        var archive = TestArchives.CreateZip(
            host.Tree,
            "needs-dependency.zip",
            ("Mods/plugins/dependent.dll", "bytes"));
        var staged = await StageAsync(host, archive);

        var withDependency = staged with
        {
            Metadata = staged.Metadata with { Dependencies = ["MissingMod-1.0.0"] },
        };

        var result = await host.Install.InstallAsync(
            withDependency,
            new TestGameAdapter(),
            gameRoot,
            host.Logger);

        Assert.False(result.Succeeded, "A missing required dependency must block installation.");
        Assert.Contains("缺少必需依赖", result.Error);
        Assert.False(File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "dependent.dll")));
    }

    [Fact]
    public async Task DocumentationFilesAreNotDeployedIntoTheGameFolder()
    {
        var (host, gameRoot) = NewEnvironment();
        using var _ = host;

        var archive = TestArchives.CreateZip(
            host.Tree,
            "with-docs.zip",
            ("Mods/plugins/real.dll", "bytes"),
            ("Mods/README.md", "docs"),
            ("Mods/LICENSE", "license"),
            ("Mods/CHANGELOG.txt", "changes"));

        var staged = await StageAsync(host, archive);
        var result = await InstallAsync(host, gameRoot, staged);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(File.Exists(Path.Combine(gameRoot, "Mods", "plugins", "real.dll")));
        Assert.False(File.Exists(Path.Combine(gameRoot, "Mods", "README.md")));
        Assert.False(File.Exists(Path.Combine(gameRoot, "Mods", "LICENSE")));
        Assert.False(File.Exists(Path.Combine(gameRoot, "Mods", "CHANGELOG.txt")));
    }
}
