using System.Text.Json.Serialization;

namespace ModManager.Core;

/// <summary>
/// A declarative description of one game, plus the adapter metadata the engine needs.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — Modrix's <c>game.toml</c> (<c>api_version = 2</c>) and Vortex's game
/// extensions. Modrix's stated goal is that core contains <i>no</i> game-specific code:
/// "Everything Bethesda-shaped about this game lives in this file." That is the property we
/// want, because it means adding a game never means editing the engine.
/// </para>
/// <para>
/// We keep Modrix's shape but express it as a C# record so definitions can come from three
/// places at once: built-in adapters, JSON files dropped into <c>%APPDATA%/ModManager/Games</c>,
/// and (later) a downloaded registry. All three produce the same type, so the engine cannot
/// tell them apart.
/// </para>
/// <para>
/// Fields marked "Modrix parity" map 1:1 onto Modrix's <c>game.toml</c> keys, which keeps our
/// vocabulary aligned with a project whose semantics are already battle-tested.
/// </para>
/// </remarks>
public sealed record GameDefinition
{
    /// <summary>Stable machine id, e.g. <c>skyrimse</c>. Used in paths, so it must be filename-safe. Modrix parity: <c>id</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Display name. Modrix parity: <c>name</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Short blurb shown on the Games page.</summary>
    public string? Description { get; init; }

    /// <summary>Steam application id, used for install detection. Modrix parity: <c>steam_appid</c>.</summary>
    public int? SteamAppId { get; init; }

    /// <summary>GOG product id. Modrix parity: <c>gog_id</c>.</summary>
    public string? GogId { get; init; }

    /// <summary>Epic catalog id. Modrix parity: <c>epic_id</c>.</summary>
    public string? EpicId { get; init; }

    /// <summary>Xbox/Microsoft Store id. Modrix parity: <c>xbox_id</c>.</summary>
    public string? XboxId { get; init; }

    /// <summary>Origin/EA id. Modrix parity: <c>origin_id</c>.</summary>
    public string? OriginId { get; init; }

    /// <summary>Ubisoft Connect id. Modrix parity: <c>uplay_id</c>.</summary>
    public string? UplayId { get; init; }

    /// <summary>Nexus Mods domain (the <c>skyrimspecialedition</c> in a nexusmods.com URL). Modrix parity: <c>nexus_domain</c>.</summary>
    public string? NexusDomain { get; init; }

    /// <summary>Game executable relative to the install root, when we can launch it. Modrix parity: <c>executable</c> / <c>exe</c>.</summary>
    public string? Executable { get; init; }

    /// <summary>
    /// Extra arguments passed when launching (e.g. loaders that need a flag).
    /// </summary>
    public string? LaunchArguments { get; init; }

    /// <summary>
    /// Names of top-level directories that count as mod content and must never be treated as a
    /// packaging wrapper. Lower-case; compared case-insensitively.
    /// </summary>
    /// <remarks>
    /// Modrix parity: <c>content_dirs</c>. This is the field that makes smart Mod Root detection
    /// possible without guessing from directory names alone. Modrix documents the exact failure
    /// it prevents: "A lone <c>BepInEx/</c> (or legacy <c>QMods/</c>) tree at an archive root is
    /// content, not a packaging wrapper — hoisting it would strip the container and deploy the
    /// plugin where the loader never finds it."
    /// </remarks>
    public IReadOnlyList<string> ContentDirectories { get; init; } = [];

    /// <summary>
    /// Directory prefixes that are safe to strip off an archive root because the game's mod root
    /// already implies them (e.g. a BepInEx mod shipped as <c>BepInEx/plugins/Mod/...</c> when
    /// the mod root already <i>is</i> <c>BepInEx/plugins</c>).
    /// </summary>
    /// <remarks>
    /// Modrix parity: the "redundant prefix stripped at staging" rule documented in
    /// <c>games/subnautica/game.toml</c>. Modrix describes the two styles it must reconcile:
    /// "Most mods ship their files bare and land there directly; the minority that ship the full
    /// <c>BepInEx/plugins/&lt;Mod&gt;/…</c> path have that redundant prefix stripped at staging,
    /// so both styles end up in the same place."
    /// </remarks>
    public IReadOnlyList<string> StripPrefixes { get; init; } = [];

    /// <summary>
    /// Vanilla files the game ships inside the mod root, lower-case. An unowned file matching one
    /// of these is base-game content, not a foreign mod. A trailing <c>*</c> means prefix match.
    /// </summary>
    /// <remarks>Modrix parity: <c>base_files</c>. Prevents the scanner from reporting Creation Club content as a leftover mod.</remarks>
    public IReadOnlyList<string> BaseFiles { get; init; } = [];

