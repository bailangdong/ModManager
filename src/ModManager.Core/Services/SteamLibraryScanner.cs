using ModManager.Core.Safety;

namespace ModManager.Core.Services;

/// <summary>
/// Scans every Steam library and reports every installed game it finds.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that was missing: the previous detection path asked "where is game X?" for each game
/// the application happened to know about, so a game with no definition could never appear. A library scan
/// asks the opposite question — "what is installed?" — and reports the answer in full.
/// </para>
/// <para>
/// Failures are per-game. An unreadable manifest, a deleted install directory or a bizarre name produces an
/// entry marked invalid and the scan continues, because a scan that gives up on the first problem is worse
/// than useless when the user has forty games installed.
/// </para>
/// </remarks>
public sealed class SteamLibraryScanner
{
    /// <summary>Maximum manifests read per library, bounding the directory walk.</summary>
    public const int MaxManifestsPerLibrary = 4_000;

    /// <summary>Scans all Steam libraries.</summary>
    public SteamScanResult Scan() => Scan(GameDetector.EnumerateSteamLibraries());

    /// <summary>
    /// Scans a specific set of library roots.
    /// </summary>
    /// <param name="libraries">Library roots to search.</param>
    /// <remarks>
    /// Exposed so the scan can be exercised without depending on the machine's registry: an explicit list of
    /// roots is the only difference between a real scan and a test one.
    /// </remarks>
    public SteamScanResult Scan(IReadOnlyList<string> libraries)
    {
        ArgumentNullException.ThrowIfNull(libraries);

        var found = new List<InstalledSteamGame>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifestCount = 0;

        foreach (var library in libraries)
        {
            var steamApps = Path.Combine(library, "steamapps");
            string[] manifests;

            try
            {
                if (!Directory.Exists(FileSystemOps.ToExtendedPath(steamApps)))
                {
                    continue;
                }

                manifests = Directory.GetFiles(FileSystemOps.ToExtendedPath(steamApps), "appmanifest_*.acf");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A library we cannot enumerate is skipped; the others still report.
                continue;
            }

            foreach (var manifest in manifests.Take(MaxManifestsPerLibrary))
            {
                manifestCount++;
                var game = ReadManifest(library, manifest);
                if (game is not null && seen.Add(game.IdentityKey))
                {
                    found.Add(game);
                }
            }
        }

        var ordered = found
            .OrderByDescending(game => game.IsValid)
            .ThenBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SteamScanResult(ordered, libraries, manifestCount);
    }

    /// <summary>Finds one installed Steam game by app id, for the manual "add by AppID" action.</summary>
    /// <param name="appId">Steam application id.</param>
    public InstalledSteamGame? FindByAppId(int appId) =>
        FindByAppId(appId, GameDetector.EnumerateSteamLibraries());

    /// <summary>Finds one installed Steam game by app id within specific library roots.</summary>
    /// <param name="appId">Steam application id.</param>
    /// <param name="libraries">Library roots to search.</param>
    public InstalledSteamGame? FindByAppId(int appId, IReadOnlyList<string> libraries) =>
        Scan(libraries).Games.FirstOrDefault(game => game.AppId == appId);

    /// <summary>Reads one manifest, converting any failure into an invalid entry rather than an exception.</summary>
    /// <param name="library">Library root.</param>
    /// <param name="manifestPath">Manifest file.</param>
    private static InstalledSteamGame? ReadManifest(string library, string manifestPath)
    {
        try
        {
            var fileName = Path.GetFileNameWithoutExtension(manifestPath);
            var fileAppId = int.TryParse(
                fileName.Replace("appmanifest_", string.Empty, StringComparison.OrdinalIgnoreCase),
                out var parsed)
                ? parsed
                : 0;

            var contentAppId = GameDetector.ReadAcfValue(manifestPath, "appid");
            var appId = int.TryParse(contentAppId, out var fromContent) ? fromContent : fileAppId;

            if (appId <= 0)
            {
                return null;
            }

            var name = GameDetector.ReadAcfValue(manifestPath, "name");
            var installDir = GameDetector.ReadAcfValue(manifestPath, "installdir");

            if (string.IsNullOrWhiteSpace(installDir))
            {
                return new InstalledSteamGame(
                    appId,
                    string.IsNullOrWhiteSpace(name) ? $"Steam {appId}" : name,
                    string.Empty,
                    library,
                    manifestPath,
                    IsValid: false,
                    Problem: "清单里没有 installdir 字段，无法确定安装目录。");
            }

            var installPath = Path.Combine(library, "steamapps", "common", installDir);
            var exists = Directory.Exists(FileSystemOps.ToExtendedPath(installPath));

            return new InstalledSteamGame(
                appId,
                string.IsNullOrWhiteSpace(name) ? installDir : name,
                Path.GetFullPath(installPath),
                library,
                manifestPath,
                exists,
                exists ? null : "清单指向的安装目录不存在（游戏可能已被移动或删除）。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new InstalledSteamGame(
                0,
                Path.GetFileNameWithoutExtension(manifestPath),
                string.Empty,
                library,
                manifestPath,
                IsValid: false,
                Problem: "清单无法读取：" + ex.Message);
        }
    }
}

