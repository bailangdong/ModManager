using System.IO;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Regression cover for <see cref="ArchiveReader.DetectFormat"/>.
/// </summary>
/// <remarks>
/// DetectFormat is the first thing that happens to a user's download, and it reads the file header into a
/// buffer that is then compared with LINQ. That buffer used to be a <c>stackalloc</c> <see cref="Span{T}"/>,
/// which a lambda cannot capture (CS8175) — it is a heap array now. These tests exist so the comparison
/// against every signature keeps working, including the ones that read fewer than eight bytes.
/// </remarks>
public sealed class ArchiveReaderTests
{
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] Rar5Signature = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];
    private static readonly byte[] SevenZipSignature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    private static readonly byte[] GzipSignature = [0x1F, 0x8B];

    /// <summary>ZIP is identified from the local-file header, not from the file name.</summary>
    [Fact]
    public void DetectsZipFromItsMagicBytes()
    {
        using var tree = new TemporaryTree();
        var path = tree.WriteBytes("download.bin", [.. ZipSignature, 0x14, 0x00, 0x00, 0x00]);

        Assert.Equal(ArchiveFormat.Zip, new ArchiveReader().DetectFormat(path));
    }

    /// <summary>RAR5 needs all eight header bytes, so this also covers the length guard.</summary>
    [Fact]
    public void DetectsRarFromItsMagicBytes()
    {
        using var tree = new TemporaryTree();
        var path = tree.WriteBytes("download.bin", Rar5Signature);

        Assert.Equal(ArchiveFormat.Rar, new ArchiveReader().DetectFormat(path));
    }

    /// <summary>7z has a six-byte signature, which is shorter than the read buffer.</summary>
    [Fact]
    public void DetectsSevenZipFromItsMagicBytes()
    {
        using var tree = new TemporaryTree();
        var path = tree.WriteBytes("download.bin", [.. SevenZipSignature, 0x00, 0x04]);

        Assert.Equal(ArchiveFormat.SevenZip, new ArchiveReader().DetectFormat(path));
    }

    /// <summary>Gzip has only a two-byte signature — the shortest case in the comparison.</summary>
    [Fact]
    public void DetectsGzipFromItsMagicBytes()
    {
        using var tree = new TemporaryTree();
        var path = tree.WriteBytes("download.bin", [.. GzipSignature, 0x08, 0x00, 0x00, 0x00]);

        Assert.Equal(ArchiveFormat.Gzip, new ArchiveReader().DetectFormat(path));
    }

    /// <summary>An unreadable header still resolves from the extension, so an odd archive is attempted.</summary>
    [Fact]
    public void FallsBackToTheExtensionWhenTheBytesAreNotASignature()
    {
        using var tree = new TemporaryTree();
        var path = tree.WriteFile("download.zip", "this is not really an archive");

        Assert.Equal(ArchiveFormat.Zip, new ArchiveReader().DetectFormat(path));
    }

    /// <summary>Extensions that we know but cannot read are still recognised by name.</summary>
    [Fact]
    public void RecognisesSevenZipAndRarByExtensionAlone()
    {
        using var tree = new TemporaryTree();
        var reader = new ArchiveReader();

        Assert.Equal(ArchiveFormat.SevenZip, reader.DetectFormat(tree.WriteFile("a.7z", "x")));
        Assert.Equal(ArchiveFormat.Rar, reader.DetectFormat(tree.WriteFile("b.rar", "x")));
    }

    /// <summary>An empty file reads zero bytes; that must not be mistaken for a signature.</summary>
    [Fact]
    public void TreatsAnEmptyFileAsUnknown()
    {
        using var tree = new TemporaryTree();
        var path = tree.WriteFile("empty.bin", string.Empty);

        Assert.Equal(ArchiveFormat.Unknown, new ArchiveReader().DetectFormat(path));
    }

    /// <summary>Unknown bytes plus an unknown extension is honestly reported rather than guessed.</summary>
    [Fact]
    public void ReportsUnknownWhenNeitherBytesNorExtensionMatch()
    {
        using var tree = new TemporaryTree();
        var path = tree.WriteFile("payload.dat", "hello");

        Assert.Equal(ArchiveFormat.Unknown, new ArchiveReader().DetectFormat(path));
    }
}
