using System.IO;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// A generic game's mod folder may be chosen freely, but only where the safety policy allows.
/// </summary>
/// <remarks>
/// The engine used to accept only the game directory or Documents, because a definition's mod root is a
/// relative path. It now also accepts a user-chosen absolute folder, which is what lets someone keep mods on
/// another drive — while Windows, Program Files and drive roots stay refused by the application's own
/// protected-location list.
/// </remarks>
public sealed class GenericModRootTests
{
    /// <summary>A folder outside the game and Documents is accepted when the policy allows it.</summary>
    [Fact]
    public void UserChosenFolderIsAcceptedWhenPolicyAllows()
    {
        using var tree = new TemporaryTree();
        var install = tree.Dir("steam/steamapps/common/Some Game");
        var documents = tree.Dir("documents");
        var elsewhere = tree.Dir("other-drive/MyMods");

        var result = SteamGameDefinitions.ResolveChoice(
            install,
            documents,
            elsewhere,
            isProtected: _ => false);

        Assert.True(result.Succeeded);
        Assert.Equal(Path.GetFullPath(elsewhere), result.Value!.RelativePath);
        Assert.False(result.Value.InUserProfile);
    }

    /// <summary>A protected location is refused even when it is not the game directory.</summary>
    [Fact]
    public void ProtectedLocationIsRefused()
    {
        using var tree = new TemporaryTree();
        var install = tree.Dir("steam/steamapps/common/Some Game");
        var documents = tree.Dir("documents");
        var windows = tree.Dir("windows");

        var refused = SteamGameDefinitions.ResolveChoice(
            install,
            documents,
            windows,
            isProtected: path => path.Contains("windows", StringComparison.OrdinalIgnoreCase));

        Assert.False(refused.Succeeded);
        Assert.Contains("受保护", refused.Error!, StringComparison.Ordinal);
    }

    /// <summary>Without a policy the previous, narrower behaviour is kept.</summary>
    [Fact]
    public void WithoutPolicyOutsideFoldersStayRefused()
    {
        using var tree = new TemporaryTree();
        var install = tree.Dir("steam/steamapps/common/Some Game");
        var documents = tree.Dir("documents");
        var elsewhere = tree.Dir("elsewhere");

        Assert.False(SteamGameDefinitions.ResolveChoice(install, documents, elsewhere).Succeeded);
    }

    /// <summary>Documents and the game directory still resolve to the engine's relative form.</summary>
    [Fact]
    public void KnownBasesKeepTheirRelativeForm()
    {
        using var tree = new TemporaryTree();
        var install = tree.Dir("steam/steamapps/common/Some Game");
        var documents = tree.Dir("documents");
        var inGame = Directory.CreateDirectory(Path.Combine(install, "Mods")).FullName;
        var inDocuments = Directory.CreateDirectory(Path.Combine(documents, "SomeGameMods")).FullName;

        var game = SteamGameDefinitions.ResolveChoice(install, documents, inGame, _ => false);
        Assert.True(game.Succeeded);
        Assert.Equal("Mods", game.Value!.RelativePath);
        Assert.False(game.Value.InUserProfile);

        var docs = SteamGameDefinitions.ResolveChoice(install, documents, inDocuments, _ => false);
        Assert.True(docs.Succeeded);
        Assert.Equal("SomeGameMods", docs.Value!.RelativePath);
        Assert.True(docs.Value.InUserProfile);
    }

    /// <summary>A user-chosen absolute root is deployed where the user pointed it, not under the game.</summary>
    [Fact]
    public void AbsoluteModRootIsHonouredByThePlanner()
    {
        using var tree = new TemporaryTree();
        var install = tree.Dir("steam/steamapps/common/Some Game");
        var chosen = tree.Dir("other-drive/MyMods");

        var definition = new GameDefinition
        {
            Id = "generic-user-root",
            Name = "Generic Game",
            ModRoot = chosen,
            IsUserDefined = true,
            IsGenericFallback = true,
        };

        var resolved = DeploymentPlanner.ResolveDeploymentRoot(definition, install);

        Assert.Equal(Path.GetFullPath(chosen), resolved);
    }

    /// <summary>The application's protection list rejects system locations and accepts ordinary folders.</summary>
    [Fact]
    public void ProtectionListClassifiesLocations()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));
        paths.EnsureCreated();

        var safety = new ModManager.Core.Safety.PathSafetyService(paths, new SafetyOptions());

        Assert.True(safety.IsProtectedLocation(@"C:\Windows"));
        Assert.True(safety.IsProtectedLocation(@"C:\Windows\System32"));
        Assert.True(safety.IsProtectedLocation(@"C:\"));
        Assert.False(safety.IsProtectedLocation(tree.Dir("ordinary/mods")));
    }
}
