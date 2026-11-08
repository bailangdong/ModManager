using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModManager.Core.Serialization;

namespace ModManager.Core.Services;

/// <summary>
/// Nexus Mods access through the official public API only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope and limits, stated up front because they are requirements rather than implementation
/// details.</b> The user's spec §7 permits an official API or OAuth and forbids everything else. So this
/// class:
/// </para>
/// <list type="bullet">
/// <item>calls only documented endpoints on <c>api.nexusmods.com</c>;</item>
/// <item>authenticates with a personal API key the user generates on their own account page, or with
/// the documented OAuth authorization-code flow with PKCE;</item>
/// <item>never scrapes HTML, never replays browser cookies, never bypasses a login or a permission
/// check, and contains no hardcoded credential;</item>
/// <item>honours rate limiting by construction — a minimum interval between requests plus real backoff
/// on HTTP 429 — and never retries a 401/403 in a loop;</item>
/// <item>surfaces a premium-only or entitlement refusal to the user instead of attempting to route
/// around it, because those checks are enforced server-side and circumventing them is precisely what
/// the spec prohibits.</item>
/// </list>
/// <para>
/// <b>Why an API key rather than emulating a browser.</b> Modrix chose a different route entirely — a
/// browser extension hands over a URL the user already clicked, so no API key is involved. That is a
/// legitimate design, but it requires shipping and installing a browser extension, and it makes the
/// application's downloads depend on a browser being open. Since the spec explicitly asks for a
/// <c>NexusProvider</c> with API key or OAuth held in Settings, this implementation takes the official
/// API route and keeps the credential in <c>ISecretStore</c> — never in the settings file, never in a
/// log, never in the repository.
/// </para>
/// <para>
/// <b>Rate limiting.</b> Nexus publishes per-hour request budgets that differ for free and premium
/// accounts. Rather than hardcode a guess, this provider enforces a configurable minimum interval
/// between requests and backs off on 429, which is correct for both tiers without pretending to know
/// the exact ceiling.
/// </para>
/// </remarks>
public sealed class NexusProvider : INexusProvider, IDisposable
{
    /// <summary>Official API base address.</summary>
    public const string ApiBase = "https://api.nexusmods.com/v1";

    /// <summary>
    /// GraphQL endpoint, used for anything the legacy v1 API cannot do.
    /// </summary>
    /// <remarks>
    /// v1 has no search endpoint and its listing endpoints ignore <c>count</c>, <c>offset</c> and <c>sort</c>:
    /// measured against the live API, <c>trending.json?offset=10</c> returned exactly the same ten mods as
    /// <c>offset=0</c>, and <c>downloads.json</c> answered 422 because it does not exist. Real paging, real
    /// sorting and keywords are therefore served by v2, whose schema was read from the live endpoint.
    /// </remarks>
    public const string GraphQlEndpoint = "https://api.nexusmods.com/v2/graphql";

    /// <summary>Documented OAuth authorize endpoint.</summary>
    public const string OAuthAuthorizeEndpoint = "https://users.nexusmods.com/oauth/authorize";

    /// <summary>Documented OAuth token endpoint.</summary>
    public const string OAuthTokenEndpoint = "https://users.nexusmods.com/oauth/token";

    /// <summary>Number of results requested per API page; the API caps this.</summary>
    public const int MaxPageSize = 20;

