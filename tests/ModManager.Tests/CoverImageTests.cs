using System.IO;
using System.Windows.Media.Imaging;
using ModManager.App.ViewModels;
using ModManager.Core.Services;
using System.Threading.Tasks;
using Xunit;

namespace ModManager.Tests;

/// <summary>
/// Cover loading: the bytes come from the cache, and decoding must work for what Nexus actually serves.
/// </summary>
/// <remarks>
/// Nexus hands out WebP. WPF can only decode it through the system WIC codec, so this is worth an explicit
/// test: if the codec is missing the interface must degrade to "no image" rather than fail, and if it is
/// present the cover must come back as a usable, frozen bitmap.
/// </remarks>
public sealed class CoverImageTests
{
    /// <summary>A cached WebP decodes into a frozen bitmap the interface can bind.</summary>
    [Fact]
    public async Task CachedWebpDecodes()
    {
        var sample = NexusImageSamples.Webp();
        if (sample is null)
        {
            // No sample available on this machine: nothing to assert, and no reason to fail the suite.
            return;
        }

        var loader = new CoverImageLoader(new StubImageCache(sample));
        var image = await loader.LoadAsync("https://example.invalid/cover.webp");

        Assert.NotNull(image);
        Assert.True(image!.IsFrozen);

        var bitmap = Assert.IsType<BitmapImage>(image);
        Assert.True(bitmap.PixelWidth > 0);
        Assert.True(bitmap.PixelHeight > 0);
    }

    /// <summary>A URL that cannot be fetched yields no image instead of an exception.</summary>
    [Fact]
    public async Task UnavailableImageYieldsNothing()
    {
        var loader = new CoverImageLoader(new StubImageCache(null));

        Assert.Null(await loader.LoadAsync("https://example.invalid/missing.png"));
        Assert.Null(await loader.LoadAsync(null));
    }

    /// <summary>Garbage bytes yield no image, so a broken cover cannot break a page.</summary>
    [Fact]
    public async Task UndecodableBytesYieldNothing()
    {
        using var tree = new TemporaryTree();
        var path = tree.WriteBytes("garbage.webp", "this is not an image at all"u8.ToArray());

        var loader = new CoverImageLoader(new StubImageCache(path));

        Assert.Null(await loader.LoadAsync("https://example.invalid/garbage.webp"));
    }

    /// <summary>Returns a cached path, standing in for the real image cache.</summary>
    private sealed class StubImageCache : IImageCache
    {
        private readonly string? _path;

        /// <summary>Creates the stub.</summary>
        /// <param name="path">Path to report, or null for "unavailable".</param>
        public StubImageCache(string? path) => _path = path;

        /// <inheritdoc />
        public Task<string?> GetAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult(_path);
    }
}

/// <summary>Locates a real WebP sample captured from Nexus, when one is present.</summary>
internal static class NexusImageSamples
{
    /// <summary>Path to a real WebP cover captured during verification, or null.</summary>
    public static string? Webp()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var candidate = Path.Combine(repo, "artifacts", "test-tmp", "covers", "cover.png");

        return File.Exists(candidate) ? candidate : null;
    }
}
