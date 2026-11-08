using ModManager.Core;

namespace ModManager.App.ViewModels;

/// <summary>How an online mod relates to what is installed locally.</summary>
public enum OnlineModLocalState
{
    /// <summary>Nothing installed from this online entry.</summary>
    NotInstalled = 0,

    /// <summary>Installed at the published version.</summary>
    InstalledLatest = 1,

    /// <summary>Installed, but a newer version is published.</summary>
    UpdateAvailable = 2,

    /// <summary>Installed, but no version information to compare.</summary>
    InstalledUnknown = 3,
}

/// <summary>
/// What the interface may offer for one online mod, and why.
/// </summary>
/// <param name="LocalState">Relationship to the local install.</param>
/// <param name="StateText">Local state as displayed.</param>
/// <param name="PrimaryActionText">Label of the primary button.</param>
/// <param name="CanDownloadViaManager">True when the manager is allowed to download this file.</param>
/// <param name="CanInstall">True when an install would change anything.</param>
/// <param name="CanUninstall">True when there is a local mod to uninstall.</param>
/// <param name="BlockingReason">Why the primary action is unavailable, when it is.</param>
/// <param name="NeedsConfiguration">True when the obstacle is a setting the user can complete.</param>
public sealed record OnlineModActionPlan(
    OnlineModLocalState LocalState,
    string StateText,
    string PrimaryActionText,
    bool CanDownloadViaManager,
    bool CanInstall,
    bool CanUninstall,
    string? BlockingReason,
    bool NeedsConfiguration)
{
    /// <summary>
    /// Decides what may be offered, never pretending a capability we do not have.
    /// </summary>
    /// <param name="summary">Online entry.</param>
    /// <param name="installed">Installed mod, when one is associated.</param>
    /// <param name="authenticated">True when a usable Nexus credential is configured.</param>
    /// <remarks>
    /// The order of the checks is the point. Nexus decides server-side whether a file may be downloaded
    /// through the API, so an unauthenticated session or a mod whose author disabled manager downloads is
    /// reported as "go to the Nexus page" — never as a button that quietly does nothing, and never as a
    /// download that would fail later with a confusing error.
    /// </remarks>
    public static OnlineModActionPlan Evaluate(NexusModSummary summary, ModMetadata? installed, bool authenticated)
    {
        ArgumentNullException.ThrowIfNull(summary);

        // A local mod with the same name but a different Nexus id is a different mod. Treating it as
        // "installed" would compare versions across two unrelated mods and hide the ability to install the
        // one the user actually selected.
        var sameMod = installed is not null
            && (installed.NexusModId is null || installed.NexusModId == summary.ModId);

        var related = sameMod ? installed : null;

        var localState = EvaluateLocalState(summary, related);
        var stateText = localState switch
        {
            OnlineModLocalState.NotInstalled => "未安装",
            OnlineModLocalState.UpdateAvailable => "有更新",
            OnlineModLocalState.InstalledUnknown => "已安装（版本未知）",
            _ => "已是最新",
        };

        if (installed is not null && !sameMod)
        {
            stateText = "已安装同名 Mod";
        }

        if (!authenticated)
        {
            return new OnlineModActionPlan(
                localState,
                stateText,
                "前往 Nexus 页面",
                CanDownloadViaManager: false,
                CanInstall: false,
                CanUninstall: installed is not null,
                "尚未配置 Nexus API 密钥，无法通过管理器下载。可在「设置 → Nexus」配置，或前往 Nexus 页面下载。",
                NeedsConfiguration: true);
        }

        if (!summary.ModManagerDownloadAllowed)
        {
            return new OnlineModActionPlan(
                localState,
                stateText,
                "前往 Nexus 页面",
                CanDownloadViaManager: false,
                CanInstall: false,
                CanUninstall: installed is not null,
                "该 Mod 未授权管理器下载，需要在 Nexus 页面手动下载。",
                NeedsConfiguration: false);
        }

        if (localState == OnlineModLocalState.InstalledLatest)
        {
            return new OnlineModActionPlan(
                localState,
                stateText,
                "已是最新",
                CanDownloadViaManager: true,
                CanInstall: false,
                CanUninstall: true,
                "当前已是最新版本。",
                NeedsConfiguration: false);
        }

        return new OnlineModActionPlan(
            localState,
            stateText,
            localState == OnlineModLocalState.UpdateAvailable ? "更新" : "安装",
            CanDownloadViaManager: true,
            CanInstall: true,
            CanUninstall: installed is not null,
            null,
            NeedsConfiguration: false);
    }

    /// <summary>Determines the local state from the online entry and the installed mod.</summary>
    /// <param name="summary">Online entry.</param>
    /// <param name="installed">Installed mod.</param>
    private static OnlineModLocalState EvaluateLocalState(NexusModSummary summary, ModMetadata? installed)
    {
        if (installed is null)
        {
            return OnlineModLocalState.NotInstalled;
        }

        if (string.IsNullOrWhiteSpace(installed.Version) || string.IsNullOrWhiteSpace(summary.Version))
        {
            return OnlineModLocalState.InstalledUnknown;
        }

        return VersionOrder.IsNewer(summary.Version, installed.Version)
            ? OnlineModLocalState.UpdateAvailable
            : OnlineModLocalState.InstalledLatest;
    }
}
