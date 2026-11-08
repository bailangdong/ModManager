using ModManager.Core.Safety;

namespace ModManager.Core;

/// <summary>How much support the application has for a detected game.</summary>
public enum GameSupportKind
{
    /// <summary>A dedicated definition exists, so the game gets its own mod root, loader and rules.</summary>
    Supported = 0,

    /// <summary>Detected on disk, but no dedicated definition. Mods go through the generic adapter.</summary>
    Generic = 1,

    /// <summary>The manifest or install directory is broken, so nothing can be managed yet.</summary>
    Invalid = 2,
}

/// <summary>
/// One game found in a Steam library.
/// </summary>
/// <param name="AppId">Steam application id.</param>
/// <param name="Name">Name recorded in the manifest.</param>
/// <param name="InstallDirectory">Absolute install directory (library/steamapps/common/installdir).</param>
/// <param name="LibraryPath">Steam library root the manifest was found in.</param>
/// <param name="ManifestPath">The <c>appmanifest_*.acf</c> file.</param>
/// <param name="IsValid">True when the install directory exists.</param>
/// <param name="Problem">Why the entry is not usable, when it is not.</param>
public sealed record InstalledSteamGame(
    int AppId,
    string Name,
    string InstallDirectory,
    string LibraryPath,
    string ManifestPath,
    bool IsValid,
    string? Problem)
{
    /// <summary>
    /// Identity used for de-duplication: the app id plus the install directory.
    /// </summary>
    /// <remarks>
    /// Deliberately not the name. Two different games can share a name, the same game can be installed in
    /// two libraries, and a name can be localized — so a name-based key would either merge unrelated games
    /// or list one game twice.
    /// </remarks>
    public string IdentityKey =>
        $"{AppId}|{InstallDirectory.TrimEnd('\\', '/')}".ToLowerInvariant();
}

/// <summary>What a scan of the Steam libraries found.</summary>
/// <param name="Games">Every manifest that was read, valid or not.</param>
/// <param name="Libraries">Steam library roots that were searched.</param>
/// <param name="ManifestCount">Manifests seen, before de-duplication.</param>
public sealed record SteamScanResult(
    IReadOnlyList<InstalledSteamGame> Games,
    IReadOnlyList<string> Libraries,
    int ManifestCount)
{
    /// <summary>Games whose install directory exists.</summary>
    public int ValidCount => Games.Count(game => game.IsValid);

    /// <summary>Entries whose manifest or directory is broken.</summary>
    public int InvalidCount => Games.Count(game => !game.IsValid);

    /// <summary>An empty result, for when Steam is not installed at all.</summary>
    public static SteamScanResult Empty { get; } = new([], [], 0);
}

/// <summary>A detected game together with how (and whether) it is supported.</summary>
/// <param name="Detected">What was found on disk.</param>
/// <param name="Support">Support level.</param>
/// <param name="Definition">Definition to use, when one exists for this game.</param>
public sealed record ResolvedGame(InstalledSteamGame Detected, GameSupportKind Support, GameDefinition? Definition)
{
    /// <summary>Game id that would be used if this entry were added.</summary>
    public string GameId => Definition?.Id ?? SteamGameDefinitions.GenericIdFor(Detected.AppId);

    /// <summary>True when a dedicated definition exists.</summary>
    public bool IsSupported => Support == GameSupportKind.Supported;

    /// <summary>True when the game can be added through the generic adapter.</summary>
    public bool IsGeneric => Support == GameSupportKind.Generic;
}

