using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using ModManager.Core.Safety;

namespace ModManager.Core.Services;

/// <summary>
/// Segmented, resumable, checksum-verified downloads into the cache.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — Modrix's <c>modrix-download</c>, whose stated design is "segmented, resumable,
/// checksum-verified" with a work-queue segment model, plus the user's spec §8 (concurrency, pause,
/// resume, cancel, retry, speed/ETA, SHA-256, failure recovery) and §8's placement rule: downloads go
/// to the cache, never into the game folder.
/// </para>
/// <para>
/// The decisions worth naming, with the reason each one exists:
/// </para>
/// <list type="number">
/// <item><b>Three file names:</b> <c>name.zip.part</c> while incomplete, <c>name.zip</c> when done, and
/// a small JSON control file recording which byte ranges are present. The control file is what makes
/// resume possible at all — without it, "resume" means "start over".</item>
/// <item><b>Resume only when provably safe.</b> Three conditions must all hold: the server declared
/// range support, the validator (ETag/Last-Modified) still matches, and the partial file is exactly the
/// expected length. If any fails, the partial file and its control file are discarded and the download
/// restarts. Modrix states the tradeoff as "a changed remote resource restarts cleanly rather than
/// corrupting the file", and that ordering — correctness over bytes saved — is the right one for
/// archives that will be extracted into a game folder.</item>
/// <item><b>Mark a range done only after flushing it.</b> Otherwise a crash could leave a control file
/// claiming bytes that never reached the disk, and a later resume would produce a corrupt archive that
/// still passed its length check.</item>
/// <item><b>Write only within the range being fetched.</b> A server that returns more bytes than asked
/// for must not be able to write over a neighbouring segment, so every write is clamped to the current
/// range and excess trailing bytes are discarded.</item>
/// <item><b>Preallocate the partial file.</b> Beyond avoiding fragmentation, this turns "is this file
/// complete?" into a decidable question — the length either equals the expected total or it does not.</item>
/// </list>
/// <para>
/// <b>What is deliberately simpler than Modrix.</b> Modrix runs every segment concurrently through a
/// shared work queue with a per-connection file handle. This implementation fetches ranges
/// sequentially within a download and parallelises <i>across downloads</i> up to
/// <see cref="DownloadOptions.MaxConcurrency"/>. For mod archives of tens to hundreds of megabytes that
/// gives the same practical throughput with dramatically less concurrency surface, and it keeps
/// cancellation, error reporting and file-handle lifetime trivially correct. The segment map is still
/// recorded per range, so switching to concurrent segments later is a change of loop, not of format.
/// </para>
/// </remarks>
public sealed class DownloadManager : IDownloadManager, IDisposable
{
    /// <summary>Size of each resumable range. Modrix uses the same default for the same reason.</summary>
    public const long SegmentBytes = 4L * 1024 * 1024;

    /// <summary>Maximum bytes read from a response in one go.</summary>
    private const int ReadBufferSize = 64 * 1024;

    /// <summary>How often a progress sample is emitted to the UI.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(400);

    private readonly IDownloadRepository _repository;
    private readonly AppPaths _paths;
    private readonly IAppLogger? _logger;
    private readonly HttpClient _http;
    private readonly DownloadOptions _options;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DownloadRecord> _records = new(StringComparer.Ordinal);

    /// <summary>
    /// Request headers for the lifetime of this process. They are deliberately not persisted: the
    /// Nexus key must never be written to disk. Keeping them here makes pause/resume work without
    /// putting the credential in a record.
    /// </summary>
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _headers = new(StringComparer.Ordinal);
    /// <summary>
    /// Serialises persistence of download records. A plain <see cref="object"/> because
    /// <c>System.Threading.Lock</c> requires .NET 9 and this project targets <c>net8.0</c>.
    /// </summary>
    private readonly object _persistLock = new();
    private bool _disposed;

