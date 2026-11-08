namespace ModManager.Core.Services;

/// <summary>
/// The supported-game catalogue, as declarative definitions.
/// </summary>
/// <remarks>
/// <para>
/// DESIGN REFERENCE — Modrix's <c>games/&lt;id&gt;/game.toml</c> set (86 games ported from Vortex) and
/// the user's spec §20, which asks for phase one to cover a <c>GenericModAdapter</c> plus a few
/// <b>structurally different</b> games. The point of that request is coverage of the <i>shapes</i>, not
/// the number of titles: if the adapter model can express a BepInEx game, a UE4SS game and a
/// Bethesda game without special-casing, the model is general.
/// </para>
/// <para>
/// So the list below is chosen for structural variety:
/// </para>
/// <list type="table">
/// <item><term>GenericGameAdapter</term><description>no loader, plain Mods folder — the baseline.</description></item>
/// <item><term>Stardew Valley (SMAPI)</term><description>folder-per-mod, sub-manifest, no load order.</description></item>
/// <item><term>Subnautica (BepInEx)</term><description>nested <c>BepInEx/plugins</c> root, redundant-prefix stripping, no load order — the case that proves <c>content_dirs</c> is load-bearing.</description></item>
/// <item><term>Palworld (UE4SS + pak)</term><description>mods deploy to the install root and split between <c>Pal/Content/Paks</c> and <c>Pal/Binaries/Win64</c>; no load order.</description></item>
/// <item><term>Skyrim SE (Bethesda)</term><description>plugins, <c>Plugins.txt</c> load order outside the game folder, a script extender, and vanilla files that must never be flagged as leftovers.</description></item>
/// <item><term>Cyberpunk 2077</term><description>mods deploy to the install root, archive-based.</description></item>
/// </list>
/// <para>
/// Every entry's metadata (Steam AppIDs, Nexus domains) is public information taken from the games'
/// own store pages and Nexus URLs. No Modrix or Vortex content is copied — the definitions are written
/// from the documented behaviour of each game's modding toolchain.
/// </para>
/// </remarks>
public static class GameCatalogue
{
    /// <summary>Every built-in definition.</summary>
    public static IReadOnlyList<GameDefinition> All { get; } =
    [
        Generic(),
        StardewValley(),
        Subnautica(),
        Palworld(),
        SkyrimSpecialEdition(),
        Cyberpunk2077(),
        BepInExGeneric(),
    ];

    /// <summary>Registers every built-in definition plus the user's own into a registry.</summary>
    /// <param name="registry">Registry to populate.</param>
    /// <param name="userDefinitionDirectory">Directory holding user JSON definitions, or null.</param>
    /// <param name="logger">Logger.</param>
    public static void RegisterAll(IGameAdapterRegistry registry, string? userDefinitionDirectory, IAppLogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);

        foreach (var definition in All)
        {
            registry.RegisterDefinition(definition);
        }

