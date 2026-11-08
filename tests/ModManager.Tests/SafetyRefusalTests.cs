using System.IO;
using ModManager.Core.Safety;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// The protected-location rules the spec names explicitly: Windows and Program Files directories
/// must never be approvable as game roots, and dangerous path shapes are refused before any I/O.
/// </summary>
public sealed class SafetyRefusalTests
{
    /// <summary>The Windows directory can never be approved as a game root.</summary>
    [Fact]
    public void WindowsDirectoryCannotBeApprovedAsAGameRoot()
    {
        using var tree = new TemporaryTree();
        var safety = new PathSafetyService(new Core.AppPaths(tree.Dir("appdata")));
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.Throws<InvalidOperationException>(() =>
            safety.ApproveGameRoot("testgame", windows, windows));
    }

    /// <summary>The Program Files directory can never be approved as a game root.</summary>
    [Fact]
    public void ProgramFilesCannotBeApprovedAsAGameRoot()
    {
        using var tree = new TemporaryTree();
        var safety = new PathSafetyService(new Core.AppPaths(tree.Dir("appdata")));
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        Assert.Throws<InvalidOperationException>(() =>
            safety.ApproveGameRoot("testgame", programFiles, programFiles));
    }

    /// <summary>A path under Program Files is refused as a write target even with an approved root.</summary>
    [Fact]
    public void WriteUnderProgramFilesIsRefusedEvenWithAnApprovedRoot()
    {
        using var tree = new TemporaryTree();
        var gameRoot = tree.Dir("game");
        var safety = new PathSafetyService(new Core.AppPaths(tree.Dir("appdata")));
        safety.ApproveGameRoot("testgame", gameRoot, tree.Dir("game/Mods"));

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var result = safety.ValidateWrite("testgame", Path.Combine(programFiles, "evil.dll"));

        Assert.False(result.Allowed);
        Assert.Contains("protected", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A colon in a path segment (alternate data stream) is a forbidden segment.</summary>
    [Fact]
    public void AlternateDataStreamSegmentIsForbidden()
    {
        Assert.True(
            PathSafetyService.ContainsForbiddenSegment(@"C:\game\Mods\evil.dll:stream", out var segment));
        Assert.Contains(":", segment);
    }

    /// <summary>UNC roots are refused as write targets.</summary>
    [Fact]
    public void UncPathIsRefusedAsAWriteTarget()
    {
        using var tree = new TemporaryTree();
        var gameRoot = tree.Dir("game");
        var safety = new PathSafetyService(new Core.AppPaths(tree.Dir("appdata")));
        safety.ApproveGameRoot("testgame", gameRoot, tree.Dir("game/Mods"));

        var result = safety.ValidateWrite("testgame", @"\\server\share\evil.dll");

        Assert.False(result.Allowed);
    }

    /// <summary>A drive root is a forbidden segment even when written with a separator.</summary>
    [Fact]
    public void DriveRootWriteTargetIsRefused()
    {
        using var tree = new TemporaryTree();
        var gameRoot = tree.Dir("game");
        var safety = new PathSafetyService(new Core.AppPaths(tree.Dir("appdata")));
        safety.ApproveGameRoot("testgame", gameRoot, tree.Dir("game/Mods"));

        var result = safety.ValidateWrite("testgame", Path.GetPathRoot(gameRoot) ?? "C:\\");

        Assert.False(result.Allowed);
    }
}
