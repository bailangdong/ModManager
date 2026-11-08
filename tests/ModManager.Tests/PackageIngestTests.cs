using System.IO;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Promotion from the download cache into the package library is the seam that makes "download"
/// and "install" separate actions. These tests pin that a completed record gets a package id and
/// that the library owns the bytes afterwards.
/// </summary>
public sealed class PackageIngestTests
{
    [Fact]
    public async Task Ingest_CompletedDownload_CreatesPackageAndWritesIdBack()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("Data"));
        paths.EnsureCreated();
        var library = new PackageLibrary(paths);
        var archive = TestArchives.CreateZip(tree, "download.zip", ("Mods/Example.dll", "bytes"));
        var record = new DownloadRecord
        {
            Id = "download-1",
            FileName = "download.zip",
            Url = "https://example.invalid/download.zip",
            TargetPath = archive,
            PartialPath = archive + ".part",
            State = DownloadState.Completed,
            GameId = "testgame",
            PackageSource = new PackageSourceRef
            {
                Platform = PackagePlatforms.NexusMods,
                GameKey = "testgame",
                ModKey = "42",
                FileKey = "99",
            },
            PackageName = "Example",
            PackageVersion = "1.0.0",
        };

        var downloads = new MemoryDownloads(record);
        var ingest = new PackageIngestService(library, downloads, new NoopLogger());

        var package = await ingest.IngestAsync(record);

        Assert.NotNull(package);
        Assert.Single(library.All());
        Assert.Equal("nexusmods:testgame:42:99", package!.PackageId);
        Assert.Equal(package.PackageId, downloads.Get("download-1")!.PackageId);
        Assert.True(
            Path.GetFullPath(package.ArchivePath).StartsWith(
                Path.GetFullPath(paths.Library),
                StringComparison.OrdinalIgnoreCase),
            "The package library must own the promoted archive.");
    }

    [Fact]
    public async Task Ingest_IsIdempotentWhenTheRecordAlreadyHasAPackageId()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("Data"));
        paths.EnsureCreated();
        var library = new PackageLibrary(paths);
        var archive = TestArchives.CreateZip(tree, "download.zip", ("a.txt", "a"));
        var record = new DownloadRecord
        {
            Id = "download-2",
            FileName = "download.zip",
            Url = "https://example.invalid/download.zip",
            TargetPath = archive,
            PartialPath = archive + ".part",
            State = DownloadState.Completed,
            PackageId = "already-committed",
        };

        var downloads = new MemoryDownloads(record);
        var ingest = new PackageIngestService(library, downloads, new NoopLogger());

        var package = await ingest.IngestAsync(record);

        Assert.Null(package);
        Assert.Empty(library.All());
    }

    private sealed class MemoryDownloads : IDownloadRepository
    {
        private readonly Dictionary<string, DownloadRecord> _records = new(StringComparer.Ordinal);

        public MemoryDownloads(params DownloadRecord[] records)
        {
            foreach (var record in records)
            {
                _records[record.Id] = record;
            }
        }

        public IReadOnlyList<DownloadRecord> All() => _records.Values.ToList();

        public DownloadRecord? Get(string downloadId) =>
            _records.TryGetValue(downloadId, out var record) ? record : null;

        public void Upsert(DownloadRecord record) => _records[record.Id] = record;

        public bool Remove(string downloadId) => _records.Remove(downloadId);
    }

    private sealed class NoopLogger : IAppLogger
    {
        public void Log(LogEntry entry)
        {
        }

        public IReadOnlyList<LogEntry> Recent(int count = 500) => [];

        public event EventHandler<LogEntry>? EntryWritten
        {
            add { }
            remove { }
        }
    }
}
