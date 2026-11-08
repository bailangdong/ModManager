namespace ModManager.Core;

/// <summary>
/// What a game definition declares it can do.
/// </summary>
/// <remarks>
/// DESIGN REFERENCE — Modrix's <c>Engine::capabilities(game)</c> and Vortex's
/// <c>IGame.capabilities</c>. Modrix states the consequence plainly: "Frontends light up
/// features based on <c>Engine::capabilities(game)</c>, so Subnautica (which has no plugin load
/// order) simply hides the Load Order tab."
/// <para>
/// This is the single most important idea we borrow. The alternative — asking
/// <c>if (game is SkyrimAdapter)</c> in every view — spreads game knowledge across the whole
/// UI and makes "add a game without touching core" impossible. Declaring capabilities as data
/// lets the shell render a page per capability and nothing more, and lets a new game be a data
/// file rather than a code change.
/// </para>
/// </remarks>
[Flags]
public enum GameCapability
{
    None = 0,

    /// <summary>Has an ordered plugin list the game reads (Skyrim's <c>Plugins.txt</c>).</summary>
    LoadOrder = 1 << 0,

    /// <summary>Supports named, switchable profiles (MO2-style mod sets).</summary>
    Profiles = 1 << 1,

    /// <summary>Deployment may use hardlinks. False forces Copy, which is slower but universal.</summary>
    HardlinkDeployment = 1 << 2,

    /// <summary>Deployment may use symlinks/reparse points.</summary>
    SymlinkDeployment = 1 << 3,

    /// <summary>A mod loader (BepInEx/UE4SS/SMAPI/SKSE) must be present for mods to work at all.</summary>
    ModLoader = 1 << 4,

    /// <summary>Has plugin files (.esp/.esm/.esl) needing activation and sorting.</summary>
    PluginManagement = 1 << 5,

    /// <summary>MO2-style virtual filesystem. Declared honestly; nothing implements it yet.</summary>
    VirtualDeployment = 1 << 6,

    /// <summary>Mods may specify dependencies we should resolve and check.</summary>
    Dependencies = 1 << 7,

    /// <summary>Two enabled mods can supply the same target path, so the Conflicts page is meaningful.</summary>
    FileConflicts = 1 << 8,

    /// <summary>Has a launcher/executable we can start from the UI.</summary>
    Launchable = 1 << 9,

    /// <summary>Mods land under the user profile (Documents/AppData) instead of the install dir.</summary>
    UserProfileModBase = 1 << 10,

    /// <summary>Game populates its mod folder itself, so "external content" scanning is useful.</summary>
    ExternalContentScan = 1 << 11,

    /// <summary>Archive format support beyond zip is required (7z/rar common in the wild).</summary>
    NonZipArchives = 1 << 12,
}

/// <summary>
/// Human-readable capability helpers. Kept next to the enum so the UI never re-derives meaning.
/// </summary>
public static class GameCapabilityExtensions
{
    /// <summary>True when <paramref name="value"/> carries every flag in <paramref name="flags"/>.</summary>
    public static bool Has(this GameCapability value, GameCapability flags)
        => (value & flags) == flags;

    /// <summary>True when <paramref name="value"/> carries at least one flag in <paramref name="flags"/>.</summary>
    public static bool HasAny(this GameCapability value, GameCapability flags)
        => (value & flags) != 0;

    /// <summary>Localised display name used by the capabilities panel.</summary>
    public static string Describe(this GameCapability value) => value switch
    {
        GameCapability.LoadOrder => "加载顺序",
        GameCapability.Profiles => "配置方案",
        GameCapability.HardlinkDeployment => "硬链接部署",
        GameCapability.SymlinkDeployment => "符号链接部署",
        GameCapability.ModLoader => "Mod 加载器",
        GameCapability.PluginManagement => "插件管理",
        GameCapability.VirtualDeployment => "虚拟部署（VFS）",
        GameCapability.Dependencies => "依赖管理",
        GameCapability.FileConflicts => "文件冲突",
        GameCapability.Launchable => "可启动",
        GameCapability.UserProfileModBase => "用户目录 Mod",
        GameCapability.ExternalContentScan => "外部内容扫描",
        GameCapability.NonZipArchives => "非 ZIP 压缩包",
        _ => value.ToString(),
    };
}