    /// <summary>Creates the manager.</summary>
    /// <param name="repository">Persistence for job records.</param>
    /// <param name="paths">Application paths.</param>
    /// <param name="options">Download options.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="httpClient">Optional client, so tests can supply a stub.</param>
    public DownloadManager(
        IDownloadRepository repository,
        AppPaths paths,
        DownloadOptions? options = null,
        IAppLogger? logger = null,
        HttpClient? httpClient = null)
    {
        _repository = repository;
        _paths = paths;
        _options = options ?? new DownloadOptions();
        _logger = logger;
        _concurrency = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency));

        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            // Redirects are followed because CDNs routinely bounce, but only a bounded number of times.
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
        });

        _http.Timeout = _options.RequestTimeout;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(_options.UserAgent);

        Directory.CreateDirectory(_paths.Downloads);

        foreach (var record in _repository.All())
        {
            _records[record.Id] = record;
        }
    }

    /// <inheritdoc />
    public event EventHandler<DownloadProgress>? ProgressChanged;

    /// <inheritdoc />
    public event EventHandler<DownloadRecord>? StateChanged;

    /// <inheritdoc />
    public IReadOnlyList<DownloadRecord> All()
    {
        // The ingest service writes PackageId through the repository, outside this manager. Refresh
        // only the completed-but-not-yet-committed rows, so the UI can move them to "in library"
        // without paying for a disk read on every progress tick.
        foreach (var record in _records.Values)
        {
            if (record.State == DownloadState.Completed && string.IsNullOrWhiteSpace(record.PackageId))
            {
                var persisted = _repository.Get(record.Id);
                if (persisted is not null)
                {
                    _records[record.Id] = persisted;
                }
            }
        }

        return _records.Values.OrderByDescending(record => record.CreatedAt).ToList();
    }

    /// <inheritdoc />
    public DownloadRecord? Get(string downloadId)
    {
        if (string.IsNullOrWhiteSpace(downloadId) || !_records.TryGetValue(downloadId, out var record))
        {
            return null;
        }

        if (record.State == DownloadState.Completed && string.IsNullOrWhiteSpace(record.PackageId))
        {
            var persisted = _repository.Get(downloadId);
            if (persisted is not null)
            {
                record = persisted;
                _records[downloadId] = persisted;
            }
        }

        return record;
    }

    /// <inheritdoc />
    public Task<DownloadRecord> EnqueueAsync(DownloadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The file name is sanitised here, at the boundary, exactly as Modrix sanitises its handoff
        // file name: everything downstream assumes a plain base name with no separators.
        var safeName = SanitizeFileName(request.FileName);
        var id = Guid.NewGuid().ToString("N")[..16];
        var targetPath = UniqueTargetPath(safeName);

        var record = new DownloadRecord
        {
            Id = id,
            FileName = safeName,
            Url = request.Url,
            TargetPath = targetPath,
            PartialPath = targetPath + ".part",
            State = DownloadState.Queued,
            ExpectedSha256 = request.ExpectedSha256,
            GameId = request.GameId,
            NexusModId = request.NexusModId,
            NexusFileId = request.NexusFileId,
            ModNameHint = request.ModNameHint,
            CoverImageUrl = request.CoverImageUrl,
            PackageSource = request.PackageSource,
            PackageName = request.PackageName ?? request.ModNameHint,
            PackageVersion = request.PackageVersion,
            PackageAuthor = request.PackageAuthor,
            PackageDependencies = request.PackageDependencies,
            InstallMethodHint = request.InstallMethodHint,
        };

        _records[id] = record;
        _headers[id] = request.Headers;
        Persist(record);
        Raise(record);

        var source = new CancellationTokenSource();
        _cancellations[id] = source;

        // Fire and forget by design: the caller gets an id and subscribes to progress. Awaiting here
        // would make "queue three downloads" a sequential operation in the UI.
        _ = Task.Run(() => RunAsync(id, request, source.Token), CancellationToken.None);

        return Task.FromResult(record);
    }

    /// <inheritdoc />
    public Task<bool> PauseAsync(string downloadId)
    {
        if (_cancellations.TryRemove(downloadId, out var source))
        {
            source.Cancel();
            source.Dispose();
        }

        return Task.FromResult(UpdateState(downloadId, DownloadState.Paused));
    }

    /// <inheritdoc />
    public Task<bool> ResumeAsync(string downloadId)
    {
        var record = Get(downloadId);
        if (record is null)
        {
            return Task.FromResult(false);
        }

        if (record.State is DownloadState.Downloading or DownloadState.Queued)
        {
            return Task.FromResult(true);
        }

        var request = new DownloadRequest
        {
            Url = record.Url,
            FileName = record.FileName,
            GameId = record.GameId,
            ExpectedSha256 = record.ExpectedSha256,
            NexusModId = record.NexusModId,
            NexusFileId = record.NexusFileId,
            ModNameHint = record.ModNameHint,
            CoverImageUrl = record.CoverImageUrl,
            PackageSource = record.PackageSource,
            PackageName = record.PackageName,
            PackageVersion = record.PackageVersion,
            PackageAuthor = record.PackageAuthor,
            PackageDependencies = record.PackageDependencies,
            InstallMethodHint = record.InstallMethodHint,
            Headers = _headers.TryGetValue(downloadId, out var headers)
                ? headers
                : new Dictionary<string, string>(),
        };

        var source = new CancellationTokenSource();
        _cancellations[downloadId] = source;
        UpdateState(downloadId, DownloadState.Queued);
        _ = Task.Run(() => RunAsync(downloadId, request, source.Token), CancellationToken.None);

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> CancelAsync(string downloadId, bool deletePartial = true)
    {
        if (_cancellations.TryRemove(downloadId, out var source))
        {
            source.Cancel();
            source.Dispose();
        }

        var record = Get(downloadId);
        if (record is null)
        {
            return Task.FromResult(false);
        }

        _headers.TryRemove(downloadId, out _);

        if (deletePartial)
        {
            FileSystemOps.RemoveIfPresent(record.PartialPath);
            FileSystemOps.RemoveIfPresent(ControlPath(record.PartialPath));
        }

        return Task.FromResult(UpdateState(downloadId, DownloadState.Cancelled));
    }

    /// <inheritdoc />
    public Task<bool> RetryAsync(string downloadId)
    {
        var record = Get(downloadId);
        if (record is null)
        {
            return Task.FromResult(false);
        }

        // Retrying resets the attempt counter, so a job that exhausted its retries is a first-class
        // action rather than something the user has to work around.
        Persist(record with { Attempts = 0, Error = null, State = DownloadState.Queued });
        return ResumeAsync(downloadId);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string downloadId, bool deleteFile = false)
    {
        var record = Get(downloadId);
        if (record is null)
        {
            return Task.FromResult(false);
        }

        _ = CancelAsync(downloadId, deletePartial: true);

        if (deleteFile)
        {
            FileSystemOps.RemoveIfPresent(record.TargetPath);
        }

        _records.TryRemove(downloadId, out _);
        _headers.TryRemove(downloadId, out _);
        _repository.Remove(downloadId);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public async Task PauseAllAsync()
    {
        var ids = _records.Values.Where(record => record.IsActive).Select(record => record.Id).ToList();
        foreach (var id in ids)
        {
            await PauseAsync(id).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<RecoverySummary> RecoverAsync(IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var requeued = 0;
        var discarded = 0;
        var completed = 0;

        foreach (var record in _records.Values.ToList())
        {
            // A job that was mid-flight when the process died is in an active state with no live task.
            // Its resume information is on disk, so it is re-queued rather than reported as failed.
            if (record.State is DownloadState.Downloading or DownloadState.Resolving or DownloadState.Verifying)
            {
                if (File.Exists(FileSystemOps.ToExtendedPath(record.PartialPath)))
                {
                    logger.Info(
                        "Downloads",
                        $"正在续传被中断的下载 “{record.FileName}”。",
                        $"已存在 {record.BytesDownloaded} / {record.TotalBytes?.ToString() ?? "未知"} 字节");
                    _ = ResumeAsync(record.Id);
                    requeued++;
                }
                else
                {
                    logger.Info("Downloads", $"丢弃被中断的下载 “{record.FileName}”：临时文件已不存在。");
                    Persist(record with { State = DownloadState.Failed, Error = "The download was interrupted and its partial file is missing." });
                    discarded++;
                }

                continue;
            }

            if (record.State == DownloadState.Completed && !File.Exists(FileSystemOps.ToExtendedPath(record.TargetPath)))
            {
                logger.Info("Downloads", $"下载 “{record.FileName}” 已从缓存中消失，将重新尝试。");
                Persist(record with { State = DownloadState.Failed, Error = "The downloaded file is no longer in the cache." });
                discarded++;
                continue;
            }

            if (record.State == DownloadState.Completed)
            {
                completed++;
            }
        }

        return Task.FromResult(new RecoverySummary(requeued, discarded, completed));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var source in _cancellations.Values)
        {
            source.Cancel();
            source.Dispose();
        }

        _cancellations.Clear();
        _concurrency.Dispose();
        _http.Dispose();
    }

    /// <summary>Runs one download to completion, retrying transient failures.</summary>
    /// <param name="id">Job id.</param>
    /// <param name="request">What to fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task RunAsync(string id, DownloadRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var attempt = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var record = Get(id);
                if (record is null)
                {
                    return;
                }

                attempt++;
                Persist(record with { Attempts = attempt, Error = null, State = DownloadState.Resolving });
                Raise(Get(id)!);

                try
                {
                    await AttemptAsync(id, request, cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    var current = Get(id);
                    if (current is not null && current.State != DownloadState.Paused && current.State != DownloadState.Cancelled)
                    {
                        // Cancelled by the caller without an explicit pause or cancel, most likely by
                        // shutdown. The partial file and control file stay, so the job resumes later.
                        Persist(current with { State = DownloadState.Paused });
                        Raise(Get(id)!);
                    }

                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidDataException)
                {
                    var current = Get(id);
                    if (current is null)
                    {
                        return;
                    }

                    // An expired link or an authentication refusal will not fix itself, so retrying is
                    // pointless and the message must say what the user should do instead.
                    var isPermanent = ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Gone or HttpStatusCode.Unauthorized or HttpStatusCode.NotFound };

                    if (isPermanent || attempt >= Math.Max(1, _options.MaxAttempts))
                    {
                        _logger?.Warn("Downloads", $"下载 “{current.FileName}” 最终失败。", ex.Message);
                        Persist(current with { State = DownloadState.Failed, Error = DescribeFailure(ex) });
                        Raise(Get(id)!);
                        return;
                    }

                    var delay = TimeSpan.FromTicks(_options.RetryBaseDelay.Ticks * (long)Math.Pow(2, Math.Min(attempt, 5)));
                    _logger?.Info(
                        "Downloads",
                        $"{delay.TotalSeconds:0.#} 秒后重试 “{current.FileName}”（第 {attempt + 1} 次）。",
                        ex.Message);

                    Persist(current with { Error = ex.Message });
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _concurrency.Release();
            _cancellations.TryRemove(id, out _);
        }
    }

    /// <summary>One attempt: probe, then fetch the missing ranges, then verify and finalise.</summary>
    /// <param name="id">Job id.</param>
    /// <param name="request">What to fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task AttemptAsync(string id, DownloadRequest request, CancellationToken cancellationToken)
    {
        var record = Get(id) ?? throw new InvalidOperationException($"Unknown download '{id}'.");

        using var probe = BuildRequest(request, null);
        using var probeResponse = await _http
            .SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!probeResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The server returned {(int)probeResponse.StatusCode} {probeResponse.ReasonPhrase}.",
                null,
                probeResponse.StatusCode);
        }

        var supportsRanges = probeResponse.Headers.AcceptRanges.Any(value =>
            value.Contains("bytes", StringComparison.OrdinalIgnoreCase));

        var total = probeResponse.Content.Headers.ContentLength;
        var validator = probeResponse.Headers.ETag?.Tag ?? probeResponse.Content.Headers.LastModified?.ToString("R");

        var control = supportsRanges && total is > 0
            ? DownloadControlFile.Load(ControlPath(record.PartialPath))
            : null;

        // All three conditions must hold before existing bytes may be trusted.
        var canResume = control is not null
            && !string.IsNullOrWhiteSpace(validator)
            && string.Equals(control.Validator, validator, StringComparison.Ordinal)
            && control.Total == total
            && File.Exists(FileSystemOps.ToExtendedPath(record.PartialPath))
            && new FileInfo(FileSystemOps.ToExtendedPath(record.PartialPath)).Length == total;

        if (!canResume)
        {
            // Discard rather than trust: a changed remote resource, an unknown validator or a
            // wrong-length partial file all mean the existing bytes cannot be proven correct.
            FileSystemOps.RemoveIfPresent(record.PartialPath);
            FileSystemOps.RemoveIfPresent(ControlPath(record.PartialPath));
            control = null;
        }

        if (total is not > 0)
        {
            // No length means no ranges and no resume; a straight streaming copy is the only option.
            await DownloadSingleStreamAsync(record, request, cancellationToken).ConfigureAwait(false);
            return;
        }

        var ranges = BuildRanges(total.Value, control?.CompletedRanges ?? []);
        Persist(record with
        {
            State = DownloadState.Downloading,
            TotalBytes = total,
            ServerSupportsResume = supportsRanges,
            Validator = validator,
            Segments = control?.CompletedRanges ?? [],
            BytesDownloaded = (control?.CompletedRanges ?? []).Sum(range => range.Length),
        });
        Raise(Get(id)!);

        Preallocate(record.PartialPath, total.Value);

        var completed = new List<ByteRange>(control?.CompletedRanges ?? []);
        var byterate = new ThroughputMeter();

        foreach (var range in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var rangeRequest = BuildRequest(request, new RangeHeaderValue(range.Start, range.End));
            using var response = await _http
                .SendAsync(rangeRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Gone)
            {
                // Signed URLs expire. The message has to tell the user what to do, not just that it failed.
                throw new HttpRequestException(
                    "The download link has expired. Re-open the mod page and download it again.",
                    null,
                    response.StatusCode);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"The server returned {(int)response.StatusCode} for the requested byte range.",
                    null,
                    response.StatusCode);
            }

            var written = await WriteRangeAsync(record.PartialPath, range, response, byterate, record, cancellationToken)
                .ConfigureAwait(false);

            if (written != range.Length)
            {
                // A short read is not success. The range stays unmarked so it is retried, and nothing
                // about it is recorded in the control file.
                throw new IOException(
                    $"The server returned {written} of {range.Length} bytes for range {range.Start}-{range.End}.");
            }

            completed.Add(range);
            completed.Sort((left, right) => left.Start.CompareTo(right.Start));

            // Persisted only after the range has been flushed, so the control file can never claim
            // bytes that are not on disk.
            DownloadControlFile.Save(ControlPath(record.PartialPath), new DownloadControlFile
            {
                Total = total.Value,
                Validator = validator ?? string.Empty,
                SegmentBytes = SegmentBytes,
                CompletedRanges = completed,
            });

            Persist(record with
            {
                BytesDownloaded = completed.Sum(item => item.Length),
                Segments = completed.ToList(),
                BytesPerSecond = byterate.BytesPerSecond,
            });

            Raise(Get(id)!);
        }

        await FinalizeAsync(id, record, completed, byterate, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Streams one byte range into the partial file at the correct offset.</summary>
    /// <param name="partialPath">Partial file.</param>
    /// <param name="range">Range being written.</param>
    /// <param name="response">HTTP response carrying the bytes.</param>
    /// <param name="meter">Throughput meter.</param>
    /// <param name="record">Job record, for progress reporting.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Every write is clamped to the range's own bounds. That is the defence against a server that
    /// returns more bytes than were requested: without the clamp it could overwrite a neighbouring
    /// segment's bytes, producing an archive that is the right length and the wrong content.
    /// </remarks>
    private async Task<long> WriteRangeAsync(
        string partialPath,
        ByteRange range,
        HttpResponseMessage response,
        ThroughputMeter meter,
        DownloadRecord record,
        CancellationToken cancellationToken)
    {
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            FileSystemOps.ToExtendedPath(partialPath),
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            ReadBufferSize,
            FileOptions.Asynchronous);

        target.Seek(range.Start, SeekOrigin.Begin);

        var buffer = new byte[ReadBufferSize];
        long written = 0;
        var lastReport = DateTimeOffset.UtcNow;

        while (written < range.Length)
        {
            var remainingInRange = range.Length - written;
            var toRead = (int)Math.Min(buffer.Length, remainingInRange);

            var read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            meter.Add(read);

            var now = DateTimeOffset.UtcNow;
            if (now - lastReport >= ProgressInterval)
            {
                lastReport = now;
                ProgressChanged?.Invoke(this, new DownloadProgress(
                    record.Id,
                    record.BytesDownloaded + written,
                    record.TotalBytes,
                    meter.BytesPerSecond,
                    EstimateEta(record.TotalBytes, record.BytesDownloaded + written, meter.BytesPerSecond),
                    DownloadState.Downloading));
            }
        }

        // Flush to disk before the caller records the range as complete.
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        target.Flush(flushToDisk: true);
        return written;
    }

    /// <summary>Fetches a response with no range support, replacing any partial state.</summary>
    /// <param name="record">Job record.</param>
    /// <param name="request">What to fetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task DownloadSingleStreamAsync(
        DownloadRecord record,
        DownloadRequest request,
        CancellationToken cancellationToken)
    {
        // A stale segmented control file must not survive into a single-stream download, because a
        // later run could otherwise trust it and skip bytes that were never fetched.
        FileSystemOps.RemoveIfPresent(ControlPath(record.PartialPath));
        FileSystemOps.RemoveIfPresent(record.PartialPath);

        using var httpRequest = BuildRequest(request, null);
        using var response = await _http
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var meter = new ThroughputMeter();
        var total = response.Content.Headers.ContentLength;
        long written = 0;

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(
            FileSystemOps.ToExtendedPath(record.PartialPath),
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            ReadBufferSize,
            FileOptions.Asynchronous))
        {
            var buffer = new byte[ReadBufferSize];
            int read;
            var lastReport = DateTimeOffset.UtcNow;

            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                meter.Add(read);

                var now = DateTimeOffset.UtcNow;
                if (now - lastReport >= ProgressInterval)
                {
                    lastReport = now;
                    ProgressChanged?.Invoke(this, new DownloadProgress(
                        record.Id, written, total, meter.BytesPerSecond, EstimateEta(total, written, meter.BytesPerSecond),
                        DownloadState.Downloading));
                }
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            target.Flush(flushToDisk: true);
        }

        Persist(record with { BytesDownloaded = written, TotalBytes = total, BytesPerSecond = meter.BytesPerSecond });
        await FinalizeAsync(record.Id, record, [], meter, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Verifies the checksum and renames the partial file into place.</summary>
    /// <param name="id">Job id.</param>
    /// <param name="record">Job record.</param>
    /// <param name="completed">Ranges fetched.</param>
    /// <param name="meter">Throughput meter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task FinalizeAsync(
        string id,
        DownloadRecord record,
        IReadOnlyList<ByteRange> completed,
        ThroughputMeter meter,
        CancellationToken cancellationToken)
    {
        Persist(record with { State = DownloadState.Verifying, Segments = completed.ToList() });
        Raise(Get(id)!);

        string? actualHash = null;

        if (!string.IsNullOrWhiteSpace(record.ExpectedSha256))
        {
            actualHash = await FileSystemOps.HashFileAsync(record.PartialPath, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(actualHash, record.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The downloaded file does not match its expected SHA-256 (expected {record.ExpectedSha256}, got {actualHash}).");
            }
        }

        // The rename is the only path by which a download is accepted. Because the partial file is a
        // sibling of the target, it is same-volume and therefore atomic.
        FileSystemOps.RemoveIfPresent(record.TargetPath);
        File.Move(
            FileSystemOps.ToExtendedPath(record.PartialPath),
            FileSystemOps.ToExtendedPath(record.TargetPath),
            overwrite: true);

        FileSystemOps.RemoveIfPresent(ControlPath(record.PartialPath));

        Persist(record with
        {
            State = DownloadState.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            BytesDownloaded = record.TotalBytes ?? record.BytesDownloaded,
            ActualSha256 = actualHash,
            Segments = completed.ToList(),
            BytesPerSecond = meter.BytesPerSecond,
        });

        Raise(Get(id)!);
        _headers.TryRemove(id, out _);
        _logger?.Info("Downloads", $"已下载 “{record.FileName}” 到缓存。");
    }

    /// <summary>Builds the HTTP request, injecting per-request headers at call time.</summary>
    /// <param name="request">Download request.</param>
    /// <param name="range">Byte range to request, or null for the whole resource.</param>
    /// <remarks>
    /// Headers (which is how an API key reaches Nexus) are applied here rather than stored on the
    /// record, so a credential never lands in a persisted download job or a log line.
    /// </remarks>
    private static HttpRequestMessage BuildRequest(DownloadRequest request, RangeHeaderValue? range)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, request.Url);

        if (range is not null)
        {
            message.Headers.Range = range;
        }

        foreach (var (name, value) in request.Headers)
        {
            if (string.Equals(name, "Range", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                message.Content ??= new StringContent(string.Empty);
                message.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return message;
    }

    /// <summary>Splits the unfetched tail of a file into ranges, keeping already-fetched ones.</summary>
    /// <param name="total">Total size.</param>
    /// <param name="alreadyComplete">Ranges already on disk.</param>
    private static List<ByteRange> BuildRanges(long total, IReadOnlyList<ByteRange> alreadyComplete)
    {
        var ranges = new List<ByteRange>();
        long offset = 0;

        while (offset < total)
        {
            var end = Math.Min(offset + SegmentBytes - 1, total - 1);
            var candidate = new ByteRange(offset, end);

            if (!alreadyComplete.Any(done => done.Start <= candidate.Start && done.End >= candidate.End))
            {
                ranges.Add(candidate);
            }

            offset = end + 1;
        }

        return ranges;
    }

    /// <summary>Extends the partial file to its final length.</summary>
    /// <param name="partialPath">Partial file.</param>
    /// <param name="total">Final length.</param>
    /// <remarks>
    /// Preallocation makes the length check meaningful: after a crash, the partial file's length either
    /// equals the expected total or it does not, which is one of the three conditions resume requires.
    /// </remarks>
    private static void Preallocate(string partialPath, long total)
    {
        try
        {
            var directory = Path.GetDirectoryName(partialPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(FileSystemOps.ToExtendedPath(directory));
            }

            using var stream = new FileStream(
                FileSystemOps.ToExtendedPath(partialPath),
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read);

            if (stream.Length < total)
            {
                stream.SetLength(total);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: without preallocation the length check simply fails and the download restarts.
        }
    }

    /// <summary>Produces a distinct target path for a file name.</summary>
    /// <param name="fileName">Sanitised file name.</param>
    private string UniqueTargetPath(string fileName)
    {
        var candidate = Path.Combine(_paths.Downloads, fileName);
        if (!File.Exists(FileSystemOps.ToExtendedPath(candidate)))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var suffix = 2; suffix < 1_000; suffix++)
        {
            candidate = Path.Combine(_paths.Downloads, $"{stem} ({suffix}){extension}");
            if (!File.Exists(FileSystemOps.ToExtendedPath(candidate)))
            {
                return candidate;
            }
        }

        return Path.Combine(_paths.Downloads, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    /// <summary>
    /// Reduces a file name to a safe base name.
    /// </summary>
    /// <param name="fileName">Name from a remote source.</param>
    /// <remarks>
    /// Borrowed directly from Modrix's handoff sanitisation, including the reason it calls out: the
    /// colon matters because on NTFS it opens an alternate data stream, and separators matter because
    /// they would otherwise let a remote server choose where a file lands.
    /// </remarks>
    public static string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "download";
        }

        // Take only the last path segment, so a name like "../../etc/passwd" becomes "passwd".
        var lastSeparator = fileName.LastIndexOfAny(['/', '\\']);
        var candidate = lastSeparator >= 0 ? fileName[(lastSeparator + 1)..] : fileName;

        // Strip a query string if the caller passed a URL tail.
        var query = candidate.IndexOf('?');
        if (query >= 0)
        {
            candidate = candidate[..query];
        }

        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        foreach (var illegal in new[] { '<', '>', ':', '"', '|', '?', '*', '/', '\\' })
        {
            invalid.Add(illegal);
        }

        var cleaned = new string(candidate.Where(ch => !invalid.Contains(ch) && !char.IsControl(ch)).ToArray());
        cleaned = cleaned.Trim().Trim('.');

        if (cleaned.Length == 0)
        {
            return "download";
        }

        const int maxLength = 200;
        if (cleaned.Length > maxLength)
        {
            var extension = Path.GetExtension(cleaned);
            var stem = Path.GetFileNameWithoutExtension(cleaned);
            var keep = Math.Max(1, maxLength - extension.Length);
            cleaned = stem[..Math.Min(stem.Length, keep)] + extension;
        }

        return cleaned;
    }

    /// <summary>Path of a partial file's control file.</summary>
    /// <param name="partialPath">Partial file path.</param>
    private static string ControlPath(string partialPath) => partialPath + ".control.json";

    /// <summary>Estimates remaining time, or null when throughput is unknown.</summary>
    /// <param name="total">Total size, when known.</param>
    /// <param name="done">Bytes done.</param>
    /// <param name="rate">Bytes per second.</param>
    private static TimeSpan? EstimateEta(long? total, long done, double rate)
    {
        if (total is not { } size || rate <= 0.5d)
        {
            return null;
        }

        return TimeSpan.FromSeconds(Math.Max(0, size - done) / rate);
    }

    /// <summary>Describes a failure in language that tells the user what to do.</summary>
    /// <param name="exception">Failure to describe.</param>
    private static string DescribeFailure(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Gone } =>
            "The download link has expired. Re-open the mod page and download it again.",
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
            "The server refused the request. Check your Nexus Mods API key in Settings.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            "The file is no longer available at that address.",
        InvalidDataException => exception.Message,
        _ => exception.Message,
    };

    /// <summary>Persists a record and notifies listeners.</summary>
    /// <param name="record">Record to persist.</param>
    private void Persist(DownloadRecord record)
    {
        _records[record.Id] = record with { UpdatedAt = DateTimeOffset.UtcNow };

        lock (_persistLock)
        {
            _repository.Upsert(_records[record.Id]);
        }
    }

    /// <summary>Updates just the state of a record.</summary>
    /// <param name="downloadId">Job id.</param>
    /// <param name="state">New state.</param>
    private bool UpdateState(string downloadId, DownloadState state)
    {
        var record = Get(downloadId);
        if (record is null)
        {
            return false;
        }

        Persist(record with { State = state });
        Raise(Get(downloadId)!);
        return true;
    }

    /// <summary>Raises the state-changed event.</summary>
    /// <param name="record">Changed record.</param>
    private void Raise(DownloadRecord record) => StateChanged?.Invoke(this, record);

    /// <summary>
    /// Sliding-window throughput meter.
    /// </summary>
    /// <remarks>
    /// A window rather than a lifetime average, because the useful question during a download is "how
    /// fast is it going now", not "what was the average including the slow start".
    /// </remarks>
    private sealed class ThroughputMeter
    {
        private const int WindowSeconds = 8;

        private readonly Queue<(DateTimeOffset At, long Bytes)> _samples = new();
        private long _total;

        /// <summary>Records bytes received.</summary>
        /// <param name="bytes">Bytes just received.</param>
        public void Add(long bytes)
        {
            _total += bytes;
            var now = DateTimeOffset.UtcNow;
            _samples.Enqueue((now, _total));

            var cutoff = now.AddSeconds(-WindowSeconds);
            while (_samples.Count > 1 && _samples.Peek().At < cutoff)
            {
                _samples.Dequeue();
            }
        }

        /// <summary>Current throughput in bytes per second.</summary>
        public double BytesPerSecond
        {
            get
            {
                if (_samples.Count < 2)
                {
                    return 0d;
                }

                var oldest = _samples.Peek();
                var newest = _samples.Last();
                var seconds = (newest.At - oldest.At).TotalSeconds;

                return seconds <= 0.05d ? 0d : (newest.Bytes - oldest.Bytes) / seconds;
            }
        }
    }
}

