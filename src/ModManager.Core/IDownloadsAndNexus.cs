namespace ModManager.Core;

/// <summary>
/// Downloads archives into a cache, with resume, retry and integrity checking.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — Modrix's download engine is described as "segmented, resumable,
/// checksum-verified", and it stages into the library rather than the game folder. The user's spec
/// §8 adds concurrency, pause, resume, cancel, retry, speed/ETA, SHA-256 and failure recovery, and
/// §8 also states the placement rule: <i>"所有下载文件进入 Download Cache 不要直接下载到游戏目录"</i>.
/// </para>
/// <para>
/// The design consequence worth stating: a download is a <i>durable job</i>, not an HTTP call. It
/// has an id, a partial file, a recorded segment map and a state that survives process death. That
/// is why <see cref="DownloadRecord"/> is persisted after every meaningful change — "下载失败能够恢复"
/// is only true if the resume information outlives the process.
/// </para>
/// </remarks>
public interface IDownloadManager
{
    /// <summary>Raised on every progress sample. Handlers run on a background thread.</summary>
    event EventHandler<DownloadProgress>? ProgressChanged;

    /// <summary>Raised when a job reaches a terminal state.</summary>
    event EventHandler<DownloadRecord>? StateChanged;

    /// <summary>All jobs, newest first.</summary>
    IReadOnlyList<DownloadRecord> All();

    /// <summary>One job.</summary>
    DownloadRecord? Get(string downloadId);

    /// <summary>
    /// Queues a download. Returns immediately; the job runs according to the concurrency limit.
    /// </summary>
    /// <param name="request">What to download and where it belongs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DownloadRecord> EnqueueAsync(DownloadRequest request, CancellationToken cancellationToken = default);

    /// <summary>Pauses a job, preserving the partial file and segment map.</summary>
    /// <param name="downloadId">Job to pause.</param>
    Task<bool> PauseAsync(string downloadId);

    /// <summary>Resumes a paused or failed job.</summary>
    /// <param name="downloadId">Job to resume.</param>
    Task<bool> ResumeAsync(string downloadId);

    /// <summary>Cancels a job and deletes its partial file.</summary>
    /// <param name="downloadId">Job to cancel.</param>
    /// <param name="deletePartial">True to also remove the partial file.</param>
    Task<bool> CancelAsync(string downloadId, bool deletePartial = true);

    /// <summary>Retries a failed job, resetting its attempt counter.</summary>
    /// <param name="downloadId">Job to retry.</param>
    Task<bool> RetryAsync(string downloadId);

    /// <summary>Removes a completed job's record and, optionally, the archive it left behind.</summary>
    /// <param name="downloadId">Job to remove.</param>
    /// <param name="deleteFile">True to delete the downloaded archive.</param>
    Task<bool> RemoveAsync(string downloadId, bool deleteFile = false);

    /// <summary>Pauses everything, e.g. on shutdown or when the user hits "pause all".</summary>
    Task PauseAllAsync();

    /// <summary>
    /// Reconciles persisted state with the disk at startup: drops jobs whose files vanished, and
    /// re-queues interrupted downloads so they resume rather than silently stall.
    /// </summary>
    /// <param name="logger">Logger.</param>
    Task<RecoverySummary> RecoverAsync(IAppLogger logger);
}

/// <summary>Summary of download-queue reconciliation at startup.</summary>
/// <param name="Requeued">Jobs restarted automatically.</param>
/// <param name="Discarded">Jobs dropped because their partial file was gone.</param>
/// <param name="Completed">Jobs found already complete on disk.</param>
public sealed record RecoverySummary(int Requeued, int Discarded, int Completed);

/// <summary>What to download.</summary>
public sealed record DownloadRequest
{
    /// <summary>Absolute URL.</summary>
    public required string Url { get; init; }

    /// <summary>Display name and suggested file name.</summary>
    public required string FileName { get; init; }

    /// <summary>Game the archive is for, when known.</summary>
    public string? GameId { get; init; }

    /// <summary>Expected SHA-256, when the source published one.</summary>
    public string? ExpectedSha256 { get; init; }

    /// <summary>Nexus mod id.</summary>
    public int? NexusModId { get; init; }