        if (!string.IsNullOrWhiteSpace(userDefinitionDirectory))
        {
            // Loaded last so a user definition can override a built-in with the same id, which is the
            // layering Modrix uses for its three definition sources.
            registry.LoadUserDefinitions(userDefinitionDirectory, logger);
        }
    }

    /// <summary>
    /// The baseline: any game, a plain <c>Mods</c> folder, no loader and no load order.
    /// </summary>
    private static GameDefinition Generic() => new()
    {
        Id = "generic",
        Name = "Generic Game (any game)",
        Description = "Add any game by pointing at its folder. Mods deploy into a Mods folder inside it.",
        ModRoot = "Mods",
        Capabilities =
            GameCapability.HardlinkDeployment
            | GameCapability.SymlinkDeployment
            | GameCapability.Profiles
            | GameCapability.FileConflicts
            | GameCapability.Dependencies
            | GameCapability.ExternalContentScan,
        DeploymentPreference = [DeploymentMethod.Hardlink, DeploymentMethod.Symlink, DeploymentMethod.Copy],
        InstallProbes = [InstallProbeKind.PathHint],
        ExternalScans =
        [
            new ExternalScanDefinition { Kind = "folder", Label = "Manually installed mod", Directory = string.Empty },
        ],
    };

    /// <summary>
    /// A BepInEx game with no other specialisation.
    /// </summary>
    /// <remarks>
    /// Exists so a user can adopt any Unity title that is modded through BepInEx by editing one field
    /// (the executable), rather than writing a definition from scratch. This is the "most games need
    /// only data" case made concrete.
    /// </remarks>
    private static GameDefinition BepInExGeneric() => new()
    {
        Id = "bepinex-generic",
        Name = "Generic Unity Game (BepInEx)",
        Description = "A Unity game modded through BepInEx. Mods deploy into BepInEx/plugins.",
        ModRoot = "BepInEx/plugins",
        Capabilities =
            GameCapability.HardlinkDeployment
            | GameCapability.SymlinkDeployment
            | GameCapability.ModLoader
            | GameCapability.Profiles
            | GameCapability.FileConflicts
            | GameCapability.Dependencies
            | GameCapability.Launchable
            | GameCapability.ExternalContentScan,
        DeploymentPreference = [DeploymentMethod.Hardlink, DeploymentMethod.Symlink, DeploymentMethod.Copy],
        InstallProbes = [InstallProbeKind.PathHint],

        // A lone `BepInEx/` at an archive root is the loader container, not packaging. This single line
        // is what stops the analyzer from hoisting it away and deploying the plugin somewhere the
        // loader never looks — the failure Modrix documents for Subnautica.
        ContentDirectories = ["bepinex", "plugins", "patchers", "config", "core", "monomod", "fomod"],

        // Mods that ship the full `BepInEx/plugins/<Mod>/...` path have that redundant prefix stripped,
        // so both packaging styles land in the same place.
        StripPrefixes = ["BepInEx/plugins", "BepInEx"],

        // BepInEx is one loader with several sibling trees. A config or patcher that ships beside the
        // plugin must not be flattened into plugins/ where the loader never reads it.
        InstallTargets =
        [
            new("BepInEx/plugins", "BepInEx/plugins"),
            new("BepInEx/config", "BepInEx/config"),
            new("BepInEx/patchers", "BepInEx/patchers"),
            new("plugins", "BepInEx/plugins"),
            new("config", "BepInEx/config"),
            new("patchers", "BepInEx/patchers"),
        ],

        ModFileExtensions = ["dll", "json", "cfg", "png", "txt"],
        Loader = new ModLoaderDefinition
        {
            Kind = ModLoaderKind.BepInEx,
            DisplayName = "BepInEx",
            PluginsDirectory = "plugins",
            MarkerFiles = ["BepInEx/core/BepInEx.dll", "BepInEx/core/BepInEx.Preloader.dll"],
            RootFilePrefixes = ["winhttp", "doorstop", "version.dll"],
            MissingMessage =
                "BepInEx is not installed. Unity mods do nothing without it, so install BepInEx first.",
            DownloadUrl = "https://github.com/BepInEx/BepInEx/releases",
        },
        ExternalScans =
        [
            new ExternalScanDefinition { Kind = "folder", Label = "BepInEx plugin", Directory = string.Empty },
        ],
    };

    /// <summary>
    /// Subnautica: the reference BepInEx case, with a nested mod root and case-sensitive container.
    /// </summary>
    /// <remarks>
    /// Two details are load-bearing and both come from the game's own behaviour rather than from any
    /// reference implementation: the mod root is <c>BepInEx/plugins</c> and not a bare directory, and
    /// the container's exact casing matters because BepInEx looks for the literal path.
    /// </remarks>
    private static GameDefinition Subnautica() => new()
    {
        Id = "subnautica",
        Name = "Subnautica",
        Description = "Unity game modded through BepInEx. Mods deploy into BepInEx/plugins.",
        SteamAppId = 264710,
        NexusDomain = "subnautica",
        Executable = "Subnautica.exe",
        ModRoot = "BepInEx/plugins",
        Capabilities =
            GameCapability.HardlinkDeployment
            | GameCapability.SymlinkDeployment
            | GameCapability.ModLoader
            | GameCapability.Profiles
            | GameCapability.FileConflicts
            | GameCapability.Dependencies
            | GameCapability.Launchable
            | GameCapability.ExternalContentScan,
        DeploymentPreference = [DeploymentMethod.Hardlink, DeploymentMethod.Symlink, DeploymentMethod.Copy],
        InstallProbes = [InstallProbeKind.Steam, InstallProbeKind.Registry, InstallProbeKind.PathHint],
        RequiredFiles = ["Subnautica.exe"],
        ContentDirectories = ["bepinex", "qmods", "plugins", "patchers", "config", "core", "fomod"],
        StripPrefixes = ["BepInEx/plugins", "BepInEx", "QMods"],

        InstallTargets =
        [
            new("BepInEx/plugins", "BepInEx/plugins"),
            new("BepInEx/config", "BepInEx/config"),
            new("BepInEx/patchers", "BepInEx/patchers"),
            new("plugins", "BepInEx/plugins"),
            new("config", "BepInEx/config"),
            new("patchers", "BepInEx/patchers"),
        ],
        ModFileExtensions = ["dll", "json", "cfg", "txt", "png"],

        // Deliberately NO LoadOrder: BepInEx discovers plugins and orders them by their declared
        // dependencies, so a load-order page would be a lie. The UI hides it because the capability is
        // absent, which is the whole point of modelling capabilities as data.
        Loader = new ModLoaderDefinition
        {
            Kind = ModLoaderKind.BepInEx,
            DisplayName = "BepInEx",
            PluginsDirectory = "plugins",
            MarkerFiles = ["BepInEx/core/BepInEx.dll"],
            RootFilePrefixes = ["winhttp", "doorstop"],
            MissingMessage =
                "BepInEx is not installed in this game folder. Subnautica mods do nothing without it.",
            DownloadUrl = "https://github.com/BepInEx/BepInEx/releases",
        },
        ExternalScans =
        [
            new ExternalScanDefinition { Kind = "folder", Label = "BepInEx plugin", Directory = string.Empty },
        ],
    };

    /// <summary>
    /// Stardew Valley: folder-per-mod with SMAPI, and no load-order file.
    /// </summary>
    /// <remarks>
    /// Structurally interesting because each mod is a <i>directory</i> containing its own
    /// <c>manifest.json</c>, and SMAPI is a prerequisite the user installs separately — like a script
    /// extender. The game also ships nothing in <c>Mods/</c>, so every folder found there is genuinely
    /// an unmanaged mod.
    /// </remarks>
    private static GameDefinition StardewValley() => new()
    {
        Id = "stardewvalley",
        Name = "Stardew Valley",
        Description = "SMAPI mods, one folder per mod. SMAPI must be installed separately.",
        SteamAppId = 413150,
        GogId = "1453375253",
        XboxId = "ConcernedApe.StardewValleyPC",
        NexusDomain = "stardewvalley",
        Executable = "Stardew Valley.exe",
        ModRoot = "Mods",
        Capabilities =
            GameCapability.HardlinkDeployment
            | GameCapability.SymlinkDeployment
            | GameCapability.ModLoader
            | GameCapability.Profiles
            | GameCapability.FileConflicts
            | GameCapability.Dependencies
            | GameCapability.Launchable
            | GameCapability.ExternalContentScan,
        DeploymentPreference = [DeploymentMethod.Hardlink, DeploymentMethod.Symlink, DeploymentMethod.Copy],
        InstallProbes = [InstallProbeKind.Steam, InstallProbeKind.Gog, InstallProbeKind.Xbox, InstallProbeKind.WellKnown],
        RequiredFiles = ["Stardew Valley.exe"],
        ModFileExtensions = ["dll", "json", "png", "xnb", "tbin", "yaml"],
        Loader = new ModLoaderDefinition
        {
            Kind = ModLoaderKind.Smapi,
            DisplayName = "SMAPI",
            PluginsDirectory = string.Empty,
            MarkerFiles = ["StardewModdingAPI.exe", "smapi-internal/SMAPI.Toolkit.CoreInterfaces.dll"],
            RootFilePrefixes = ["stardewmoddingapi"],
            MissingMessage =
                "SMAPI is not installed. Stardew Valley mods need SMAPI, which is installed separately.",
            DownloadUrl = "https://smapi.io/",
        },
        ExternalScans =
        [
            new ExternalScanDefinition { Kind = "folder", Label = "SMAPI mod", Directory = string.Empty },
        ],
    };

    /// <summary>
    /// Palworld: Unreal Engine, mods land at the install root and split across two subtrees.
    /// </summary>
    /// <remarks>
    /// Structurally the opposite of Subnautica: there is no single mod folder at all. Pak mods belong
    /// under <c>Pal/Content/Paks/~mods</c> and UE4SS Lua/C++ mods under
    /// <c>Pal/Binaries/Win64/ue4ss/Mods</c>, so the archive's own layout is the only guide. The
    /// definition therefore deploys at the install root and keeps a broad content-directory list
    /// rather than trying to force everything through one folder.
    /// </remarks>
    private static GameDefinition Palworld() => new()
    {
        Id = "palworld",
        Name = "Palworld",
        Description = "Unreal Engine game. Pak mods and UE4SS mods deploy into the game's own subtrees.",
        SteamAppId = 1623730,
        NexusDomain = "palworld",
        Executable = "Pal/Binaries/Win64/Palworld-Win64-Shipping.exe",
        ModRoot = string.Empty,
        Capabilities =
            GameCapability.HardlinkDeployment
            | GameCapability.SymlinkDeployment
            | GameCapability.ModLoader
            | GameCapability.Profiles
            | GameCapability.FileConflicts
            | GameCapability.Dependencies
            | GameCapability.Launchable
            | GameCapability.ExternalContentScan,
        DeploymentPreference = [DeploymentMethod.Hardlink, DeploymentMethod.Symlink, DeploymentMethod.Copy],
        InstallProbes = [InstallProbeKind.Steam],

        // Broad on purpose: a pak mod ships `Pal/Content/Paks/...` and a UE4SS mod ships
        // `Pal/Binaries/Win64/...`, and both trees must be recognised as content so neither is treated
        // as a wrapper and hoisted away.
        ContentDirectories =
        [
            "pal", "content", "paks", "binaries", "win64", "ue4ss", "mods", "logicmods",
            "cookedpc", "config", "fomod",
        ],
        ModFileExtensions = ["pak", "utoc", "ucas", "sig", "dll", "lua", "json", "ini", "txt"],

        // Palworld reads paks directly, so there is no load-order file to manage.
        Loader = new ModLoaderDefinition
        {
            Kind = ModLoaderKind.Ue4ss,
            DisplayName = "UE4SS",
            PluginsDirectory = "Pal/Binaries/Win64/ue4ss/Mods",
            MarkerFiles = ["Pal/Binaries/Win64/ue4ss/UE4SS.dll"],
            RootFilePrefixes = ["dwmapi", "xinput"],
            MissingMessage =
                "UE4SS is not installed. Pak mods still work, but Lua and blueprint mods need UE4SS.",
            DownloadUrl = "https://github.com/UE4SS-RE/RE-UE4SS/releases",
        },
        ExternalScans =
        [
            new ExternalScanDefinition
            {
                Kind = "file",
                Label = "Pak mod",
                Directory = "Pal/Content/Paks",
                Extensions = ["pak", "utoc", "ucas"],
            },
            new ExternalScanDefinition
            {
                Kind = "folder",
                Label = "UE4SS mod",
                Directory = "Pal/Binaries/Win64/ue4ss/Mods",
            },
        ],
    };

    /// <summary>
    /// Skyrim Special Edition: the Bethesda shape, with a plugin load order stored outside the game.
    /// </summary>
    /// <remarks>
    /// The most demanding case in the set, and the one that exercises every capability at once: a
    /// plugin list with activation flags, a script extender that must be present for plugin mods to do
    /// anything, and vanilla masters that must be excluded from "unmanaged content" reporting so the
    /// user is not told their own game files are stray mods.
    /// </remarks>
    private static GameDefinition SkyrimSpecialEdition() => new()
    {
        Id = "skyrimse",
        Name = "The Elder Scrolls V: Skyrim Special Edition",
        Description = "Bethesda mods with plugins and a load order, plus the SKSE script extender.",
        SteamAppId = 489830,
        NexusDomain = "skyrimspecialedition",
        Executable = "SkyrimSE.exe",
        ModRoot = "Data",
        Capabilities =
            GameCapability.HardlinkDeployment
            | GameCapability.SymlinkDeployment
            | GameCapability.LoadOrder
            | GameCapability.PluginManagement
            | GameCapability.ModLoader
            | GameCapability.Profiles
            | GameCapability.FileConflicts
            | GameCapability.Dependencies
            | GameCapability.Launchable
            | GameCapability.ExternalContentScan,
        DeploymentPreference = [DeploymentMethod.Hardlink, DeploymentMethod.Symlink, DeploymentMethod.Copy],
        InstallProbes = [InstallProbeKind.Steam, InstallProbeKind.Registry, InstallProbeKind.PathHint],
        RequiredFiles = ["SkyrimSE.exe", "Data/Skyrim.esm"],

        // Directories that ARE content at an archive root. A lone `meshes/` is a mod that ships meshes;
        // a lone `SkyUI_5_2/` is packaging. This list is what distinguishes them.
        ContentDirectories =
        [
            "meshes", "textures", "scripts", "interface", "sound", "music", "strings", "seq",
            "grass", "shaders", "lodsettings", "skse", "fomod", "source", "mcm", "video", "calientetools",
        ],
        ModFileExtensions = ["esp", "esm", "esl", "bsa", "ba2", "dll", "pex", "psc", "nif", "dds", "ini", "json"],

        // Vanilla files, so unowned content is classified honestly. The `cc*` prefix match covers
        // Creation Club content, which otherwise looks exactly like an unmanaged mod.
        BaseFiles =
        [
            "skyrim.esm", "update.esm", "dawnguard.esm", "hearthfires.esm", "dragonborn.esm",
            "_resourcepack.esl", "skyrimse.exe", "skse64_loader.exe", "cc*",
        ],

        LoadOrder = new LoadOrderDefinition
        {
            Strategy = LoadOrderStrategy.PluginsTxt,

            // The plugin list lives in the user's local app data, NOT in the game folder. Modrix encodes
            // this as appdata_dir and warns that getting it wrong is a silent failure.
            AppDataDirectory = "Skyrim Special Edition",
            PluginExtensions = ["esp", "esm", "esl"],
            VanillaPlugins =
            [
                "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
                "_ResourcePack.esl",
            ],
            EnableNewPluginsByDefault = true,
        },
        Loader = new ModLoaderDefinition
        {
            Kind = ModLoaderKind.ScriptExtender,
            DisplayName = "SKSE64",
            PluginsDirectory = "SKSE/Plugins",
            MarkerFiles = ["skse64_loader.exe", "skse64_1_6_1170.dll"],
            RootFilePrefixes = ["skse64"],
            MissingMessage =
                "SKSE plugins are installed but SKSE64 is missing. Mods that use SKSE will not load.",
            DownloadUrl = "https://skse.silverlock.org/",
        },
        ExternalScans =
        [
            new ExternalScanDefinition
            {
                Kind = "file",
                Label = "Plugin",
                Directory = string.Empty,
                Extensions = ["esp", "esm", "esl"],
                SkipBaseFiles = true,
            },
            new ExternalScanDefinition
            {
                Kind = "file",
                Label = "SKSE plugin",
                Directory = "SKSE/Plugins",
                Extensions = ["dll"],
            },
        ],
    };

    /// <summary>
    /// Cyberpunk 2077: mods deploy to the install root, archive-based, no load order.
    /// </summary>
    /// <remarks>
    /// Included because it is the "mods must preserve a deep fixed tree" shape: REDmod and archive mods
    /// ship paths like <c>archive/pc/mod</c> or <c>mods/&lt;name&gt;</c> that must be reproduced exactly,
    /// so the deployment root is the install directory itself and nothing may be hoisted.
    /// </remarks>
    private static GameDefinition Cyberpunk2077() => new()
    {
        Id = "cyberpunk2077",
        Name = "Cyberpunk 2077",
        Description = "REDengine mods and REDmod packages, deployed at the install root.",
        SteamAppId = 1091500,
        NexusDomain = "cyberpunk2077",
        Executable = "bin/x64/Cyberpunk2077.exe",
        ModRoot = string.Empty,
        Capabilities =
            GameCapability.HardlinkDeployment
            | GameCapability.SymlinkDeployment
            | GameCapability.Profiles
            | GameCapability.FileConflicts
            | GameCapability.Dependencies
            | GameCapability.Launchable
            | GameCapability.ExternalContentScan,
        DeploymentPreference = [DeploymentMethod.Hardlink, DeploymentMethod.Symlink, DeploymentMethod.Copy],
        InstallProbes = [InstallProbeKind.Steam],
        ContentDirectories =
        [
            "archive", "pc", "mod", "mods", "bin", "x64", "r6", "scripts", "red4ext",
            "cyber_engine_tweaks", "engine", "tools", "fomod",
        ],
        ModFileExtensions = ["archive", "xl", "dll", "lua", "json", "ini", "toml", "tweak"],
        ExternalScans =
        [
            new ExternalScanDefinition { Kind = "folder", Label = "REDmod package", Directory = "mods" },
            new ExternalScanDefinition
            {
                Kind = "file",
                Label = "Archive mod",
                Directory = "archive/pc/mod",
                Extensions = ["archive", "xl"],
            },
        ],
    };
}
