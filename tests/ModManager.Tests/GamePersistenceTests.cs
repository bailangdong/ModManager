using System.IO;
using ModManager.Core;
using ModManager.Core.Persistence;
using ModManager.Core.Safety;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Game registration: add, query, remove, and the settings-carried default game — each proven to
/// survive a simulated restart (a brand-new repository over the same files).
/// </summary>
public sealed class GamePersistenceTests
{
    /// <summary>A game registration round-trips through a repository restart.</summary>
    [Fact]
    public void GameAddsQueriesAndPersistsAcrossRestarts()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));
        paths.EnsureCreated();

        var first = new LibraryRepository(new JsonDocumentStore(paths), paths);
        first.Upsert(new GameInstance
        {
            GameId = "testgame",
            Name = "Test Game",
            InstallPath = tree.Dir("game"),
            ModRootPath = tree.Dir("game/Mods"),
            AddedManually = true,
        });

        var second = new LibraryRepository(new JsonDocumentStore(paths), paths);
        var instance = second.AllGames().Single();

        Assert.Equal("testgame", instance.GameId);
        Assert.Equal(tree.Dir("game"), instance.InstallPath);
        Assert.True(instance.AddedManually);

        // Remove through the repository interface (the concrete class implements it explicitly).
        IGameRepository games = second;
        Assert.True(games.Remove("testgame"));
        Assert.Empty(second.AllGames());
    }

    /// <summary>The default game id persists through the settings store.</summary>
    [Fact]
    public void DefaultGameSelectionPersistsThroughSettings()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));
        paths.EnsureCreated();

        var repository = new LibraryRepository(new JsonDocumentStore(paths), paths);
        repository.Save(new AppSettings { LastSelectedGameId = "testgame" });

        var reloaded = new LibraryRepository(new JsonDocumentStore(paths), paths);
        Assert.Equal("testgame", reloaded.Load().LastSelectedGameId);
    }

    /// <summary>A game approved for writes today stays approvable for a restarted host.</summary>
    [Fact]
    public void ApprovedGameRootIsReApprovedByARestartedHost()
    {
        using var tree = new TemporaryTree();
        var host = TestHost.Create(tree);
        using var _ = host;

        var gameRoot = host.CreateGameRoot();
        host.PathSafety.ApproveGameRoot("testgame", gameRoot, Path.Combine(gameRoot, "Mods"));

        var restarted = TestHost.Create(tree);
        using var __ = restarted;

        restarted.PathSafety.ApproveGameRoot("testgame", gameRoot, Path.Combine(gameRoot, "Mods"));

        var result = restarted.PathSafety.ValidateWrite("testgame", Path.Combine(gameRoot, "Mods", "x.dll"));
        Assert.True(result.Allowed, result.Reason);
    }
}
