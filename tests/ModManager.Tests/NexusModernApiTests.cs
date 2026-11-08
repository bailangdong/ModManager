using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// The GraphQL search path and the translation service, both driven through scripted HTTP.
/// </summary>
/// <remarks>
/// v1 cannot page or sort (measured against the live API: offset is ignored, downloads.json does not exist),
/// so the search now runs on v2. These tests pin that contract down, including the fallback, without touching
/// the network.
/// </remarks>
public sealed class NexusModernApiTests
{
    /// <summary>A GraphQL page maps to the shared summary model with real totals.</summary>
    [Fact]
    public async Task GraphQlSearchMapsResultsAndTotals()
    {
        const string body = """
            { "data": { "mods": { "totalCount": 21418, "nodes": [
              { "modId": 107, "name": "Cyber Engine Tweaks", "summary": "Scripting", "version": "1.37.1",
                "author": "yamashi", "downloads": 18350578, "endorsements": 237548,
                "createdAt": "2020-12-12T22:39:34Z", "updatedAt": "2025-09-30T00:55:08Z",
                "pictureUrl": "https://staticdelivery.nexusmods.com/mods/3333/images/107/107.png",
                "adultContent": false } ] } } }
            """;

        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(body));
        var provider = CreateProvider(handler);

        var result = await provider.SearchModsAsync(new NexusSearchQuery
        {
            GameDomain = "cyberpunk2077",
            Page = 1,
            PageSize = 20,
        });

        Assert.True(result.Succeeded);
        Assert.Contains("api.nexusmods.com/v2/graphql", handler.Uris[0], StringComparison.Ordinal);

        var page = result.Value!;
        Assert.Equal(21418, page.TotalCount);
        Assert.True(page.HasNextPage);

