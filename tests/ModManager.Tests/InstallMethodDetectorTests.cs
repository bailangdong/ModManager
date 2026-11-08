using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// The classifier is intentionally conservative: it must recognise the strong structural signals
/// (FOMOD, loader distributions, loader-specific layouts) and fall back to a low-confidence direct
/// copy rather than guessing a path.
/// </summary>
public sealed class InstallMethodDetectorTests
{
    private readonly InstallMethodDetector _detector = new();

    [Fact]
    public void Fomod_IsDetectedAndRequiresAWizard()
    {
        var result = _detector.Analyze(new TestGameAdapter(), Index("fomod/ModuleConfig.xml"));

        Assert.Equal(InstallMethodKind.Fomod, result.Kind);
        Assert.True(result.RequiresWizard);
        Assert.True(result.NeedsConfirmation);
        Assert.False(result.CanAutoStage);
        Assert.Contains("向导", result.AutoStageBlockReason);
        Assert.True(result.Confidence > 0.9);
    }

    [Fact]
    public void BepInExDistribution_IsDetectedAsLoaderDistribution()
    {
        var result = _detector.Analyze(new TestGameAdapter(), Index("BepInEx/core/BepInEx.dll"));

        Assert.Equal(InstallMethodKind.LoaderDistribution, result.Kind);
        Assert.False(result.CanAutoStage);
        Assert.Contains("Loader", result.AutoStageBlockReason);
        Assert.True(result.Confidence > 0.9);
    }

    [Fact]
    public void SmapiManifest_IsDetectedOnlyForASmapiGame()
    {
        var definition = TestGame.Definition() with
        {
            Loader = new ModLoaderDefinition { Kind = ModLoaderKind.Smapi },
        };
        var adapter = new GenericGameAdapter(definition);

        var result = _detector.Analyze(adapter, Index("manifest.json", "ContentPack/a.json"));

        Assert.Equal(InstallMethodKind.Smapi, result.Kind);
        Assert.False(result.RequiresWizard);
        Assert.True(result.CanAutoStage);
        Assert.Null(result.AutoStageBlockReason);
    }

    [Fact]
    public void UnknownLayout_FallsBackToLowConfidenceDirectCopy()
    {
        var result = _detector.Analyze(new TestGameAdapter(), Index("readme.txt", "data/config.json"));

        Assert.Equal(InstallMethodKind.DirectCopy, result.Kind);
        Assert.True(result.NeedsConfirmation);
    }

    private static ArchiveIndex Index(params string[] paths) =>
        new("test.zip", paths.Select(path => new ArchiveEntry(path, false, 10)).ToList());
}