    /// <summary>Nexus file id.</summary>
    public int? NexusFileId { get; init; }

    /// <summary>Display name for the mod, when the source provided one.</summary>
    public string? ModNameHint { get; init; }

    /// <summary>Cover image URL.</summary>
    public string? CoverImageUrl { get; init; }

    /// <summary>Platform/source identity used to file the completed archive in the package library.</summary>
    public PackageSourceRef? PackageSource { get; init; }

    /// <summary>Display name of the package, when known before the download completes.</summary>
    public string? PackageName { get; init; }

    /// <summary>Package version, when known.</summary>
    public string? PackageVersion { get; init; }

    /// <summary>Package author, when known.</summary>
    public string? PackageAuthor { get; init; }

    /// <summary>Declared dependencies, when the source exposed them before download.</summary>
    public IReadOnlyList<string>? PackageDependencies { get; init; }

    /// <summary>Install-method hint supplied by the source or a previous analysis.</summary>
    public string? InstallMethodHint { get; init; }

    /// <summary>
    /// Extra request headers. Used for the Nexus API key, which is injected here rather than being
    /// baked into the URL — so the key never ends up in a persisted record or a log line.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Access to Nexus Mods through the official API only.
/// </summary>
/// <remarks>
/// <para>
/// HARD CONSTRAINTS (user's spec §7) — these bound what this interface may ever do:
/// </para>
/// <list type="bullet">
/// <item>Only the documented public API and the documented OAuth flow are used.</item>
/// <item>No scraping, no login bypass, no permission bypass, no cookie harvesting, no hardcoded
/// tokens, no circumvention of premium or rate restrictions.</item>
/// <item>A missing or rejected credential is reported as <see cref="NexusError.IsAuthFailure"/>;
/// the caller asks the user to configure it in Settings. It is never worked around.</item>
/// <item>Rate limiting is respected by construction: a minimum request interval is enforced and a
/// 429 backs off rather than retrying hard.</item>
/// </list>
/// <para>
/// Note the deliberate scope limit: because premium-only files are gated <i>server-side</i> by
/// Nexus, this provider simply surfaces that refusal. Nothing here attempts to route around it.
/// </para>
/// </remarks>
public interface INexusProvider
{
    /// <summary>Current authentication mode.</summary>
    NexusAuthMode AuthMode { get; }

    /// <summary>True when a usable credential is configured.</summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// Why the most recent request failed, when it did.
    /// </summary>
    /// <remarks>
    /// Exposed so the interface can say which of "no key", "key rejected", "needs premium", "rate limited",
    /// "server error", "network down" or "unreadable response" happened, instead of showing one generic
    /// failure for all seven. It is cleared at the start of every call and is safe to display: the message it
    /// carries never contains the credential or a signed URL.
    /// </remarks>
    NexusError? LastError { get; }

