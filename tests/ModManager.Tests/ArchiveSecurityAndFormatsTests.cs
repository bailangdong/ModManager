using System.IO;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Archive format coverage and the hostile-input contract: every entry path is refused rather than
/// sanitised, and extraction is confined to the caller-supplied root.
/// </summary>
/// <remarks>
/// All archives are built or generated locally; nothing is downloaded. ZIP gets a full round-trip
/// via the BCL writer; 7z uses fixtures generated locally with py7zr (normal + encrypted). RAR has
/// no local writer, so RAR coverage is signature detection plus the corrupt-archive error path —
/// the decode path itself is SharpCompress's <c>RarArchive</c>.
/// </remarks>
public sealed class ArchiveSecurityAndFormatsTests
{
    private const string GameMarker = "Mods";

    // ---------------------------------------------------------------- ZIP

    /// <summary>A normal zip is indexed and extracted byte-for-byte.</summary>
    [Fact]
    public async Task ZipIndexesAndExtractsNormalEntries()
    {
        using var tree = new TemporaryTree();
        var archive = TestArchives.CreateZip(
            tree,
            "mod.zip",
            ("MyMod/Mods/data/config.json", "{\"a\":1}"),
            ("MyMod/Mods/plugins/mod.dll", "dll-bytes"),
            ("MyMod/README.txt", "readme"));

        var reader = new ArchiveReader();

        Assert.Equal(ArchiveFormat.Zip, reader.DetectFormat(archive));
        Assert.True(reader.CanRead(ArchiveFormat.Zip));

        var index = await reader.IndexAsync(archive);
        Assert.Equal(3, index.Entries.Count);

        var extraction = await reader.ExtractAsync(archive, tree.Dir("out"), [.. index.Entries.Select(e => e.Path)]);
        Assert.True(extraction.IsClean);
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(tree.Root, "out", "MyMod", "Mods", "data", "config.json")));
        Assert.Equal("dll-bytes", File.ReadAllText(Path.Combine(tree.Root, "out", "MyMod", "Mods", "plugins", "mod.dll")));
    }

    /// <summary>Parent traversal is refused at extraction, and nothing is written outside the root.</summary>
    [Fact]
    public async Task ZipRefusesParentTraversalAtExtraction()
    {
        using var tree = new TemporaryTree();
        var archive = TestArchives.CreateZip(
            tree,
            "evil.zip",
            ("ok.txt", "fine"),
            ("../escape.txt", "bad"));

        var reader = new ArchiveReader();
        var index = await reader.IndexAsync(archive);

        var traversal = index.Entries.Single(e => e.Path == "../escape.txt");
        var extraction = await reader.ExtractAsync(archive, tree.Dir("out"), [.. index.Entries.Select(e => e.Path)]);

        var rejection = extraction.Rejected.Single();
        Assert.True(rejection.IsSecurityRejection);
        Assert.Contains("..", rejection.EntryPath);
        Assert.False(File.Exists(Path.Combine(tree.Root, "escape.txt")), "Nothing may be written outside the extraction root.");
        Assert.True(File.Exists(Path.Combine(tree.Root, "out", "ok.txt")));
    }

    /// <summary>An absolute entry path is dropped from the index entirely.</summary>
    [Fact]
    public async Task ZipDropsAbsolutePathEntriesFromTheIndex()
    {
        using var tree = new TemporaryTree();
        var archive = TestArchives.CreateZip(tree, "abs.zip", ("/absolute/evil.txt", "bad"), ("ok.txt", "fine"));

        var index = await new ArchiveReader().IndexAsync(archive);

        Assert.DoesNotContain(index.Entries, e => e.Path.Contains("absolute"));
        Assert.Single(index.Entries);
    }

    /// <summary>A colon (alternate data stream) in an entry name is a security refusal.</summary>
    [Fact]
    public async Task ZipRefusesAlternateDataStreamEntryNames()
    {
        using var tree = new TemporaryTree();
        var archive = TestArchives.CreateZip(tree, "ads.zip", ("Mods/evil.dll:stream", "bad"), ("Mods/ok.dll", "fine"));

        var reader = new ArchiveReader();
        var index = await reader.IndexAsync(archive);
        var extraction = await reader.ExtractAsync(archive, tree.Dir("out"), [.. index.Entries.Select(e => e.Path)]);

        var rejection = extraction.Rejected.Single();
        Assert.True(rejection.IsSecurityRejection);
        Assert.Contains("colon", rejection.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Duplicate entry names both extract, and the last one wins — the documented zip semantics.</summary>
    [Fact]
    public async Task ZipDuplicateEntriesExtractWithLastEntryWinning()
    {
        using var tree = new TemporaryTree();
        var archive = TestArchives.CreateZip(tree, "dup.zip", ("a.txt", "first"), ("a.txt", "second"));

        var reader = new ArchiveReader();
        var index = await reader.IndexAsync(archive);
        Assert.Equal(2, index.Entries.Count);

        var extraction = await reader.ExtractAsync(archive, tree.Dir("out"), [.. index.Entries.Select(e => e.Path)]);
        Assert.True(extraction.IsClean);
        Assert.Equal("second", File.ReadAllText(Path.Combine(tree.Root, "out", "a.txt")));
    }

    /// <summary>An empty zip indexes to no entries and extracts to nothing.</summary>
    [Fact]
    public async Task ZipEmptyArchiveIndexesToNoEntries()
    {
        using var tree = new TemporaryTree();
        var archive = TestArchives.CreateZip(tree, "empty.zip");

        var index = await new ArchiveReader().IndexAsync(archive);

        Assert.Empty(index.Entries);
    }

    /// <summary>A file that is not an archive is refused with a clear error.</summary>
    [Fact]
    public async Task UnsupportedFormatIsRefusedWithAClearError()
    {
        using var tree = new TemporaryTree();
        var file = tree.WriteFile("not-an-archive.exe", "MZ garbage");

        await Assert.ThrowsAsync<NotSupportedException>(() => new ArchiveReader().IndexAsync(file));
    }

    /// <summary>A corrupt zip fails loudly rather than producing a half index.</summary>
    [Fact]
    public async Task ZipCorruptArchiveThrowsInvalidData()
    {
        using var tree = new TemporaryTree();
        var file = tree.WriteBytes("broken.zip", [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00]);

        await Assert.ThrowsAsync<InvalidDataException>(() => new ArchiveReader().IndexAsync(file));
    }

    // ---------------------------------------------------------------- 7z

    /// <summary>A 7z archive is detected, indexed and extracted.</summary>
    [Fact]
    public async Task SevenZipIndexesAndExtractsNormalEntries()
    {
        using var tree = new TemporaryTree();
        var archive = TestArchives.FixturePath("sample.7z");

        var reader = new ArchiveReader();

        Assert.Equal(ArchiveFormat.SevenZip, reader.DetectFormat(archive));
        Assert.True(reader.CanRead(ArchiveFormat.SevenZip));

        var index = await reader.IndexAsync(archive);
        Assert.Equal(2, index.Entries.Count);

        var extraction = await reader.ExtractAsync(archive, tree.Dir("out"), [.. index.Entries.Select(e => e.Path)]);
        Assert.True(extraction.IsClean);
        Assert.Equal("dll-bytes", File.ReadAllText(Path.Combine(tree.Root, "out", "MyMod", "Mods", "plugins", "mod.dll")));
        Assert.Equal("[core]\nenabled=1", File.ReadAllText(Path.Combine(tree.Root, "out", "MyMod", "Mods", "config.ini")));
    }

    /// <summary>Encrypted entries are reported as refused, never written.</summary>
    /// <remarks>
    /// The traversal-refusal path is proven through the shared validator by the zip traversal test —
    /// both formats resolve destinations through <see cref="ArchiveReader.TryResolveSafeDestination"/>.
    /// A 7z archive containing a traversal entry cannot be produced locally (no local 7z writer
    /// accepts such names), which is stated here rather than papered over.
    /// </remarks>
    [Fact]
    public async Task SevenZipEncryptedEntriesAreRefused()
    {
        using var tree = new TemporaryTree();
        var archive = TestArchives.FixturePath("encrypted.7z");

        var reader = new ArchiveReader();
        var index = await reader.IndexAsync(archive);
        Assert.Single(index.Entries);

        var extraction = await reader.ExtractAsync(archive, tree.Dir("out"), [.. index.Entries.Select(e => e.Path)]);

        Assert.Contains(extraction.Rejected, r => r.Reason.Contains("encrypted", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(tree.Root, "out", "Mods", "secret.dll")));
    }

    // ---------------------------------------------------------------- RAR

    /// <summary>The RAR signature is recognised.</summary>
    [Fact]
    public void RarIsDetectedBySignature()
    {
        using var tree = new TemporaryTree();
        var file = TestArchives.CreateCorruptRar(tree, "sample.rar");

        Assert.Equal(ArchiveFormat.Rar, new ArchiveReader().DetectFormat(file));
    }

    /// <summary>A corrupt RAR fails with a clear message naming the file.</summary>
    [Fact]
    public async Task RarCorruptArchiveFailsWithAClearError()
    {
        using var tree = new TemporaryTree();
        var file = TestArchives.CreateCorruptRar(tree, "broken.rar");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => new ArchiveReader().IndexAsync(file));

        Assert.Contains("broken.rar", exception.Message);
        Assert.Contains("archive", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
