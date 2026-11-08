using System.IO;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Steam detection and generic support: every installed game must be detected, and a game with no dedicated
/// definition must be manageable rather than hidden.
/// </summary>
/// <remarks>
/// The tests build a fake Steam tree under <c>artifacts/test-mods</c> and pass its roots to the scanner
/// explicitly, so nothing here reads the machine's real Steam installation or its registry.
/// </remarks>
public sealed class SteamDetectionTests
{
    /// <summary>
    /// A: every installed game is detected; only the ones with a definition are marked as supported.
    /// </summary>
    /// <remarks>
    /// This is the whole point of the change. Before it, a game with no <c>GameDefinition</c> never reached
    /// the list, so a full Steam library looked like two games.
    /// </remarks>
    [Fact]
    public void EveryInstalledSteamGameIsDetected()
    {
        using var tree = new TemporaryTree();
        var library = SteamFixture.Library(tree, "steam");
        SteamFixture.Game(library, 1091500, "Cyberpunk 2077");
        SteamFixture.Game(library, 1001, "Palworld");
        SteamFixture.Game(library, 2001, "Some Unknown Game");
        SteamFixture.Game(library, 2002, "Another Unknown Game");
        SteamFixture.Game(library, 2003, "Third Unknown Game");

        var scan = new SteamLibraryScanner().Scan([library]);

        Assert.Equal(5, scan.Games.Count);
        Assert.Equal(5, scan.ValidCount);

        var registry = new GameAdapterRegistry();
        registry.RegisterDefinition(new GameDefinition
        {
            Id = "cyberpunk2077",
            Name = "Cyberpunk 2077",
            SteamAppId = 1091500,
            ModRoot = "Mods",
        });
        registry.RegisterDefinition(new GameDefinition
        {
            Id = "palworld",
            Name = "Palworld",
            SteamAppId = 1001,
            ModRoot = "Pal/Content/Paks/~mods",
        });

        var resolved = new GameSupportResolver().ResolveAll(scan.Games, registry);
        var summary = GameSupportResolver.Summarize(resolved, []);

        Assert.Equal(5, summary.Detected);
        Assert.Equal(2, summary.Supported);
        Assert.Equal(3, summary.Generic);
        Assert.Equal(0, summary.Invalid);

        // The three unknown games are still listed, and each is offered as generic.
        Assert.All(
            resolved.Where(entry => entry.Support == GameSupportKind.Generic),
            entry => Assert.Null(entry.Definition));
        Assert.Contains(resolved, entry => entry.Detected.Name == "Some Unknown Game");
    }

    /// <summary>B: a generic game can be added, and the definition survives in the registry.</summary>
    [Fact]
    public void GenericGameCanBeAdded()
    {
        using var tree = new TemporaryTree();
        var library = SteamFixture.Library(tree, "steam");
        var install = SteamFixture.Game(library, 5555, "Unknown Game");

        var scan = new SteamLibraryScanner().Scan([library]);
        var resolved = new GameSupportResolver().Resolve(Assert.Single(scan.Games), new GameAdapterRegistry());
        Assert.True(resolved.IsGeneric);

        var definition = SteamGameDefinitions.Build(
            resolved.Detected.AppId,
            resolved.Detected.Name,
            "Mods");

        var gamesDirectory = tree.Dir("appdata/Games");
        var registry = new GameAdapterRegistry();
        var path = registry.SaveUserDefinition(definition, gamesDirectory);

        Assert.True(File.Exists(path));
        Assert.True(registry.Contains("steam-5555"));

        var adapter = registry.Get("steam-5555");
        Assert.NotNull(adapter);
        Assert.Equal(Path.Combine(install, "Mods"), adapter!.ResolveModRoot(install));
    }

