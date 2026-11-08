using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModManager.Core.Services;

/// <summary>
/// Resolves and caches Nexus game artwork by domain.
/// </summary>
/// <remarks>
/// <para>
/// The artwork URL is not published directly: the GraphQL API answers with templates containing
/// <c>{gameId}</c> and the id comes from a per-domain lookup, so this type joins the two and stores the result
/// beside the other caches. Only domains that already have a mapping are ever asked about — a game with no
/// Nexus domain shows "no artwork" rather than a guess derived from its name.
/// </para>
/// <para>
/// The store is keyed by domain, which is stable, and holds the resolved absolute URLs. Images themselves are
/// downloaded and stored by <see cref="ImageCacheService"/>, so there is exactly one image cache in the
/// application.
/// </para>
/// </remarks>
public sealed class NexusGameImageCache
{
    /// <summary>How long a stored entry stays usable.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromDays(7);

    private readonly AppPaths _paths;
    private readonly IAppLogger? _logger;

    /// <summary>Creates the cache.</summary>
    /// <param name="paths">Application paths.</param>
    /// <param name="logger">Optional logger.</param>
    public NexusGameImageCache(AppPaths paths, IAppLogger? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger;
    }

    /// <summary>Path of the stored domain-to-artwork map.</summary>
    public string FilePath => Path.Combine(_paths.Root, "Cache", "NexusGameArtwork.json");

    /// <summary>
    /// Returns artwork for the requested domains, resolving only what is missing or stale.
    /// </summary>
    /// <param name="nexus">Provider used to resolve artwork.</param>
    /// <param name="domains">Nexus domains that are actually in use.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyDictionary<string, NexusGameArtwork>> GetAsync(
        INexusProvider nexus,
        IReadOnlyList<string> domains,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nexus);
        ArgumentNullException.ThrowIfNull(domains);

        var wanted = domains
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Select(domain => domain.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var stored = Read();
        var known = stored?.Games ?? new Dictionary<string, NexusGameArtwork>(StringComparer.OrdinalIgnoreCase);

        if (wanted.Count == 0)
        {
            return known;
        }

        var fresh = stored is not null && DateTimeOffset.UtcNow - stored.SavedAt < FreshFor;
        var missing = fresh
            ? []
            : wanted.Where(domain => !known.ContainsKey(domain)).ToList();

        if (missing.Count == 0)
        {
            return wanted
                .Where(known.ContainsKey)
                .ToDictionary(domain => domain, domain => known[domain], StringComparer.OrdinalIgnoreCase);
        }

        var resolved = await nexus.GetGameArtworkAsync(missing, cancellationToken).ConfigureAwait(false);
        if (!resolved.Succeeded || resolved.Value is null)
        {
            _logger?.Warn("Nexus", "游戏封面解析失败，沿用已有缓存。", resolved.Error);

            return known;
        }

        foreach (var artwork in resolved.Value)
        {
            known[artwork.DomainName] = artwork;
        }

        Write(new StoredArtwork(DateTimeOffset.UtcNow, known));

        return known;
    }

    /// <summary>Reads the stored map, or null when there is none or it is unreadable.</summary>
    private StoredArtwork? Read()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var stored = JsonSerializer.Deserialize<StoredArtwork>(File.ReadAllText(FilePath));

            return stored?.Games.Count > 0 ? stored : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger?.Warn("Nexus", "游戏封面缓存无法读取，将重新解析。", ex.Message);
            return null;
        }
    }

    /// <summary>Stores the map; a write failure is not worth failing the caller for.</summary>
    /// <param name="artwork">Map to store.</param>
    private void Write(StoredArtwork artwork)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(artwork, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warn("Nexus", "游戏封面缓存无法写入。", ex.Message);
        }
    }

    /// <summary>Stored payload.</summary>
    /// <param name="SavedAt">When it was captured.</param>
    /// <param name="Games">Domain to artwork.</param>
    private sealed record StoredArtwork(
        [property: JsonPropertyName("savedAt")] DateTimeOffset SavedAt,
        [property: JsonPropertyName("games")] Dictionary<string, NexusGameArtwork> Games);
}
