namespace ModManager.Core;

/// <summary>
/// Credentials and endpoints for the Nexus Mods provider.
/// </summary>
/// <remarks>
/// <para>
/// LEGAL / ETHICAL CONSTRAINTS — these are requirements, not preferences, and they shape the API:
/// </para>
/// <list type="bullet">
/// <item>Only the official Nexus Mods API and its documented OAuth flow are used. There is no
/// scraping, no login bypass, no permission bypass, no cookie theft, and no attempt to work around
/// premium/rate restrictions.</item>
/// <item>No token is ever hardcoded or committed. The API key lives only in settings, is written
/// to disk with user-only permissions, and is held in memory as a secret type that refuses to
/// print itself.</item>
/// <item>Downloads go through the documented <c>download_link.json</c> endpoints, which enforce
/// the user's own account entitlements server-side. When Nexus says no, we surface that to the
/// user and stop — we do not route around it.</item>
/// </list>
/// <para>
/// The API key is the officially supported mechanism for personal, non-commercial use and requires
/// the user to generate it themselves on their own account page; that keeps the trust boundary in
/// the right place.
/// </para>
/// </remarks>
public sealed record NexusCredentials
{
    /// <summary>
    /// Personal API key. Never logged, never serialised into an export, never shown in the UI after entry.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>OAuth access token when the user authorised through the browser flow instead.</summary>
    public string? AccessToken { get; init; }

    /// <summary>OAuth refresh token, used to renew <see cref="AccessToken"/> silently.</summary>
    public string? RefreshToken { get; init; }

    /// <summary>When the access token expires, so we renew before failing a request.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Account name, cached for display. Non-sensitive.</summary>
    public string? UserName { get; init; }

    /// <summary>True when the account is premium, as reported by the API. Used only to explain rate limits.</summary>
    public bool? IsPremium { get; init; }

    /// <summary>True when we have something usable to authenticate with.</summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(AccessToken);

    /// <summary>True when the access token needs refreshing before use.</summary>
    public bool NeedsRefresh =>
        string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(RefreshToken)
        && ExpiresAt is { } expiry
        && expiry <= DateTimeOffset.UtcNow.AddMinutes(2);

    /// <summary>An empty credential set.</summary>
    public static NexusCredentials Empty { get; } = new();
}

/// <summary>How the provider authenticates. Both options are official.</summary>
public enum NexusAuthMode
{
    /// <summary>No credentials; browsing public metadata still works.</summary>
    None = 0,

    /// <summary>Personal API key entered by the user in Settings.</summary>
    ApiKey = 1,

    /// <summary>OAuth browser authorization with PKCE.</summary>
    OAuth = 2,
}

/// <summary>A game as Nexus knows it, used to map a local game to a Nexus domain.</summary>
/// <param name="Id">Nexus numeric game id.</param>
/// <param name="DomainName">URL domain, e.g. <c>skyrimspecialedition</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="ModCount">Published mod count.</param>
/// <param name="DownloadCount">Cumulative downloads.</param>
/// <param name="NexusFileCount">Published file count.</param>
/// <param name="PictureUrl">
/// Artwork URL the API reports for the game, when it reports one. Nexus serves it as WebP, which the local
/// image cache stores and the system codec decodes.
/// </param>
public sealed record NexusGame(
    int Id,
    string DomainName,
    string Name,
    int ModCount,
    long DownloadCount,
    int NexusFileCount,
    string? PictureUrl = null);

/// <summary>
/// A game's artwork, resolved from the GraphQL artwork templates.
/// </summary>
/// <param name="DomainName">Nexus domain the artwork belongs to.</param>
/// <param name="GameId">Numeric Nexus game id, substituted into the template.</param>
/// <param name="Schema">Artwork schema the API reported for this game.</param>
/// <param name="TileUrl">Small square image, the right shape for a game card.</param>
/// <param name="ThumbnailUrl">Medium image.</param>
/// <param name="HeroUrl">Wide banner image.</param>
/// <remarks>
/// The API does not return a finished URL: <c>gameArtwork</c> answers with templates such as
/// <c>https://images.nexusmods.com/images/games/4_3/tile_{gameId}.jpg</c> and the client fills in the id it
/// read from <c>game(domainName:)</c>. That indirection is why the v1 game list had nothing usable.
/// </remarks>
public sealed record NexusGameArtwork(
    string DomainName,
    int GameId,
    string? Schema = null,
    string? TileUrl = null,
    string? ThumbnailUrl = null,
    string? HeroUrl = null)
{
    /// <summary>
    /// Best image for a game card: the square tile, falling back to the thumbnail and then the banner.
    /// </summary>
    public string? CardUrl => TileUrl ?? ThumbnailUrl ?? HeroUrl;
}