    /// <summary>C: the mod root is only accepted inside the game directory or the user's Documents.</summary>
    [Fact]
    public void GenericModRootMustBeInsideAKnownBase()
    {
        using var tree = new TemporaryTree();
        var install = tree.Dir("steam/steamapps/common/Unknown Game");
        var documents = tree.Dir("documents");
        var insideGame = Path.Combine(install, "Mods");
        Directory.CreateDirectory(insideGame);
        var insideDocuments = Path.Combine(documents, "UnknownGameMods");
        Directory.CreateDirectory(insideDocuments);
        var elsewhere = tree.Dir("elsewhere");

        var inGame = SteamGameDefinitions.ResolveChoice(install, documents, insideGame);
        Assert.True(inGame.Succeeded);
        Assert.Equal("Mods", inGame.Value!.RelativePath);
        Assert.False(inGame.Value.InUserProfile);

        var inDocuments = SteamGameDefinitions.ResolveChoice(install, documents, insideDocuments);
        Assert.True(inDocuments.Succeeded);
        Assert.Equal("UnknownGameMods", inDocuments.Value!.RelativePath);
        Assert.True(inDocuments.Value.InUserProfile);

        // A directory that is neither: refused with an explanation rather than accepted silently.
        var refused = SteamGameDefinitions.ResolveChoice(install, documents, elsewhere);
        Assert.False(refused.Succeeded);
        Assert.Contains("文档", refused.Error!, StringComparison.Ordinal);

        // The install root itself is a legitimate choice: some games take mods next to the executable.
        var root = SteamGameDefinitions.ResolveChoice(install, documents, install);
        Assert.True(root.Succeeded);
        Assert.Equal(string.Empty, root.Value!.RelativePath);
    }

