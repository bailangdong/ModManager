namespace ModManager.Core.Services;

/// <summary>
/// Promotes completed downloads into the package library. This is the only place where a download
/// record turns into a <see cref="ModPackage"/>; the installer never reads the download cache.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="DownloadManager"/> on purpose: the download engine knows bytes and
/// HTTP, not platforms or package identity. It also means a completed-but-not-yet-committed archive
/// survives a crash and is retried on the next start without downloading it again.
/// </remarks>
public sealed class PackageIngestService
{
    private readonly IPackageLibrary _packages;
    private readonly IDownloadRepository _downloads;
    private readonly IAppLogger _logger;

    /// <summary>Creates the service.</summary>
    public PackageIngestService(IPackageLibrary packages, IDownloadRepository downloads, IAppLogger logger)
    {
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Subscribes to download completion events.</summary>
    public void Attach(IDownloadManager downloads)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        downloads.StateChanged += OnStateChanged;
    }

    private async void OnStateChanged(object? sender, DownloadRecord record)
    {
        if (record.State != DownloadState.Completed || !string.IsNullOrWhiteSpace(record.PackageId))
        {
            return;
        }

        try
        {
            await IngestAsync(record).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MarkFailed(record, ex.Message);
            _logger.Error("Library", $"“{record.FileName}”自动入库失败。", ex.Message);
        }
    }

    /// <summary>
    /// Commits one completed download. Safe to call repeatedly: the library is idempotent by hash and
    /// by source identity.
    /// </summary>
    public async Task<ModPackage?> IngestAsync(DownloadRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.State != DownloadState.Completed || !string.IsNullOrWhiteSpace(record.PackageId))
        {
            return null;
        }

        if (!File.Exists(record.TargetPath))
        {
            MarkFailed(record, "下载文件已丢失，无法入库。");
            return null;
        }

        var source = record.PackageSource ?? new PackageSourceRef
        {
            Platform = PackagePlatforms.Other,
            GameKey = string.IsNullOrWhiteSpace(record.GameId) ? "generic" : record.GameId,
            ModKey = Path.GetFileNameWithoutExtension(record.FileName),
            FileKey = record.Id,
        };

        var result = await _packages.CommitAsync(
            new PackageCommitRequest
            {
                ArchivePath = record.TargetPath,
                Source = source,
                Name = record.PackageName ?? record.ModNameHint ?? Path.GetFileNameWithoutExtension(record.FileName),
                Version = record.PackageVersion,
                Author = record.PackageAuthor,
                ExpectedSha256 = record.ExpectedSha256 ?? record.ActualSha256,
                ExpectedSizeBytes = record.TotalBytes,
                Dependencies = record.PackageDependencies ?? [],
                InstallMethodHint = record.InstallMethodHint,
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || result.Value is null)
        {
            MarkFailed(record, result.Error ?? "入库失败。");
            return null;
        }

        var updated = record with { PackageId = result.Value.PackageId, CommitError = null };
        _downloads.Upsert(updated);
        _logger.Info("Library", $"下载“{updated.FileName}”已进入本地仓库。", result.Value.PackageId);
        return result.Value;
    }

    /// <summary>Retries every completed download that has no package id yet.</summary>
    public async Task<int> IngestCompletedAsync(CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var record in _downloads.All())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.State == DownloadState.Completed && string.IsNullOrWhiteSpace(record.PackageId))
            {
                if (await IngestAsync(record, cancellationToken).ConfigureAwait(false) is not null)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private void MarkFailed(DownloadRecord record, string error)
    {
        _downloads.Upsert(record with { CommitError = error });
        _logger.Warn("Library", $"“{record.FileName}”入库失败。", error);
    }
}