/// <summary>Search filters for the mod browser.</summary>
public sealed record NexusSearchQuery
{
    /// <summary>Nexus game domain to search within.</summary>
    public required string GameDomain { get; init; }

    /// <summary>Free-text query.</summary>
    public string? SearchText { get; init; }

    /// <summary>Filter by author.</summary>
    public string? Author { get; init; }

    /// <summary>Filter by uploader.</summary>
    public string? UploadedBy { get; init; }

    /// <summary>Sort field, e.g. <c>endorsements</c>, <c>downloads</c>, <c>lastupdated</c>.</summary>
    public string SortField { get; init; } = "endorsements";

    /// <summary>Sort direction, <c>desc</c> or <c>asc</c>.</summary>
    public string SortDirection { get; init; } = "desc";

    /// <summary>Page index, 1-based.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Page size; the API caps this.</summary>
    public int PageSize { get; init; } = 20;

    /// <summary>Restrict to adult content. Never enabled by default.</summary>
    public bool IncludeAdult { get; init; }

    /// <summary>Filter by category name.</summary>
    public string? Category { get; init; }
}

/// <summary>A page of search results.</summary>
/// <param name="Items">Mods on this page.</param>
/// <param name="Page">Page index.</param>
/// <param name="PageSize">Requested page size.</param>
/// <param name="TotalCount">Total matches, when the API reported it.</param>
/// <param name="KeywordFilteredLocally">
/// True when a keyword was matched by this application rather than by Nexus, because the v1 API has no search
/// endpoint. The interface says so, so a short list is not mistaken for the site having nothing.
/// </param>
public sealed record NexusSearchResult(
    IReadOnlyList<NexusModSummary> Items,
    int Page,
    int PageSize,
    int? TotalCount,
    bool KeywordFilteredLocally = false)
{
    /// <summary>Convenience for the UI's paging controls.</summary>
    public bool HasNextPage => TotalCount is { } total
        ? Page * PageSize < total
        : Items.Count == PageSize;
}

/// <summary>A mod as listed by search or the latest-updated feed.</summary>
public sealed record NexusModSummary
{
    /// <summary>Nexus mod id.</summary>
    public required int ModId { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Author.</summary>
    public string? Author { get; init; }

    /// <summary>Uploader, which may differ from the author.</summary>
    public string? UploadedBy { get; init; }

    /// <summary>Short summary.</summary>
    public string? Summary { get; init; }

    /// <summary>Current version string.</summary>
    public string? Version { get; init; }

    /// <summary>Endorsement count, used for credibility.</summary>
    public int EndorsementCount { get; init; }

    /// <summary>Total downloads.</summary>
    public long DownloadCount { get; init; }

    /// <summary>Unique downloads.</summary>
    public long UniqueDownloadCount { get; init; }

    /// <summary>Cover image URL.</summary>
    public string? PictureUrl { get; init; }

    /// <summary>Page URL, so the user can open the real page.</summary>
    public string? PageUrl { get; init; }

    /// <summary>Last update timestamp.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Creation timestamp.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Category name.</summary>
    public string? Category { get; init; }

    /// <summary>True when the mod is flagged adult.</summary>
    public bool ContainsAdultContent { get; init; }

    /// <summary>True when the author allows mod-manager downloads via the API.</summary>
    public bool ModManagerDownloadAllowed { get; init; } = true;

    /// <summary>Game domain the mod belongs to.</summary>
    public string? GameDomain { get; init; }
}

/// <summary>A downloadable file attached to a Nexus mod.</summary>
public sealed record NexusModFile
{
    /// <summary>Nexus file id, required to request a download link.</summary>
    public required int FileId { get; init; }

    /// <summary>File name as published.</summary>
    public required string Name { get; init; }

    /// <summary>Version string.</summary>
    public string? Version { get; init; }

    /// <summary>Category, e.g. MAIN / OPTIONAL / MISCELLANEOUS.</summary>
    public string? Category { get; init; }

    /// <summary>Size in kilobytes, as the API reports it.</summary>
    public long SizeKilobytes { get; init; }

    /// <summary>Upload timestamp, used to pick a sensible default.</summary>
    public DateTimeOffset? UploadedAt { get; init; }

    /// <summary>Human description of the file.</summary>
    public string? Description { get; init; }

    /// <summary>True when the uploader marked this the main file.</summary>
    public bool IsPrimary { get; init; }

    /// <summary>Size in bytes.</summary>
    public long SizeBytes => SizeKilobytes * 1024L;
}

/// <summary>Full detail for one mod, including the requirement graph.</summary>
public sealed record NexusModDetail
{
    /// <summary>Summary fields.</summary>
    public required NexusModSummary Summary { get; init; }