/// <summary>Counts shown on the Games page and the overview.</summary>
/// <param name="Detected">Games found on disk.</param>
/// <param name="Supported">Games with a dedicated definition.</param>
/// <param name="Generic">Games that would use the generic adapter.</param>
/// <param name="Invalid">Games whose install cannot be used.</param>
/// <param name="Managed">Games already added to the library.</param>
public sealed record GameDetectionSummary(
    int Detected,
    int Supported,
    int Generic,
    int Invalid,
    int Managed)
{
    /// <summary>An empty summary.</summary>
    public static GameDetectionSummary Empty { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>
/// Builds the definition used for a Steam game that has no dedicated one.
/// </summary>
/// <remarks>
/// The user's spec is explicit that this path must not guess: the mod root comes from the user's choice,
/// it is stored relative to a known base (the install directory, or the user profile), and everything
/// else stays the plainest possible treatment — no loader, no load order, no plugins.
/// </remarks>
public static class SteamGameDefinitions
{
    /// <summary>Game id used for a generic Steam game.</summary>
    /// <param name="appId">Steam application id.</param>
    public static string GenericIdFor(int appId) => $"steam-{appId}";

    /// <summary>Builds the generic definition for a Steam game.</summary>
    /// <param name="appId">Steam application id.</param>
    /// <param name="name">Display name.</param>
    /// <param name="modRootRelative">
    /// Mod root relative to the install directory (or to the user profile when
    /// <paramref name="modRootInUserProfile"/> is true).
    /// </param>
    /// <param name="modRootInUserProfile">True when the mod root lives under the user profile.</param>
    public static GameDefinition Build(
        int appId,
        string name,
        string modRootRelative,
        bool modRootInUserProfile = false) => new()
        {
            Id = GenericIdFor(appId),
            Name = string.IsNullOrWhiteSpace(name) ? $"Steam {appId}" : name,
            Description = "Steam 上检测到、但没有专用定义的游戏：Mod 会部署到你指定的目录。",
            SteamAppId = appId,
            ModRoot = NormalizeRelative(modRootRelative),
            ModRootInUserProfile = modRootInUserProfile,

            // Only what the generic adapter can actually honour. Load order, plugin management and launcher
            // support are absent, and the interface hides the pages that need them.
            Capabilities =
                GameCapability.HardlinkDeployment
                | GameCapability.SymlinkDeployment
                | GameCapability.Profiles
                | GameCapability.FileConflicts
                | GameCapability.Dependencies
                | GameCapability.NonZipArchives,

            DeploymentPreference = [DeploymentMethod.Hardlink, DeploymentMethod.Symlink, DeploymentMethod.Copy],
            InstallProbes = [InstallProbeKind.Steam],
            IsUserDefined = true,
            IsGenericFallback = true,
            ApiVersion = 2,
        };

    /// <summary>
    /// Converts an absolute folder the user picked into a definition-shaped relative path.
    /// </summary>
    /// <param name="installDirectory">Game install directory.</param>
    /// <param name="documentsDirectory">The user's Documents directory.</param>
    /// <param name="chosenDirectory">Directory the user selected.</param>
    /// <param name="isProtected">
    /// Policy hook that answers whether a location is a system or otherwise protected directory. Supplied by
    /// the caller so the application's own safety list decides, rather than a second copy of those rules.
    /// </param>
    /// <remarks>
    /// This is the only place a mod root is decided for a generic game, and it never invents one. A folder
    /// inside the game or inside Documents keeps the engine's relative form; anything else is accepted only as
    /// a user-chosen absolute root and only while the safety policy allows the location, so the user may point
    /// a generic game at another drive while Windows, Program Files and drive roots stay refused.
    /// </remarks>
    public static OperationResult<ModRootChoice> ResolveChoice(
        string installDirectory,
        string documentsDirectory,
        string chosenDirectory,
        Func<string, bool>? isProtected = null)
    {
        if (string.IsNullOrWhiteSpace(chosenDirectory))
        {
            return OperationResult<ModRootChoice>.Fail("没有选择目录。");
        }

        if (!Directory.Exists(FileSystemOps.ToExtendedPath(chosenDirectory)))
        {
            return OperationResult<ModRootChoice>.Fail("该目录不存在。");
        }

        var chosen = Path.GetFullPath(chosenDirectory);

        if (TryRelativeTo(installDirectory, chosen, out var insideGame))
        {
            return OperationResult<ModRootChoice>.Ok(
                new ModRootChoice(insideGame, InUserProfile: false, $"游戏目录内：{insideGame}"));
        }

        if (!string.IsNullOrWhiteSpace(documentsDirectory) && TryRelativeTo(documentsDirectory, chosen, out var inDocuments))
        {
            return OperationResult<ModRootChoice>.Ok(
                new ModRootChoice(inDocuments, InUserProfile: true, $"用户文档目录内：{inDocuments}"));
        }

        // Outside both: allowed as a user-chosen folder when the safety policy accepts the location. The root
        // is stored absolute, which the deployment planner honours.
        if (isProtected is null)
        {
            return OperationResult<ModRootChoice>.Fail(
                "这个目录不在游戏目录或“文档”内，且没有通过路径安全检查，因此未被接受。");
        }

        if (isProtected(chosen))
        {
            return OperationResult<ModRootChoice>.Fail(
                "这个位置是系统或受保护目录（例如 Windows、Program Files、驱动器根目录），不能作为 Mod 目录。");
        }

        return OperationResult<ModRootChoice>.Ok(new ModRootChoice(
            chosen,
            InUserProfile: false,
            $"你选择的目录：{chosen}"));
    }

    /// <summary>True when <paramref name="candidate"/> is inside <paramref name="baseDirectory"/>.</summary>
    /// <param name="baseDirectory">Base directory.</param>
    /// <param name="candidate">Candidate directory.</param>
    /// <param name="relative">Relative path when inside.</param>
    private static bool TryRelativeTo(string baseDirectory, string candidate, out string relative)
    {
        relative = string.Empty;

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return false;
        }

        var basePath = Path.GetFullPath(baseDirectory).TrimEnd('\\', '/');
        var target = candidate.TrimEnd('\\', '/');

        if (string.Equals(basePath, target, StringComparison.OrdinalIgnoreCase))
        {
            // The install root itself: an empty mod root means "deploy straight into the game folder", which
            // is legitimate for games whose mods sit next to the executable.
            relative = string.Empty;
            return true;
        }

        if (!target.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith(basePath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        relative = target[(basePath.Length + 1)..].Replace('\\', '/');
        return relative.Length > 0;
    }

    /// <summary>Normalises a relative mod root for storage.</summary>
    /// <param name="value">Relative path.</param>
    private static string NormalizeRelative(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Trim('\\', '/').Replace('\\', '/');
}

/// <summary>A mod root the user chose, expressed the way a definition stores it.</summary>
/// <param name="RelativePath">Path relative to the chosen base.</param>
/// <param name="InUserProfile">True when the base is the user's Documents directory.</param>
/// <param name="Description">Explanation shown in the interface.</param>
public sealed record ModRootChoice(string RelativePath, bool InUserProfile, string Description);
