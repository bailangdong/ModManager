namespace ModManager.Core.Services;

/// <summary>Which source a game's cover art came from.</summary>
public enum GameArtworkSource
{
    /// <summary>No reliable source; the interface shows the shared placeholder.</summary>
    None,

    /// <summary>Nexus Mods artwork for a domain the game's definition declares.</summary>
    Nexus,

    /// <summary>The public Steam image CDN, addressed by Steam app id.</summary>
    Steam,
}

/// <summary>One resolved artwork decision.</summary>
/// <param name="Url">Image address, or null when there is no reliable source.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Describe">Short explanation, for the acceptance report and for tooltips.</param>
public sealed record GameArtworkCandidate(string? Url, GameArtworkSource Source, string Describe)
{
    /// <summary>True when there is an image to fetch.</summary>
    public bool HasImage => Url is { Length: > 0 };
}

/// <summary>
/// Decides where a game's cover art should come from.
/// </summary>
/// <remarks>
/// <para>
/// WHY A GAME HAD NO COVER: the application only ever asked Nexus for artwork, and it can only ask about
/// domains a game's <c>GameDefinition</c> declares. Games added through the generic adapter have no dedicated
/// definition, so they have no domain, so they were never queried and always rendered an empty tile. That was
/// fourteen of the sixteen games on this machine. The fix is not a better cache — it is a second source.
/// </para>
/// <para>
/// WHY STEAM AND NOT A NAME SEARCH: the Steam image CDN is public, requires no key, and is addressed by
/// <b>app id</b>, which is a stable identifier the scanner already has. Searching a store by game name is the
/// approach this deliberately avoids: "Age of Empires II" matches a 1999 release, a 2013 HD edition and a 2019
/// Definitive Edition, and silently showing the wrong box art is worse than showing none. An app id that does
/// not exist answers 404, which the image cache records so it is not asked for again.
/// </para>
/// <para>
/// ORDER OF PRECEDENCE: Nexus first. When a game does have a definition, that artwork is the one belonging to
/// the site the mods actually come from, and it is already domain-confirmed rather than id-derived.
/// </para>
/// </remarks>
public static class GameArtworkResolver
{
    /// <summary>
    /// Steam's portrait cover. Portrait rather than the 460×215 header because it is the shape the game rows
    /// and the dashboard hero are laid out for, so nothing has to be letterboxed.
    /// </summary>
    public const string SteamLibraryTemplate =
        "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/library_600x900.jpg";

    /// <summary>Nexus artwork never resolves below this app id; used to reject placeholder ids.</summary>
    private const int MinimumPlausibleAppId = 1;

    /// <summary>
    /// Picks the best available source for one game.
    /// </summary>
    /// <param name="steamAppId">Steam app id from the scanner, when the game came from Steam.</param>
    /// <param name="nexusDomain">Nexus domain declared by the game's definition, when it has one.</param>
    /// <param name="nexusArtwork">Resolved Nexus artwork, keyed by domain.</param>
    public static GameArtworkCandidate Resolve(
        int? steamAppId,
        string? nexusDomain,
        IReadOnlyDictionary<string, NexusGameArtwork>? nexusArtwork)
    {
        if (nexusDomain is { Length: > 0 } domain
            && nexusArtwork is not null
            && nexusArtwork.TryGetValue(domain, out var artwork))
        {
            var url = FirstNonEmpty(artwork.CardUrl, artwork.TileUrl, artwork.ThumbnailUrl);
            if (url is not null)
            {
                return new GameArtworkCandidate(url, GameArtworkSource.Nexus, $"Nexus · {domain}");
            }
        }

        if (steamAppId is { } appId && appId >= MinimumPlausibleAppId)
        {
            return new GameArtworkCandidate(
                string.Format(System.Globalization.CultureInfo.InvariantCulture, SteamLibraryTemplate, appId),
                GameArtworkSource.Steam,
                $"Steam · AppID {appId}");
        }

        return new GameArtworkCandidate(
            null,
            GameArtworkSource.None,
            nexusDomain is { Length: > 0 } ? "Nexus 未收录该游戏" : "没有可用的封面来源");
    }

    /// <summary>Returns the first value that is present and non-empty.</summary>
    /// <param name="values">Candidates, best first.</param>
    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
