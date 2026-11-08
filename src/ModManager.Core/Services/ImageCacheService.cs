using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModManager.Core.Services;

/// <summary>
/// Downloads remote images once and keeps them on disk.
/// </summary>
/// <remarks>
/// <para>
/// WPF's own remote-image loading is not usable here: binding an <c>Image</c> directly to an <c>https</c> URL
/// loads asynchronously through the shell stack and fails silently, so the interface kept falling back to a
/// letter tile. Fetching the bytes ourselves and decoding a local file is both reliable and testable.
/// </para>
/// <para>
/// Nexus serves WebP for its covers. Windows ships a WIC WebP codec (Microsoft.WebpImageExtension), so the
/// bytes are stored exactly as received and decoded locally; nothing is re-encoded and no image library is
/// added.
/// </para>
/// </remarks>
public interface IImageCache
{
    /// <summary>
    /// Returns a local path for the image, downloading it on first use.
    /// </summary>
    /// <param name="url">Remote image URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Local file path, or null when the image is unavailable (never throws for a bad image).</returns>
    Task<string?> GetAsync(string url, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ImageCacheService : IImageCache, IDisposable
{
    /// <summary>Refuse anything larger than this; a cover is not worth more.</summary>
    public const long MaxBytes = 8 * 1024 * 1024;

    /// <summary>
    /// How long a definitive "there is no image here" answer is remembered.
    /// </summary>
    /// <remarks>
    /// Only definitive answers are recorded: a 404, 403 or 410 means that address will not produce an image
    /// however many times it is asked, which is exactly what a Steam app id with no store page returns. A
    /// timeout, a DNS failure or a 5xx is deliberately <b>not</b> recorded — those are the conditions that
    /// clear up on their own, and remembering them would keep a game without its cover long after the network
    /// recovered.
    /// </remarks>
    public static readonly TimeSpan MissingFor = TimeSpan.FromDays(30);

    private readonly string _directory;
    private readonly string _missingFile;
    private readonly IAppLogger? _logger;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _gate = new(4, 4);
    private readonly Dictionary<string, string?> _resolved = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _missing = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _missingLock = new();

    /// <summary>Creates the cache.</summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="httpClient">Optional client, so tests can supply a stub.</param>
    public ImageCacheService(AppPaths paths, IAppLogger? logger = null, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _directory = Path.Combine(paths.Root, "Cache", "Images");
        _missingFile = Path.Combine(paths.Root, "Cache", "ImageMisses.json");
        _logger = logger;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(NexusProvider.Identity);
        _http.DefaultRequestHeaders.Accept.ParseAdd("image/*");

        LoadMissing();
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        if (_resolved.TryGetValue(url, out var known))
        {
            return known;
        }

        // Answered definitively on an earlier run: do not ask again, and do not touch the network.
        if (IsKnownMissing(url))
        {
            _resolved[url] = null;
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_resolved.TryGetValue(url, out known))
            {
                return known;
            }

            var outcome = await DownloadAsync(uri, cancellationToken).ConfigureAwait(false);

            if (outcome.Path is null && outcome.Definitive)
            {
                RecordMissing(url);
            }

            _resolved[url] = outcome.Path;
            return outcome.Path;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>True when the address is remembered as having no image.</summary>
    /// <param name="url">Image address.</param>
    public bool IsKnownMissing(string url)
    {
        lock (_missingLock)
        {
            if (!_missing.TryGetValue(url, out var recordedAt))
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - recordedAt < MissingFor)
            {
                return true;
            }

            // Expired: forget it, so a store page that appeared later can still be picked up.
            _missing.Remove(url);
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();

        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    /// <summary>Downloads the image unless it is already on disk.</summary>
    /// <param name="uri">Image address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<DownloadOutcome> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..24];

        try
        {
            Directory.CreateDirectory(_directory);

            // A previous run may have stored it under any of the supported extensions.
            foreach (var existing in Directory.GetFiles(_directory, key + ".*"))
            {
                if (new FileInfo(existing).Length > 0)
                {
                    return new DownloadOutcome(existing, Definitive: false);
                }
            }

            using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                _logger?.Warn("Images", $"封面下载失败：HTTP {status}（{uri.Host}）。");

                var definitive = response.StatusCode is System.Net.HttpStatusCode.NotFound
                    or System.Net.HttpStatusCode.Forbidden
                    or System.Net.HttpStatusCode.Gone;

                return new DownloadOutcome(null, definitive);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                // A 200 that is not an image is an answer about this address, not a transient failure.
                _logger?.Warn("Images", $"封面地址返回的不是图片：{mediaType ?? "未知类型"}。");
                return new DownloadOutcome(null, Definitive: true);
            }

            if (response.Content.Headers.ContentLength is { } length && length > MaxBytes)
            {
                _logger?.Warn("Images", $"封面过大（{length} 字节），已跳过。");
                return new DownloadOutcome(null, Definitive: true);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxBytes)
            {
                return new DownloadOutcome(null, Definitive: false);
            }

            var path = Path.Combine(_directory, key + ExtensionFor(mediaType, uri));
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);

            return new DownloadOutcome(path, Definitive: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            // A cover that cannot be fetched must never break a page, and a network fault is never recorded:
            // the next run should try again.
            _logger?.Warn("Images", "封面下载失败。", ex.Message);
            return new DownloadOutcome(null, Definitive: false);
        }
    }

    /// <summary>Reads the persisted "no image here" set, tolerating absence and corruption.</summary>
    private void LoadMissing()
    {
        try
        {
            if (!File.Exists(_missingFile))
            {
                return;
            }

            var stored = JsonSerializer.Deserialize<StoredMisses>(File.ReadAllText(_missingFile));
            if (stored?.Missing is null)
            {
                return;
            }

            lock (_missingLock)
            {
                foreach (var (url, at) in stored.Missing)
                {
                    if (DateTimeOffset.UtcNow - at < MissingFor)
                    {
                        _missing[url] = at;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger?.Warn("Images", "无封面记录无法读取，将重新尝试。", ex.Message);
        }
    }

    /// <summary>Remembers that an address has no image, and persists the set.</summary>
    /// <param name="url">Image address.</param>
    private void RecordMissing(string url)
    {
        try
        {
            Dictionary<string, DateTimeOffset> snapshot;

            lock (_missingLock)
            {
                _missing[url] = DateTimeOffset.UtcNow;

                // Bounded so the file cannot grow without limit on a machine that has seen many games.
                snapshot = _missing
                    .Where(pair => DateTimeOffset.UtcNow - pair.Value < MissingFor)
                    .OrderByDescending(pair => pair.Value)
                    .Take(500)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

                _missing.Clear();
                foreach (var (key, at) in snapshot)
                {
                    _missing[key] = at;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_missingFile)!);
            File.WriteAllText(
                _missingFile,
                JsonSerializer.Serialize(new StoredMisses(DateTimeOffset.UtcNow, snapshot)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warn("Images", "无封面记录无法写入。", ex.Message);
        }
    }

    /// <summary>Picks a file extension from the reported media type, falling back to the URL's.</summary>
    /// <param name="mediaType">Response content type.</param>
    /// <param name="uri">Requested address.</param>
    private static string ExtensionFor(string mediaType, Uri uri) => mediaType.ToLowerInvariant() switch
    {
        "image/webp" => ".webp",
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        _ => Path.GetExtension(uri.AbsolutePath) is { Length: > 0 } extension ? extension : ".img",
    };

    /// <summary>Result of one download attempt.</summary>
    /// <param name="Path">Local file, or null.</param>
    /// <param name="Definitive">True when the address itself has no image, as opposed to a transient fault.</param>
    private readonly record struct DownloadOutcome(string? Path, bool Definitive);

    /// <summary>Persisted set of addresses known to have no image.</summary>
    /// <param name="SavedAt">When it was written.</param>
    /// <param name="Missing">Address to the moment it was found missing.</param>
    private sealed record StoredMisses(
        [property: JsonPropertyName("savedAt")] DateTimeOffset SavedAt,
        [property: JsonPropertyName("missing")] Dictionary<string, DateTimeOffset> Missing);
}
