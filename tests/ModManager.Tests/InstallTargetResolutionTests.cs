using System.IO;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Target resolution must be driven by the game definition and its loader, never by the mod's name.
/// These tests pin the two fallbacks that make that true for loader-only definitions.
/// </summary>
public sealed class InstallTargetResolutionTests
{
    [Fact]
    public void LoaderPluginsDirectoryIsUsedWhenModRootIsEmpty()
    {
        var install = Path.Combine(Path.GetTempPath(), "mm-target-" + Guid.NewGuid().ToString("N"));
        var definition = new GameDefinition
        {
            Id = "loaderonly",
            Name = "Loader Only",
            Loader = new ModLoaderDefinition
            {
                Kind = ModLoaderKind.BepInEx,
                PluginsDirectory = "BepInEx/plugins",
            },
        };

        var root = DeploymentPlanner.ResolveDeploymentRoot(definition, install);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(install, "BepInEx", "plugins")),
            root);
    }

    [Fact]
    public void ExplicitModRootWinsOverLoaderDirectory()
    {
        var install = Path.Combine(Path.GetTempPath(), "mm-target-" + Guid.NewGuid().ToString("N"));
        var definition = new GameDefinition
        {
            Id = "explicit",
            Name = "Explicit Root",
            ModRoot = "Mods",
            Loader = new ModLoaderDefinition
            {
                Kind = ModLoaderKind.BepInEx,
                PluginsDirectory = "BepInEx/plugins",
            },
        };

        var root = DeploymentPlanner.ResolveDeploymentRoot(definition, install);

        Assert.Equal(Path.GetFullPath(Path.Combine(install, "Mods")), root);
    }
}
