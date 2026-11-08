using System.IO;
using System.Security.Cryptography;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// The package library is the seam between acquisition and installation. These tests pin the three
/// properties the rest of the engine relies on: content identity, source identity, and a rebuildable
/// on-disk record that never points at the download cache.
/// </summary>
public sealed class PackageLibraryTests
{
    [Fact]
    public async Task Commit_CopiesArchiveAndCreatesPackageRecord()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("Data"));
        paths.EnsureCreated();
        var library = new PackageLibrary(paths);
        var archive = TestArchives.CreateZip(tree, "example.zip", ("Mods/Example.dll", "binary"));

        var result = await library.CommitAsync(new PackageCommitRequest
        {
            ArchivePath = archive,
            Source = new PackageSourceRef
            {
                Platform = PackagePlatforms.NexusMods,
                GameKey = "testgame",
                ModKey = "1234",
                FileKey = "5678",
                PageUrl = "https://www.nexusmods.com/testgame/mods/1234",
            },
            Name = "Example",
            Version = "1.2.3",
        });

        Assert.True(result.Succeeded, result.Error);
        var package = Assert.IsType<ModPackage>(result.Value);
        Assert.Equal("nexusmods:testgame:1234:5678", package.PackageId);
        Assert.True(File.Exists(package.ArchivePath));
        Assert.True(
            Path.GetFullPath(package.ArchivePath).StartsWith(Path.GetFullPath(paths.Library), StringComparison.OrdinalIgnoreCase),
            "The library must own the committed archive.");
        Assert.Equal(PackageState.Ready, package.State);
        Assert.Equal(new FileInfo(archive).Length, package.ArchiveSizeBytes);
        Assert.Single(library.All());
    }

    [Fact]
    public async Task Commit_IsIdempotentByHash()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("Data"));
        paths.EnsureCreated();
        var library = new PackageLibrary(paths);
        var archive = TestArchives.CreateZip(tree, "same.zip", ("a.txt", "same"));

        var first = await library.CommitAsync(Request(archive, "mod-a"));
        var second = await library.CommitAsync(Request(archive, "mod-b"));

        Assert.True(first.Succeeded, first.Error);
        Assert.True(second.Succeeded, second.Error);
        Assert.Equal(first.Value!.PackageId, second.Value!.PackageId);
        Assert.Single(library.All());
    }

    [Fact]
    public async Task Commit_SameArchiveForDifferentGames_IsStoredPerGame()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("Data"));
        paths.EnsureCreated();
        var library = new PackageLibrary(paths);
        var archive = TestArchives.CreateZip(tree, "shared.zip", ("a.txt", "shared"));

        var first = await library.CommitAsync(Request(archive, "mod-a", "game-a"));
        var second = await library.CommitAsync(Request(archive, "mod-a", "game-b"));

        Assert.True(first.Succeeded, first.Error);
        Assert.True(second.Succeeded, second.Error);
        Assert.NotEqual(first.Value!.PackageId, second.Value!.PackageId);
        Assert.Single(library.ForGame("game-a"));
        Assert.Single(library.ForGame("game-b"));
        Assert.NotNull(library.FindByHash(first.Value.ArchiveSha256, "game-a"));
        Assert.NotNull(library.FindByHash(first.Value.ArchiveSha256, "game-b"));
    }

    [Fact]
    public async Task Commit_SameSourceWithDifferentBytes_IsRejected()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("Data"));
        paths.EnsureCreated();
        var library = new PackageLibrary(paths);
        var firstArchive = TestArchives.CreateZip(tree, "first.zip", ("a.txt", "first"));
        var secondArchive = TestArchives.CreateZip(tree, "second.zip", ("a.txt", "second"));

        var first = await library.CommitAsync(Request(firstArchive, "same-mod"));
        var second = await library.CommitAsync(Request(secondArchive, "same-mod"));

        Assert.True(first.Succeeded, first.Error);
        Assert.False(second.Succeeded);
        Assert.Contains("内容哈希不同", second.Error);
        Assert.Single(library.All());
    }

    [Fact]
    public async Task Verify_MarksCorruptWhenTheArchiveChanges()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("Data"));
        paths.EnsureCreated();
        var library = new PackageLibrary(paths);
        var archive = TestArchives.CreateZip(tree, "verify.zip", ("a.txt", "original"));
        var committed = await library.CommitAsync(Request(archive, "verify"));
        Assert.True(committed.Succeeded, committed.Error);

        File.WriteAllText(committed.Value!.ArchivePath, "tampered");

        var verified = await library.VerifyAsync(committed.Value.PackageId);
        Assert.True(verified.Succeeded, verified.Error);
        Assert.Equal(PackageState.Corrupt, verified.Value!.State);
    }

    [Fact]
    public async Task Remove_DeletesOnlyTheManagedPackageDirectory()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("Data"));
        paths.EnsureCreated();
        var library = new PackageLibrary(paths);
        var archive = TestArchives.CreateZip(tree, "remove.zip", ("a.txt", "content"));
        var committed = await library.CommitAsync(Request(archive, "remove"));
        Assert.True(committed.Succeeded, committed.Error);
        var directory = Path.GetDirectoryName(committed.Value!.ArchivePath)!;

        var removed = await library.RemoveAsync(committed.Value.PackageId);

        Assert.True(removed.Succeeded, removed.Error);
        Assert.False(Directory.Exists(directory));
        Assert.Empty(library.All());
        Assert.True(File.Exists(archive), "The original uploaded archive must not be touched.");
    }

    [Fact]
    public async Task ImportLocal_ReusesAnAlreadyImportedArchive()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("Data"));
        paths.EnsureCreated();
        var library = new PackageLibrary(paths);
        var archive = TestArchives.CreateZip(tree, "local.zip", ("a.txt", "local"));

        var first = await library.ImportLocalAsync(archive, "testgame");
        var second = await library.ImportLocalAsync(archive, "testgame");

        Assert.True(first.Succeeded, first.Error);
        Assert.True(second.Succeeded, second.Error);
        Assert.False(first.Value!.WasAlreadyPresent);
        Assert.True(second.Value!.WasAlreadyPresent);
        Assert.True(
            second.Value.Package.PackageId.StartsWith("local:", StringComparison.Ordinal),
            second.Value.Package.PackageId);
        Assert.Single(library.All());
    }

    private static PackageCommitRequest Request(string archive, string modKey, string gameKey = "testgame") => new()
    {
        ArchivePath = archive,
        Source = new PackageSourceRef
        {
            Platform = PackagePlatforms.NexusMods,
            GameKey = gameKey,
            ModKey = modKey,
            FileKey = "file",
        },
        Name = modKey,
        Version = "1.0.0",
        ExpectedSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))).ToLowerInvariant(),
    };
}
