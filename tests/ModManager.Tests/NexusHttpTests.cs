using System.Net;
using System.Net.Http;
using System.Text;
using ModManager.Core;
using ModManager.Core.Services;
using ModManager.App.ViewModels;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// The real <see cref="NexusProvider"/> against scripted HTTP responses.
/// </summary>
/// <remarks>
/// <para>
/// The fake provider proves the page logic; it cannot prove that the client classifies a real 401, honours a
/// <c>Retry-After</c>, survives a malformed body, or keeps the key out of the URL and out of its messages.
/// That is what these tests do, by driving the production code through an <see cref="HttpMessageHandler"/>.
/// </para>
/// <para>
/// Nothing here touches the network: every response is scripted, which is the only way to exercise 429 and 5xx
/// without hammering the live API.
/// </para>
/// </remarks>
public sealed class NexusHttpTests
{
    /// <summary>A: with no credential the call fails as "not configured" without any HTTP request.</summary>
    [Fact]
    public async Task NoApiKeyFailsWithoutARequest()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("{}"));
        var provider = CreateProvider(handler, apiKey: null);

        var result = await provider.GetGamesAsync();

        Assert.False(result.Succeeded);
        Assert.False(provider.IsAuthenticated);
        Assert.Equal(NexusFailureKind.MissingCredential, provider.LastError!.Kind);
        Assert.Empty(handler.Uris);
        Assert.Equal(NexusUiState.MissingKey, NexusUi.Classify(provider.LastError, result.Error));
    }

    /// <summary>B/E: a rejected credential is reported as such, with the status, and never as "no network".</summary>
    [Fact]
    public async Task InvalidKeyIsReportedAsUnauthorized()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.Unauthorized));
        var provider = CreateProvider(handler, "bad-key");

        var result = await provider.GetGamesAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(NexusFailureKind.Unauthorized, provider.LastError!.Kind);
        Assert.Equal(401, provider.LastError.StatusCode);
        Assert.True(provider.LastError.IsAuthFailure);
        Assert.Equal(NexusUiState.Unauthorized, NexusUi.Classify(provider.LastError, result.Error));
        Assert.DoesNotContain("bad-key", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>F: a refusal is classified as forbidden and points at the website, not at Settings.</summary>
    [Fact]
    public async Task ForbiddenIsReportedAsForbiddenAndNeedsTheNexusPage()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.Forbidden));
        var provider = CreateProvider(handler, "key");

        var result = await provider.GetDownloadLinkAsync("palworld", 1, 2);

        Assert.False(result.Succeeded);
        Assert.Equal(NexusFailureKind.Forbidden, provider.LastError!.Kind);
        Assert.True(provider.LastError.IsPremiumRequired);
        Assert.True(NexusUi.NeedsNexusPage(provider.LastError, result.Error));
        Assert.False(NexusUi.NeedsSettings(provider.LastError, result.Error));
    }

    /// <summary>G: a missing mod is reported as not found.</summary>
    [Fact]
    public async Task NotFoundIsReportedAsNotFound()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.NotFound));
        var provider = CreateProvider(handler, "key");

        var result = await provider.GetModDetailAsync("palworld", 999999);

        Assert.False(result.Succeeded);
        Assert.Equal(NexusFailureKind.NotFound, provider.LastError!.Kind);
        Assert.Equal(404, provider.LastError.StatusCode);
        Assert.Equal(NexusUiState.NotFound, NexusUi.Classify(provider.LastError, result.Error));
    }

    /// <summary>
    /// C: a timeout is reported as a timeout, and the message says so rather than blaming the network.
    /// </summary>
    [Fact]
    public async Task TimeoutIsReportedAsTimeout()
    {
        var handler = new StubHttpHandler(_ => throw new TaskCanceledException("timed out", new TimeoutException()));
        var provider = CreateProvider(handler, "key");

        var result = await provider.SearchModsAsync(new NexusSearchQuery { GameDomain = "palworld" });

        Assert.False(result.Succeeded);
        Assert.Equal(NexusFailureKind.Timeout, provider.LastError!.Kind);
        Assert.Equal(NexusUiState.Timeout, NexusUi.Classify(provider.LastError, result.Error));
    }

    /// <summary>D: a connection failure is reported as a network problem.</summary>
    [Fact]
    public async Task ConnectionFailureIsReportedAsNetwork()
    {
        var handler = new StubHttpHandler(_ => throw new HttpRequestException("no such host is known"));
        var provider = CreateProvider(handler, "key");

        var result = await provider.GetGamesAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(NexusFailureKind.Network, provider.LastError!.Kind);
        Assert.Equal(UiStateNetwork(), NexusUi.Classify(provider.LastError, result.Error));
    }

    /// <summary>
    /// H: a 429 carries the server's requested wait, is not retried into a storm, and is not slept on.
    /// </summary>
    [Fact]
    public async Task RateLimitCarriesTheServersWaitAndDoesNotRetry()
    {
        var handler = new StubHttpHandler(_ =>
        {
            var response = StubHttpHandler.Status(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return response;
        });

        var provider = CreateProvider(handler, "key");
        var started = DateTimeOffset.UtcNow;

        var result = await provider.SearchModsAsync(new NexusSearchQuery { GameDomain = "palworld" });

        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.False(result.Succeeded);
        Assert.Equal(NexusFailureKind.RateLimited, provider.LastError!.Kind);
        Assert.True(provider.LastError.IsRateLimited);
        Assert.Equal(60d, provider.LastError.RetryAfter!.Value.TotalSeconds, 1);
        Assert.Contains("等待", NexusUi.Describe(provider.LastError, result.Error), StringComparison.Ordinal);

        // One request, and no 60-second freeze before the failure is reported.
        // One legacy request — the GraphQL attempt was refused — and no 60-second freeze before failing.
        Assert.Single(handler.Uris, candidate => candidate.Contains("/v1/", StringComparison.Ordinal));
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"限流处理不应阻塞：实际 {elapsed.TotalSeconds:0.0}s");
    }

    /// <summary>I: a server error is classified separately from a transport failure.</summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ServerErrorsAreReportedAsServerSide(HttpStatusCode status)
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Status(status));
        var provider = CreateProvider(handler, "key");

        var result = await provider.GetGamesAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(NexusFailureKind.ServerError, provider.LastError!.Kind);
        Assert.Equal((int)status, provider.LastError.StatusCode);
    }

    /// <summary>J: an empty search result is a valid answer, not a failure.</summary>
    [Fact]
    public async Task EmptySearchResultIsSuccessWithNoItems()
    {
        var handler = new StubHttpHandler(request => StubHttpHandler.LegacyOnly(request, "[]"));
        var provider = CreateProvider(handler, "key");

        var result = await provider.SearchModsAsync(new NexusSearchQuery { GameDomain = "palworld" });

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Items);
        Assert.Null(provider.LastError);
    }

    /// <summary>J/K: an empty detail payload is "no data", and malformed JSON is unreadable — not "no network".</summary>
    [Fact]
    public async Task EmptyOrMalformedBodiesAreReportedAsUnreadable()
    {
        var emptyArray = CreateProvider(new StubHttpHandler(_ => StubHttpHandler.Json("[]")), "key");
        var emptyResult = await emptyArray.GetModDetailAsync("palworld", 5);
        Assert.False(emptyResult.Succeeded);
        Assert.Equal(NexusFailureKind.Malformed, emptyArray.LastError!.Kind);
        Assert.Equal(NexusUiState.Malformed, NexusUi.Classify(emptyArray.LastError, emptyResult.Error));

        var brokenJson = CreateProvider(new StubHttpHandler(_ => StubHttpHandler.Json("{ not json")), "key");
        var brokenResult = await brokenJson.SearchModsAsync(new NexusSearchQuery { GameDomain = "palworld" });
        Assert.False(brokenResult.Succeeded);
        Assert.Equal(NexusFailureKind.Malformed, brokenJson.LastError!.Kind);
    }

    /// <summary>K: a file entry without an id is dropped instead of producing an unusable download target.</summary>
    [Fact]
    public async Task FileWithoutAnIdIsDropped()
    {
        const string body = """
            { "files": [ { "name": "no id here" }, { "file_id": 42, "name": "real.zip", "version": "1.0", "size_kb": 10, "is_primary": true, "uploaded_time": 1700000000 } ] }
            """;

        var provider = CreateProvider(new StubHttpHandler(request => StubHttpHandler.LegacyOnly(request, body)), "key");
        var result = await provider.GetModFilesAsync("palworld", 7);

        Assert.True(result.Succeeded);
        var file = Assert.Single(result.Value!);
        Assert.Equal(42, file.FileId);
        Assert.True(file.IsPrimary);
        Assert.Equal(10 * 1024L, file.SizeBytes);
    }

    /// <summary>K: a file list without the expected property is a shape problem, not "no files published".</summary>
    [Fact]
    public async Task FileListWithoutTheFilesPropertyIsUnreadable()
    {
        var provider = CreateProvider(new StubHttpHandler(_ => StubHttpHandler.Json("{}")), "key");

        var result = await provider.GetModFilesAsync("palworld", 7);

        Assert.False(result.Succeeded);
        Assert.Equal(NexusFailureKind.Malformed, provider.LastError!.Kind);
    }

    /// <summary>M: a refused download link is a failure with a reason, never a silent success.</summary>
    [Fact]
    public async Task MissingDownloadLinkIsAFailure()
    {
        var provider = CreateProvider(new StubHttpHandler(request => StubHttpHandler.LegacyOnly(request, "[]")), "key");

        var result = await provider.GetDownloadLinkAsync("palworld", 7, 8);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Contains("premium", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// N: a real-shaped requirement list reaches the dependency resolver, including prose it cannot parse.
    /// </summary>
    [Fact]
    public async Task RealShapedRequirementsReachTheDependencyResolver()
    {
        const string body = """
            {
              "mod_id": 7,
              "name": "Example Mod",
              "summary": "An example.",
              "description": "Text",
              "version": "2.1",
              "author": "Someone",
              "uploaded_by": "Someone",
              "picture_url": "https://staticdelivery.nexusmods.com/mods/7/images/7-1.jpg",
              "mod_downloads": 1234,
              "endorsement_count": 12,
              "updated_time": 1700000000,
              "created_time": 1600000000,
              "requirements": [ { "mod_id": 99, "name": "Required Framework", "notes": "Load it first" } ],
              "mods_recommending_this_mod": [ { "mod_id": 100, "name": "see notes, needs patch" } ]
            }
            """;

        var provider = CreateProvider(new StubHttpHandler(request => StubHttpHandler.LegacyOnly(request, body)), "key");
        var detail = await provider.GetModDetailAsync("palworld", 7);

        Assert.True(detail.Succeeded);
        Assert.Equal("Example Mod", detail.Value!.Summary.Name);
        Assert.Equal("2.1", detail.Value.Summary.Version);
        Assert.Equal(1234, detail.Value.Summary.DownloadCount);
        Assert.Equal(7, detail.Value.Summary.ModId);
        Assert.Equal("palworld", detail.Value.Summary.GameDomain);

        // A one-element array body is the other shape the API uses; this response was an object and parsed.
        var requirement = Assert.Single(detail.Value.Requirements);
        Assert.Equal(99, requirement.ModId);
        Assert.Equal("Required Framework", requirement.Name);

        var report = new DependencyResolver().ResolveNexusReferences(
            detail.Value.Summary.Name,
            [.. detail.Value.Requirements, .. detail.Value.Recommended],
            []);

        Assert.Equal(2, report.Roots.Count);
        Assert.Equal(DependencyNodeState.Missing, report.Roots[0].State);
        Assert.Equal(99, report.Roots[0].NexusModId);

        // Prose is reported as unsupported rather than silently treated as a resolvable dependency.
        var prose = new DependencyResolver().ResolveNexusReferences("Example Mod", detail.Value.Recommended, []);
        Assert.Equal(DependencyNodeState.Missing, prose.Roots[0].State);
    }

    /// <summary>A one-element array is accepted for the detail endpoint as well.</summary>
    [Fact]
    public async Task DetailAcceptsTheArrayShape()
    {
        const string body = """
            [ { "mod_id": 11, "name": "Wrapped Mod", "summary": "s", "version": "1.0" } ]
            """;

        var provider = CreateProvider(new StubHttpHandler(request => StubHttpHandler.LegacyOnly(request, body)), "key");
        var detail = await provider.GetModDetailAsync("palworld", 11);

        Assert.True(detail.Succeeded);
        Assert.Equal("Wrapped Mod", detail.Value!.Summary.Name);
    }

    /// <summary>The API key travels in a header, never in the URL, and never appears in an error.</summary>
    [Fact]
    public async Task KeyIsSentInAHeaderAndNeverLeaksIntoUrlsOrMessages()
    {
        const string secret = "super-secret-key-value";
        var handler = new StubHttpHandler(request =>
        {
            Assert.True(request.Headers.TryGetValues("apikey", out var values));
            Assert.Equal(secret, values!.Single());
            return StubHttpHandler.Status(HttpStatusCode.Forbidden);
        });

        var provider = CreateProvider(handler, secret);
        var result = await provider.GetGamesAsync();

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(secret, handler.Uris[0], StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, provider.LastError!.Message, StringComparison.Ordinal);
    }

    /// <summary>The client identifies itself to Nexus with a real application name.</summary>
    [Fact]
    public async Task RequestsCarryAnApplicationIdentity()
    {
        var handler = new StubHttpHandler(request => StubHttpHandler.LegacyOnly(request, "[]"));
        var provider = CreateProvider(handler, "key");

        await provider.GetGamesAsync();

        Assert.Equal(NexusProvider.Identity, handler.UserAgents[0]);
        Assert.Contains("ModManager", handler.UserAgents[0]!, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", handler.UserAgents[0]!, StringComparison.Ordinal);
    }

    /// <summary>The UI mapping never claims success for a failure.</summary>
    [Fact]
    public void UiMappingNeverReportsSuccessForAFailure()
    {
        var cases = new (NexusError Error, NexusUiState Expected)[]
        {
            (NexusError.Missing("m"), NexusUiState.MissingKey),
            (NexusError.Auth("m"), NexusUiState.Unauthorized),
            (NexusError.Premium("m"), NexusUiState.Forbidden),
            (NexusError.NotFound("m"), NexusUiState.NotFound),
            (NexusError.RateLimited("m", TimeSpan.FromSeconds(30)), NexusUiState.RateLimited),
            (NexusError.Server("m", 503), NexusUiState.ServerError),
            (NexusError.Network("m"), NexusUiState.Network),
            (NexusError.Timeout("m"), NexusUiState.Timeout),
            (NexusError.Malformed("m"), NexusUiState.Malformed),
        };

        foreach (var (error, expected) in cases)
        {
            Assert.Equal(expected, NexusUi.Classify(error, null));
            Assert.NotEqual(NexusUiState.Ok, NexusUi.Classify(error, null));

            var text = NexusUi.Describe(error);

            // No English plumbing leaks into the interface, and no state is described as an update check.
            Assert.DoesNotContain("Nexus Mods returned", text, StringComparison.Ordinal);
            Assert.DoesNotContain("已是最新", text, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }

    /// <summary>
    /// The API's two timestamp shapes are both parsed, in the form the live API actually returns.
    /// </summary>
    /// <remarks>
    /// Captured from a live response: <c>updated_time</c> is an ISO-8601 string and <c>updated_timestamp</c> is
    /// unix seconds. Reading only the ISO field with a unix parser left every date blank in the interface.
    /// </remarks>
    [Fact]
    public async Task BothRealTimestampShapesAreParsed()
    {
        const string body = """
            [
              { "mod_id": 1, "name": "Iso Only", "updated_time": "2026-08-27T16:52:47.000+00:00", "created_time": "2019-11-18T16:09:54.000+00:00" },
              { "mod_id": 2, "name": "Numeric Only", "updated_timestamp": 1700000000, "created_timestamp": 1600000000 },
              { "mod_id": 3, "name": "Both", "updated_time": "2026-08-27T16:52:47.000+00:00", "updated_timestamp": 1700000000 },
              { "mod_id": 4, "name": "Neither" }
            ]
            """;

        var provider = CreateProvider(new StubHttpHandler(request => StubHttpHandler.LegacyOnly(request, body)), "key");
        var result = await provider.SearchModsAsync(new NexusSearchQuery { GameDomain = "skyrimspecialedition" });

        Assert.True(result.Succeeded);
        var items = result.Value!.Items;

        Assert.Equal(new DateTimeOffset(2026, 8, 27, 16, 52, 47, TimeSpan.Zero), items[0].UpdatedAt);
        Assert.Equal(new DateTimeOffset(2019, 11, 18, 16, 9, 54, TimeSpan.Zero), items[0].CreatedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), items[1].UpdatedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), items[2].UpdatedAt);
        Assert.Null(items[3].UpdatedAt);
    }

    /// <summary>File timestamps use the same two shapes.</summary>
    [Fact]
    public async Task FileTimestampsUseBothShapes()
    {
        const string body = """
            { "files": [
              { "file_id": 1, "name": "iso.zip", "uploaded_time": "2019-11-15T17:03:44.000+00:00" },
              { "file_id": 2, "name": "unix.zip", "uploaded_timestamp": 1573837424 }
            ] }
            """;

        var provider = CreateProvider(new StubHttpHandler(request => StubHttpHandler.LegacyOnly(request, body)), "key");
        var result = await provider.GetModFilesAsync("skyrimspecialedition", 266);

        Assert.True(result.Succeeded);
        var files = result.Value!;
        Assert.Equal(2, files.Count);
        Assert.All(files, file => Assert.Equal(new DateTimeOffset(2019, 11, 15, 17, 3, 44, TimeSpan.Zero), file.UploadedAt));
    }

    /// <summary>
    /// A keyword is filtered locally against a listing endpoint, because the v1 API has no search endpoint.
    /// </summary>
    /// <remarks>
    /// Verified against the live API: <c>mods/search.json</c> answers 422 and the global search endpoint 404.
    /// The important part of this test is that no request is ever sent to a non-existent search route.
    /// </remarks>
    [Fact]
    public async Task KeywordIsMatchedLocallyOnAListingEndpoint()
    {
        const string body = """
            [
              { "mod_id": 1, "name": "Skyrim Script Extender (SKSE64)", "summary": "Scripting", "author": "Ian Patterson", "contains_adult_content": false },
              { "mod_id": 2, "name": "Some Armour Replacer", "summary": "Armour", "author": "Someone", "contains_adult_content": false },
              { "mod_id": 3, "name": "Skyrim Floral Overhaul", "summary": "SKSE plugin and flowers", "author": "Other", "contains_adult_content": false }
            ]
            """;

        var handler = new StubHttpHandler(request => StubHttpHandler.LegacyOnly(request, body));
        var provider = CreateProvider(handler, "key");

        var result = await provider.SearchModsAsync(new NexusSearchQuery
        {
            GameDomain = "skyrimspecialedition",
            SearchText = "skse",
        });

        Assert.True(result.Succeeded);
        Assert.True(result.Value!.KeywordFilteredLocally);

        // Name match and summary match both count; the unrelated mod is excluded.
        Assert.Equal(2, result.Value.Items.Count);
        Assert.Contains(result.Value.Items, item => item.ModId == 1);
        Assert.Contains(result.Value.Items, item => item.ModId == 3);

        var url = Assert.Single(handler.Uris, candidate => candidate.Contains("/v1/", StringComparison.Ordinal));
        Assert.Contains("trending.json", url, StringComparison.Ordinal);
        Assert.DoesNotContain("search.json", url, StringComparison.Ordinal);
    }

    /// <summary>Adult content is excluded locally unless the user opted in.</summary>
    [Fact]
    public async Task AdultContentIsFilteredLocally()
    {
        const string body = """
            [
              { "mod_id": 1, "name": "Safe Mod", "contains_adult_content": false },
              { "mod_id": 2, "name": "Adult Mod", "contains_adult_content": true }
            ]
            """;

        var handler = new StubHttpHandler(request => StubHttpHandler.LegacyOnly(request, body));
        var provider = CreateProvider(handler, "key");

        var hidden = await provider.SearchModsAsync(new NexusSearchQuery { GameDomain = "skyrimspecialedition" });
        Assert.True(hidden.Succeeded);
        var hiddenItem = Assert.Single(hidden.Value!.Items);
        Assert.Equal(1, hiddenItem.ModId);

        var shown = await provider.SearchModsAsync(new NexusSearchQuery
        {
            GameDomain = "skyrimspecialedition",
            IncludeAdult = true,
        });

        Assert.True(shown.Succeeded);
        Assert.Equal(2, shown.Value!.Items.Count);
    }

    /// <summary>A 422 is reported as our own invalid request, not as a server fault.</summary>
    [Fact]
    public async Task UnprocessableEntityIsReportedAsAnInvalidRequest()
    {
        var provider = CreateProvider(
            new StubHttpHandler(_ => StubHttpHandler.Json("""{"message":"Invalid parameter 'id' value \"search\": Must be a number."}""", HttpStatusCode.UnprocessableEntity)),
            "key");

        var result = await provider.SearchModsAsync(new NexusSearchQuery { GameDomain = "skyrimspecialedition" });

        Assert.False(result.Succeeded);
        Assert.Equal(NexusFailureKind.BadRequest, provider.LastError!.Kind);
        Assert.Equal(422, provider.LastError.StatusCode);
        Assert.Equal(NexusUiState.BadRequest, NexusUi.Classify(provider.LastError, result.Error));

        // The server's own explanation is carried through, so the cause is visible instead of a bare code.
        Assert.Contains("Must be a number", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>Builds a provider over a scripted handler.</summary>
    /// <param name="handler">Scripted transport.</param>
    /// <param name="apiKey">Key to store, or null for none.</param>
    private static NexusProvider CreateProvider(StubHttpHandler handler, string? apiKey) =>
        new(
            new FakeSecretStore(apiKey),
            new NexusSettings { MinimumRequestInterval = TimeSpan.Zero },
            logger: null,
            httpClient: new HttpClient(handler));

    /// <summary>The expected classification for a transport failure.</summary>
    private static NexusUiState UiStateNetwork() => NexusUiState.Network;
}

/// <summary>An HTTP transport that answers from a scripted function.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

    /// <summary>Creates the handler.</summary>
    /// <param name="responder">Scripted response or exception.</param>
    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    /// <summary>Request URIs, captured at send time so disposal cannot hide them.</summary>
    public List<string> Uris { get; } = [];

    /// <summary>User agent sent with each request.</summary>
    public List<string?> UserAgents { get; } = [];

    /// <summary>Authorization headers, captured at send time. Only populated for requests that set one.</summary>
    public List<string?> Authorizations { get; } = [];

    /// <summary>Request bodies, captured at send time.</summary>
    public List<string> Bodies { get; } = [];

    /// <summary>Builds a JSON response.</summary>
    /// <param name="json">Body.</param>
    /// <param name="status">Status code, 200 by default.</param>
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    /// <summary>Builds a response with raw bytes and a content type.</summary>
    /// <param name="bytes">Body bytes.</param>
    /// <param name="mediaType">Content type.</param>
    public static HttpResponseMessage Bytes(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>
    /// Answers a v1 request normally and refuses the GraphQL endpoint.
    /// </summary>
    /// <param name="request">Incoming request.</param>
    /// <param name="json">Body to return for the legacy endpoint.</param>
    /// <remarks>
    /// Search now tries GraphQL first, so a test that means to exercise the legacy listing path has to make
    /// the modern endpoint unavailable — which is also the real fallback condition.
    /// </remarks>
    public static HttpResponseMessage LegacyOnly(HttpRequestMessage request, string json) =>
        request.RequestUri!.AbsoluteUri.Contains("/v2/graphql", StringComparison.Ordinal)
            ? Status(HttpStatusCode.ServiceUnavailable)
            : Json(json);

    /// <summary>Builds a response with a status and no body.</summary>
    /// <param name="status">Status code.</param>
    public static HttpResponseMessage Status(HttpStatusCode status) => new(status)
    {
        Content = new StringContent(string.Empty, Encoding.UTF8, "application/json"),
    };

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Uris.Add(request.RequestUri?.ToString() ?? string.Empty);
        UserAgents.Add(request.Headers.UserAgent.ToString());
        Authorizations.Add(request.Headers.Authorization?.ToString());
        Bodies.Add(request.Content is null
            ? string.Empty
            : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());

        return Task.FromResult(_responder(request));
    }
}

/// <summary>An in-memory secret store, so tests never touch DPAPI or the user's real key.</summary>
internal sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>Creates the store with an optional Nexus key.</summary>
    /// <param name="apiKey">Nexus API key, or null.</param>
    /// <param name="translationKey">Translation API key, or null.</param>
    public FakeSecretStore(string? apiKey, string? translationKey = null)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _values[ISecretStore.NexusApiKey] = apiKey;
        }

        if (!string.IsNullOrWhiteSpace(translationKey))
        {
            _values[ISecretStore.TranslationApiKey] = translationKey;
        }
    }

    /// <inheritdoc />
    public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    /// <inheritdoc />
    public void Set(string name, string value) => _values[name] = value;

    /// <inheritdoc />
    public void Remove(string name) => _values.Remove(name);

    /// <inheritdoc />
    public bool Contains(string name) => _values.ContainsKey(name);
}