    /// <summary>
    /// Resolves artwork for one or more games by Nexus domain.
    /// </summary>
    /// <param name="domains">Nexus domains, e.g. <c>cyberpunk2077</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One entry per domain that has artwork; unknown domains are simply absent.</returns>
    /// <remarks>
    /// Domains are resolved to game ids in one request using GraphQL aliases, and the artwork template is read
    /// once. Callers get finished URLs; nothing here guesses an image from a game's name.
    /// </remarks>
    Task<OperationResult<IReadOnlyList<NexusGameArtwork>>> GetGameArtworkAsync(
        IReadOnlyList<string> domains,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the stored credential against the API and caches the account name/premium flag.
    /// </summary>
    /// <param name="credentials">Credentials to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationResult<NexusCredentials>> ValidateCredentialsAsync(
        NexusCredentials credentials,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Begins the OAuth authorisation-code + PKCE flow. Returns the URL the user must open; the
    /// caller then waits on <see cref="CompleteOAuthAsync"/>.
    /// </summary>
    /// <param name="redirectUri">Loopback redirect URI.</param>
    OperationResult<string> BeginOAuth(string redirectUri);

    /// <summary>Completes the OAuth flow using the code the redirect delivered.</summary>
    /// <param name="code">Authorization code.</param>
    /// <param name="redirectUri">Same redirect URI used to begin.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationResult<NexusCredentials>> CompleteOAuthAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken = default);

    /// <summary>Lists games Nexus knows about, for mapping a local game to a domain.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationResult<IReadOnlyList<NexusGame>>> GetGamesAsync(CancellationToken cancellationToken = default);

    /// <summary>Searches mods in one game's domain.</summary>
    /// <param name="query">Search parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationResult<NexusSearchResult>> SearchModsAsync(
        NexusSearchQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches full detail, including the requirement graph.</summary>
    /// <param name="gameDomain">Game domain.</param>
    /// <param name="modId">Nexus mod id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationResult<NexusModDetail>> GetModDetailAsync(
        string gameDomain,
        int modId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the files published for a mod.</summary>
    /// <param name="gameDomain">Game domain.</param>
    /// <param name="modId">Nexus mod id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationResult<IReadOnlyList<NexusModFile>>> GetModFilesAsync(
        string gameDomain,
        int modId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks Nexus for a signed download URL. The link is short-lived and bound to the user's own
    /// account entitlements; when Nexus declines, we report it and stop.
    /// </summary>
    /// <param name="gameDomain">Game domain.</param>
    /// <param name="modId">Nexus mod id.</param>
    /// <param name="fileId">Nexus file id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationResult<NexusDownloadLink>> GetDownloadLinkAsync(
        string gameDomain,
        int modId,
        int fileId,
        CancellationToken cancellationToken = default);

    /// <summary>Checks several installed mods for newer versions.</summary>
    /// <param name="gameDomain">Game domain.</param>
    /// <param name="mods">(local mod id, nexus mod id, current version) triples.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<OperationResult<IReadOnlyList<ModUpdateInfo>>> CheckForUpdatesAsync(
        string gameDomain,
        IReadOnlyList<(string ModId, int NexusModId, string? Version)> mods,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Header set to attach to a download request for this provider. The API key is injected here
    /// at request time so it is never persisted alongside the job.
    /// </summary>
    IReadOnlyDictionary<string, string> GetDownloadHeaders();
}

/// <summary>Checks what a mod needs against what is installed.</summary>
public interface IDependencyService
{
    /// <summary>
    /// Reports every unsatisfied requirement for a set of mods, including a missing mod loader.
    /// </summary>
    /// <param name="adapter">Game adapter, for loader requirements.</param>
    /// <param name="installPath">Absolute install directory.</param>
    /// <param name="mods">Mods to check.</param>
    IReadOnlyList<MissingDependency> FindMissing(IGameAdapter adapter, string installPath, IReadOnlyList<ModMetadata> mods);

    /// <summary>Reports declared incompatibilities between enabled mods.</summary>
    /// <param name="mods">Mods to check.</param>
    IReadOnlyList<HealthIssue> FindIncompatibilities(IReadOnlyList<ModMetadata> mods);
}

/// <summary>Runs health checks for a game and produces human-actionable issues.</summary>
public interface IHealthService
{
    /// <summary>
    /// Aggregates loader checks, dependency checks, incompatibilities, deploy blockers and
    /// file-drift findings into one list.
    /// </summary>
    /// <param name="adapter">Game adapter.</param>
    /// <param name="instance">Registered game.</param>
    /// <param name="mods">Installed mods.</param>
    /// <param name="conflicts">Current conflicts.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<HealthIssue>> CheckAsync(
        IGameAdapter adapter,
        GameInstance instance,
        IReadOnlyList<ModMetadata> mods,
        IReadOnlyList<FileConflict> conflicts,
        CancellationToken cancellationToken = default);
}

/// <summary>Scans a game folder for content this manager did not deploy.</summary>
public interface IExternalContentService
{
    /// <summary>
    /// Lists unmanaged content under the mod root, classified using the definition's external-scan
    /// rules and base-file list.
    /// </summary>
    /// <param name="adapter">Game adapter.</param>
    /// <param name="installPath">Absolute install directory.</param>
    /// <param name="manifests">Manifests, so our own files are excluded.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ExternalContent>> ScanAsync(
        IGameAdapter adapter,
        string installPath,
        IReadOnlyList<ModManifest> manifests,
        CancellationToken cancellationToken = default);
}
