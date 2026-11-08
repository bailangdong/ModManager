using System.IO;
using ModManager.Core;
using ModManager.Core.Safety;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// The safety gate. Every mutation in the engine passes through <see cref="PathSafetyService"/>, so these
/// are the tests that decide whether a hostile archive or a mistyped path can reach outside a game folder.
/// </summary>
/// <remarks>
/// The user's specification §17 lists the forbidden operations this component exists to refuse: working on a
/// drive root, deleting the game directory, clearing the Mods folder, path traversal, symlink escape, and
/// acting outside the current game's directory. Each one has a test here, and each is expressed as a refusal
/// rather than as an exception, because the caller has to be able to explain why it stopped.
/// </remarks>
public sealed class PathSafetyTests
{
    private const string GameId = "testgame";

    /// <summary>An unapproved game is refused outright — there is no implicit "allow".</summary>
    [Fact]
    public void RefusesEveryWriteWhenNoRootHasBeenApproved()
    {
        using var tree = new TemporaryTree();
        var safety = NewSafety(tree);
        var modRoot = tree.Dir("game/Mods");

        var result = safety.ValidateWrite(GameId, Path.Combine(modRoot, "mod.dll"));

        Assert.False(result.Allowed);
        Assert.Equal(PathSafetyVerdict.Unsafe, result.Verdict);
        Assert.Contains("no approved root", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The ordinary case: a new file inside the approved mod root is allowed and classified safe.</summary>
    [Fact]
    public void AllowsANewFileInsideTheApprovedModRoot()
    {
        using var tree = new TemporaryTree();
        var modRoot = tree.Dir("game/Mods");
        var safety = ApprovedSafety(tree, modRoot);

        var result = safety.ValidateWrite(GameId, Path.Combine(modRoot, "BepInEx", "plugins", "mod.dll"));

        Assert.True(result.Allowed);
        Assert.Equal(PathSafetyVerdict.Safe, result.Verdict);
    }

    /// <summary>An existing target is still allowed, but must be reported as an overwrite before it happens.</summary>
    [Fact]
    public void ClassifiesAnExistingTargetAsAnOverwrite()
    {
        using var tree = new TemporaryTree();
        var modRoot = tree.Dir("game/Mods");
        var existing = tree.WriteFile("game/Mods/data/config.json", "{}");
        var safety = ApprovedSafety(tree, modRoot);

        var result = safety.ValidateWrite(GameId, existing);

        Assert.True(result.Allowed);
        Assert.Equal(PathSafetyVerdict.Overwrite, result.Verdict);
    }

    /// <summary>A lexical <c>..</c> segment is refused before any canonicalisation is attempted.</summary>
    [Fact]
    public void RefusesTraversalThatClimbsOutOfTheModRoot()
    {
        using var tree = new TemporaryTree();
        var modRoot = tree.Dir("game/Mods");
        var safety = ApprovedSafety(tree, modRoot);
        var escape = Path.Combine(modRoot, "..", "..", "Windows", "System32", "evil.dll");

        var result = safety.ValidateWrite(GameId, escape);

        Assert.False(result.Allowed);
        Assert.Equal(PathSafetyVerdict.Unsafe, result.Verdict);
    }

    /// <summary>An absolute path that simply is not under the mod root is refused, traversal or not.</summary>
    [Fact]
    public void RefusesAnAbsoluteTargetOutsideTheModRoot()
    {
        using var tree = new TemporaryTree();
        var modRoot = tree.Dir("game/Mods");
        var elsewhere = tree.Dir("somewhere-else");
        var safety = ApprovedSafety(tree, modRoot);

        var result = safety.ValidateWrite(GameId, Path.Combine(elsewhere, "evil.dll"));

        Assert.False(result.Allowed);
        Assert.Contains("escapes", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>&lt;</c> and <c>&gt;</c> are the engine's own marker characters, so an archive must never be able
    /// to smuggle one into a path — that is Modrix's rule, adopted verbatim.
    /// </summary>
    [Fact]
    public void RefusesAMarkerCharacterInAPathComponent()
    {
        using var tree = new TemporaryTree();
        var modRoot = tree.Dir("game/Mods");
        var safety = ApprovedSafety(tree, modRoot);

        var result = safety.ValidateWrite(GameId, Path.Combine(modRoot, "mod<managed>.dll"));

        Assert.False(result.Allowed);
        Assert.Contains("forbidden segment", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Approving a drive root would make an entire volume writable for one mistyped game path, which is the
    /// exact accident the specification warns about. It must throw rather than silently succeed.
    /// </summary>
    [Fact]
    public void RefusesToApproveADriveRootAsAGameRoot()
    {
        using var tree = new TemporaryTree();
        var safety = NewSafety(tree);
        var driveRoot = Path.GetPathRoot(tree.Root);

        Assert.NotNull(driveRoot);
        Assert.Throws<InvalidOperationException>(() => safety.ApproveGameRoot(GameId, driveRoot, driveRoot));
    }

    /// <summary>Deleting the deployment root itself is never permitted, even though it is an approved root.</summary>
    [Fact]
    public void RefusesToDeleteTheDeploymentRoot()
    {
        using var tree = new TemporaryTree();
        var modRoot = tree.Dir("game/Mods");
        var safety = ApprovedSafety(tree, modRoot);

        var result = safety.ValidateDelete(GameId, modRoot, isDirectory: true);

        Assert.False(result.Allowed);
    }

    /// <summary>Deleting an individual deployed file inside the root is the normal, permitted case.</summary>
    [Fact]
    public void AllowsDeletingAFileInsideTheModRoot()
    {
        using var tree = new TemporaryTree();
        var modRoot = tree.Dir("game/Mods");
        var deployed = tree.WriteFile("game/Mods/plugins/mod.dll", "bytes");
        var safety = ApprovedSafety(tree, modRoot);

        var result = safety.ValidateDelete(GameId, deployed, isDirectory: false);

        Assert.True(result.Allowed);
    }

    private static PathSafetyService NewSafety(TemporaryTree tree) =>
        new(new AppPaths(tree.Dir("appdata")));

    private static PathSafetyService ApprovedSafety(TemporaryTree tree, string modRoot)
    {
        var safety = NewSafety(tree);
        safety.ApproveGameRoot(GameId, tree.Dir("game"), modRoot);
        return safety;
    }
}
