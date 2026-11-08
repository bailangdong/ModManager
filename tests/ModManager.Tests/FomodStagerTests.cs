using System.IO;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

public sealed class FomodStagerTests
{
    [Fact]
    public async Task StageAsync_SelectedFile_IsWrittenToDestination()
    {
        using var tree = new TemporaryTree();
        var archive = TestArchives.CreateZip(
            tree,
            "fomod.zip",
            ("fomod/ModuleConfig.xml", "<config />"),
            ("A/a.dll", "a"),
            ("B/b.dll", "b"));

        var plan = Plan(
            FomodGroupType.SelectExactlyOne,
            Option("A", "A/a.dll", "Data/A.dll"),
            Option("B", "B", "Data/B", isFolder: true));

        var result = await FomodStager.StageAsync(
            archive,
            plan,
            [new FomodSelection(0, 0, [0])],
            Path.Combine(tree.Root, "staging"),
            new ArchiveReader());

        Assert.True(result.Succeeded, result.Error);
        var file = Assert.Single(result.Value!.WrittenFiles);
        Assert.Equal("Data/A.dll", file);
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(tree.Root, "staging", "Data", "A.dll")));
    }

    [Fact]
    public async Task StageAsync_SelectedFolder_CopiesNestedFiles()
    {
        using var tree = new TemporaryTree();
        var archive = TestArchives.CreateZip(
            tree,
            "fomod.zip",
            ("fomod/ModuleConfig.xml", "<config />"),
            ("B/b.dll", "b"),
            ("B/sub/c.dll", "c"));

        var plan = Plan(
            FomodGroupType.SelectExactlyOne,
            Option("A", "A/a.dll", "Data/A.dll"),
            Option("B", "B", "Data/B", isFolder: true));

        var result = await FomodStager.StageAsync(
            archive,
            plan,
            [new FomodSelection(0, 0, [1])],
            Path.Combine(tree.Root, "staging"),
            new ArchiveReader());

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, result.Value!.WrittenFiles.Count);
        Assert.Contains("Data/B/b.dll", result.Value.WrittenFiles);
        Assert.Contains("Data/B/sub/c.dll", result.Value.WrittenFiles);
        Assert.True(File.Exists(Path.Combine(tree.Root, "staging", "Data", "B", "sub", "c.dll")));
    }

    [Fact]
    public async Task StageAsync_UnsafeDestination_Fails()
    {
        using var tree = new TemporaryTree();
        var archive = TestArchives.CreateZip(
            tree,
            "fomod.zip",
            ("fomod/ModuleConfig.xml", "<config />"),
            ("a.dll", "a"));

        var plan = Plan(
            FomodGroupType.SelectExactlyOne,
            Option("A", "a.dll", "../outside.dll"));

        var result = await FomodStager.StageAsync(
            archive,
            plan,
            [new FomodSelection(0, 0, [0])],
            Path.Combine(tree.Root, "staging"),
            new ArchiveReader());

        Assert.False(result.Succeeded);
        Assert.Contains("越界", result.Error);
        Assert.False(File.Exists(Path.Combine(tree.Root, "outside.dll")));
    }

    [Fact]
    public async Task StageAsync_MissingSource_FailsWhenNothingWritten()
    {
        using var tree = new TemporaryTree();
        var archive = TestArchives.CreateZip(
            tree,
            "fomod.zip",
            ("fomod/ModuleConfig.xml", "<config />"),
            ("other.dll", "other"));

        var plan = Plan(
            FomodGroupType.SelectExactlyOne,
            Option("A", "missing.dll", "Data/missing.dll"));

        var result = await FomodStager.StageAsync(
            archive,
            plan,
            [new FomodSelection(0, 0, [0])],
            Path.Combine(tree.Root, "staging"),
            new ArchiveReader());

        Assert.False(result.Succeeded);
        Assert.Contains("没有产生", result.Error);
    }

    private static FomodPlan Plan(FomodGroupType type, params FomodOption[] options) =>
        new(
            "Test",
            [new FomodStep("Step", [new FomodGroup("Group", type, options)])],
            [],
            []);

    private static FomodOption Option(
        string name,
        string source,
        string destination,
        bool isFolder = false) =>
        new(
            name,
            null,
            [new FomodFile(source, destination, 0, isFolder)],
            []);
}