    /// <summary>D: a generic definition and its game record survive a restart.</summary>
    [Fact]
    public void GenericGameSurvivesARestart()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));
        paths.EnsureCreated();

        var library = SteamFixture.Library(tree, "steam");
        var install = SteamFixture.Game(library, 7777, "Restart Game");

        var definition = SteamGameDefinitions.Build(7777, "Restart Game", "Mods");
        var registry = new GameAdapterRegistry();
        var definitionPath = registry.SaveUserDefinition(definition, paths.Games);

        var firstLibrary = new LibraryRepository(new ModManager.Core.Persistence.JsonDocumentStore(paths), paths);
        firstLibrary.Upsert(new GameInstance
        {
            GameId = definition.Id,
            Name = definition.Name,
            InstallPath = install,
            ModRootPath = Path.Combine(install, "Mods"),
            SteamAppId = 7777,
            SupportKind = GameSupportKind.Generic,
        });

        // Restart: fresh objects over the same files.
        var reloadedRegistry = new GameAdapterRegistry();
        var loaded = reloadedRegistry.LoadUserDefinitions(paths.Games, new AppLogger(paths.Logs, LogLevel.Warning));
        var reloadedLibrary = new LibraryRepository(new ModManager.Core.Persistence.JsonDocumentStore(paths), paths);

        Assert.Contains(definition.Id, loaded);
        Assert.True(reloadedRegistry.Contains(definition.Id));
        Assert.Equal(Path.Combine(install, "Mods"), reloadedRegistry.Get(definition.Id)!.ResolveModRoot(install));

        var game = Assert.Single(reloadedLibrary.AllGames());
        Assert.Equal(definition.Id, game.GameId);
        Assert.Equal(7777, game.SteamAppId);
        Assert.Equal(GameSupportKind.Generic, game.SupportKind);
        Assert.Equal(Path.Combine(install, "Mods"), game.ModRootPath);
        Assert.True(File.Exists(definitionPath));
    }

    /// <summary>E: re-scanning does not duplicate games, including across multiple libraries.</summary>
    [Fact]
    public void RescanningDoesNotDuplicateGames()
    {
        using var tree = new TemporaryTree();
        var first = SteamFixture.Library(tree, "steam");
        var second = SteamFixture.Library(tree, "steam2");

        SteamFixture.Game(first, 1111, "Shared Game");
        SteamFixture.Game(second, 2222, "Second Library Game");

        var scanner = new SteamLibraryScanner();
        var one = scanner.Scan([first, second]);
        var two = scanner.Scan([first, second]);

        Assert.Equal(2, one.Games.Count);
        Assert.Equal(one.Games.Count, two.Games.Count);

        // The same app id installed twice appears twice — two installs are two installs — but a repeated
        // library root does not.
        var expectedKeys = one.Games.Select(game => game.IdentityKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.Equal(expectedKeys, one.Games.Count);

        var duplicated = scanner.Scan([first, first]);
        Assert.Single(duplicated.Games);
    }

    /// <summary>F: one broken manifest does not stop the others being detected.</summary>
    [Fact]
    public void BrokenManifestDoesNotStopTheScan()
    {
        using var tree = new TemporaryTree();
        var library = SteamFixture.Library(tree, "steam");
        SteamFixture.Game(library, 3333, "Good Game");

        // A manifest with no installdir, and one that is not even a valid VDF.
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_4444.acf"), "\"AppState\"\n{\n\t\"appid\"\t\"4444\"\n\t\"name\"\t\"Broken Game\"\n}\n");
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_5555.acf"), "this is not a steam manifest at all");

        var scan = new SteamLibraryScanner().Scan([library]);

        Assert.Contains(scan.Games, game => game.Name == "Good Game" && game.IsValid);
        Assert.Contains(scan.Games, game => game.Name == "Broken Game" && !game.IsValid);
        Assert.True(scan.InvalidCount >= 1);
    }

    /// <summary>G: an uninstalled game is reported as invalid rather than as usable.</summary>
    [Fact]
    public void UninstalledGameIsReportedAsInvalid()
    {
        using var tree = new TemporaryTree();
        var library = SteamFixture.Library(tree, "steam");
        SteamFixture.Game(library, 6666, "Present Game");

        // A manifest whose install directory was removed: the record exists, the game does not.
        File.WriteAllText(
            Path.Combine(library, "steamapps", "appmanifest_6667.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\"6667\"\n\t\"name\"\t\"Gone Game\"\n\t\"installdir\"\t\"Gone Game\"\n}\n");

        var scan = new SteamLibraryScanner().Scan([library]);

        var gone = Assert.Single(scan.Games, game => game.Name == "Gone Game");
        Assert.False(gone.IsValid);
        Assert.Contains("不存在", gone.Problem!, StringComparison.Ordinal);

        var resolved = new GameSupportResolver().Resolve(gone, new GameAdapterRegistry());
        Assert.Equal(GameSupportKind.Invalid, resolved.Support);
        Assert.False(resolved.IsSupported);
        Assert.False(resolved.IsGeneric);
    }

    /// <summary>H: a generic game becomes supported once a definition exists for its app id.</summary>
    [Fact]
    public void GenericGameBecomesSupportedWhenADefinitionAppears()
    {
        using var tree = new TemporaryTree();
        var library = SteamFixture.Library(tree, "steam");
        SteamFixture.Game(library, 8888, "Later Supported");
        var game = Assert.Single(new SteamLibraryScanner().Scan([library]).Games);

        var resolver = new GameSupportResolver();
        var registry = new GameAdapterRegistry();

        Assert.Equal(GameSupportKind.Generic, resolver.Resolve(game, registry).Support);

        // The developer adds the game to the catalogue.
        registry.RegisterDefinition(new GameDefinition
        {
            Id = "latersupported",
            Name = "Later Supported",
            SteamAppId = 8888,
            ModRoot = "Mods",
        });

        var upgraded = resolver.Resolve(game, registry);
        Assert.Equal(GameSupportKind.Supported, upgraded.Support);
        Assert.Equal("latersupported", upgraded.GameId);
    }

    /// <summary>I: re-detecting keeps the user's game record and its chosen mod root.</summary>
    [Fact]
    public void RescanKeepsTheUsersConfiguration()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));
        paths.EnsureCreated();

        var library = SteamFixture.Library(tree, "steam");
        var install = SteamFixture.Game(library, 9999, "Configured Game");
        var chosenRoot = Directory.CreateDirectory(Path.Combine(install, "MyMods")).FullName;

        var definition = SteamGameDefinitions.Build(9999, "Configured Game", "MyMods");
        var registry = new GameAdapterRegistry();
        registry.SaveUserDefinition(definition, paths.Games);

        var repository = new LibraryRepository(new ModManager.Core.Persistence.JsonDocumentStore(paths), paths);
        repository.Upsert(new GameInstance
        {
            GameId = definition.Id,
            Name = definition.Name,
            InstallPath = install,
            ModRootPath = chosenRoot,
            SteamAppId = 9999,
            SupportKind = GameSupportKind.Generic,
        });

        // A second scan plus a re-save of the same definition must not move the user's mod root.
        var scan = new SteamLibraryScanner().Scan([library]);
        var resolved = new GameSupportResolver().Resolve(Assert.Single(scan.Games), registry);
        registry.SaveUserDefinition(definition, paths.Games);

        var stored = repository.AllGames().Single(game => game.GameId == definition.Id);
        Assert.Equal(chosenRoot, stored.ModRootPath);
        Assert.Equal(chosenRoot, registry.Get(definition.Id)!.ResolveModRoot(install));
        Assert.True(resolved.IsGeneric);
    }

    /// <summary>J: path safety still governs what a generic game may write to.</summary>
    [Fact]
    public void PathSafetyStillGovernsGenericGames()
    {
        using var tree = new TemporaryTree();
        var host = TestHost.Create(tree);
        using var _ = host;

        var install = tree.Dir("steam/steamapps/common/Generic Game");
        var modRoot = Directory.CreateDirectory(Path.Combine(install, "Mods")).FullName;

        // Approving the game's own root is what authorises writes.
        host.PathSafety.ApproveGameRoot("steam-1234", install, modRoot);
        Assert.True(host.PathSafety.ValidateWrite("steam-1234", Path.Combine(modRoot, "a.txt")).Allowed);

        // A directory outside the approved root stays refused.
        var outside = Path.Combine(tree.Root, "outside", "file.txt");
        Assert.False(host.PathSafety.ValidateWrite("steam-1234", outside).Allowed);

        // And a protected location cannot be approved at all.
        Assert.Throws<InvalidOperationException>(() =>
            host.PathSafety.ApproveGameRoot("steam-1234", @"C:\Windows", @"C:\Windows\Mods"));
    }

    /// <summary>
    /// K: a game added in generic mode installs and uninstalls through the normal pipeline.
    /// </summary>
    /// <remarks>
    /// This is the claim that matters for generic support: it is not a second installation system. The generic
    /// adapter feeds the same ArchiveReader, planner, journal, backup and applier that a fully supported game
    /// uses, so enable/disable, rollback and uninstall all behave identically.
    /// </remarks>
    [Fact]
    public async Task GenericGameUsesTheNormalInstallPipeline()
    {
        using var tree = new TemporaryTree();
        var host = TestHost.Create(tree);
        using var _ = host;

        var install = tree.Dir("steam/steamapps/common/Generic Game");
        var modRoot = Path.Combine(install, "Mods");

        var definition = SteamGameDefinitions.Build(4242, "Generic Game", "Mods");
        var adapter = new GenericGameAdapter(definition);

        Assert.Equal(modRoot, adapter.ResolveModRoot(install));

        // Approving the root is the same gate every other game goes through.
        host.PathSafety.ApproveGameRoot(definition.Id, install, modRoot);

        var staged = await host.Install.StageAsync(
            new InstallRequest
            {
                ArchivePath = TestArchives.CreateZip(tree, "generic-mod.zip", ("plugins/mod.dll", "generic bytes")),
                GameId = definition.Id,
                AcceptLowConfidence = true,
            },
            adapter,
            host.Logger);

        Assert.True(staged.Succeeded, staged.Error);
        Assert.Equal(modRoot, host.PathSafety.Canonicalize(modRoot));
        staged.Value!.Metadata.RootConfirmedByUser = true;

        var deployed = await host.Install.InstallAsync(staged.Value!, adapter, install, host.Logger);
        Assert.True(deployed.Succeeded, deployed.Error);

        var target = Path.Combine(modRoot, "plugins", "mod.dll");
        Assert.True(File.Exists(target));
        Assert.Equal("generic bytes", File.ReadAllText(target));

        var manifest = host.Library.GetManifest(definition.Id, staged.Value!.Metadata.Id);
        Assert.NotNull(manifest);
        Assert.Single(manifest!.Files);

        var removed = await host.Install.UninstallAsync(
            definition.Id,
            staged.Value.Metadata.Id,
            adapter,
            install,
            false,
            host.Logger);

        Assert.True(removed.Succeeded, removed.Error);
        Assert.False(File.Exists(target));
        Assert.Empty(host.Library.ModsForGame(definition.Id));
    }
}

/// <summary>Builds a fake Steam library tree for the detection tests.</summary>
internal static class SteamFixture
{
    /// <summary>Creates a library root with its <c>steamapps</c> directory.</summary>
    /// <param name="tree">Temporary tree.</param>
    /// <param name="name">Library directory name.</param>
    public static string Library(TemporaryTree tree, string name)
    {
        var root = tree.Dir(name);
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        return root;
    }

    /// <summary>Creates a manifest plus its install directory.</summary>
    /// <param name="library">Library root.</param>
    /// <param name="appId">Steam app id.</param>
    /// <param name="name">Game name and installdir.</param>
    public static string Game(string library, int appId, string name)
    {
        var install = Path.Combine(library, "steamapps", "common", name);
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "game.exe"), "stub");

        File.WriteAllText(
            Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf"),
            "\"AppState\"\n{\n"
            + $"\t\"appid\"\t\"{appId}\"\n"
            + $"\t\"name\"\t\"{name}\"\n"
            + $"\t\"installdir\"\t\"{name}\"\n"
            + "}\n");

        return install;
    }
}