    /// <summary>Long description (HTML from the API).</summary>
    public string? Description { get; init; }

    /// <summary>Available files.</summary>
    public IReadOnlyList<NexusModFile> Files { get; init; } = [];

    /// <summary>Hard requirements: mods that must be installed for this one to work.</summary>
    public IReadOnlyList<NexusModReference> Requirements { get; init; } = [];

    /// <summary>Mods that require this one.</summary>
    public IReadOnlyList<NexusModReference> RequiredBy { get; init; } = [];

    /// <summary>Mods this one is known to conflict with, plus recommendations.</summary>
    public IReadOnlyList<NexusModReference> Conflicts { get; init; } = [];

    /// <summary>Mods that supersede this one.</summary>
    public IReadOnlyList<NexusModReference> SupersededBy { get; init; } = [];

    /// <summary>Mods contained in the same page's "mods requiring this file" lists.</summary>
    public IReadOnlyList<NexusModReference> Recommended { get; init; } = [];

    /// <summary>Tags supplied by the author.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Changelog HTML, when published.</summary>
    public string? Changelog { get; init; }
}

/// <summary>A reference from one Nexus mod to another, as the requirement graph carries it.</summary>
/// <param name="ModId">Referenced mod id.</param>
/// <param name="Name">Referenced mod name, as published.</param>
/// <param name="GameDomain">Domain the reference lives in.</param>
/// <param name="Notes">Author notes about the relationship.</param>
/// <param name="Url">Page URL.</param>
public sealed record NexusModReference(int ModId, string Name, string? GameDomain, string? Notes, string? Url);

/// <summary>A resolved, short-lived download URL for one file.</summary>
/// <remarks>
/// Nexus download links are deliberately ephemeral and bound to the requesting account. We treat
/// them as secrets: not logged, not persisted, and re-requested when expired.
/// </remarks>
/// <param name="Url">The signed URL.</param>
/// <param name="FileName">Suggested file name.</param>
/// <param name="ExpiresAt">When the link should be re-requested.</param>
/// <param name="RequiresPremium">True when Nexus reported the file is premium-only for this account.</param>
public sealed record NexusDownloadLink(string Url, string FileName, DateTimeOffset? ExpiresAt, bool RequiresPremium);

/// <summary>Result of asking the provider about an update.</summary>
/// <param name="ModId">Local mod id.</param>
/// <param name="NexusModId">Nexus mod id.</param>
/// <param name="CurrentVersion">Version we have.</param>
/// <param name="LatestVersion">Newest published version.</param>
public sealed record ModUpdateInfo(string ModId, int NexusModId, string? CurrentVersion, string? LatestVersion)
{
    /// <summary>True when the versions differ textually and the newer one is not older.</summary>
    public bool HasUpdate => !string.IsNullOrWhiteSpace(LatestVersion)
        && !string.Equals(CurrentVersion?.Trim(), LatestVersion.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Why a Nexus request failed, in terms the interface can act on.
/// </summary>
/// <remarks>
/// Collapsing every failure into "connection failed" is what hides the difference between "you have not
/// configured a key", "your key was rejected", "this file needs premium", "you are asking too often" and "the
/// network is down". Each of those needs a different message and a different action from the user, so the
/// classification travels with the error instead of being guessed from prose at the call site.
/// </remarks>
public enum NexusFailureKind
{
    /// <summary>No failure.</summary>
    None = 0,

    /// <summary>No credential is configured.</summary>
    MissingCredential = 1,

    /// <summary>The credential was rejected (401).</summary>
    Unauthorized = 2,

    /// <summary>Refused (403): premium-only, author-disabled API access, or age gating.</summary>
    Forbidden = 3,

    /// <summary>The mod or file does not exist (404).</summary>
    NotFound = 4,

    /// <summary>Rate limited (429).</summary>
    RateLimited = 5,

    /// <summary>The server failed (5xx).</summary>
    ServerError = 6,

    /// <summary>The host could not be reached (DNS, TLS, connection refused).</summary>
    Network = 7,

    /// <summary>The request timed out.</summary>
    Timeout = 8,

    /// <summary>A response arrived but could not be parsed, or lacked required fields.</summary>
    Malformed = 9,

    /// <summary>The request was rejected as invalid (400/422): our parameters do not match the API.</summary>
    BadRequest = 10,
}

/// <summary>
/// Error returned by the Nexus provider, carrying enough context to explain it to the user.
/// </summary>
/// <param name="Message">Safe, user-facing message. Never contains the API key or a signed URL.</param>
/// <param name="StatusCode">HTTP status when applicable.</param>
/// <param name="IsAuthFailure">True when the credential is missing, invalid or expired.</param>
/// <param name="IsRateLimited">True when we were throttled; the caller should back off, not retry hard.</param>
/// <param name="IsPremiumRequired">True when Nexus declined because the file needs a premium account.</param>
/// <param name="Kind">Classification the interface switches on.</param>
/// <param name="RetryAfter">How long the server asked us to wait, when it said.</param>
public sealed record NexusError(
    string Message,
    int? StatusCode,
    bool IsAuthFailure,
    bool IsRateLimited,
    bool IsPremiumRequired,
    NexusFailureKind Kind = NexusFailureKind.None,
    TimeSpan? RetryAfter = null)
{
    /// <summary>Builds an authentication error.</summary>
    public static NexusError Auth(string message) => new(message, 401, true, false, false, NexusFailureKind.Unauthorized);

    /// <summary>Builds a rate-limit error.</summary>
    public static NexusError RateLimited(string message) => new(message, 429, false, true, false, NexusFailureKind.RateLimited);

    /// <summary>Builds a rate-limit error that records how long the server asked us to wait.</summary>
    /// <param name="message">Message.</param>
    /// <param name="retryAfter">Server-provided delay.</param>
    public static NexusError RateLimited(string message, TimeSpan? retryAfter) =>
        new(message, 429, false, true, false, NexusFailureKind.RateLimited, retryAfter);

    /// <summary>Builds a premium-required error.</summary>
    public static NexusError Premium(string message) => new(message, 403, false, false, true, NexusFailureKind.Forbidden);

    /// <summary>Builds a "no credential configured" error.</summary>
    /// <param name="message">Message.</param>
    public static NexusError Missing(string message) =>
        new(message, null, true, false, false, NexusFailureKind.MissingCredential);

    /// <summary>Builds a "not found" error.</summary>
    /// <param name="message">Message.</param>
    public static NexusError NotFound(string message) =>
        new(message, 404, false, false, false, NexusFailureKind.NotFound);

    /// <summary>Builds a server-side error.</summary>
    /// <param name="message">Message.</param>
    /// <param name="status">HTTP status.</param>
    public static NexusError Server(string message, int? status) =>
        new(message, status, false, false, false, NexusFailureKind.ServerError);

    /// <summary>Builds a transport error.</summary>
    /// <param name="message">Message.</param>
    public static NexusError Network(string message) =>
        new(message, null, false, false, false, NexusFailureKind.Network);

    /// <summary>Builds a timeout error.</summary>
    /// <param name="message">Message.</param>
    public static NexusError Timeout(string message) =>
        new(message, null, false, false, false, NexusFailureKind.Timeout);

    /// <summary>Builds a "the response could not be understood" error.</summary>
    /// <param name="message">Message.</param>
    public static NexusError Malformed(string message) =>
        new(message, null, false, false, false, NexusFailureKind.Malformed);

    /// <summary>Classifies an HTTP failure.</summary>
    /// <param name="status">Response status.</param>
    /// <param name="message">Message to carry.</param>
    /// <param name="retryAfter">Server-provided delay, when any.</param>
    public static NexusError FromStatus(System.Net.HttpStatusCode status, string message, TimeSpan? retryAfter = null) =>
        status switch
        {
            System.Net.HttpStatusCode.Unauthorized => Auth(message),
            System.Net.HttpStatusCode.Forbidden => Premium(message),
            System.Net.HttpStatusCode.NotFound => NotFound(message),
            System.Net.HttpStatusCode.TooManyRequests => RateLimited(message, retryAfter),

            // 422 and 400 are our fault, not the server's, and saying "server error" would send the user
            // looking for an outage that is not there.
            System.Net.HttpStatusCode.UnprocessableEntity or System.Net.HttpStatusCode.BadRequest =>
                BadRequest(message, (int)status),

            _ => Server(message, (int)status),
        };

    /// <summary>Builds an "invalid request" error.</summary>
    /// <param name="message">Message.</param>
    /// <param name="status">HTTP status.</param>
    public static NexusError BadRequest(string message, int status) =>
        new(message, status, false, false, false, NexusFailureKind.BadRequest);

    /// <summary>
    /// Classifies a transport exception.
    /// </summary>
    /// <param name="exception">Exception raised by the HTTP stack.</param>
    /// <param name="message">Message to carry.</param>
    /// <remarks>
    /// A cancelled request is only a timeout when the caller did not cancel it; an explicit cancellation is
    /// not an error and is reported as a transport failure so it is never shown as "the network is down".
    /// </remarks>
    public static NexusError FromException(Exception exception, string message)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            TaskCanceledException { InnerException: TimeoutException } => Timeout(message),
            TaskCanceledException => Timeout(message),
            TimeoutException => Timeout(message),
            System.Text.Json.JsonException => Malformed(message),
            _ => Network(message),
        };
    }
}
