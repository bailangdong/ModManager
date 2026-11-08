namespace ModManager.Core;

/// <summary>How the archive expects to be installed.</summary>
public enum InstallMethodKind
{
    Unknown = 0,

    /// <summary>Files map directly onto the game's mod root.</summary>
    DirectCopy = 1,

    /// <summary>BepInEx plugin/mod layout.</summary>
    BepInEx = 2,

    /// <summary>MelonLoader Mods/UserLibs layout.</summary>
    MelonLoader = 3,

    /// <summary>Stardew SMAPI manifest layout.</summary>
    Smapi = 4,

    /// <summary>Unreal Engine pak/LogicMods layout.</summary>
    UePak = 5,

    /// <summary>Cyberpunk REDmod or archive layout.</summary>
    Cyberpunk = 6,

    /// <summary>FOMOD installer with user-selectable components.</summary>
    Fomod = 7,

    /// <summary>A dedicated installer executable or script.</summary>
    DedicatedInstaller = 8,

    /// <summary>The archive contains a loader distribution itself (BepInEx/MelonLoader/SMAPI).</summary>
    LoaderDistribution = 9,

    /// <summary>The layout is ambiguous and needs a user decision.</summary>
    Manual = 10,
}

/// <summary>Result of inspecting an archive for its install method.</summary>
/// <param name="Kind">Detected method.</param>
/// <param name="Confidence">0..1; below <see cref="RootAnalysis.Threshold"/> needs confirmation.</param>
/// <param name="Evidence">Short, user-facing reasons, e.g. "contains BepInEx/plugins".</param>
/// <param name="RequiresWizard">True when choices or a dedicated installer are required.</param>
/// <param name="SuggestedRoot">Best root candidate, if the detector has one.</param>
/// <param name="Notes">Additional guidance for the preview.</param>
public sealed record InstallMethodAnalysis(
    InstallMethodKind Kind,
    double Confidence,
    IReadOnlyList<string> Evidence,
    bool RequiresWizard,
    string? SuggestedRoot,
    IReadOnlyList<string> Notes)
{
    /// <summary>True when the automatic decision is weak enough that the UI must ask.</summary>
    public bool NeedsConfirmation => RequiresWizard || Confidence < 0.70d;

    /// <summary>
    /// True when this archive may go through the ordinary "extract into the mod staging area" path
    /// without a wizard or a dedicated loader installer.
    /// </summary>
    public bool CanAutoStage =>
        Kind != InstallMethodKind.LoaderDistribution && !RequiresWizard;

    /// <summary>
    /// User-facing reason why <see cref="CanAutoStage"/> is false, or null when auto-staging is allowed.
    /// Kept on the analysis so every caller gives the same explanation.
    /// </summary>
    public string? AutoStageBlockReason => Kind switch
    {
        InstallMethodKind.LoaderDistribution =>
            "这是 BepInEx/MelonLoader 运行环境，不是普通 Mod。请使用 Loader 安装入口。",
        _ when RequiresWizard =>
            "该安装包需要安装向导（FOMOD 或专用安装器），当前版本尚未实现向导，已停止自动安装。",
        _ => null,
    };
}

/// <summary>Inspects archive layout and game definition to classify the install method.</summary>
public interface IInstallMethodDetector
{
    /// <summary>Classifies an indexed archive for one game. Performs no extraction or writes.</summary>
    InstallMethodAnalysis Analyze(IGameAdapter adapter, ArchiveIndex index);
}
