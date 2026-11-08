using System.Text.Json;
using ModManager.Core.Persistence;
using ModManager.Core.Safety;
using ModManager.Core.Serialization;

namespace ModManager.Core.Services;

/// <summary>
/// File-system package library. Archives are copied into the app data tree and indexed by
/// SHA-256 plus source identity; the index is rebuildable from the per-package JSON files.
/// </summary>
/// <remarks>
/// Layout:
/// <code>
/// Data/Library/packages/&lt;platform&gt;/&lt;game&gt;/&lt;mod&gt;/&lt;version&gt;__&lt;file&gt;/
///   archive.zip
///   package.json
/// Data/packages.json             (fast index; rebuildable from the package.json files)
/// </code>
/// The archive is never moved out of this tree by the installer; it is read in place, so a
/// package can be staged again for another profile or game.
/// </remarks>
public sealed class PackageLibrary : IPackageLibrary
{
    private readonly AppPaths _paths;
    private readonly IAppLogger? _logger;
    private readonly JsonDocumentStore _store;
    private readonly object _gate = new();
    private List<ModPackage> _packages;
    private bool _loaded;

    /// <summary>Creates the library.</summary>
    public PackageLibrary(AppPaths paths, IAppLogger? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger;
        _store = new JsonDocumentStore(paths, logger);
        _packages = [];
    }