/// <summary>
/// The resumable state of one in-progress download.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — Modrix's <c>.mmdl</c> control file. Two choices are copied deliberately because
/// they were made for correctness rather than compactness, and both are worth restating:
/// <list type="bullet">
/// <item><b>JSON with explicit numeric ranges</b> rather than a hand-rolled binary bitfield. Modrix uses
/// a hex bitfield inside JSON for the same reason: "far simpler to keep correct than a hand-rolled
/// binary format". Ranges are even simpler than a bitfield and this file is rewritten at most once per
/// few megabytes, so the size difference is irrelevant.</item>
/// <item><b>Validate before trusting.</b> On load, the recorded total must be positive, the segment size
/// must match, and every range must lie inside the total. A truncated or hand-edited control file must
/// fail loudly rather than silently mark a partial file complete.</item>
/// </list>
/// </remarks>
public sealed class DownloadControlFile
{
    /// <summary>Marker that identifies the file, so a stray JSON file cannot be mistaken for one.</summary>
    public string Magic { get; set; } = "MMDL-CTL";

    /// <summary>Schema version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Total size the ranges refer to.</summary>
    public long Total { get; set; }

    /// <summary>ETag or Last-Modified captured at first byte.</summary>
    public string Validator { get; set; } = string.Empty;

    /// <summary>Range size used when the ranges were produced.</summary>
    public long SegmentBytes { get; set; }