    /// <summary>
    /// Files that must exist for a candidate directory to be accepted as this game's install.
    /// Relative to the install root, case-insensitive. Modrix parity: <c>required_files</c>.
    /// </summary>
    public IReadOnlyList<string> RequiredFiles { get; init; } = [];

    /// <summary>
    /// <c>true</c> when the mod root is anchored to the user profile (Documents/AppData) rather
    /// than the install directory. Modrix parity: <c>mod_base = "documents"</c>.
    /// </summary>
    /// <remarks>
    /// The Sims 4 is the canonical case: mods live in
    /// <c>Documents/Electronic Arts/The Sims 4/Mods</c>, nowhere near the game install. Modelling
    /// this as a flag instead of a special adapter is what keeps the engine generic.
    /// </remarks>
    public bool ModRootInUserProfile { get; init; }

    /// <summary>
    /// Mod root path relative to whichever base <see cref="ModRootInUserProfile"/> selects.
    /// Empty string means the install root itself (Palworld, Cyberpunk 2077). Modrix parity: <c>mod_root</c>.
    /// </summary>
    public string ModRoot { get; init; } = string.Empty;

    /// <summary>
    /// Ordered deployment preference. The applier walks it and uses the first method that works
    /// for each file. Modrix parity: <c>deploy = "link"</c> expands to hardlink → symlink → copy.
    /// </summary>
    public IReadOnlyList<DeploymentMethod> DeploymentPreference { get; init; } =
        [DeploymentMethod.Hardlink, DeploymentMethod.Symlink, DeploymentMethod.Copy];

    /// <summary>Ordered install-detection strategies. Modrix parity: <c>install_probe</c>.</summary>
    public IReadOnlyList<InstallProbeKind> InstallProbes { get; init; } = [];

    /// <summary>Vendor registry locations that identify an install. Modrix parity: <c>[[registry_keys]]</c>.</summary>
    public IReadOnlyList<RegistryProbe> RegistryKeys { get; init; } = [];

    /// <summary>Load-order strategy, or null when the game has none. Modrix parity: <c>[load_order]</c>.</summary>
    public LoadOrderDefinition? LoadOrder { get; init; }

    /// <summary>Mod loader(s) this game needs. Modrix parity: <c>[health.loader]</c> plus the loader concept.</summary>
    public ModLoaderDefinition? Loader { get; init; }

    /// <summary>How to spot hand-installed content already sitting in the game folder. Modrix parity: <c>[[external_scan]]</c>.</summary>
    public IReadOnlyList<ExternalScanDefinition> ExternalScans { get; init; } = [];

    /// <summary>Declared capability set; the UI shows a page per capability and hides the rest.</summary>
    public GameCapability Capabilities { get; init; } = GameCapability.None;

    /// <summary>File extensions this game treats as mod content, for analyzer scoring (e.g. <c>.pak</c>, <c>.dll</c>, <c>.esp</c>).</summary>
    public IReadOnlyList<string> ModFileExtensions { get; init; } = [];

    /// <summary>Suggested source archives format hint, purely informational today.</summary>
    public string? RecommendedArchiveTool { get; init; }

    /// <summary>True for definitions loaded from disk rather than compiled in.</summary>
    public bool IsUserDefined { get; init; }

    /// <summary>
    /// True when this definition is a generated fallback for a game with no real support.
    /// </summary>
    /// <remarks>
    /// A game added in generic mode needs <i>a</i> definition so the engine knows where its mod root is, but
    /// that definition is not support: the game still has no loader, load order or layout rules of its own.
    /// Marking it keeps the interface honest — such a game continues to read as "通用模式" instead of being
    /// promoted to "已支持" by the very record that was created to admit it is not.
    /// </remarks>
    public bool IsGenericFallback { get; init; }

    /// <summary>Where this definition came from, shown in Settings → Games.</summary>
    public string? SourcePath { get; init; }

    /// <summary>
    /// Definition schema version. Modrix parity: <c>api_version</c>. Lets a future release migrate
    /// or reject stale definitions instead of silently misreading them.
    /// </summary>
    public int ApiVersion { get; init; } = 2;

    /// <summary>True when deploying this game can use a link instead of a copy.</summary>
    [JsonIgnore]
    public bool SupportsLinking => DeploymentPreference.Any(m => m is not DeploymentMethod.Copy);
}

/// <summary>Install-detection strategy. Modrix parity: an <c>install_probe</c> entry.</summary>
public enum InstallProbeKind
{
    /// <summary>Read Steam's <c>libraryfolders.vdf</c> and appmanifest files.</summary>
    Steam = 0,

    /// <summary>Read vendor registry keys / uninstall entries.</summary>
    Registry = 1,

    /// <summary>Look for a previously remembered path.</summary>
    PathHint = 2,

    /// <summary>Read GOG Galaxy's registry + <c>goggame-*.info</c>.</summary>
    Gog = 3,

    /// <summary>Read Epic's launcher manifest JSON files.</summary>
    Epic = 4,

