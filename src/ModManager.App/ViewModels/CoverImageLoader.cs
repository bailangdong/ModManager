using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModManager.Core.Services;

namespace ModManager.App.ViewModels;

/// <summary>
/// Loads cover images from the local image cache and hands the interface a decoded bitmap.
/// </summary>
/// <remarks>
/// <para>
/// Binding an <c>Image</c> straight to an <c>https</c> address does not work reliably here — WPF fetches it
/// through the shell stack, fails silently, and the tile stays empty. This loader fetches the bytes with the
/// same client the rest of the application uses, stores them, and decodes the local file, which is what makes
/// covers appear at all.
/// </para>
/// <para>
/// Nexus returns WebP; the Windows WIC codec handles it, so the bytes are decoded as received. Every failure
/// path returns null: a missing cover must never break a page or a search.
/// </para>
/// </remarks>
public sealed class CoverImageLoader
{
    private readonly IImageCache _cache;
    private readonly ConcurrentDictionary<string, ImageSource?> _decoded = new(StringComparer.Ordinal);

    /// <summary>Creates the loader.</summary>
    /// <param name="cache">Image cache used to obtain the bytes.</param>
    public CoverImageLoader(IImageCache cache) =>
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    /// <summary>
    /// Returns the decoded cover for a URL, downloading and caching it on first use.
    /// </summary>
    /// <param name="url">Remote cover URL, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A frozen bitmap ready for binding, or null when there is no usable cover.</returns>
    public async Task<ImageSource?> LoadAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (_decoded.TryGetValue(url, out var known))
        {
            return known;
        }

        var path = await _cache.GetAsync(url, cancellationToken).ConfigureAwait(true);
        var image = path is null ? null : await Task.Run(() => Decode(path), cancellationToken).ConfigureAwait(true);

        _decoded[url] = image;
        return image;
    }

    /// <summary>Decodes a local image file, returning null instead of throwing.</summary>
    /// <param name="path">Local image path.</param>
    /// <remarks>
    /// Two decoders are tried. <c>IgnoreImageCache</c> is deliberately not used: for the WebP that Nexus
    /// serves, WIC fails with it and succeeds without, which is what silently produced blank covers.
    /// </remarks>
    private static ImageSource? Decode(string path)
    {
        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();

            // Frozen so a background decode can be handed to the UI thread safely.
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            // Fall through to the decoder path below.
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            var frame = BitmapDecoder
                .Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad)
                .Frames[0];

            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            // An undecodable cover is not an application failure.
            return null;
        }
    }

    /// <summary>True for the exceptions an undecodable image raises.</summary>
    /// <param name="exception">Exception to inspect.</param>
    private static bool IsDecodeFailure(Exception exception) =>
        exception is NotSupportedException or FileFormatException or IOException
            or ArgumentException or OutOfMemoryException or InvalidOperationException;
}