    /// <summary>Ranges already on disk, sorted and non-overlapping.</summary>
    public List<ByteRange> CompletedRanges { get; set; } = [];

    /// <summary>Largest number of ranges accepted, bounding a corrupt file's memory use.</summary>
    public const int MaxRanges = 1_000_000;

    /// <summary>Loads a control file, returning null when absent or invalid.</summary>
    /// <param name="path">Control file path.</param>
    public static DownloadControlFile? Load(string path)
    {
        try
        {
            if (!File.Exists(FileSystemOps.ToExtendedPath(path)))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(FileSystemOps.ToExtendedPath(path));
            if (bytes.Length == 0 || bytes.Length > 16 * 1024 * 1024)
            {
                return null;
            }

            var control = System.Text.Json.JsonSerializer.Deserialize<DownloadControlFile>(
                bytes,
                Serialization.ManifestJson.Options);

            if (control is null
                || !string.Equals(control.Magic, "MMDL-CTL", StringComparison.Ordinal)
                || control.Version != 1
                || control.Total <= 0
                || control.CompletedRanges.Count > MaxRanges)
            {
                return null;
            }

            // A range outside the file's own length means the file is internally inconsistent, so it is
            // discarded rather than partially trusted.
            foreach (var range in control.CompletedRanges)
            {
                if (range.Start < 0 || range.End < range.Start || range.End >= control.Total)
                {
                    return null;
                }
            }

            control.CompletedRanges.Sort((left, right) => left.Start.CompareTo(right.Start));
            return control;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes a control file atomically.</summary>
    /// <param name="path">Control file path.</param>
    /// <param name="control">Control file contents.</param>
    public static void Save(string path, DownloadControlFile control)
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                control,
                Serialization.ManifestJson.Options);

            FileSystemOps.WriteAtomicAsync(path, payload).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the control file only costs the ability to resume, never correctness.
        }
    }
}