    /// <summary>Probe common well-known install directories.</summary>
    WellKnown = 5,

    /// <summary>
    /// Read Microsoft Store / Game Pass installs (referenced by <see cref="GameDefinition.XboxId"/>).
    /// Declared so a definition can express intent; <c>GameDetector</c> reports it as "not found"
    /// rather than pretending to support it.
    /// </summary>
    Xbox = 6,
}

/// <summary>A registry location that identifies an install. Modrix parity: <c>[[registry_keys]]</c>.</summary>
/// <param name="Hive">Registry hive, e.g. <c>HKLM</c>.</param>
/// <param name="Key">Subkey path.</param>
/// <param name="Value">Value name holding the install directory.</param>
public sealed record RegistryProbe(string Hive, string Key, string Value);

/// <summary>Load-order strategy declaration. Modrix parity: <c>[load_order]</c>.</summary>
public sealed record LoadOrderDefinition
{
    /// <summary>Which mechanism the game reads.</summary>
    public LoadOrderStrategy Strategy { get; init; } = LoadOrderStrategy.None;

    /// <summary>
    /// Directory under the user's local app data holding the order file, when the strategy is
    /// file-based. Modrix parity: <c>appdata_dir</c>.
    /// </summary>
    public string? AppDataDirectory { get; init; }

    /// <summary>Absolute override for the order file, for games that store it elsewhere.</summary>
    public string? ExplicitPath { get; init; }

    /// <summary>Extensions treated as plugins, e.g. <c>esp</c>/<c>esm</c>/<c>esl</c>.</summary>
    public IReadOnlyList<string> PluginExtensions { get; init; } = [];

    /// <summary>Vanilla plugins never written to the user's order file. Modrix parity: <c>vanilla_plugins</c>.</summary>
    public IReadOnlyList<string> VanillaPlugins { get; init; } = [];

    /// <summary>Plugins enabled by default when first seen.</summary>
    public bool EnableNewPluginsByDefault { get; init; } = true;
}

/// <summary>How a game orders its plugins.</summary>
public enum LoadOrderStrategy
{
    /// <summary>Game has no load order; the planner falls back to mod priority alone.</summary>
    None = 0,

    /// <summary>Bethesda <c>Plugins.txt</c> (<c>*Name.esp</c> means enabled).</summary>
    PluginsTxt = 1,

    /// <summary>A JSON/INI order file owned by a mod loader rather than the game.</summary>
    LoaderConfig = 2,

    /// <summary>Order is implied by directory naming (<c>0001_Mod</c>, <c>0002_Mod</c>).</summary>
    DirectoryPrefix = 3,
}

/// <summary>A mod loader that must be installed for mods to function. Modrix parity: <c>[health.loader]</c>.</summary>
public sealed record ModLoaderDefinition
{
    /// <summary>Loader family, which selects the detection strategy.</summary>
    public ModLoaderKind Kind { get; init; } = ModLoaderKind.Unknown;

    /// <summary>Display name, e.g. "BepInEx".</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Plugin root relative to the deployment root, e.g. <c>plugins</c> for BepInEx.</summary>
    public string? PluginsDirectory { get; init; }

    /// <summary>Files whose presence proves the loader is installed, relative to the install root.</summary>
    public IReadOnlyList<string> MarkerFiles { get; init; } = [];

    /// <summary>File-name prefixes that mark loader-owned binaries at the install root.</summary>
    public IReadOnlyList<string> RootFilePrefixes { get; init; } = [];

    /// <summary>Message shown when mods need the loader but it is absent.</summary>
    public string MissingMessage { get; init; } = string.Empty;

    /// <summary>Where the user can get the loader. Shown as a link, never auto-installed.</summary>
    public string? DownloadUrl { get; init; }
}

/// <summary>Known mod-loader families.</summary>
public enum ModLoaderKind
{
    Unknown = 0,
    BepInEx = 1,
    Ue4ss = 2,
    MelonLoader = 3,
    Smapi = 4,
    ScriptExtender = 5,
    Forge = 6,
    Fabric = 7,
    GenericDll = 8,
    ModManagerFolder = 9,
}

/// <summary>How to detect hand-installed content. Modrix parity: <c>[[external_scan]]</c>.</summary>
public sealed record ExternalScanDefinition
{
    /// <summary><c>folder</c> or <c>file</c>. Modrix parity: <c>kind</c>.</summary>
    public string Kind { get; init; } = "folder";

    /// <summary>Label shown in the External Content list. Modrix parity: <c>label</c>.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Subdirectory to scan, relative to the mod root. Empty means the root itself.</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>Extensions that count, for <c>kind = "file"</c>.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>Skip files matching the game's base-file list. Modrix parity: <c>skip_base</c>.</summary>
    public bool SkipBaseFiles { get; init; }
}