        var item = Assert.Single(page.Items);
        Assert.Equal(107, item.ModId);
        Assert.Equal("Cyber Engine Tweaks", item.Name);
        Assert.Equal("1.37.1", item.Version);
        Assert.Equal("yamashi", item.Author);
        Assert.Equal(18350578, item.DownloadCount);
        Assert.Equal(new DateTimeOffset(2025, 9, 30, 0, 55, 8, TimeSpan.Zero), item.UpdatedAt);
        Assert.False(item.ContainsAdultContent);
        Assert.Equal("cyberpunk2077", item.GameDomain);
        Assert.Equal("https://www.nexusmods.com/cyberpunk2077/mods/107", item.PageUrl);
        Assert.NotNull(item.PictureUrl);
    }

    /// <summary>The requested sort and page reach the query as variables.</summary>
    [Fact]
    public async Task GraphQlSearchSendsSortAndOffset()
    {
        const string body = """{ "data": { "mods": { "totalCount": 100, "nodes": [] } } } """;

        string? sent = null;
        var handler = new StubHttpHandler(request =>
        {
            sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpHandler.Json(body);
        });

        var provider = CreateProvider(handler);
        await provider.SearchModsAsync(new NexusSearchQuery
        {
            GameDomain = "cyberpunk2077",
            SortField = "downloads",
            SortDirection = "desc",
            Page = 3,
            PageSize = 20,
            SearchText = "tweak",
        });

        Assert.NotNull(sent);
        using var document = JsonDocument.Parse(sent!);
        var variables = document.RootElement.GetProperty("variables");

        Assert.Equal(40, variables.GetProperty("o").GetInt32());
        Assert.Equal(20, variables.GetProperty("c").GetInt32());
        Assert.Equal("downloads", variables.GetProperty("s")[0].EnumerateObject().Single().Name);

        var filter = variables.GetProperty("f");
        Assert.Equal("cyberpunk2077", filter.GetProperty("gameDomainName")[0].GetProperty("value").GetString());
        Assert.Equal("tweak", filter.GetProperty("nameStemmed")[0].GetProperty("value").GetString());
    }

    /// <summary>
    /// A GraphQL error makes the search fall back, and never makes it report a successful empty search.
    /// </summary>
    [Fact]
    public async Task GraphQlErrorsFallBackWithoutClaimingSuccess()
    {
        const string errors = """{ "errors": [ { "message": "Field 'mods' doesn't exist" } ] } """;
        const string listing = """[ { "mod_id": 1, "name": "Fallback Mod" } ]""";

        var handler = new StubHttpHandler(request => request.RequestUri!.AbsoluteUri.Contains("/v2/graphql", StringComparison.Ordinal)
            ? StubHttpHandler.Json(errors)
            : StubHttpHandler.Json(listing));

        var provider = CreateProvider(handler);
        var result = await provider.SearchModsAsync(new NexusSearchQuery
        {
            GameDomain = "cyberpunk2077",
            SearchText = "fallback",
        });

        // The failure of the modern endpoint is visible in the log and in the result's provenance, and the
        // caller still gets the legacy answer rather than an error pretending nothing was fetched.
        Assert.True(result.Succeeded);
        Assert.True(result.Value!.KeywordFilteredLocally);
        Assert.Equal("Fallback Mod", Assert.Single(result.Value.Items).Name);
    }

    /// <summary>
    /// When GraphQL is unavailable the old listing endpoint still answers, flagged as locally filtered.
    /// </summary>
    [Fact]
    public async Task GraphQlFailureFallsBackToTheLegacyListing()
    {
        const string listing = """
            [ { "mod_id": 1, "name": "Fallback Mod", "version": "1.0", "mod_downloads": 5 } ]
            """;

        var handler = new StubHttpHandler(request => request.RequestUri!.AbsoluteUri.Contains("/v2/graphql", StringComparison.Ordinal)
            ? StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable)
            : StubHttpHandler.Json(listing));

        var provider = CreateProvider(handler);
        var result = await provider.SearchModsAsync(new NexusSearchQuery
        {
            GameDomain = "cyberpunk2077",
            SearchText = "fallback",
        });

        Assert.True(result.Succeeded);
        Assert.Equal(2, handler.Uris.Count);
        Assert.Contains("trending.json", handler.Uris[1], StringComparison.Ordinal);
        Assert.True(result.Value!.KeywordFilteredLocally);
        Assert.Equal("Fallback Mod", Assert.Single(result.Value.Items).Name);
    }

    /// <summary>Without a key the translation service says what is missing instead of guessing.</summary>
    [Fact]
    public async Task TranslationWithoutAKeyIsReported()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));

        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("{}"));
        var service = new TranslationService(
            new FakeSecretStore(null),
            paths,
            new TranslationSettings(),
            logger: null,
            httpClient: new HttpClient(handler));

        var result = await service.TranslateAsync("cyberpunk2077-107", "Cyber Engine Tweaks", "s", "d");

        Assert.False(result.Succeeded);
        Assert.Contains("设置", result.Error!, StringComparison.Ordinal);
        Assert.Empty(handler.Uris);
    }

    /// <summary>A successful translation is parsed and then served from cache without another request.</summary>
    [Fact]
    public async Task TranslationIsParsedAndCached()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));

        const string answer = """
            { "choices": [ { "message": { "content": "{\"title\":\"赛博引擎调整\",\"summary\":\"脚本框架\",\"description\":\"为改模者提供的脚本框架。\"}" } } ] }
            """;

        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(answer));
        var service = new TranslationService(
            new FakeSecretStore(null, "translation-key"),
            paths,
            new TranslationSettings { Endpoint = "https://api.deepseek.com/chat/completions", Model = "deepseek-chat" },
            logger: null,
            httpClient: new HttpClient(handler));

        var first = await service.TranslateAsync("cyberpunk2077-107", "Cyber Engine Tweaks", "Scripting", "Framework");
        Assert.True(first.Succeeded);
        Assert.Equal("赛博引擎调整", first.Value!.Title);
        Assert.Equal("脚本框架", first.Value.Summary);
        Assert.False(first.Value.FromCache);

        // The key travels as a bearer header and never in the body.
        var sentBody = handler.Bodies[0];
        Assert.DoesNotContain("translation-key", sentBody, StringComparison.Ordinal);
        Assert.Contains("Bearer translation-key", handler.Authorizations[0] ?? string.Empty, StringComparison.Ordinal);

        var second = await service.TranslateAsync("cyberpunk2077-107", "Cyber Engine Tweaks", "Scripting", "Framework");
        Assert.True(second.Succeeded);
        Assert.True(second.Value!.FromCache);
        Assert.Single(handler.Uris);
    }

    /// <summary>An updated description is translated again rather than served stale.</summary>
    [Fact]
    public async Task TranslationCacheInvalidatesWhenTheSourceChanges()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));

        const string answer = """
            { "choices": [ { "message": { "content": "{\"title\":\"标题\",\"summary\":\"s\",\"description\":\"d\"}" } } ] }
            """;

        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(answer));
        var service = new TranslationService(
            new FakeSecretStore(null, "k"),
            paths,
            new TranslationSettings(),
            logger: null,
            httpClient: new HttpClient(handler));

        await service.TranslateAsync("cyberpunk2077-107", "Title", "s", "first version");
        await service.TranslateAsync("cyberpunk2077-107", "Title", "s", "second version");

        Assert.Equal(2, handler.Uris.Count);
    }

    /// <summary>A rejected key is reported as an authentication problem.</summary>
    [Fact]
    public async Task TranslationRejectionIsReported()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));

        var service = new TranslationService(
            new FakeSecretStore(null, "bad"),
            paths,
            new TranslationSettings(),
            logger: null,
            httpClient: new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.Unauthorized))));

        var result = await service.TranslateAsync("cyberpunk2077-107", "T", null, null);

        Assert.False(result.Succeeded);
        Assert.Contains("密钥", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>An answer that is not the expected JSON is a failure, not a silent empty translation.</summary>
    [Fact]
    public async Task TranslationWithUnparsableAnswerFails()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));

        const string answer = """{ "choices": [ { "message": { "content": "抱歉，我无法完成。" } } ] } """;
        var service = new TranslationService(
            new FakeSecretStore(null, "k"),
            paths,
            new TranslationSettings(),
            logger: null,
            httpClient: new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Json(answer))));

        var result = await service.TranslateAsync("cyberpunk2077-107", "T", null, null);

        Assert.False(result.Succeeded);
        Assert.Contains("无法解析", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>Builds a provider over a scripted handler.</summary>
    /// <param name="handler">Scripted transport.</param>
    private static NexusProvider CreateProvider(StubHttpHandler handler) =>
        new(
            new FakeSecretStore("key"),
            new NexusSettings { MinimumRequestInterval = TimeSpan.Zero },
            logger: null,
            httpClient: new HttpClient(handler));
}