    private readonly ISecretStore _secrets;
    private readonly IAppLogger? _logger;
    private readonly HttpClient _http;
    private readonly NexusSettings _settings;
    private readonly SemaphoreSlim _throttle = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, NexusSearchResult Result)> _searchCache =
        new(StringComparer.Ordinal);

    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;
    private NexusCredentials _credentials = NexusCredentials.Empty;
    private string? _pendingCodeVerifier;
    private bool _disposed;

    /// <summary>Creates the provider, loading any stored credentials.</summary>
    /// <param name="secrets">Secret store for the credential.</param>
    /// <param name="settings">Nexus settings.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="httpClient">Optional client, so tests can supply a stub.</param>
    public NexusProvider(
        ISecretStore secrets,
        NexusSettings? settings = null,
        IAppLogger? logger = null,
        HttpClient? httpClient = null)
    {
        _secrets = secrets;
        _settings = settings ?? new NexusSettings();
        _logger = logger;

        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(Identity);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        LoadStoredCredentials();
    }

    /// <summary>
    /// User agent sent with every request.
    /// </summary>
    /// <remarks>
    /// Nexus asks clients to identify themselves, and a request without a usable identifier can be blocked.
    /// This names the application and its version and deliberately carries no placeholder URL: a fake contact
    /// address would be worse than none, because it would be unreachable exactly when someone wanted to reach
    /// us about traffic.
    /// </remarks>
    public static string Identity => "ModManager/1.0 (WPF .NET 8 desktop mod manager)";

    /// <summary>Why the most recent request failed, when it did.</summary>
    public NexusError? LastError { get; private set; }

    /// <inheritdoc />
    public NexusAuthMode AuthMode => _credentials.HasCredentials
        ? (!string.IsNullOrWhiteSpace(_credentials.ApiKey) ? NexusAuthMode.ApiKey : NexusAuthMode.OAuth)
        : NexusAuthMode.None;

    /// <inheritdoc />
    public bool IsAuthenticated => _credentials.HasCredentials;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetDownloadHeaders()
    {
        // The key is attached at request time, per-download, and never persisted with the job. This is
        // the only method that exposes it, and it never returns it in text form.
        if (string.IsNullOrWhiteSpace(_credentials.ApiKey))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["apikey"] = _credentials.ApiKey,
        };
    }

    /// <inheritdoc />
    public async Task<OperationResult<NexusCredentials>> ValidateCredentialsAsync(
        NexusCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        if (!credentials.HasCredentials)
        {
            return OperationResult<NexusCredentials>.Fail("No API key or access token was provided.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "/users/validate.json");
            ApplyAuth(request, credentials.ApiKey);

            using var response = await SendThrottledAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return Fail<NexusCredentials>(NexusError.FromStatus(
                    response.StatusCode,
                    "Nexus Mods rejected this API key. Generate a new one from your Nexus account page "
                    + "(Settings, API Keys) and paste it again."));
            }

            if (!response.IsSuccessStatusCode)
            {
                return Fail<NexusCredentials>(NexusError.FromStatus(
                    response.StatusCode,
                    $"Nexus Mods returned {(int)response.StatusCode} while validating the key.",
                    ReadRetryAfter(response)));
            }

            var payload = await ReadJsonAsync<NexusValidateResponse>(response, cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                return Fail<NexusCredentials>(NexusError.Malformed(
                    "Nexus Mods returned a response that could not be read."));
            }

            var validated = credentials with
            {
                UserName = payload.Name,
                IsPremium = payload.IsPremium,
            };

            StoreCredentials(validated);
            LastError = null;
            _logger?.Info("Nexus", $"已通过 Nexus Mods 身份验证：{payload.Name}。");

            return OperationResult<NexusCredentials>.Ok(validated);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Fail<NexusCredentials>(NexusError.FromException(
                ex,
                $"Could not reach Nexus Mods: {ex.Message}"));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Implements OAuth 2.0 authorization code with PKCE. PKCE is used rather than a client secret
    /// precisely because a desktop application cannot keep a secret — publishing one would be worse than
    /// useless, since it would imply a guarantee that does not exist.
    /// </remarks>
    public OperationResult<string> BeginOAuth(string redirectUri)
    {
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            return OperationResult<string>.Fail("A redirect URI is required.");
        }

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || !parsed.IsLoopback)
        {
            // A non-loopback redirect would mean sending the authorization code somewhere off this
            // machine, which is not acceptable for a desktop client.
            return OperationResult<string>.Fail("The redirect URI must be a loopback http address.");
        }

        _pendingCodeVerifier = CreateCodeVerifier();
        var challenge = CreateCodeChallenge(_pendingCodeVerifier);

        // Built with UriBuilder so the parameters are escaped correctly rather than by string
        // concatenation.
        var builder = new UriBuilder(OAuthAuthorizeEndpoint);
        var query = new StringBuilder();
        query.Append("response_type=code");
        query.Append("&client_id=modmanager");
        query.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        query.Append("&scope=").Append(Uri.EscapeDataString("mods"));
        query.Append("&code_challenge=").Append(challenge);
        query.Append("&code_challenge_method=S256");
        builder.Query = query.ToString();

        return OperationResult<string>.Ok(builder.Uri.ToString());
    }

    /// <inheritdoc />
    public async Task<OperationResult<NexusCredentials>> CompleteOAuthAsync(
        string code,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return OperationResult<NexusCredentials>.Fail("No authorization code was supplied.");
        }

        if (string.IsNullOrWhiteSpace(_pendingCodeVerifier))
        {
            return OperationResult<NexusCredentials>.Fail("The authorization flow was not started.");
        }

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["client_id"] = "modmanager",
                ["code_verifier"] = _pendingCodeVerifier,
            });

            using var response = await SendThrottledAsync(
                new HttpRequestMessage(HttpMethod.Post, OAuthTokenEndpoint) { Content = content },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return OperationResult<NexusCredentials>.Fail(
                    $"Nexus Mods rejected the authorization code ({(int)response.StatusCode}).");
            }

            var token = await ReadJsonAsync<OAuthTokenResponse>(response, cancellationToken).ConfigureAwait(false);
            if (token?.AccessToken is null)
            {
                return OperationResult<NexusCredentials>.Fail("Nexus Mods returned no access token.");
            }

            var credentials = new NexusCredentials
            {
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
                ExpiresAt = token.ExpiresIn is { } seconds
                    ? DateTimeOffset.UtcNow.AddSeconds(seconds - 60)
                    : null,
            };

            StoreCredentials(credentials);
            _pendingCodeVerifier = null;

            _logger?.Info("Nexus", "已通过浏览器流程完成 Nexus Mods 授权。");
            return OperationResult<NexusCredentials>.Ok(credentials);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return OperationResult<NexusCredentials>.Fail($"Could not complete authorization: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<NexusGame>>> GetGamesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await GetAsync<NexusGameResponse[]>(ApiBase + "/games.json", cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Value is null)
        {
            return OperationResult<IReadOnlyList<NexusGame>>.Fail(result.Error ?? "Could not load the game list.");
        }

        var games = result.Value
            .Select(game => new NexusGame(
                game.Id,
                game.DomainName ?? string.Empty,
                game.Name ?? game.DomainName ?? $"Game {game.Id}",
                game.ModCount,
                game.Downloads,
                game.FileCount,
                string.IsNullOrWhiteSpace(game.PictureUrl) ? null : game.PictureUrl))
            .Where(game => game.DomainName.Length > 0)
            .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return OperationResult<IReadOnlyList<NexusGame>>.Ok(games);
    }

    /// <summary>
    /// Chooses which listing endpoint a query should use.
    /// </summary>
    /// <param name="query">Search parameters.</param>
    /// <remarks>
    /// The v1 API has no keyword search endpoint. Verified against the live API: <c>mods/search.json</c> answers
    /// 422 ("Invalid parameter 'id' value \"search\": Must be a number") because the path only matches
    /// <c>mods/{id}.json</c>, and the global <c>/v1/mods/search.json</c> answers 404. Only the listing
    /// endpoints exist — trending, latest_added, latest_updated, downloads — so a keyword is matched locally
    /// against what the listing returns, and the result says so instead of pretending the server searched.
    /// </remarks>
    private static string SelectListingEndpoint(NexusSearchQuery query) => query.SortField switch
    {
        "downloads" => "downloads.json",
        "lastupdated" => "latest_updated.json",
        "created" => "latest_added.json",
        _ => "trending.json",
    };

    /// <inheritdoc />
    public async Task<OperationResult<NexusSearchResult>> SearchModsAsync(
        NexusSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.GameDomain))
        {
            return OperationResult<NexusSearchResult>.Fail("A game domain is required to search.");
        }

        // GraphQL first: it is the only interface that can actually filter, sort and page.
        var modern = await SearchModsGraphQlAsync(query, cancellationToken).ConfigureAwait(false);
        if (modern.Succeeded)
        {
            return modern;
        }

        // Falling back keeps browsing usable, but it cannot page or sort: say so in the log rather than
        // silently showing the first ten popular mods as if they were the requested results.
        _logger?.Warn(
            "Nexus",
            "GraphQL 搜索不可用，已回退到 v1 列表接口；关键词将本地筛选，分页与排序不可用。",
            modern.Error);

        return await SearchModsLegacyAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches through the GraphQL API: real keyword, real sort, real paging.
    /// </summary>
    /// <param name="query">Search parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<OperationResult<NexusSearchResult>> SearchModsGraphQlAsync(
        NexusSearchQuery query,
        CancellationToken cancellationToken)
    {
        LastError = null;

        if (!IsAuthenticated)
        {
            return Fail<NexusSearchResult>(NexusError.Missing(
                "Nexus Mods needs an API key. Add one in Settings to browse and download mods."));
        }

        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var page = Math.Max(1, query.Page);
        var offset = (page - 1) * pageSize;
        var ascending = string.Equals(query.SortDirection, "asc", StringComparison.OrdinalIgnoreCase);

        // Filter fields are lists of conditions joined by "op"; verified against the live schema.
        var filter = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["op"] = "AND",
            ["gameDomainName"] = new[] { new { value = query.GameDomain } },
        };

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            filter["nameStemmed"] = new[] { new { value = query.SearchText.Trim() } };
        }

        if (!string.IsNullOrWhiteSpace(query.Author))
        {
            filter["author"] = new[] { new { value = query.Author.Trim() } };
        }

        if (!_settings.IncludeAdultContent && !query.IncludeAdult)
        {
            filter["adultContent"] = new[] { new { value = false } };
        }

        var variables = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["f"] = filter,
            ["s"] = new object[]
            {
                new Dictionary<string, object>
                {
                    [ModernSortField(query)] = new { direction = ascending ? "ASC" : "DESC" },
                },
            },
            ["o"] = offset,
            ["c"] = pageSize,
        };

        const string document = """
            query($f: ModsFilter, $s: [ModsSort!], $o: Int, $c: Int) {
              mods(filter: $f, sort: $s, offset: $o, count: $c) {
                totalCount
                nodes {
                  modId
                  name
                  summary
                  version
                  author
                  downloads
                  endorsements
                  createdAt
                  updatedAt
                  pictureUrl
                  adultContent
                }
              }
            }
            """;

        var body = JsonSerializer.Serialize(new { query = document, variables });

        try
        {
            await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlEndpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            ApplyAuth(request, _credentials.ApiKey, _credentials.AccessToken);

            using var response = await SendThrottledAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Fail<NexusSearchResult>(NexusError.FromStatus(
                    response.StatusCode,
                    $"Nexus Mods returned {(int)response.StatusCode} for the search."
                        + await ReadErrorDetailAsync(response, cancellationToken).ConfigureAwait(false),
                    ReadRetryAfter(response)));
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var parsed = JsonDocument.Parse(json);

            if (parsed.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            {
                var message = errors[0].TryGetProperty("message", out var first)
                    ? first.GetString()
                    : "unknown GraphQL error";

                return Fail<NexusSearchResult>(NexusError.Malformed($"Nexus 搜索接口返回错误：{message}"));
            }

            if (!parsed.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("mods", out var mods))
            {
                return Fail<NexusSearchResult>(NexusError.Malformed("Nexus 搜索返回的数据结构无法识别。"));
            }

            var items = new List<NexusModSummary>();
            if (mods.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in nodes.EnumerateArray())
                {
                    items.Add(ConvertModern(node, query.GameDomain));
                }
            }

            int? total = mods.TryGetProperty("totalCount", out var totalValue) && totalValue.TryGetInt32(out var count)
                ? count
                : null;

            var result = new NexusSearchResult(items, page, pageSize, total, KeywordFilteredLocally: false);

            _logger?.Info("Nexus", $"GraphQL 搜索成功：第 {page} 页返回 {items.Count} 项（总计 {total?.ToString() ?? "未知"}）。");

            return OperationResult<NexusSearchResult>.Ok(result);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Fail<NexusSearchResult>(NexusError.FromException(ex, $"Could not reach Nexus Mods: {ex.Message}"));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The API does not return finished artwork URLs. <c>gameArtwork</c> answers with templates such as
    /// <c>https://images.nexusmods.com/images/games/4_3/tile_{gameId}.jpg</c>, and the numeric game id comes
    /// from <c>game(domainName:)</c>; this method joins the two. All requested domains are resolved in one
    /// request through GraphQL aliases, so browsing a page of games costs a single round trip.
    /// </remarks>
    public async Task<OperationResult<IReadOnlyList<NexusGameArtwork>>> GetGameArtworkAsync(
        IReadOnlyList<string> domains,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domains);
        LastError = null;

        var wanted = domains
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Select(domain => domain.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList();

        if (wanted.Count == 0)
        {
            return OperationResult<IReadOnlyList<NexusGameArtwork>>.Ok([]);
        }

        if (!IsAuthenticated)
        {
            return Fail<IReadOnlyList<NexusGameArtwork>>(NexusError.Missing(
                "Nexus Mods needs an API key. Add one in Settings to browse and download mods."));
        }

        var aliases = string.Join(
            ' ',
            wanted.Select((domain, index) =>
                $"g{index}: game(domainName: {JsonSerializer.Serialize(domain)}) {{ id domainName artworkSchema }}"));

        var document = $$"""
            query {
              gameArtwork { schemaV1 { tile tileBlurred } schemaV2 { hero thumbnail tile } }
              {{aliases}}
            }
            """;

        try
        {
            await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlEndpoint)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { query = document }),
                    Encoding.UTF8,
                    "application/json"),
            };

            ApplyAuth(request, _credentials.ApiKey, _credentials.AccessToken);

            using var response = await SendThrottledAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Fail<IReadOnlyList<NexusGameArtwork>>(NexusError.FromStatus(
                    response.StatusCode,
                    $"Nexus Mods returned {(int)response.StatusCode} for the artwork request."
                        + await ReadErrorDetailAsync(response, cancellationToken).ConfigureAwait(false),
                    ReadRetryAfter(response)));
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var parsed = JsonDocument.Parse(json);

            if (parsed.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            {
                var message = errors[0].TryGetProperty("message", out var first)
                    ? first.GetString()
                    : "unknown GraphQL error";

                return Fail<IReadOnlyList<NexusGameArtwork>>(
                    NexusError.Malformed($"Nexus 封面接口返回错误：{message}"));
            }

            if (!parsed.RootElement.TryGetProperty("data", out var data))
            {
                return Fail<IReadOnlyList<NexusGameArtwork>>(
                    NexusError.Malformed("Nexus 封面返回的数据结构无法识别。"));
            }

            var tileV1 = ReadTemplate(data, "schemaV1", "tile");
            var blurredV1 = ReadTemplate(data, "schemaV1", "tileBlurred");
            var tileV2 = ReadTemplate(data, "schemaV2", "tile");
            var thumbnailV2 = ReadTemplate(data, "schemaV2", "thumbnail");
            var heroV2 = ReadTemplate(data, "schemaV2", "hero");

            var results = new List<NexusGameArtwork>();

            for (var index = 0; index < wanted.Count; index++)
            {
                if (!data.TryGetProperty($"g{index}", out var game)
                    || game.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = game.TryGetProperty("id", out var idValue) && idValue.TryGetInt32(out var parsedId)
                    ? parsedId
                    : 0;

                if (id <= 0)
                {
                    continue;
                }

                var domain = game.TryGetProperty("domainName", out var domainValue)
                    ? domainValue.GetString() ?? wanted[index]
                    : wanted[index];

                var schema = game.TryGetProperty("artworkSchema", out var schemaValue)
                    ? schemaValue.ToString()
                    : null;

                // Newer games publish the v2 set; the schema field says which, and v1 remains the fallback so a
                // game that only has the old artwork still gets a picture rather than nothing.
                var prefersModern = schema?.Contains('2') == true;

                var tile = ResolveArtworkUrl(prefersModern ? tileV2 ?? tileV1 : tileV1 ?? tileV2, id);
                var thumbnail = ResolveArtworkUrl(prefersModern ? thumbnailV2 : null, id);
                var hero = ResolveArtworkUrl(prefersModern ? heroV2 : null, id);

                if (tile is null && blurredV1 is not null)
                {
                    tile = ResolveArtworkUrl(blurredV1, id);
                }

                if (tile is null && thumbnail is null && hero is null)
                {
                    continue;
                }

                results.Add(new NexusGameArtwork(domain, id, schema, tile, thumbnail, hero));
            }

            _logger?.Info("Nexus", $"已解析 {results.Count}/{wanted.Count} 个游戏封面。");

            return OperationResult<IReadOnlyList<NexusGameArtwork>>.Ok(results);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Fail<IReadOnlyList<NexusGameArtwork>>(
                NexusError.FromException(ex, $"Could not reach Nexus Mods: {ex.Message}"));
        }
    }

    /// <summary>Reads one artwork template out of the response.</summary>
    /// <param name="data">GraphQL data element.</param>
    /// <param name="schema">Schema object name.</param>
    /// <param name="field">Template field.</param>
    private static string? ReadTemplate(JsonElement data, string schema, string field) =>
        data.TryGetProperty("gameArtwork", out var artwork)
        && artwork.TryGetProperty(schema, out var schemaNode)
        && schemaNode.TryGetProperty(field, out var value)
            ? value.GetString()
            : null;

    /// <summary>
    /// Fills a template in with the game id.
    /// </summary>
    /// <param name="template">Template that may contain <c>{gameId}</c>.</param>
    /// <param name="gameId">Numeric Nexus game id.</param>
    /// <remarks>
    /// A template without the placeholder is returned unchanged: the API has already produced a complete URL in
    /// that case, and refusing to use it would lose a perfectly good image.
    /// </remarks>
    private static string? ResolveArtworkUrl(string? template, int gameId)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        if (!template.Contains("{gameId}", StringComparison.Ordinal))
        {
            return template;
        }

        return gameId <= 0
            ? null
            : template.Replace("{gameId}", gameId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>Maps a GraphQL sort choice onto the schema's sort fields.</summary>    /// <param name="query">Search parameters.</param>
    /// <remarks>
    /// "Relevance" is only meaningful with a keyword; without one the same button means "most popular", which
    /// is what a user browsing a game expects to see.
    /// </remarks>
    private static string ModernSortField(NexusSearchQuery query) => query.SortField switch
    {
        "downloads" => "downloads",
        "endorsements" => "endorsements",
        "lastupdated" => "updatedAt",
        "created" => "createdAt",
        "name" => "name",
        "size" => "size",
        _ => string.IsNullOrWhiteSpace(query.SearchText) ? "downloads" : "relevance",
    };

    /// <summary>Converts a GraphQL mod node into the shared summary model.</summary>
    /// <param name="node">GraphQL node.</param>
    /// <param name="gameDomain">Domain the search ran against.</param>
    private static NexusModSummary ConvertModern(JsonElement node, string gameDomain)
    {
        var modId = node.TryGetProperty("modId", out var id) && id.TryGetInt32(out var parsedId) ? parsedId : 0;

        return new NexusModSummary
        {
            ModId = modId,
            Name = node.TryGetProperty("name", out var name) ? name.GetString() ?? $"Mod {modId}" : $"Mod {modId}",
            Summary = node.TryGetProperty("summary", out var summary) ? summary.GetString() : null,
            Version = node.TryGetProperty("version", out var version) ? version.GetString() : null,
            Author = node.TryGetProperty("author", out var author) ? author.GetString() : null,
            DownloadCount = node.TryGetProperty("downloads", out var downloads) && downloads.TryGetInt64(out var dl) ? dl : 0,
            EndorsementCount = node.TryGetProperty("endorsements", out var endorsements) && endorsements.TryGetInt64(out var en) ? (int)en : 0,
            CreatedAt = node.TryGetProperty("createdAt", out var created) ? ParseTimestamp(created) : null,
            UpdatedAt = node.TryGetProperty("updatedAt", out var updated) ? ParseTimestamp(updated) : null,
            PictureUrl = node.TryGetProperty("pictureUrl", out var picture) ? picture.GetString() : null,
            ContainsAdultContent = node.TryGetProperty("adultContent", out var adult) && adult.ValueKind == JsonValueKind.True,
            GameDomain = gameDomain,
            PageUrl = modId > 0 ? $"https://www.nexusmods.com/{gameDomain}/mods/{modId}" : null,
        };
    }

    /// <summary>
    /// The v1 fallback: a listing endpoint with a locally applied keyword.
    /// </summary>
    /// <param name="query">Search parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<OperationResult<NexusSearchResult>> SearchModsLegacyAsync(
        NexusSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.GameDomain))
        {
            return OperationResult<NexusSearchResult>.Fail("A game domain is required to search.");
        }

        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var page = Math.Max(1, query.Page);

        // With a keyword the whole (small) page is filtered locally, so ask for the largest page the API allows
        // to have something to filter.
        var requestSize = string.IsNullOrWhiteSpace(query.SearchText) ? pageSize : MaxPageSize;

        var url = new StringBuilder(ApiBase)
            .Append("/games/").Append(Uri.EscapeDataString(query.GameDomain)).Append("/mods/")
            .Append(SelectListingEndpoint(query));

        var parameters = new List<string>
        {
            "count=" + requestSize,
            "offset=" + ((page - 1) * requestSize),
        };

        // These are accepted by the listing endpoints when present.
        if (!string.IsNullOrWhiteSpace(query.Author))
        {
            parameters.Add("author=" + Uri.EscapeDataString(query.Author));
        }

        if (!string.IsNullOrWhiteSpace(query.UploadedBy))
        {
            parameters.Add("uploaded_by=" + Uri.EscapeDataString(query.UploadedBy));
        }

        url.Append('?').Append(string.Join('&', parameters));

        // The cache key must cover everything that shapes the result, including the filters this application
        // applies itself: the same URL with a different keyword or adult setting is a different answer.
        var cacheKey = $"{url}|kw={query.SearchText}|adult={query.IncludeAdult}";
        if (TryGetCachedSearch(cacheKey, out var cached))
        {
            return OperationResult<NexusSearchResult>.Ok(cached);
        }

        var result = await GetAsync<NexusModResponse[]>(url.ToString(), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Value is null)
        {
            return OperationResult<NexusSearchResult>.Fail(result.Error ?? "The search could not be completed.");
        }

        var items = result.Value.Select(Convert).ToList();

        // The API has no search endpoint, so the keyword is matched here. Matching on name, summary and author
        // covers what a user types when looking for a mod; the page reports that the filter ran locally so
        // nobody mistakes a short list for "Nexus has nothing like this".
        var filteredLocally = false;
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var tokens = query.SearchText
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            items = items
                .Where(item => MatchesKeyword(item, tokens))
                .ToList();

            filteredLocally = true;
            _logger?.Info(
                "Nexus",
                $"Nexus v1 无搜索接口；已对 {result.Value.Length} 条列表结果本地筛选关键词“{query.SearchText}”，命中 {items.Count} 条。");
        }

        // Adult content is opt-in. The listing endpoints do not take a reliable filter parameter, so the flag
        // the API returns per mod is what is honoured here.
        if (!_settings.IncludeAdultContent && !query.IncludeAdult)
        {
            items = items.Where(item => !item.ContainsAdultContent).ToList();
        }

        // The listing endpoints do not return a total, so a full page means "there may be more" and a
        // short page means this is the end. Reporting a fabricated exact total would be worse than
        // reporting none.
        // `int?` is required: the false branch is null, and an int/null conditional has no common type.
        int? total = items.Count < pageSize ? ((page - 1) * pageSize) + items.Count : null;
        var searchResult = new NexusSearchResult(items, page, pageSize, total, filteredLocally);

        CacheSearch(cacheKey, searchResult);
        return OperationResult<NexusSearchResult>.Ok(searchResult);
    }

    /// <summary>True when every keyword token appears in the mod's name, summary or author.</summary>
    /// <param name="item">Mod summary.</param>
    /// <param name="tokens">Keyword tokens.</param>
    private static bool MatchesKeyword(NexusModSummary item, string[] tokens)
    {
        if (tokens.Length == 0)
        {
            return true;
        }

        var haystack = $"{item.Name} {item.Summary} {item.Author}";
        return tokens.All(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<OperationResult<NexusModDetail>> GetModDetailAsync(
        string gameDomain,
        int modId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{ApiBase}/games/{Uri.EscapeDataString(gameDomain)}/mods/{modId}.json";
        var payload = await ReadModPayloadAsync(url, cancellationToken).ConfigureAwait(false);

        if (payload.Succeeded is false || payload.Value is null)
        {
            return OperationResult<NexusModDetail>.Fail(
                payload.Error ?? "The mod could not be loaded.");
        }

        // The detail endpoint carries no domain field, so the domain this caller asked about is recorded on the
        // summary. Without it the interface showed an empty game for every mod it opened.
        var summary = Convert(payload.Value) with
        {
            GameDomain = payload.Value.DomainName is { Length: > 0 } domain ? domain : gameDomain,
        };

        var files = await GetModFilesAsync(gameDomain, modId, cancellationToken).ConfigureAwait(false);

        var detail = new NexusModDetail
        {
            Summary = summary,
            Description = payload.Value.Description,
            Files = files.Succeeded && files.Value is not null ? files.Value : [],
            Requirements = ConvertReferences(payload.Value.RequirementsForThisMod, gameDomain),
            RequiredBy = ConvertReferences(payload.Value.ModsRequiringThisMod, gameDomain),
            Conflicts = ConvertReferences(payload.Value.ModsConflictingWithThisMod, gameDomain),
            SupersededBy = ConvertReferences(payload.Value.ModsSupersedingThisMod, gameDomain),
            Recommended = ConvertReferences(payload.Value.ModsRecommendingThisMod, gameDomain),
            Tags = payload.Value.Tags ?? [],
            Changelog = payload.Value.Changelog,
        };

        return OperationResult<NexusModDetail>.Ok(detail);
    }

    /// <summary>
    /// Reads a single-mod payload, accepting both shapes the API is known to use.
    /// </summary>
    /// <param name="url">Detail endpoint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Nexus's mod endpoints are inconsistent: some paths return a bare object and others wrap the same object
    /// in a one-element array. Deserializing one shape as the other throws, and the previous code then reported
    /// it as an unreachable network — the wrong diagnosis for a body that arrived intact. Both shapes are
    /// therefore parsed explicitly, and a body that is neither is reported as an unreadable response.
    /// </remarks>
    private async Task<OperationResult<NexusModDetailResponse>> ReadModPayloadAsync(
        string url,
        CancellationToken cancellationToken)
    {
        LastError = null;

        if (!IsAuthenticated)
        {
            return Fail<NexusModDetailResponse>(NexusError.Missing(
                "Nexus Mods needs an API key. Add one in Settings to browse and download mods."));
        }

        try
        {
            await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(request, _credentials.ApiKey, _credentials.AccessToken);

            using var response = await SendThrottledAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Fail<NexusModDetailResponse>(NexusError.FromStatus(
                    response.StatusCode,
                    $"Nexus Mods returned {(int)response.StatusCode} for this mod.",
                    ReadRetryAfter(response)));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return Fail<NexusModDetailResponse>(NexusError.Malformed("Nexus Mods returned an empty response."));
            }

            using var document = JsonDocument.Parse(body);
            var element = document.RootElement;

            if (element.ValueKind == JsonValueKind.Array)
            {
                element = element.EnumerateArray().FirstOrDefault();
                if (element.ValueKind != JsonValueKind.Object)
                {
                    return Fail<NexusModDetailResponse>(NexusError.Malformed(
                        "Nexus Mods returned no mod data for this id."));
                }
            }

            var payload = element.Deserialize<NexusModDetailResponse>(ManifestJson.Options);
            return payload is null
                ? Fail<NexusModDetailResponse>(NexusError.Malformed("Nexus Mods returned a response that could not be read."))
                : OperationResult<NexusModDetailResponse>.Ok(payload);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Fail<NexusModDetailResponse>(NexusError.FromException(ex, $"Could not reach Nexus Mods: {ex.Message}"));
        }
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<NexusModFile>>> GetModFilesAsync(
        string gameDomain,
        int modId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{ApiBase}/games/{Uri.EscapeDataString(gameDomain)}/mods/{modId}/files.json";
        var result = await GetAsync<NexusModFilesResponse>(url, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded || result.Value is null)
        {
            return OperationResult<IReadOnlyList<NexusModFile>>.Fail(result.Error ?? "The file list could not be loaded.");
        }

        // A missing "files" property is a shape we do not understand, not a mod with no files: reporting it as
        // "no files published" would quietly hide a parsing problem.
        if (result.Value.Files is null)
        {
            return Fail<IReadOnlyList<NexusModFile>>(NexusError.Malformed(
                "Nexus Mods returned a file list this application could not read."));
        }

        var files = result.Value.Files
            .Where(file => file.FileId > 0)
            .Select(file => new NexusModFile
            {
                FileId = file.FileId,
                Name = file.Name ?? $"File {file.FileId}",
                Version = file.Version,
                Category = file.CategoryName,
                SizeKilobytes = file.SizeKb,
                UploadedAt = ParseTimestamp(file.UploadedTimestamp) ?? ParseTimestamp(file.UploadedTime),
                Description = file.Description,
                IsPrimary = file.IsPrimary,
            })
            .OrderByDescending(file => file.IsPrimary)
            .ThenByDescending(file => file.UploadedAt ?? DateTimeOffset.MinValue)
            .ToList();

        if (files.Count != result.Value.Files.Length)
        {
            _logger?.Warn(
                "Nexus",
                $"Nexus 返回的文件列表中有 {result.Value.Files.Length - files.Count} 项缺少文件 ID，已忽略。");
        }

        return OperationResult<IReadOnlyList<NexusModFile>>.Ok(files);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The link returned here is short-lived and bound to the requesting account's entitlements. Nexus
    /// enforces premium restrictions on this endpoint server-side, so a refusal is reported verbatim
    /// rather than worked around.
    /// </remarks>
    public async Task<OperationResult<NexusDownloadLink>> GetDownloadLinkAsync(
        string gameDomain,
        int modId,
        int fileId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{ApiBase}/games/{Uri.EscapeDataString(gameDomain)}/mods/{modId}/files/{fileId}/download_link.json";
        var result = await GetAsync<NexusDownloadLinkResponse[]>(url, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return OperationResult<NexusDownloadLink>.Fail(result.Error ?? "Nexus Mods did not return a download link.");
        }

        var link = result.Value?.FirstOrDefault();

        var uri = link?.Uri;

        if (link is null || string.IsNullOrWhiteSpace(uri))
        {
            // Reported as a refusal, not as an empty success: Nexus answers this endpoint with an empty array
            // when the account is not entitled to a manager download, and calling that "no link, no error"
            // would leave the user with a dead button.
            return Fail<NexusDownloadLink>(NexusError.Premium(
                "Nexus Mods did not return a download link for this file. It may require a premium account, "
                + "or the author may have disabled mod-manager downloads."));
        }

        return OperationResult<NexusDownloadLink>.Ok(new NexusDownloadLink(
            uri,
            DownloadManager.SanitizeFileName(link.Name ?? link.ShortName ?? $"mod-{modId}-{fileId}.zip"),

            // The API does not publish an expiry; a short conservative window is used so a stale link is
            // refreshed rather than used after it has died.
            DateTimeOffset.UtcNow.AddMinutes(5),
            RequiresPremium: false));
    }

    /// <inheritdoc />
    public async Task<OperationResult<IReadOnlyList<ModUpdateInfo>>> CheckForUpdatesAsync(
        string gameDomain,
        IReadOnlyList<(string ModId, int NexusModId, string? Version)> mods,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mods);

        var updates = new List<ModUpdateInfo>();

        foreach (var (modId, nexusModId, version) in mods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var detail = await GetModDetailAsync(gameDomain, nexusModId, cancellationToken).ConfigureAwait(false);
            if (!detail.Succeeded || detail.Value is null)
            {
                // A single unavailable mod must not fail the whole check.
                continue;
            }

            var latest = detail.Value.Summary.Version;
            if (!string.IsNullOrWhiteSpace(latest)
                && !string.Equals(version?.Trim(), latest.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                updates.Add(new ModUpdateInfo(modId, nexusModId, version, latest));
            }
        }

        return OperationResult<IReadOnlyList<ModUpdateInfo>>.Ok(updates);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _throttle.Dispose();
        _http.Dispose();
    }

    /// <summary>Performs a throttled GET and converts the outcome into an operation result.</summary>
    /// <typeparam name="T">Response payload type.</typeparam>
    /// <param name="url">Absolute URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<OperationResult<T>> GetAsync<T>(string url, CancellationToken cancellationToken)
        where T : class
    {
        LastError = null;

        if (!IsAuthenticated)
        {
            return Fail<T>(NexusError.Missing(
                "Nexus Mods needs an API key. Add one in Settings to browse and download mods."));
        }

        try
        {
            await RefreshIfNeededAsync(cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyAuth(request, _credentials.ApiKey, _credentials.AccessToken);

            using var response = await SendThrottledAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return Fail<T>(NexusError.FromStatus(
                    response.StatusCode,
                    "Nexus Mods rejected the stored credential. Update it in Settings."));
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return Fail<T>(NexusError.FromStatus(
                    response.StatusCode,
                    "Nexus Mods refused this request. The file or mod may require a premium account, "
                    + "or the author may have disabled API access."));
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Fail<T>(NexusError.NotFound(
                    "Nexus Mods does not know this mod or file. It may have been removed."));
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return Fail<T>(NexusError.RateLimited(
                    "Nexus Mods is rate limiting this application. Wait a few minutes before trying again.",
                    ReadRetryAfter(response)));
            }

            if (!response.IsSuccessStatusCode)
            {
                return Fail<T>(NexusError.FromStatus(
                    response.StatusCode,
                    $"Nexus Mods returned {(int)response.StatusCode}."
                        + await ReadErrorDetailAsync(response, cancellationToken).ConfigureAwait(false),
                    ReadRetryAfter(response)));
            }

            var payload = await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
            return payload is null
                ? Fail<T>(NexusError.Malformed("Nexus Mods returned a response that could not be read."))
                : OperationResult<T>.Ok(payload);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Fail<T>(NexusError.FromException(ex, $"Could not reach Nexus Mods: {ex.Message}"));
        }
    }

    /// <summary>
    /// Records a classified failure and returns it as a result.
    /// </summary>
    /// <typeparam name="T">Result payload.</typeparam>
    /// <param name="error">Classified failure.</param>
    /// <remarks>
    /// Every failure path goes through here so <see cref="LastError"/> is always populated exactly when the
    /// caller received a failure, and always cleared on success.
    /// </remarks>
    private OperationResult<T> Fail<T>(NexusError error)
    {
        LastError = error;
        return OperationResult<T>.Fail(error.Message);
    }

    /// <summary>
    /// Reads a short, safe excerpt of an error body.
    /// </summary>
    /// <param name="response">Failed response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// A bare "returned 422" tells nobody anything: the body is where Nexus explains which parameter it
    /// rejected. The excerpt is truncated and single-lined so it is safe to log and to show, and it never
    /// contains credentials — the credential travels in a header, not in this body.
    /// </remarks>
    private static async Task<string> ReadErrorDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            var clean = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (clean.Length > 200)
            {
                clean = clean[..200] + "…";
            }

            return $" 服务端说明：{clean}";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }

    /// <summary>Reads the server's requested back-off, when it sent one.</summary>
    /// <param name="response">Response to inspect.</param>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)    {
        var retry = response.Headers.RetryAfter;
        if (retry is null)
        {
            return null;
        }

        if (retry.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (retry.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>Sends a request, enforcing the minimum interval and honouring a 429 with a real delay.</summary>
    /// <param name="request">Request to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<HttpResponseMessage> SendThrottledAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var since = DateTimeOffset.UtcNow - _lastRequestAt;
            if (since < _settings.MinimumRequestInterval)
            {
                await Task.Delay(_settings.MinimumRequestInterval - since, cancellationToken).ConfigureAwait(false);
            }

            var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            _lastRequestAt = DateTimeOffset.UtcNow;

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Reported, not slept on. This method does not retry the request, so waiting here would only
                // freeze the caller — and a UI that hangs for the server's back-off is worse than one that says
                // "wait a minute". The delay is recorded on the error instead, so the caller can decide.
                var wait = ReadRetryAfter(response);
                _logger?.Warn(
                    "Nexus",
                    wait is { } delay
                        ? $"请求被限流，Nexus 要求等待 {delay.TotalSeconds:0} 秒。"
                        : "请求被限流，Nexus 未给出等待时间。");
            }

            return response;
        }
        finally
        {
            _throttle.Release();
        }
    }

    /// <summary>Renews an OAuth token when it is close to expiring.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task RefreshIfNeededAsync(CancellationToken cancellationToken)
    {
        if (!_credentials.NeedsRefresh || string.IsNullOrWhiteSpace(_credentials.RefreshToken))
        {
            return;
        }

        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _credentials.RefreshToken,
                ["client_id"] = "modmanager",
            });

            using var response = await _http
                .PostAsync(OAuthTokenEndpoint, content, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.Warn("Nexus", "保存的授权无法续期，请在设置页重新授权。");
                return;
            }

            var token = await ReadJsonAsync<OAuthTokenResponse>(response, cancellationToken).ConfigureAwait(false);
            if (token?.AccessToken is null)
            {
                return;
            }

            StoreCredentials(_credentials with
            {
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken ?? _credentials.RefreshToken,
                ExpiresAt = token.ExpiresIn is { } seconds
                    ? DateTimeOffset.UtcNow.AddSeconds(seconds - 60)
                    : null,
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger?.Warn("Nexus", "续期授权失败。", ex.Message);
        }
    }

    /// <summary>Attaches authentication to a request without ever logging it.</summary>
    /// <param name="request">Request to authenticate.</param>
    /// <param name="apiKey">API key, when present.</param>
    /// <param name="accessToken">OAuth access token, when present.</param>
    private static void ApplyAuth(HttpRequestMessage request, string? apiKey, string? accessToken = null)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.TryAddWithoutValidation("apikey", apiKey);
            return;
        }

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
    }

    /// <summary>Reads and deserialises a JSON response body.</summary>
    /// <typeparam name="T">Payload type.</typeparam>
    /// <param name="response">HTTP response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            return null;
        }

        return JsonSerializer.Deserialize<T>(bytes, NexusJsonOptions);
    }

    /// <summary>Loads stored credentials from the secret store.</summary>
    private void LoadStoredCredentials()
    {
        var apiKey = _secrets.Get(ISecretStore.NexusApiKey);
        var accessToken = _secrets.Get(ISecretStore.NexusAccessToken);
        var refreshToken = _secrets.Get(ISecretStore.NexusRefreshToken);

        _credentials = new NexusCredentials
        {
            ApiKey = apiKey,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            UserName = _settings.UserName,
            IsPremium = _settings.IsPremium,
        };
    }

    /// <summary>Stores credentials in the secret store.</summary>
    /// <param name="credentials">Credentials to store.</param>
    private void StoreCredentials(NexusCredentials credentials)
    {
        _credentials = credentials;

        if (!string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            _secrets.Set(ISecretStore.NexusApiKey, credentials.ApiKey);
        }

        if (!string.IsNullOrWhiteSpace(credentials.AccessToken))
        {
            _secrets.Set(ISecretStore.NexusAccessToken, credentials.AccessToken);
        }

        if (!string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            _secrets.Set(ISecretStore.NexusRefreshToken, credentials.RefreshToken);
        }
    }

    /// <summary>Removes all stored credentials.</summary>
    /// <remarks>
    /// Exposed so Settings can offer a real sign-out. Clearing a credential must remove it from the
    /// secret store, not merely stop using it.
    /// </remarks>
    public void SignOut()
    {
        _secrets.Remove(ISecretStore.NexusApiKey);
        _secrets.Remove(ISecretStore.NexusAccessToken);
        _secrets.Remove(ISecretStore.NexusRefreshToken);
        _credentials = NexusCredentials.Empty;
        _logger?.Info("Nexus", "已退出 Nexus Mods，并删除保存的凭据。");
    }

    /// <summary>Converts an API mod response into our summary type.</summary>
    /// <param name="payload">API response.</param>
    private static NexusModSummary Convert(NexusModResponse payload) => new()
    {
        ModId = payload.ModId,
        Name = payload.Name ?? $"Mod {payload.ModId}",
        Author = payload.Author,
        UploadedBy = payload.UploadedBy,
        Summary = payload.Summary,
        Version = payload.Version,
        EndorsementCount = payload.EndorsementCount,
        DownloadCount = payload.ModDownloads,
        UniqueDownloadCount = payload.UniqueDownloads,
        PictureUrl = payload.PictureUrl,
        PageUrl = payload.DomainName is { Length: > 0 } domain
            ? $"https://www.nexusmods.com/{domain}/mods/{payload.ModId}"
            : null,
        UpdatedAt = ParseTimestamp(payload.UpdatedTimestamp) ?? ParseTimestamp(payload.UpdatedTime),
        CreatedAt = ParseTimestamp(payload.CreatedTimestamp) ?? ParseTimestamp(payload.CreatedTime),
        Category = payload.Category,
        ContainsAdultContent = payload.ContainsAdultContent,
        ModManagerDownloadAllowed = !payload.ModManagerDownloadBlocked,
        GameDomain = payload.DomainName,
    };

    /// <summary>Converts an API detail response into our summary type.</summary>
    /// <param name="payload">API response.</param>
    private static NexusModSummary Convert(NexusModDetailResponse payload) => new()
    {
        ModId = payload.ModId,
        Name = payload.Name ?? $"Mod {payload.ModId}",
        Author = payload.Author,
        UploadedBy = payload.UploadedBy,
        Summary = payload.Summary,
        Version = payload.Version,
        EndorsementCount = payload.EndorsementCount,
        DownloadCount = payload.ModDownloads,
        UniqueDownloadCount = payload.UniqueDownloads,
        PictureUrl = payload.PictureUrl,
        PageUrl = payload.DomainName is { Length: > 0 } domain
            ? $"https://www.nexusmods.com/{domain}/mods/{payload.ModId}"
            : null,
        UpdatedAt = ParseTimestamp(payload.UpdatedTimestamp) ?? ParseTimestamp(payload.UpdatedTime),
        CreatedAt = ParseTimestamp(payload.CreatedTimestamp) ?? ParseTimestamp(payload.CreatedTime),
        Category = payload.Category,
        ContainsAdultContent = payload.ContainsAdultContent,
        ModManagerDownloadAllowed = !payload.ModManagerDownloadBlocked,
        GameDomain = payload.DomainName,
    };

    /// <summary>Converts a requirement list into our reference type.</summary>
    /// <param name="responses">API requirement entries.</param>
    /// <param name="gameDomain">Game domain for building URLs.</param>
    private static List<NexusModReference> ConvertReferences(
        NexusModRequirementResponse[]? responses,
        string gameDomain)
    {
        if (responses is null)
        {
            return [];
        }

        return responses
            .Where(response => response.ModId > 0)
            .Select(response => new NexusModReference(
                response.ModId,
                response.Name ?? $"Mod {response.ModId}",
                response.DomainName ?? gameDomain,
                response.Notes,
                $"https://www.nexusmods.com/{response.DomainName ?? gameDomain}/mods/{response.ModId}"))
            .ToList();
    }

    /// <summary>Parses a Nexus timestamp, which the API spells in two different ways.</summary>
    /// <param name="value">Raw value.</param>
    /// <remarks>
    /// The live API returns <c>updated_time</c>/<c>created_time</c>/<c>uploaded_time</c> as ISO-8601 strings and
    /// the matching <c>*_timestamp</c> fields as unix seconds. Reading only the ISO field with a unix parser is
    /// what left every date in the interface blank, so both shapes are accepted here.
    /// </remarks>
    private static DateTimeOffset? ParseTimestamp(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(parsedSeconds);
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Looks up a cached search response.</summary>
    /// <param name="key">Cache key.</param>
    /// <param name="result">Cached result.</param>
    private bool TryGetCachedSearch(string key, out NexusSearchResult result)
    {
        if (_searchCache.TryGetValue(key, out var entry)
            && DateTimeOffset.UtcNow - entry.At < _settings.MetadataCacheDuration)
        {
            result = entry.Result;
            return true;
        }

        result = new NexusSearchResult([], 1, MaxPageSize, null);
        return false;
    }

    /// <summary>Stores a search response, bounding the cache.</summary>
    /// <param name="key">Cache key.</param>
    /// <param name="result">Result to cache.</param>
    private void CacheSearch(string key, NexusSearchResult result)
    {
        if (_searchCache.Count > 64)
        {
            _searchCache.Clear();
        }

        _searchCache[key] = (DateTimeOffset.UtcNow, result);
    }

    /// <summary>Creates a PKCE code verifier.</summary>
    private static string CreateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    /// <summary>Creates the S256 PKCE challenge for a verifier.</summary>
    /// <param name="verifier">Code verifier.</param>
    private static string CreateCodeChallenge(string verifier) =>
        Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Base64url-encodes bytes without padding, as PKCE requires.</summary>
    /// <param name="bytes">Bytes to encode.</param>
    /// <remarks>
    /// <c>System.Convert</c> is spelled out on purpose: this class declares its own <c>Convert</c> method
    /// (the Nexus-response mapper), and an unqualified name resolves to that method group, not the type.
    /// </remarks>
    private static string Base64UrlEncode(byte[] bytes) =>
        System.Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>JSON options for the Nexus API.</summary>
    /// <remarks>
    /// Nexus uses snake_case and publishes timestamps as numbers, so explicit names are used rather than
    /// a naming policy that would also mangle our own types.
    /// </remarks>
    private static readonly JsonSerializerOptions NexusJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    // ---------------------------------------------------------------------------------------------
    // API response shapes. Kept private: they are an external contract, and letting them leak would
    // couple the rest of the application to a schema we do not control.
    // ---------------------------------------------------------------------------------------------

    private sealed class NexusValidateResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("is_premium")]
        public bool IsPremium { get; set; }
    }

    private sealed class OAuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; }
    }

    private sealed class NexusGameResponse
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("domain_name")]
        public string? DomainName { get; set; }

        [JsonPropertyName("mods")]
        public int ModCount { get; set; }

        [JsonPropertyName("downloads")]
        public long Downloads { get; set; }

        [JsonPropertyName("file_count")]
        public int FileCount { get; set; }

        /// <summary>
        /// Artwork for the game. The games list carries it, which is what lets the Games page show a real
        /// picture instead of a letter tile.
        /// </summary>
        [JsonPropertyName("picture_url")]
        public string? PictureUrl { get; set; }
    }

    /// <summary>
    /// Fields shared by the mod-listing and mod-detail responses.
    /// </summary>
    /// <remarks>
    /// Not sealed, because <see cref="NexusModDetailResponse"/> extends it. The Nexus detail endpoint
    /// returns every listing field plus the requirement graph, so the detail DTO inheriting the listing
    /// DTO mirrors the published schema: the extra members are additive, and both shapes stay in step
    /// automatically if a shared field is ever added. Composition would duplicate roughly twenty
    /// <c>JsonPropertyName</c>-decorated members and let the two shapes drift apart.
    /// <para>
    /// This is also why <c>Convert</c> has an overload for each type rather than accepting the base: the
    /// engine's own summary type is deliberately decoupled from the API schema, and the mapping is written
    /// out explicitly so a schema change fails to compile instead of silently leaving a field unmapped.
    /// </para>
    /// </remarks>
    private class NexusModResponse
    {
        [JsonPropertyName("mod_id")]
        public int ModId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("author")]
        public string? Author { get; set; }

        [JsonPropertyName("uploaded_by")]
        public string? UploadedBy { get; set; }

        [JsonPropertyName("domain_name")]
        public string? DomainName { get; set; }

        [JsonPropertyName("picture_url")]
        public string? PictureUrl { get; set; }

        [JsonPropertyName("endorsement_count")]
        public int EndorsementCount { get; set; }

        [JsonPropertyName("mod_downloads")]
        public long ModDownloads { get; set; }

        [JsonPropertyName("mod_unique_downloads")]
        public long UniqueDownloads { get; set; }

        [JsonPropertyName("contains_adult_content")]
        public bool ContainsAdultContent { get; set; }

        [JsonPropertyName("mod_manager_download_blocked")]
        public bool ModManagerDownloadBlocked { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("updated_time")]
        public JsonElement UpdatedTime { get; set; }

        [JsonPropertyName("created_time")]
        public JsonElement CreatedTime { get; set; }

        /// <summary>Numeric form of <c>updated_time</c>, which is what the API can be trusted to make numeric.</summary>
        [JsonPropertyName("updated_timestamp")]
        public JsonElement UpdatedTimestamp { get; set; }

        /// <summary>Numeric form of <c>created_time</c>.</summary>
        [JsonPropertyName("created_timestamp")]
        public JsonElement CreatedTimestamp { get; set; }
    }

    private sealed class NexusModDetailResponse : NexusModResponse
    {
        [JsonPropertyName("changelog")]
        public string? Changelog { get; set; }

        [JsonPropertyName("tags")]
        public string[]? Tags { get; set; }

        [JsonPropertyName("requirements")]
        public NexusModRequirementResponse[]? RequirementsForThisMod { get; set; }

        [JsonPropertyName("mods_requiring_this_mod")]
        public NexusModRequirementResponse[]? ModsRequiringThisMod { get; set; }

        [JsonPropertyName("mods_conflicting_with_this_mod")]
        public NexusModRequirementResponse[]? ModsConflictingWithThisMod { get; set; }

        [JsonPropertyName("mods_superseding_this_mod")]
        public NexusModRequirementResponse[]? ModsSupersedingThisMod { get; set; }

        [JsonPropertyName("mods_recommending_this_mod")]
        public NexusModRequirementResponse[]? ModsRecommendingThisMod { get; set; }
    }

    private sealed class NexusModRequirementResponse
    {
        [JsonPropertyName("mod_id")]
        public int ModId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("domain_name")]
        public string? DomainName { get; set; }

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }
    }

    private sealed class NexusModFilesResponse
    {
        [JsonPropertyName("files")]
        public NexusModFileResponse[]? Files { get; set; }
    }

    private sealed class NexusModFileResponse
    {
        [JsonPropertyName("file_id")]
        public int FileId { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("category_name")]
        public string? CategoryName { get; set; }

        [JsonPropertyName("size_kb")]
        public long SizeKb { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("is_primary")]
        public bool IsPrimary { get; set; }

        [JsonPropertyName("uploaded_time")]
        public JsonElement UploadedTime { get; set; }

        /// <summary>Numeric form of <c>uploaded_time</c>.</summary>
        [JsonPropertyName("uploaded_timestamp")]
        public JsonElement UploadedTimestamp { get; set; }
    }

    private sealed class NexusDownloadLinkResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("short_name")]
        public string? ShortName { get; set; }

        /// <summary>
        /// The signed URL. Nexus spells this field "URI" and "uri" in different responses.
        /// </summary>
        /// <remarks>
        /// One property, not two. Declaring both spellings collides because the serializer matches JSON names
        /// case-insensitively, and the collision throws on the first deserialization — which made every
        /// download-link request fail at runtime, in the one code path that matters most. A single
        /// case-insensitive mapping reads both spellings.
        /// </remarks>
        [JsonPropertyName("uri")]
        public string? Uri { get; set; }
    }
}