/// <summary>
/// Decides how much support each detected game has.
/// </summary>
/// <remarks>
/// This is the seam that fixes the architecture: detection produces the full list, and this resolver only
/// <i>labels</i> each entry. A game without a definition is reported as generic rather than dropped, so the
/// interface can show everything Steam has and let the user act on any of it.
/// </remarks>
public sealed class GameSupportResolver
{
    /// <summary>Resolves one detected game against the registry.</summary>
    /// <param name="game">Detected game.</param>
    /// <param name="registry">Known adapters.</param>
    public ResolvedGame Resolve(InstalledSteamGame game, IGameAdapterRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(registry);

        if (!game.IsValid)
        {
            return new ResolvedGame(game, GameSupportKind.Invalid, null);
        }

        var definition = FindDefinition(game, registry);

        return definition switch
        {
            null => new ResolvedGame(game, GameSupportKind.Generic, null),

            // A generated fallback is not support: the game still has no rules of its own. The definition is
            // still carried so the interface knows the mod root the user chose.
            { IsGenericFallback: true } fallback => new ResolvedGame(game, GameSupportKind.Generic, fallback),

            _ => new ResolvedGame(game, GameSupportKind.Supported, definition),
        };
    }

    /// <summary>Resolves every detected game.</summary>
    /// <param name="games">Detected games.</param>
    /// <param name="registry">Known adapters.</param>
    public IReadOnlyList<ResolvedGame> ResolveAll(
        IReadOnlyList<InstalledSteamGame> games,
        IGameAdapterRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(registry);

        var resolved = games.Select(game => Resolve(game, registry)).ToList();

        return resolved
            .OrderByDescending(entry => entry.Support == GameSupportKind.Supported)
            .ThenBy(entry => entry.Support == GameSupportKind.Invalid)
            .ThenBy(entry => entry.Detected.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Counts the detected games for the page header.</summary>
    /// <param name="resolved">Resolved games.</param>
    /// <param name="managedGameIds">Game ids already in the library.</param>
    public static GameDetectionSummary Summarize(
        IReadOnlyList<ResolvedGame> resolved,
        IReadOnlyCollection<string> managedGameIds)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(managedGameIds);

        var managed = resolved.Count(entry =>
            managedGameIds.Contains(entry.GameId)
            || managedGameIds.Contains(SteamGameDefinitions.GenericIdFor(entry.Detected.AppId)));

        return new GameDetectionSummary(
            resolved.Count,
            resolved.Count(entry => entry.Support == GameSupportKind.Supported),
            resolved.Count(entry => entry.Support == GameSupportKind.Generic),
            resolved.Count(entry => entry.Support == GameSupportKind.Invalid),
            managed);
    }

    /// <summary>
    /// Finds the dedicated definition for a game.
    /// </summary>
    /// <param name="game">Detected game.</param>
    /// <param name="registry">Known adapters.</param>
    /// <remarks>
    /// The match is on <c>steam_appid</c> only. Name matching is deliberately absent: two games can share a
    /// name, names are localised, and a wrong match would silently apply another game's mod layout to the
    /// user's install. When both a generated fallback and a real definition exist, the real one wins, which
    /// is what makes a game auto-upgrade to supported once it is added to the catalogue.
    /// </remarks>
    private static GameDefinition? FindDefinition(InstalledSteamGame game, IGameAdapterRegistry registry) =>
        registry.All()
            .Select(adapter => adapter.Definition)
            .Where(definition => definition.SteamAppId == game.AppId)
            .OrderBy(definition => definition.IsGenericFallback)
            .FirstOrDefault();
}
