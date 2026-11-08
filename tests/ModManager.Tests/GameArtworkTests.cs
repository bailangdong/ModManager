using System.IO;
using System.Net;
using System.Net.Http;
using ModManager.Core;
using ModManager.Core.Services;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Cover-art resolution, and how the image cache treats a definitive "there is no image here".
/// </summary>
/// <remarks>
/// The behaviour under test is the one that was wrong: the application could only ever ask Nexus, and only
/// for games whose definition declares a domain, so every generic-mode game rendered an empty tile. These
/// cases pin the precedence rule and — just as importantly — pin that an unresolvable game resolves to
/// <b>nothing</b> rather than to some other game's artwork.
/// </remarks>
public sealed class GameArtworkTests
{
    private const string NexusTile = "https://images.nexusmods.com/images/games/v2/3333/tile.jpg";

    /// <summary>A game with a Nexus mapping uses Nexus artwork, even when it also has an app id.</summary>
    [Fact]
    public void NexusArtworkWinsWhenTheGameHasADomain()
    {
        var artwork = new Dictionary<string, NexusGameArtwork>(StringComparer.OrdinalIgnoreCase)
        {
            // CardUrl is derived from TileUrl by the record, so supplying the tile is what a real
            // GraphQL answer amounts to.
            ["cyberpunk2077"] = new NexusGameArtwork("cyberpunk2077", 3333, "V2", NexusTile),
        };

        var candidate = GameArtworkResolver.Resolve(1091500, "cyberpunk2077", artwork);

        Assert.Equal(GameArtworkSource.Nexus, candidate.Source);
        Assert.Equal(NexusTile, candidate.Url);
    }

    /// <summary>A game with no Nexus mapping falls back to the Steam CDN, addressed by app id.</summary>
    [Fact]
    public void SteamIsUsedWhenThereIsNoNexusDomain()
    {
        var candidate = GameArtworkResolver.Resolve(813780, null, null);

        Assert.Equal(GameArtworkSource.Steam, candidate.Source);
        Assert.Equal(
            "https://cdn.cloudflare.steamstatic.com/steam/apps/813780/library_600x900.jpg",
            candidate.Url);
    }

    /// <summary>A domain with no cached artwork still falls back rather than giving up.</summary>
    [Fact]
    public void AnUnresolvedDomainFallsBackToSteam()
    {
        var candidate = GameArtworkResolver.Resolve(
            949230,
            "citiesskylines2",
            new Dictionary<string, NexusGameArtwork>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(GameArtworkSource.Steam, candidate.Source);
        Assert.Contains("949230", candidate.Url!, StringComparison.Ordinal);
    }

    /// <summary>With neither identifier there is no cover, and none is invented.</summary>
    [Fact]
    public void NoIdentifierMeansNoCover()
    {
        var candidate = GameArtworkResolver.Resolve(null, null, null);

        Assert.Equal(GameArtworkSource.None, candidate.Source);
        Assert.False(candidate.HasImage);
        Assert.Null(candidate.Url);
    }

    /// <summary>An app id of zero or below never becomes a URL.</summary>
    /// <remarks>
    /// The scanner reports 0 for a non-Steam install. Building a request from it would ask the CDN for
    /// <c>/steam/apps/0/</c> on every run for no reason.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnImplausibleAppIdIsRefused(int appId)
    {
        var candidate = GameArtworkResolver.Resolve(appId, null, null);

        Assert.Equal(GameArtworkSource.None, candidate.Source);
        Assert.False(candidate.HasImage);
    }

    /// <summary>A definitive 404 is remembered, so the address is not requested again.</summary>
    [Fact]
    public async Task ADefinitiveMissIsNotRequestedTwice()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));

        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var cache = new ImageCacheService(paths, logger: null, httpClient: new HttpClient(handler));

        var url = GameArtworkResolver.SteamLibraryTemplate.Replace("{0}", "999999999");

        Assert.Null(await cache.GetAsync(url));
        Assert.Null(await cache.GetAsync(url));

        Assert.Single(handler.Uris);
        Assert.True(cache.IsKnownMissing(url));
    }

    /// <summary>
    /// A transient failure is NOT remembered across runs, so the next run tries again.
    /// </summary>
    /// <remarks>
    /// Within one session the in-memory map already collapses repeat asks for the same address — that is the
    /// same behaviour the cache had before this change, and it is what keeps a page of twenty rows from
    /// firing twenty identical requests. What matters here is the <b>persisted</b> record: a 503 must not
    /// land in it, or a game would stay coverless for a month after a momentary network fault.
    /// </remarks>
    [Fact]
    public async Task ATransientFailureIsNotRememberedAcrossRuns()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));

        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var cache = new ImageCacheService(paths, logger: null, httpClient: new HttpClient(handler));

        var url = GameArtworkResolver.SteamLibraryTemplate.Replace("{0}", "1091500");

        Assert.Null(await cache.GetAsync(url));
        Assert.False(cache.IsKnownMissing(url));
        Assert.Null(await cache.GetAsync(url));
        Assert.Single(handler.Uris);

        // A second cache over the same directory represents the next run of the application.
        using var nextRun = new ImageCacheService(paths, logger: null, httpClient: new HttpClient(handler));
        Assert.False(nextRun.IsKnownMissing(url));
    }

    /// <summary>A real image is stored once and served from disk afterwards.</summary>
    [Fact]
    public async Task AnImageIsDownloadedOnceAndThenServedFromDisk()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };

        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(png) is { } content
                ? WithImageType(content)
                : throw new InvalidOperationException(),
        });

        using var cache = new ImageCacheService(paths, logger: null, httpClient: new HttpClient(handler));

        var url = GameArtworkResolver.SteamLibraryTemplate.Replace("{0}", "1623730");

        var first = await cache.GetAsync(url);
        var second = await cache.GetAsync(url);

        Assert.NotNull(first);
        Assert.True(File.Exists(first));
        Assert.Equal(first, second);
        Assert.Single(handler.Uris);
    }

    /// <summary>A 200 that is not an image is treated as an answer, not as a transient fault.</summary>
    [Fact]
    public async Task ANonImageResponseIsNotRetried()
    {
        using var tree = new TemporaryTree();
        var paths = new AppPaths(tree.Dir("appdata"));

        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not an image</html>"),
        });

        using var cache = new ImageCacheService(paths, logger: null, httpClient: new HttpClient(handler));

        var url = GameArtworkResolver.SteamLibraryTemplate.Replace("{0}", "228980");

        Assert.Null(await cache.GetAsync(url));
        Assert.Null(await cache.GetAsync(url));
        Assert.Single(handler.Uris);
    }

    /// <summary>Attaches an image content type to a byte payload.</summary>
    /// <param name="content">Payload.</param>
    private static ByteArrayContent WithImageType(ByteArrayContent content)
    {
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return content;
    }

    /// <summary>An HTTP handler that answers from a script and records every address it was asked for.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        /// <summary>Creates the handler.</summary>
        /// <param name="respond">Produces the answer for one request.</param>
        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        /// <summary>Addresses requested, in order.</summary>
        public List<string> Uris { get; } = [];

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!.ToString());
            return Task.FromResult(_respond(request));
        }
    }
}
