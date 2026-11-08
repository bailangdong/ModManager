using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Game artwork: the GraphQL templates, the id substitution, and the shared image cache.
/// </summary>
/// <remarks>
/// Nexus does not publish a finished artwork URL. The API answers with templates containing
/// <c>{gameId}</c> plus a per-domain game id, and artwork only exists for games that have a Nexus domain. These
/// tests pin down the join, the schema preference and every failure path, using scripted HTTP only.
/// </remarks>
public sealed class NexusArtworkTests
{
    /// <summary>A: the template is filled in with the real game id.</summary>
    [Fact]
    public async Task ArtworkTemplateIsResolvedWithTheGameId()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(ArtworkResponse(
            "cyberpunk2077",
            gameId: 3333,
            schema: "V2",
            tileV2: "https://images.nexusmods.com/images/games/v2/{gameId}/tile.jpg")));

        var provider = CreateProvider(handler);
        var result = await provider.GetGameArtworkAsync(["cyberpunk2077"]);

        Assert.True(result.Succeeded);
        var artwork = Assert.Single(result.Value!);
        Assert.Equal(3333, artwork.GameId);
        Assert.Equal("V2", artwork.Schema);
        Assert.Equal("https://images.nexusmods.com/images/games/v2/3333/tile.jpg", artwork.TileUrl);
        Assert.Equal(artwork.TileUrl, artwork.CardUrl);
    }

    /// <summary>B: a missing template yields no artwork rather than an empty URL.</summary>
    [Fact]
    public async Task MissingTemplateYieldsNoArtwork()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("""
            { "data": { "gameArtwork": { "schemaV1": { "tile": "", "tileBlurred": "" }, "schemaV2": { "hero": "", "thumbnail": "", "tile": "" } },
              "g0": { "id": 3333, "domainName": "cyberpunk2077", "artworkSchema": "V2" } } }
            """));

        var provider = CreateProvider(handler);
        var result = await provider.GetGameArtworkAsync(["cyberpunk2077"]);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!);
    }

    /// <summary>C: a game on the older schema still gets its tile, and the card prefers tile over hero.</summary>
    [Fact]
    public async Task OlderSchemaStillResolvesAndCardPrefersTheTile()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(ArtworkResponse(
            "skyrimspecialedition",
            gameId: 1704,
            schema: "V1",
            tileV1: "https://images.nexusmods.com/images/games/4_3/tile_{gameId}.jpg",
            blurredV1: "https://images.nexusmods.com/images/games/4_3/b/tile_{gameId}.jpg",
            heroV2: "https://images.nexusmods.com/images/games/v2/{gameId}/hero.jpg")));

        var provider = CreateProvider(handler);
        var artwork = Assert.Single((await provider.GetGameArtworkAsync(["skyrimspecialedition"])).Value!);

        Assert.Equal("https://images.nexusmods.com/images/games/4_3/tile_1704.jpg", artwork.TileUrl);
        Assert.Equal(artwork.TileUrl, artwork.CardUrl);
    }

    /// <summary>H: an unknown domain is simply absent, never guessed.</summary>
    [Fact]
    public async Task UnknownDomainIsAbsent()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("""
            { "data": { "gameArtwork": { "schemaV1": { "tile": "https://x/{gameId}.jpg" }, "schemaV2": { } },
              "g0": null } }
            """));

        var provider = CreateProvider(handler);
        var result = await provider.GetGameArtworkAsync(["no-such-game"]);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!);
    }

    /// <summary>K: with no domains nothing is requested at all.</summary>
    [Fact]
    public async Task NoDomainsMeansNoRequest()
    {
        var handler = new StubHttpHandler(_ => StubHttpHandler.Json("{}"));
        var provider = CreateProvider(handler);

        var result = await provider.GetGameArtworkAsync([]);

        Assert.True(result.Succeeded);
        Assert.Empty(handler.Uris);
    }

    /// <summary>D: a WebP cover is downloaded once and decoded locally on later calls.</summary>
    [Fact]
    public async Task WebpArtworkIsCachedAndNotDownloadedTwice()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));

        var sample = NexusImageSamples.Webp();
        var requests = 0;
        var handler = new StubHttpHandler(_ =>
        {
            requests++;
            return sample is null
                ? StubHttpHandler.Bytes([1, 2, 3], "image/webp")
                : StubHttpHandler.Bytes(File.ReadAllBytes(sample), "image/webp");
        });

        using var cache = new ImageCacheService(paths, logger: null, httpClient: new HttpClient(handler));

        var first = await cache.GetAsync("https://images.nexusmods.com/games/v2/3333/tile.jpg");
        var second = await cache.GetAsync("https://images.nexusmods.com/games/v2/3333/tile.jpg");

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.True(File.Exists(first));
        Assert.Equal(1, requests);

        // The file lives under the application's single image cache, named by a hash rather than the URL.
        Assert.Contains("Cache", first!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Images", first!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>E: PNG and JPEG responses are accepted and stored with a matching extension.</summary>
    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    public async Task CommonImageFormatsAreCached(string mediaType, string extension)
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));

        using var cache = new ImageCacheService(
            paths,
            logger: null,
            httpClient: new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Bytes([137, 80, 78, 71], mediaType))));

        var path = await cache.GetAsync($"https://images.nexusmods.com/games/{extension}/tile{extension}");

        Assert.NotNull(path);
        Assert.EndsWith(extension, path!, StringComparison.Ordinal);
    }

    /// <summary>F: a failed download yields nothing so the page keeps its placeholder.</summary>
    [Fact]
    public async Task FailedDownloadYieldsNothing()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));

        using var cache = new ImageCacheService(
            paths,
            logger: null,
            httpClient: new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.NotFound))));

        Assert.Null(await cache.GetAsync("https://images.nexusmods.com/games/missing.jpg"));
    }

    /// <summary>F: a transport failure is a missing image, not an exception.</summary>
    [Fact]
    public async Task TransportFailureYieldsNothing()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));

        using var cache = new ImageCacheService(
            paths,
            logger: null,
            httpClient: new HttpClient(new StubHttpHandler(_ => throw new HttpRequestException("offline"))));

        Assert.Null(await cache.GetAsync("https://images.nexusmods.com/games/offline.jpg"));
    }

    /// <summary>G: a response that is not an image is refused rather than stored.</summary>
    [Fact]
    public async Task NonImageResponseIsRefused()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));

        using var cache = new ImageCacheService(
            paths,
            logger: null,
            httpClient: new HttpClient(new StubHttpHandler(_ => StubHttpHandler.Bytes(Encoding.UTF8.GetBytes("<html>"), "text/html"))));

        Assert.Null(await cache.GetAsync("https://images.nexusmods.com/games/page.html"));
    }

    /// <summary>I/J: the domain cache is reused, so a second page load makes no API request.</summary>
    [Fact]
    public async Task ArtworkMappingIsCachedPerDomain()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));

        var handler = new StubHttpHandler(_ => StubHttpHandler.Json(ArtworkResponse(
            "cyberpunk2077",
            gameId: 3333,
            schema: "V2",
            tileV2: "https://images.nexusmods.com/images/games/v2/{gameId}/tile.jpg")));

        var provider = CreateProvider(handler);
        var cache = new NexusGameImageCache(paths);

        var first = await cache.GetAsync(provider, ["cyberpunk2077"]);
        var second = await cache.GetAsync(provider, ["cyberpunk2077"]);

        Assert.Equal(3333, first["cyberpunk2077"].GameId);
        Assert.Equal(3333, second["cyberpunk2077"].GameId);
        Assert.Single(handler.Uris);
        Assert.True(File.Exists(cache.FilePath));
    }

    /// <summary>J: an unresolvable refresh keeps the previous mapping instead of emptying the interface.</summary>
    [Fact]
    public async Task FailedRefreshKeepsTheStoredMapping()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));

        var good = CreateProvider(new StubHttpHandler(_ => StubHttpHandler.Json(ArtworkResponse(
            "cyberpunk2077",
            gameId: 3333,
            schema: "V2",
            tileV2: "https://images.nexusmods.com/images/games/v2/{gameId}/tile.jpg"))));

        var cache = new NexusGameImageCache(paths);
        await cache.GetAsync(good, ["cyberpunk2077"]);

        // A second cache instance over the same files, with a provider that fails.
        var failing = CreateProvider(new StubHttpHandler(_ => StubHttpHandler.Status(HttpStatusCode.BadGateway)));
        var stored = await new NexusGameImageCache(paths).GetAsync(failing, ["cyberpunk2077"]);

        Assert.Equal(3333, stored["cyberpunk2077"].GameId);
    }

    /// <summary>Builds a GraphQL artwork response with one game and the given templates.</summary>
    /// <param name="domain">Nexus domain.</param>
    /// <param name="gameId">Numeric game id.</param>
    /// <param name="schema">Artwork schema reported by the API.</param>
    /// <param name="tileV1">V1 tile template.</param>
    /// <param name="blurredV1">V1 blurred tile template.</param>
    /// <param name="tileV2">V2 tile template.</param>
    /// <param name="heroV2">V2 hero template.</param>
    private static string ArtworkResponse(
        string domain,
        int gameId,
        string schema,
        string? tileV1 = null,
        string? blurredV1 = null,
        string? tileV2 = null,
        string? heroV2 = null) =>
        JsonSerializer.Serialize(new
        {
            data = new
            {
                gameArtwork = new
                {
                    schemaV1 = new { tile = tileV1 ?? string.Empty, tileBlurred = blurredV1 ?? string.Empty },
                    schemaV2 = new { hero = heroV2 ?? string.Empty, thumbnail = string.Empty, tile = tileV2 ?? string.Empty },
                },
                g0 = new { id = gameId, domainName = domain, artworkSchema = schema },
            },
        });

    /// <summary>Builds a provider over a scripted handler.</summary>
    /// <param name="handler">Scripted transport.</param>
    private static NexusProvider CreateProvider(StubHttpHandler handler) =>
        new(
            new FakeSecretStore("key"),
            new NexusSettings { MinimumRequestInterval = TimeSpan.Zero },
            logger: null,
            httpClient: new HttpClient(handler));
}
