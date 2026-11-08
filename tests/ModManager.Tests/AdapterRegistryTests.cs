using System.IO;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// The extensible adapter registry and the generic adapter contract, validated with the fake
/// <see cref="TestGameAdapter"/> — no real game is needed or used.
/// </summary>
public sealed class AdapterRegistryTests
{
    /// <summary>Adapters register, look up, and enumerate by display name.</summary>
    [Fact]
    public void RegistryRegistersAndLooksUpAdapters()
    {
        var registry = new GameAdapterRegistry();

        Assert.False(registry.Contains("testgame"));

        registry.Register(new TestGameAdapter());

        Assert.True(registry.Contains("testgame"));
        var adapter = registry.Get("testgame");
        Assert.NotNull(adapter);
        Assert.Equal("testgame", adapter!.GameId);
        Assert.Equal("Test Game", adapter.Definition.Name);
        Assert.Contains(registry.All(), a => a.GameId == "testgame");
    }

    /// <summary>A later registration with the same id replaces the earlier one.</summary>
    [Fact]
    public void LaterRegistrationReplacesAnEarlierOne()
    {
        var registry = new GameAdapterRegistry();
        var first = TestGame.Definition() with { Name = "First" };
        var second = TestGame.Definition() with { Name = "Second" };

        registry.RegisterDefinition(first);
        registry.RegisterDefinition(second);

        Assert.Equal("Second", registry.Get("testgame")!.Definition.Name);
    }

    /// <summary>The generic adapter resolves the mod root and validates an install directory from the definition alone.</summary>
    [Fact]
    public void GenericAdapterResolvesModRootAndValidatesInstall()
    {
        using var tree = new TemporaryTree();
        var adapter = new TestGameAdapter();

        var installPath = tree.Dir("game");
        Assert.Equal(Path.Combine(installPath, "Mods"), adapter.ResolveModRoot(installPath));

        Assert.Empty(adapter.ValidateInstall(installPath));

        // A directory that does not exist at all must be reported, not silently accepted.
        var missing = adapter.ValidateInstall(Path.Combine(tree.Root, "does-not-exist"));
        Assert.Single(missing);
    }

    /// <summary>The generic adapter maps deploy paths by stripping the definition's redundant prefixes.</summary>
    [Fact]
    public void GenericAdapterMapsDeployPathsByStrippingRedundantPrefixes()
    {
        var adapter = new TestGameAdapter();

        Assert.Equal("plugins/mod.dll", adapter.MapDeployPath(new ArchiveEntry("Mods/plugins/mod.dll", false, 0)));
        Assert.Equal("data/config.json", adapter.MapDeployPath(new ArchiveEntry("Mods/data/config.json", false, 0)));
        Assert.Equal("README.txt", adapter.MapDeployPath(new ArchiveEntry("README.txt", false, 0)));
    }

    /// <summary>The built-in catalogue registers every game through the generic definition path.</summary>
    [Fact]
    public void CatalogueRegistersEveryDefinitionGenerically()
    {
        var registry = new GameAdapterRegistry();
        GameCatalogue.RegisterAll(registry, null, new AppLogger());

        foreach (var definition in GameCatalogue.All)
        {
            Assert.True(registry.Contains(definition.Id), $"The catalogue must register '{definition.Id}'.");
        }

        // Every built-in game resolves through the same generic adapter machinery — no game-specific
        // type may exist in Core.
        foreach (var adapter in registry.All())
        {
            Assert.Equal(typeof(GenericGameAdapter), adapter.GetType());
        }
    }

    /// <summary>The test adapter behaves like any other adapter from the engine's point of view.</summary>
    [Fact]
    public void TestGameAdapterIsAnOrdinaryAdapter()
    {
        IGameAdapter adapter = new TestGameAdapter();

        Assert.Equal("testgame", adapter.GameId);
        Assert.False(adapter.CheckLoader("anywhere").Required, "The synthetic game declares no loader.");
        Assert.Contains(adapter.GetInstallPathHints(), hint => hint.EndsWith("Test Game", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(adapter.DetectPlugins(["x.dll"]));
        Assert.True(adapter.ReadLoadOrder("anywhere") is { Succeeded: false } or { Value.Count: 0 });
    }
}