    /// <inheritdoc />
    public IReadOnlyList<ModPackage> All()
    {
        EnsureLoaded();
        lock (_gate)
        {
            return _packages
                .OrderByDescending(package => package.AcquiredAt)
                .ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ModPackage> ForGame(string gameId)
    {
        if (string.IsNullOrWhiteSpace(gameId))
        {
            return [];
        }

        EnsureLoaded();
        lock (_gate)
        {
            return _packages
                .Where(package => string.Equals(package.Source.GameKey, gameId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(package => package.AcquiredAt)
                .ToList();
        }
    }

    /// <inheritdoc />
    public ModPackage? Get(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        EnsureLoaded();
        lock (_gate)
        {
            return _packages.FirstOrDefault(package =>
                string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <inheritdoc />
    public ModPackage? FindByHash(string sha256, string? gameId = null, string? platform = null)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return null;
        }

        EnsureLoaded();
        lock (_gate)
        {
            // The same bytes can be a valid package for more than one game (a shared framework
            // archive is the common case). Returning a package recorded for another game makes
            // ForGame() hide it, so scope the dedupe to the game when one is supplied. Platform
            // scoping keeps provenance intact: a local import must not silently turn an existing
            // Nexus package into a "local" one.
            return _packages.FirstOrDefault(package =>
                string.Equals(package.ArchiveSha256, sha256, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(gameId)
                    || string.Equals(package.Source.GameKey, gameId, StringComparison.OrdinalIgnoreCase))
                && (string.IsNullOrWhiteSpace(platform)
                    || string.Equals(package.Source.Platform, platform, StringComparison.OrdinalIgnoreCase)));
        }
    }

    /// <inheritdoc />
    public ModPackage? FindBySource(PackageSourceRef source)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureLoaded();
        lock (_gate)
        {
            return _packages.FirstOrDefault(package =>
                string.Equals(package.Source.Platform, source.Platform, StringComparison.OrdinalIgnoreCase)
                && string.Equals(package.Source.GameKey, source.GameKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(package.Source.ModKey, source.ModKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(package.Source.FileKey ?? string.Empty, source.FileKey ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<PackageImportResult>> ImportLocalAsync(
        string archivePath,
        string gameId,
        string? displayName = null,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return OperationResult<PackageImportResult>.Fail($"找不到要导入的压缩包：{archivePath}");
        }

        var hash = await FileSystemOps.HashFileAsync(archivePath, cancellationToken).ConfigureAwait(false);
        var existing = FindByHash(hash, gameId, PackagePlatforms.Local);
        if (existing is not null)
        {
            return OperationResult<PackageImportResult>.Ok(new PackageImportResult(existing, true));
        }

        var source = new PackageSourceRef
        {
            Platform = PackagePlatforms.Local,
            GameKey = string.IsNullOrWhiteSpace(gameId) ? "generic" : gameId,
            ModKey = Path.GetFileNameWithoutExtension(archivePath),
            FileKey = hash[..16],
        };

        var committed = await CommitAsync(
            new PackageCommitRequest
            {
                ArchivePath = archivePath,
                Source = source,
                Name = string.IsNullOrWhiteSpace(displayName)
                    ? Path.GetFileNameWithoutExtension(archivePath)
                    : displayName,
                Version = version,
                ExpectedSha256 = hash,
            },
            cancellationToken).ConfigureAwait(false);

        return committed.Succeeded && committed.Value is not null
            ? OperationResult<PackageImportResult>.Ok(new PackageImportResult(committed.Value, false))
            : OperationResult<PackageImportResult>.Fail(committed.Error ?? "导入失败。");
    }

    /// <inheritdoc />
    public async Task<OperationResult<ModPackage>> CommitAsync(
        PackageCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ArchivePath) || !File.Exists(request.ArchivePath))
        {
            return OperationResult<ModPackage>.Fail($"下载文件不存在：{request.ArchivePath}");
        }

        var hash = await FileSystemOps.HashFileAsync(request.ArchivePath, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256)
            && !string.Equals(request.ExpectedSha256, hash, StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<ModPackage>.Fail(
                $"文件校验失败：期望 {request.ExpectedSha256}，实际 {hash}。");
        }

        var byHash = FindByHash(hash, request.Source.GameKey, request.Source.Platform);
        if (byHash is not null)
        {
            return OperationResult<ModPackage>.Ok(byHash);
        }

        var bySource = FindBySource(request.Source);
        if (bySource is not null)
        {
            // A platform file id is immutable. If the same id now has different bytes, either the
            // download was corrupted or the source lied; never silently keep the old archive.
            return string.Equals(bySource.ArchiveSha256, hash, StringComparison.OrdinalIgnoreCase)
                ? OperationResult<ModPackage>.Ok(bySource)
                : OperationResult<ModPackage>.Fail(
                    "该来源文件已存在，但内容哈希不同。请先删除本地仓库中的旧包再重试。");
        }

        var size = new FileInfo(request.ArchivePath).Length;
        if (request.ExpectedSizeBytes is { } expectedSize && expectedSize > 0 && size != expectedSize)
        {
            return OperationResult<ModPackage>.Fail(
                $"文件大小校验失败：期望 {expectedSize} 字节，实际 {size} 字节。");
        }

        var packageId = BuildPackageId(request.Source, hash);
        var packageDirectory = PackageDirectory(request.Source, request.Version, request.Source.FileKey);
        var extension = ArchiveExtension(request.ArchivePath);
        var targetPath = Path.Combine(packageDirectory, "archive" + extension);

        try
        {
            Directory.CreateDirectory(packageDirectory);
            await FileSystemOps.CopyAsync(request.ArchivePath, targetPath, cancellationToken).ConfigureAwait(false);

            var package = new ModPackage
            {
                PackageId = packageId,
                Source = request.Source,
                Name = string.IsNullOrWhiteSpace(request.Name) ? Path.GetFileNameWithoutExtension(request.ArchivePath) : request.Name,
                Author = request.Author,
                Version = request.Version,
                Description = request.Description,
                ArchivePath = targetPath,
                ArchiveSha256 = hash,
                ArchiveSizeBytes = size,
                ArchiveFormat = DetectFormat(extension),
                AcquiredAt = DateTimeOffset.UtcNow,
                VerifiedAt = DateTimeOffset.UtcNow,
                State = PackageState.Ready,
                Dependencies = request.Dependencies,
                InstallMethodHint = request.InstallMethodHint,
                CoverImagePath = request.CoverImagePath,
            };

            await WritePackageJsonAsync(package, cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                _packages.RemoveAll(existing => string.Equals(existing.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase));
                _packages.Add(package);
            }

            if (!Persist())
            {
                return OperationResult<ModPackage>.Fail("包已复制，但索引写入失败；请检查磁盘空间和权限。");
            }

            _logger?.Info("Library", $"已入库“{package.Name}”{FormatVersion(package.Version)}。", package.ArchivePath);
            return OperationResult<ModPackage>.Ok(package);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.Error("Library", $"入库“{request.Name}”失败。", ex.Message);
            return OperationResult<ModPackage>.Fail($"入库失败：{ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<ModPackage>> VerifyAsync(
        string packageId,
        CancellationToken cancellationToken = default)
    {
        var package = Get(packageId);
        if (package is null)
        {
            return OperationResult<ModPackage>.Fail("找不到该包。");
        }

        if (!File.Exists(package.ArchivePath))
        {
            var missing = package with { State = PackageState.Missing };
            Replace(missing);
            Persist();
            return OperationResult<ModPackage>.Ok(missing);
        }

        var actual = await FileSystemOps.HashFileAsync(package.ArchivePath, cancellationToken).ConfigureAwait(false);
        var verified = package with
        {
            State = string.Equals(actual, package.ArchiveSha256, StringComparison.OrdinalIgnoreCase)
                ? PackageState.Ready
                : PackageState.Corrupt,
            VerifiedAt = DateTimeOffset.UtcNow,
        };

        Replace(verified);
        Persist();
        await WritePackageJsonAsync(verified, cancellationToken).ConfigureAwait(false);
        return OperationResult<ModPackage>.Ok(verified);
    }

    /// <inheritdoc />
    public Task<OperationResult<bool>> RemoveAsync(string packageId, CancellationToken cancellationToken = default)
    {
        var package = Get(packageId);
        if (package is null)
        {
            return Task.FromResult(OperationResult<bool>.Fail("找不到该包。"));
        }

        var directory = Path.GetDirectoryName(package.ArchivePath);
        if (directory is not null && IsInsideLibrary(directory))
        {
            FileSystemOps.RemoveDirectoryTree(directory);
        }

        lock (_gate)
        {
            _packages.RemoveAll(existing => string.Equals(existing.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
        }

        Persist();
        return Task.FromResult(OperationResult<bool>.Ok(true));
    }

    /// <inheritdoc />
    public PackageLibrarySummary GetSummary()
    {
        var packages = All();
        return new PackageLibrarySummary(
            packages.Count,
            packages.Count(package => package.State == PackageState.Ready),
            packages.Count(package => package.State == PackageState.Missing),
            packages.Count(package => package.State == PackageState.Corrupt),
            packages.Sum(package => package.ArchiveSizeBytes));
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            _packages = _store.ReadCollection<ModPackage>("packages");
            if (_packages.Count == 0)
            {
                _packages = RebuildFromDisk();
            }

            _loaded = true;
        }
    }

    private List<ModPackage> RebuildFromDisk()
    {
        var root = PackagesRoot();
        if (!Directory.Exists(root))
        {
            return [];
        }

        var packages = new List<ModPackage>();
        foreach (var file in Directory.EnumerateFiles(root, "package.json", SearchOption.AllDirectories))
        {
            try
            {
                var package = JsonSerializer.Deserialize<ModPackage>(File.ReadAllBytes(file), ManifestJson.Options);
                if (package is not null)
                {
                    packages.Add(package);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _logger?.Warn("Library", "无法读取包元数据，已跳过。", $"{file}: {ex.Message}");
            }
        }

        return packages;
    }

    private void Replace(ModPackage package)
    {
        lock (_gate)
        {
            _packages.RemoveAll(existing => string.Equals(existing.PackageId, package.PackageId, StringComparison.OrdinalIgnoreCase));
            _packages.Add(package);
        }
    }

    private bool Persist()
    {
        lock (_gate)
        {
            return _store.WriteCollection("packages", _packages.OrderByDescending(package => package.AcquiredAt));
        }
    }

    private async Task WritePackageJsonAsync(ModPackage package, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(package.ArchivePath);
        if (directory is null)
        {
            return;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(package, ManifestJson.Options);
        await FileSystemOps.WriteAtomicAsync(Path.Combine(directory, "package.json"), json, cancellationToken)
            .ConfigureAwait(false);
    }

    private string PackageDirectory(PackageSourceRef source, string? version, string? fileKey)
    {
        var platform = SanitizeSegment(source.Platform);
        var game = SanitizeSegment(source.GameKey);
        var mod = SanitizeSegment(source.ModKey);
        var variant = SanitizeSegment($"{version ?? "unversioned"}__{fileKey ?? "file"}");
        return Path.Combine(PackagesRoot(), platform, game, mod, variant);
    }

    private string PackagesRoot() => Path.Combine(_paths.Library, "packages");

    private bool IsInsideLibrary(string path)
    {
        var root = Path.GetFullPath(_paths.Library).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildPackageId(PackageSourceRef source, string hash)
    {
        if (string.Equals(source.Platform, PackagePlatforms.Local, StringComparison.OrdinalIgnoreCase))
        {
            return $"local:{hash[..16]}";
        }

        return string.Join(
            ':',
            SanitizeSegment(source.Platform),
            SanitizeSegment(source.GameKey),
            SanitizeSegment(source.ModKey),
            SanitizeSegment(source.FileKey ?? "file"));
    }

    private static string SanitizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(ch => Array.IndexOf(invalid, ch) >= 0 ? '_' : ch).ToArray();
        var result = new string(chars);
        return result.Length > 80 ? result[..80] : result;
    }

    private static string ArchiveExtension(string path)
    {
        var lower = path.ToLowerInvariant();
        foreach (var suffix in new[] { ".tar.gz", ".tar.bz2", ".tar.xz" })
        {
            if (lower.EndsWith(suffix, StringComparison.Ordinal))
            {
                return suffix;
            }
        }

        var extension = Path.GetExtension(path);
        return string.IsNullOrWhiteSpace(extension) ? ".zip" : extension.ToLowerInvariant();
    }

    private static ArchiveFormat DetectFormat(string extension) => extension switch
    {
        ".zip" => ArchiveFormat.Zip,
        ".7z" => ArchiveFormat.SevenZip,
        ".rar" => ArchiveFormat.Rar,
        ".tar" or ".tar.gz" or ".tgz" or ".tar.bz2" or ".tar.xz" => ArchiveFormat.Tar,
        _ => ArchiveFormat.Zip,
    };

    private static string FormatVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ? string.Empty : $" v{version}";
}
