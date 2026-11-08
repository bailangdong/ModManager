namespace ModManager.Core;

/// <summary>
/// A download job, including everything needed to resume it after a crash or a network drop.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — Modrix's download engine is "segmented, resumable, checksum-verified" and
/// stages into a cache, never into the game folder. The user's spec §8 adds concurrency, pause,
/// resume, cancel, retry, progress with speed/ETA, SHA-256 and failure recovery. The field that
/// makes all of that survive a restart is <see cref="Segments"/>: the byte ranges already on
/// disk. Without it, "resume" means "start over".
/// </remarks>
public sealed record DownloadRecord
{
    /// <summary>Stable job id.</summary>
    public required string Id { get; init; }

    /// <summary>Display name (usually the remote file name).</summary>
    public required string FileName { get; init; }

    /// <summary>Source URL. Never persisted with embedded credentials.</summary>
    public required string Url { get; init; }

    /// <summary>Final absolute path for the completed archive inside the download cache.</summary>
    public required string TargetPath { get; init; }

    /// <summary>Absolute path of the in-progress partial file.</summary>
    public required string PartialPath { get; init; }

    /// <summary>Job state.</summary>
    public DownloadState State { get; init; } = DownloadState.Queued;

    /// <summary>Total bytes if the server declared them; null when unknown (chunked responses).</summary>
    public long? TotalBytes { get; init; }

    /// <summary>Bytes already written.</summary>
    public long BytesDownloaded { get; init; }

    /// <summary>Latest observed throughput in bytes/second.</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>When the download started.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When it last made progress.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>When it completed.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Number of attempts so far, for backoff and for the retry button's label.</summary>
    public int Attempts { get; init; }

    /// <summary>Last error message, shown in the Downloads page.</summary>
    public string? Error { get; init; }

    /// <summary>Expected SHA-256, when the source declared one.</summary>
    public string? ExpectedSha256 { get; init; }

    /// <summary>Computed SHA-256 after completion, or after a resume-safety re-hash.</summary>
    public string? ActualSha256 { get; init; }

    /// <summary>True when the server advertised <c>Accept-Ranges: bytes</c>, so resuming is safe.</summary>
    public bool ServerSupportsResume { get; init; }

    /// <summary>ETag or Last-Modified captured at first byte, used to detect a changed file.</summary>
    public string? Validator { get; init; }

    /// <summary>Ranges already present in the partial file, sorted and coalesced.</summary>
    public IReadOnlyList<ByteRange> Segments { get; init; } = [];

    /// <summary>Game the archive is destined for, when known at queue time.</summary>
    public string? GameId { get; init; }

    /// <summary>Nexus mod id, when this is a Nexus download.</summary>
    public int? NexusModId { get; init; }

    /// <summary>Nexus file id.</summary>
    public int? NexusFileId { get; init; }

    /// <summary>Display name for the mod once installed, when the source provided one.</summary>
    public string? ModNameHint { get; init; }

    /// <summary>Cover image URL for the Downloads page.</summary>
    public string? CoverImageUrl { get; init; }

    /// <summary>Platform/source identity used to file the archive in the package library.</summary>
    public PackageSourceRef? PackageSource { get; init; }

    /// <summary>Package display name, when known before completion.</summary>
    public string? PackageName { get; init; }

    /// <summary>Package version, when known.</summary>
    public string? PackageVersion { get; init; }

    /// <summary>Package author, when known.</summary>
    public string? PackageAuthor { get; init; }

    /// <summary>Declared dependencies, when known before completion.</summary>
    public IReadOnlyList<string>? PackageDependencies { get; init; }

    /// <summary>Install-method hint supplied by the source.</summary>
    public string? InstallMethodHint { get; init; }

    /// <summary>Package-library id once the completed archive has been committed.</summary>
    public string? PackageId { get; init; }

    /// <summary>Last package-commit error, so a completed download can be retried without re-downloading.</summary>
    public string? CommitError { get; init; }

    /// <summary>True when the job is active.</summary>
    public bool IsActive => State is DownloadState.Resolving or DownloadState.Downloading or DownloadState.Verifying;

    /// <summary>True when the job can be resumed from where it stopped.</summary>
    public bool CanResume => State is DownloadState.Paused or DownloadState.Failed or DownloadState.Queued
        && Segments.Count > 0;

    /// <summary>Best-effort ETA, or null when throughput is unknown or zero.</summary>
    public TimeSpan? Eta
    {
        get
        {
            if (TotalBytes is not { } total || BytesPerSecond <= 0.5d)
            {
                return null;
            }

            var remaining = Math.Max(0L, total - BytesDownloaded);
            return TimeSpan.FromSeconds(remaining / BytesPerSecond);
        }
    }

    /// <summary>Completion fraction 0..1, or null when the total size is unknown.</summary>
    public double? Fraction => TotalBytes is { } total && total > 0
        ? Math.Clamp((double)BytesDownloaded / total, 0d, 1d)
        : null;
}

/// <summary>An inclusive byte range already present in a partial file.</summary>
/// <param name="Start">First byte offset.</param>
/// <param name="End">Last byte offset, inclusive.</param>
public sealed record ByteRange(long Start, long End)
{
    /// <summary>Number of bytes covered.</summary>
    public long Length => End - Start + 1;

    /// <summary>True when this range touches or overlaps <paramref name="other"/>.</summary>
    public bool IsAdjacentTo(ByteRange other) => other.Start <= End + 1 && Start <= other.End + 1;
}

/// <summary>A live progress sample pushed to the UI.</summary>
/// <param name="DownloadId">Job the sample belongs to.</param>
/// <param name="BytesDownloaded">Total bytes written so far.</param>
/// <param name="TotalBytes">Declared total, when known.</param>
/// <param name="BytesPerSecond">Throughput.</param>
/// <param name="Eta">Estimated time remaining.</param>
/// <param name="State">Current state.</param>
public sealed record DownloadProgress(
    string DownloadId,
    long BytesDownloaded,
    long? TotalBytes,
    double BytesPerSecond,
    TimeSpan? Eta,
    DownloadState State);

/// <summary>Options controlling download behaviour, persisted in settings.</summary>
public sealed record DownloadOptions
{
    /// <summary>Maximum simultaneous downloads.</summary>
    public int MaxConcurrency { get; init; } = 3;

    /// <summary>Segment count per download, for range requests. 1 disables segmentation.</summary>
    public int SegmentCount { get; init; } = 4;

    /// <summary>Minimum segment size, so tiny files are not split.</summary>
    public long MinimumSegmentBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>Attempts before a job is marked failed.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Base delay for exponential backoff.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Per-request timeout applied to the whole transfer.</summary>
    public TimeSpan RequestTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>How long a stalled connection may go without bytes before it is retried.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>User-Agent presented to servers.</summary>
    public string UserAgent { get; init; } = "ModManager/0.1 (+https://example.invalid)";

    /// <summary>Re-hash a resumed partial file before trusting it.</summary>
    public bool VerifyAfterResume { get; init; } = true;
}
